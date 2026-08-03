using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CraftArchitectEngineWorker;

public static partial class ManagedHost
{
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static readonly HttpClient SessionHttp = new();
    private static readonly UniversalisService SessionUniversalis = new(SessionHttp);
    private static readonly WorkerMarketCacheService SessionMarketCache =
        new(SessionUniversalis);
    private static readonly MarketPriceLadderAnalysisService SessionMarketLadder = new();
    private static readonly GarlandService SessionGarland = new(SessionHttp);
    private static readonly VendorCacheService SessionVendorCache =
        new(SessionGarland, NullLogger<VendorCacheService>.Instance);
    private static readonly RecipeCalculationService SessionRecipeCalculator =
        new(SessionGarland, SessionVendorCache);
    private static WorkerCanonicalSession _canonicalSession = new();
    private static readonly Dictionary<Guid, PendingMarketEvidencePublication>
        PendingMarketEvidencePublications = [];
    private static readonly TimeSpan OperationLeaseDuration = TimeSpan.FromMinutes(2);
    private static long _sessionRevision;
    private static string? _sessionRestoreWarning;
    private static bool _sessionMigratedFromLegacy;
    private static ActiveWorkerSessionOperation? _activeOperation;
    private static WorkerSessionOperationProjection? _operationProjection;

    [JSExport]
    [SupportedOSPlatform("browser")]
    public static Task<string> ExecuteSessionCommandJson(string messageJson) =>
        ExecuteSessionCommandJsonCore(messageJson);

    public static async Task<string> ExecuteSessionCommandJsonCore(string messageJson)
    {
        var message = JsonSerializer.Deserialize<EngineWorkerMessage>(messageJson, WireJsonOptions)
            ?? throw new InvalidOperationException("Worker session message is empty.");
        if (!string.Equals(message.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) ||
            !string.Equals(
                message.Kind,
                WorkerSessionProtocol.CommandMessageKind,
                StringComparison.Ordinal) ||
            message.Generation <= 0 ||
            message.ExecutionId is not { } executionId ||
            message.TransactionId is not { } transactionId ||
            message.Payload is not { } payload)
        {
            throw new InvalidOperationException("Worker session message identity is invalid.");
        }

        var command = payload.Deserialize<WorkerSessionCommandEnvelope>(WireJsonOptions)
            ?? throw new InvalidOperationException("Worker session command is empty.");

        await SessionGate.WaitAsync();
        try
        {
            WorkerSessionResultEnvelope result;
            if (!string.Equals(
                    command.ContractVersion,
                    WorkerSessionProtocol.ContractVersion,
                    StringComparison.Ordinal))
            {
                result = CreateSessionResult(
                    command.CommandKind,
                    accepted: false,
                    "contract-version-mismatch",
                    "The Worker session command contract is unsupported.",
                    CaptureShellProjection());
            }
            else if (command.ExpectedRevision != _sessionRevision)
            {
                result = CreateSessionResult(
                    command.CommandKind,
                    accepted: false,
                    "stale-revision",
                    $"Expected Worker session revision {command.ExpectedRevision}, but {_sessionRevision} is active.",
                    CaptureShellProjection());
            }
            else
            {
                result = await ExecuteSessionCommandAsync(command);
            }

            return JsonSerializer.Serialize(
                new EngineWorkerMessage(
                    ProtocolVersion,
                    WorkerSessionProtocol.ResultMessageKind,
                    message.Generation,
                    executionId,
                    transactionId,
                    JsonSerializer.SerializeToElement(result, WireJsonOptions)),
                WireJsonOptions);
        }
        finally
        {
            SessionGate.Release();
        }
    }

    private static async Task<WorkerSessionResultEnvelope> ExecuteSessionCommandAsync(
        WorkerSessionCommandEnvelope command)
    {
        try
        {
            if (PrepareOperationCommand(command) is { } operationRejection)
            {
                return operationRejection;
            }

            return command.CommandKind switch
            {
                WorkerSessionCommandKinds.OperationBegin =>
                    BeginOperation(command),
                WorkerSessionCommandKinds.OperationRenew =>
                    RenewOperation(command),
                WorkerSessionCommandKinds.OperationComplete =>
                    CompleteOperation(command),
                WorkerSessionCommandKinds.OperationAbort =>
                    AbortOperation(command),
                "restore" => RestoreSession(command),
                "shell" => CreateSessionResult(
                    command.CommandKind,
                    accepted: true,
                    null,
                    null,
                    CaptureShellProjection()),
                "export" => ExportSession(command),
                WorkerSessionCommandKinds.RecipeProjection => CreateSessionResult(
                    command.CommandKind,
                    accepted: true,
                    null,
                    null,
                    CaptureRecipeProjection()),
                WorkerSessionCommandKinds.AcquisitionProjection => CreateSessionResult(
                    command.CommandKind,
                    accepted: true,
                    null,
                    null,
                    CaptureAcquisitionProjection(command)),
                WorkerSessionCommandKinds.MarketProjection => CreateSessionResult(
                    command.CommandKind,
                    accepted: true,
                    null,
                    null,
                    CaptureMarketProjection(command.Payload
                        .Deserialize<WorkerMarketProjectionRequest>(WireJsonOptions))),
                WorkerSessionCommandKinds.ProcurementProjection => CreateSessionResult(
                    command.CommandKind,
                    accepted: true,
                    null,
                    null,
                    CaptureProcurementProjection()),
                WorkerSessionCommandKinds.TradeProjection => CreateSessionResult(
                    command.CommandKind,
                    accepted: true,
                    null,
                    null,
                    await CaptureTradeProjectionAsync(command)),
                WorkerSessionCommandKinds.ProjectItemsMutation =>
                    MutateProjectItems(command),
                WorkerSessionCommandKinds.PlanIdentityMutation =>
                    MutatePlanIdentity(command),
                WorkerSessionCommandKinds.ActiveContextMutation =>
                    MutateActiveContext(command),
                WorkerSessionCommandKinds.AcquisitionMutation =>
                    MutateAcquisition(command),
                WorkerSessionCommandKinds.RecipeBuild =>
                    await BuildRecipeAsync(command),
                WorkerSessionCommandKinds.MarketAnalysisRun =>
                    await RunMarketAnalysisAsync(command),
                WorkerSessionCommandKinds.MarketEvidencePublicationStage =>
                    PublishMarketEvidence(command),
                WorkerSessionCommandKinds.MarketEvidencePublication =>
                    PublishMarketEvidence(command),
                WorkerSessionCommandKinds.MarketItemEvidencePublication =>
                    PublishMarketItemEvidence(command),
                WorkerSessionCommandKinds.MarketItemRefresh =>
                    await RefreshMarketItemAsync(command),
                WorkerSessionCommandKinds.MarketLensMutation =>
                    await ApplyMarketLensAsync(command),
                WorkerSessionCommandKinds.ProcurementRun =>
                    await RunProcurementAsync(command),
                WorkerSessionCommandKinds.ProcurementToleranceMutation =>
                    MutateProcurementTolerance(command),
                _ => CreateSessionResult(
                    command.CommandKind,
                    accepted: false,
                    "unknown-command",
                    $"Unknown Worker session command '{command.CommandKind}'.",
                    CaptureShellProjection())
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateSessionResult(
                command.CommandKind,
                accepted: false,
                "command-rejected",
                ex.Message,
                CaptureShellProjection());
        }
    }

    private static WorkerSessionResultEnvelope? PrepareOperationCommand(
        WorkerSessionCommandEnvelope command)
    {
        ExpireOperationLease();
        if (IsOperationCommand(command.CommandKind))
        {
            return null;
        }

        if (RequiresOperationAuthority(command.CommandKind))
        {
            if (_activeOperation is null)
            {
                return CreateSessionResult(
                    command.CommandKind,
                    accepted: false,
                    "operation-required",
                    "This work no longer owns the active plan operation.",
                    CaptureShellProjection());
            }
            if (command.OperationId != _activeOperation.OperationId)
            {
                return CreateSessionResult(
                    command.CommandKind,
                    accepted: false,
                    "operation-busy",
                    _activeOperation.StatusMessage,
                    CaptureShellProjection());
            }

            _activeOperation.LastRenewedUtc = DateTime.UtcNow;
            _operationProjection = _operationProjection! with
            {
                Disposition = WorkerSessionOperationDisposition.Current,
                IsActive = true
            };
            return null;
        }

        if (SupersedesDerivedOperation(command.CommandKind) &&
            _activeOperation is not null &&
            command.OperationId != _activeOperation.OperationId)
        {
            EndActiveOperation(
                WorkerSessionOperationDisposition.Aborted,
                "The plan changed before the previous update finished.");
        }

        return null;
    }

    private static WorkerSessionResultEnvelope BeginOperation(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerSessionOperationBeginRequest>(
            WireJsonOptions)
            ?? throw new InvalidOperationException("Worker operation request is empty.");
        if (request.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.IntentKey) ||
            string.IsNullOrWhiteSpace(request.StatusMessage))
        {
            throw new InvalidOperationException(
                "Worker operation identity and status are required.");
        }

        ExpireOperationLease();
        if (_activeOperation is not null)
        {
            if (_activeOperation.OperationId == request.OperationId)
            {
                _activeOperation.LastRenewedUtc = DateTime.UtcNow;
                _operationProjection = _operationProjection! with
                {
                    Disposition = WorkerSessionOperationDisposition.Current,
                    IsActive = true
                };
            }
            else
            {
                _operationProjection = new WorkerSessionOperationProjection(
                    _activeOperation.OperationId,
                    _activeOperation.Kind,
                    _activeOperation.IntentKey,
                    _activeOperation.BaseRevision,
                    WorkerSessionOperationDisposition.Busy,
                    IsActive: true,
                    _activeOperation.StatusMessage);
            }

            return CreateSessionResult(
                command.CommandKind,
                accepted: true,
                null,
                null,
                CaptureShellProjection());
        }

        _activeOperation = new ActiveWorkerSessionOperation(
            request.OperationId,
            request.Kind,
            request.IntentKey.Trim(),
            _sessionRevision,
            request.StatusMessage.Trim(),
            DateTime.UtcNow);
        _operationProjection = new WorkerSessionOperationProjection(
            request.OperationId,
            request.Kind,
            request.IntentKey.Trim(),
            _sessionRevision,
            WorkerSessionOperationDisposition.Acquired,
            IsActive: true,
            request.StatusMessage.Trim());
        return CreateSessionResult(
            command.CommandKind,
            accepted: true,
            null,
            null,
            CaptureShellProjection());
    }

    private static WorkerSessionResultEnvelope RenewOperation(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerSessionOperationControlRequest>(
            WireJsonOptions)
            ?? throw new InvalidOperationException("Worker operation renewal is empty.");
        ExpireOperationLease();
        if (_activeOperation?.OperationId != request.OperationId)
        {
            return CreateSessionResult(
                command.CommandKind,
                accepted: false,
                "operation-superseded",
                "This operation no longer owns the active plan.",
                CaptureShellProjection());
        }

        _activeOperation.LastRenewedUtc = DateTime.UtcNow;
        _operationProjection = _operationProjection! with
        {
            Disposition = WorkerSessionOperationDisposition.Current,
            IsActive = true
        };
        return CreateSessionResult(
            command.CommandKind,
            accepted: true,
            null,
            null,
            CaptureShellProjection());
    }

    private static WorkerSessionResultEnvelope CompleteOperation(
        WorkerSessionCommandEnvelope command) =>
        EndOperation(command, WorkerSessionOperationDisposition.Completed, "Ready");

    private static WorkerSessionResultEnvelope AbortOperation(
        WorkerSessionCommandEnvelope command) =>
        EndOperation(command, WorkerSessionOperationDisposition.Aborted, "Ready");

    private static WorkerSessionResultEnvelope EndOperation(
        WorkerSessionCommandEnvelope command,
        WorkerSessionOperationDisposition disposition,
        string statusMessage)
    {
        var request = command.Payload.Deserialize<WorkerSessionOperationControlRequest>(
            WireJsonOptions)
            ?? throw new InvalidOperationException("Worker operation completion is empty.");
        ExpireOperationLease();
        if (_activeOperation?.OperationId != request.OperationId)
        {
            return CreateSessionResult(
                command.CommandKind,
                accepted: false,
                "operation-superseded",
                "This operation no longer owns the active plan.",
                CaptureShellProjection());
        }

        EndActiveOperation(disposition, statusMessage);
        return CreateSessionResult(
            command.CommandKind,
            accepted: true,
            null,
            null,
            CaptureShellProjection());
    }

    private static void EndActiveOperation(
        WorkerSessionOperationDisposition disposition,
        string statusMessage)
    {
        if (_activeOperation is null)
        {
            return;
        }

        _operationProjection = new WorkerSessionOperationProjection(
            _activeOperation.OperationId,
            _activeOperation.Kind,
            _activeOperation.IntentKey,
            _activeOperation.BaseRevision,
            disposition,
            IsActive: false,
            statusMessage);
        _activeOperation = null;
    }

    private static void ExpireOperationLease()
    {
        if (_activeOperation is not null &&
            DateTime.UtcNow - _activeOperation.LastRenewedUtc > OperationLeaseDuration)
        {
            EndActiveOperation(
                WorkerSessionOperationDisposition.Aborted,
                "The previous update expired before it finished.");
        }
    }

    private static bool IsOperationCommand(string commandKind) =>
        commandKind is
            WorkerSessionCommandKinds.OperationBegin or
            WorkerSessionCommandKinds.OperationRenew or
            WorkerSessionCommandKinds.OperationComplete or
            WorkerSessionCommandKinds.OperationAbort;

    private static bool RequiresOperationAuthority(string commandKind) =>
        commandKind is
            WorkerSessionCommandKinds.MarketAnalysisRun or
            WorkerSessionCommandKinds.MarketEvidencePublicationStage or
            WorkerSessionCommandKinds.MarketEvidencePublication or
            WorkerSessionCommandKinds.MarketItemEvidencePublication or
            WorkerSessionCommandKinds.MarketItemRefresh or
            WorkerSessionCommandKinds.MarketLensMutation or
            WorkerSessionCommandKinds.ProcurementRun or
            WorkerSessionCommandKinds.ProcurementToleranceMutation;

    private static bool SupersedesDerivedOperation(string commandKind) =>
        commandKind is
            "restore" or
            WorkerSessionCommandKinds.ProjectItemsMutation or
            WorkerSessionCommandKinds.PlanIdentityMutation or
            WorkerSessionCommandKinds.ActiveContextMutation or
            WorkerSessionCommandKinds.RecipeBuild or
            WorkerSessionCommandKinds.AcquisitionMutation;

    private static WorkerSessionResultEnvelope RestoreSession(
        WorkerSessionCommandEnvelope command)
    {
        var restore = command.Payload.Deserialize<WorkerSessionRestorePayload>(WireJsonOptions)
            ?? throw new InvalidOperationException("Worker session restore payload is empty.");
        if (restore.Revision < 0 ||
            (_sessionRevision > 0 && restore.Revision != _sessionRevision + 1))
        {
            throw new InvalidOperationException(
                $"Worker session restore revision {restore.Revision} cannot follow {_sessionRevision}.");
        }

        var replacement = new WorkerCanonicalSession();
        var restoreResult = replacement.Restore(
            restore.StoredPlan,
            restore.TrackStoredPlanIdentity);
        _canonicalSession = replacement;
        PendingMarketEvidencePublications.Clear();
        _sessionRevision = restore.Revision +
                           (restoreResult.DurableRepairPatch is null ? 0 : 1);
        _sessionRestoreWarning = restoreResult.Warning;
        _sessionMigratedFromLegacy = restore.MigratedFromLegacy;
        return CreateSessionResult(
            command.CommandKind,
            accepted: true,
            null,
            restoreResult.Warning,
            CaptureShellProjection(),
            restoreResult.DurableRepairPatch);
    }

    private static WorkerSessionResultEnvelope ExportSession(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerSessionExportRequest>(WireJsonOptions)
            ?? throw new InvalidOperationException("Worker session export payload is empty.");
        var projection = new WorkerSessionExportProjection(
            _sessionRevision,
            _canonicalSession.Export(
                request.PlanId,
                request.PlanName,
                request.IncludeSourcePlanIdentity));
        return CreateSessionResult(
            command.CommandKind,
            accepted: true,
            null,
            null,
            projection);
    }

    private static WorkerSessionResultEnvelope MutateProjectItems(
        WorkerSessionCommandEnvelope command)
    {
        var mutation = command.Payload.Deserialize<WorkerProjectItemsMutation>(WireJsonOptions)
            ?? throw new InvalidOperationException("Project-items mutation payload is empty.");
        var items = _canonicalSession.Session.ProjectItems
            .Select(CloneProjectItem)
            .ToList();

        switch (mutation.Operation.Trim().ToLowerInvariant())
        {
            case "add":
                if (mutation.Item is not null && items.All(item => item.Id != mutation.Item.Id))
                {
                    items.Add(CloneProjectItem(mutation.Item));
                }
                break;
            case "remove":
                items.RemoveAll(item => item.Id == mutation.ItemId);
                break;
            case "quantity":
                {
                    var item = items.FirstOrDefault(candidate => candidate.Id == mutation.ItemId);
                    if (item is not null)
                    {
                        item.Quantity = Math.Clamp(mutation.Quantity, 1, 9999);
                    }
                    break;
                }
            case "hq":
                {
                    var item = items.FirstOrDefault(candidate => candidate.Id == mutation.ItemId);
                    if (item is not null)
                    {
                        item.MustBeHq = mutation.MustBeHq;
                    }
                    break;
                }
            case "replace":
                items = mutation.Items?.Select(CloneProjectItem).ToList() ?? [];
                break;
            case "clear":
                _canonicalSession.Session.ActivatePlan(
                    null,
                    Array.Empty<ProjectItem>(),
                    _canonicalSession.Session.ActiveContext,
                    "session cleared",
                    CraftSessionIdentity.CreateNew());
                _canonicalSession.InvalidateLegacyProcurementRoute();
                return CompleteMutation(
                    command.CommandKind,
                    CaptureRecipeProjection,
                    _canonicalSession.CreateDurablePatch(
                        replacePlanJson: true,
                        replacePlanStateJson: true,
                        replaceProjectItems: true,
                        replaceMarketEvidence: true,
                        replaceProcurementRoute: true,
                        replaceSourceIdentity: true));
            default:
                throw new InvalidOperationException(
                    $"Unknown project-items operation '{mutation.Operation}'.");
        }

        _canonicalSession.Session.ReplaceProjectItems(items);
        _canonicalSession.InvalidateLegacyProcurementRoute();
        return CompleteMutation(
            command.CommandKind,
            CaptureRecipeProjection,
            _canonicalSession.CreateDurablePatch(
                replaceProjectItems: true,
                replaceProcurementRoute: true));
    }

    private static WorkerSessionResultEnvelope MutatePlanIdentity(
        WorkerSessionCommandEnvelope command)
    {
        var mutation = command.Payload.Deserialize<WorkerPlanIdentityMutation>(WireJsonOptions)
            ?? throw new InvalidOperationException("Plan-identity mutation payload is empty.");
        if (string.IsNullOrWhiteSpace(mutation.PlanId) ||
            string.IsNullOrWhiteSpace(mutation.PlanName))
        {
            throw new InvalidOperationException("Plan identity requires an id and name.");
        }

        _canonicalSession.Session.TrackSourceIdentity(
            mutation.PlanId.Trim(),
            mutation.PlanName.Trim());
        return CompleteMutation(
            command.CommandKind,
            CaptureShellProjection,
            _canonicalSession.CreateDurablePatch(replaceSourceIdentity: true));
    }

    private static WorkerSessionResultEnvelope MutateActiveContext(
        WorkerSessionCommandEnvelope command)
    {
        var mutation = command.Payload.Deserialize<WorkerActiveContextMutation>(
                WireJsonOptions)
            ?? throw new InvalidOperationException(
                "Active-context mutation payload is empty.");
        var session = _canonicalSession.Session;
        if (session.BorrowActivePlan() is null)
        {
            throw new InvalidOperationException(
                "Build a recipe plan before changing its market context.");
        }
        if (session.BorrowMarketEvidence().ItemAnalyses.Count > 0)
        {
            throw new InvalidOperationException(
                "Published market evidence must be repriced into the new context atomically.");
        }

        var selectedRegion = MarketFetchScopeResolver
            .NormalizeSelectedRegions(mutation.SelectedRegion, null)
            .Single();
        var selectedDataCenter = MarketFetchScopeResolver.ResolveValidDataCenter(
            selectedRegion,
            mutation.SelectedDataCenter);
        session.ReplaceActiveContext(
            new CraftSessionActiveContext(
                selectedRegion,
                selectedDataCenter,
                session.ActiveContext.World,
                mutation.Scope),
            "active market context changed");
        _canonicalSession.InvalidateLegacyProcurementRoute();
        return CompleteMutation(
            command.CommandKind,
            CaptureShellProjection,
            _canonicalSession.CreateDurablePatch(
                replaceProcurementRoute: true,
                replaceContext: true));
    }

    private static WorkerSessionResultEnvelope MutateAcquisition(
        WorkerSessionCommandEnvelope command)
    {
        var mutation = command.Payload.Deserialize<WorkerAcquisitionMutation>(WireJsonOptions)
            ?? throw new InvalidOperationException("Acquisition mutation payload is empty.");
        var operationState = new CraftOperationState();
        var operations = new CraftOperationCoordinator(
            _canonicalSession.Session,
            operationState);
        var decisions = new CoreAcquisitionDecisionService(
            _canonicalSession.Session,
            operations);
        CoreAcquisitionDecisionResult result;
        if (mutation.Source.HasValue)
        {
            result = decisions.ChangeSource(mutation.ItemId, mutation.Source.Value);
        }
        else if (mutation.MustBeHq.HasValue)
        {
            result = decisions.ChangeMarketHq(mutation.ItemId, mutation.MustBeHq.Value);
        }
        else
        {
            throw new InvalidOperationException("Acquisition mutation has no requested change.");
        }

        if (!result.Changed)
        {
            return CreateSessionResult(
                command.CommandKind,
                accepted: true,
                null,
                "The acquisition choice was already current.",
                CaptureRecipeProjection());
        }

        _canonicalSession.InvalidateLegacyProcurementRoute();
        return CompleteMutation(
            command.CommandKind,
            CaptureRecipeProjection,
            _canonicalSession.CreateDurablePatch(
                replacePlanStateJson: true,
                replaceProcurementRoute: true));
    }

    private static async Task<WorkerSessionResultEnvelope> BuildRecipeAsync(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerRecipeBuildRequest>(WireJsonOptions)
            ?? throw new InvalidOperationException("Recipe-build payload is empty.");
        if (request.ProjectItems.Count == 0)
        {
            throw new InvalidOperationException("A recipe plan needs at least one project item.");
        }

        var targets = request.ProjectItems
            .Select(item => (item.Id, item.Name, item.Quantity, item.MustBeHq))
            .ToList();
        var plan = await SessionRecipeCalculator.BuildPlanAsync(
            targets,
            request.SelectedDataCenter,
            string.Empty);
        var changedDefaults = AcquisitionPlanningService.ApplyCheapestAcquisitionDefaults(
            plan,
            Array.Empty<DetailedShoppingPlan>());
        _canonicalSession.Session.ActivatePlan(
            plan,
            request.ProjectItems,
            new CraftSessionActiveContext(
                request.SelectedRegion,
                request.SelectedDataCenter,
                string.Empty,
                request.PriceFetchScope),
            "recipe plan built");
        _canonicalSession.InvalidateLegacyProcurementRoute();

        _sessionRevision++;
        var recipe = CaptureRecipeProjection();
        var message = changedDefaults > 0
            ? $"Plan built with {plan.RootItems.Count} targets and {changedDefaults} acquisition defaults."
            : $"Plan built with {plan.RootItems.Count} targets.";
        return CreateMutationResult(
            command.CommandKind,
            new WorkerRecipeBuildOutcome(
                true,
                message,
                RecipePlannerCommandMessageLevel.Success,
                recipe),
            _canonicalSession.CreateDurablePatch(
                replacePlanJson: true,
                replacePlanStateJson: true,
                replaceProjectItems: true,
                replaceMarketEvidence: true,
                replaceProcurementRoute: true,
                replaceContext: true));
    }

    private static async Task<WorkerSessionResultEnvelope> RunMarketAnalysisAsync(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerMarketAnalysisRequest>(WireJsonOptions)
            ?? throw new InvalidOperationException("Market-analysis request is empty.");
        var session = _canonicalSession.Session;
        if (session.ActivePlan is null)
        {
            throw new InvalidOperationException("Build a recipe plan before running Market Analysis.");
        }

        var workflow = CreateMarketWorkflow(session);
        var worldData = await SessionUniversalis.GetWorldDataAsync();
        var requestedDataCenters = MarketFetchScopeResolver.GetDataCenters(
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion,
            request.SelectedRegions);
        var expectedWorlds = requestedDataCenters
            .Where(dataCenter => worldData.DataCenterToWorlds.ContainsKey(dataCenter))
            .ToDictionary(
                dataCenter => dataCenter,
                dataCenter => (IReadOnlyList<string>)worldData.DataCenterToWorlds[dataCenter],
                StringComparer.OrdinalIgnoreCase);
        var result = await workflow.RunAnalysisAsync(
            new CoreMarketAnalysisWorkflowRequest(
                request.ForceRefreshData,
                request.Scope,
                request.SelectedDataCenter,
                request.SelectedRegion,
                request.Lens,
                expectedWorlds,
                MarketAnalysisExecutionOptions.Synchronous,
                RequestedDataCenters: requestedDataCenters));
        if (!result.Published)
        {
            throw new InvalidOperationException(
                "Market Analysis could not publish against the active plan revision.");
        }

        session.ReplaceActiveContext(
            new CraftSessionActiveContext(
                request.SelectedRegion,
                request.SelectedDataCenter,
                session.ActiveContext.World,
                request.Scope),
            "market analysis context published");
        foreach (var analysis in session.BorrowMarketEvidence().ItemAnalyses)
        {
            WorkerSessionCoordinator.CompactMarketAnalysisForPublication(analysis);
        }
        _canonicalSession.InvalidateLegacyProcurementRoute();
        SessionMarketCache.Clear();
        _sessionRevision++;
        return CreateMutationResult(
            command.CommandKind,
            new WorkerMarketAnalysisOutcome(
                result.Published,
                result.AnalyzedCount,
                result.ChangedDecisionCount,
                result.FetchedCount,
                CaptureMarketProjection(includeDetails: false)),
            _canonicalSession.CreateDurablePatch(
                replacePlanStateJson: true,
                replaceMarketEvidence: true,
                replaceProcurementRoute: true,
                replaceContext: true));
    }

    private static WorkerSessionResultEnvelope PublishMarketEvidence(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerMarketEvidencePublicationRequest>(
                WireJsonOptions)
            ?? throw new InvalidOperationException("Market-evidence publication is empty.");
        if (request.OperationId == Guid.Empty ||
            request.BaseRevision != command.ExpectedRevision)
        {
            throw new InvalidOperationException(
                "Market-evidence publication operation identity is invalid.");
        }
        RemoveExpiredMarketEvidencePublications();
        if (request.ResetStaging)
        {
            if (!PendingMarketEvidencePublications.ContainsKey(request.OperationId) &&
                PendingMarketEvidencePublications.Count >= 2)
            {
                throw new InvalidOperationException(
                    "Another market-evidence publication is already being prepared.");
            }
            PendingMarketEvidencePublications[request.OperationId] =
                new PendingMarketEvidencePublication(
                request.OperationId,
                request.BaseRevision,
                request.Scope,
                request.SelectedDataCenter,
                request.SelectedRegion,
                request.Lens,
                request.RequestedDataCenters ?? Array.Empty<string>());
        }

        if (!PendingMarketEvidencePublications.TryGetValue(
                request.OperationId,
                out var staging))
        {
            throw new InvalidOperationException(
                "Market-evidence publication staging was not initialized for this operation.");
        }
        staging.Validate(request);
        staging.ItemAnalyses.AddRange(request.ItemAnalyses);
        staging.ShoppingPlans.AddRange(request.ShoppingPlans);
        staging.UnavailableItemIds.UnionWith(request.UnavailableItemIds);
        staging.FetchedCount += request.FetchedCount;
        if (!request.CompleteStaging)
        {
            return CreateSessionResult(
                command.CommandKind,
                accepted: true,
                null,
                null,
                CaptureShellProjection());
        }

        request = new WorkerMarketEvidencePublicationRequest(
            staging.OperationId,
            staging.BaseRevision,
            staging.Scope,
            staging.SelectedDataCenter,
            staging.SelectedRegion,
            staging.Lens,
            staging.ItemAnalyses,
            staging.ShoppingPlans,
            staging.UnavailableItemIds,
            staging.FetchedCount,
            ResetStaging: true,
            CompleteStaging: true,
            RequestedDataCenters: staging.RequestedDataCenters);
        PendingMarketEvidencePublications.Remove(staging.OperationId);
        var session = _canonicalSession.Session;
        var plan = session.BorrowActivePlan()
            ?? throw new InvalidOperationException(
                "Build a recipe plan before publishing market evidence.");
        var planSessionVersion = session.PlanSessionVersion;
        var stamp = session.CaptureVersionStamp();
        var recipeLayer = new WorkerRecipeLayerWorkflow(_canonicalSession);
        var recipeBasis = recipeLayer.BuildMarketAnalysisRecipeBasis(
            plan,
            request.UnavailableItemIds);
        foreach (var analysis in request.ItemAnalyses)
        {
            WorkerSessionCoordinator.CompactMarketAnalysisForPublication(analysis);
        }
        var changedDecisions = AcquisitionPlanningService.ReconcileAcquisitionDecisions(
            plan,
            request.ShoppingPlans);
        if (!session.TryPublishOwnedMarketAnalysis(
                stamp,
                plan,
                planSessionVersion,
                request.ItemAnalyses,
                request.ShoppingPlans,
                changedDecisions > 0,
                "main-thread market evidence accepted by Worker",
                request.UnavailableItemIds,
                lens: request.Lens,
                recipeBasis: recipeBasis,
                publicationContext: new MarketIntelligencePublicationContext(
                    MarketIntelligencePublicationContextKind.Known,
                    request.Scope,
                    request.SelectedDataCenter,
                    request.SelectedRegion,
                    request.RequestedDataCenters ?? Array.Empty<string>(),
                    new Dictionary<string, IReadOnlyList<string>>(
                        StringComparer.OrdinalIgnoreCase),
                    MaxAge: null,
                    ForceRefreshData: false,
                    RecommendationMode.MinimizeTotalCost,
                    request.Lens,
                    stamp,
                    planSessionVersion,
                    stamp.MarketAnalysis,
                    DateTime.UtcNow)))
        {
            throw new InvalidOperationException(
                "Market evidence became stale before the Worker could publish it.");
        }

        session.ReplaceActiveContext(
            new CraftSessionActiveContext(
                request.SelectedRegion,
                request.SelectedDataCenter,
                session.ActiveContext.World,
                request.Scope),
            "market analysis context published");
        _canonicalSession.InvalidateLegacyProcurementRoute();
        _sessionRevision++;
        return CreateMutationResult(
            command.CommandKind,
            new WorkerMarketEvidenceCommitProjection(
                request.ShoppingPlans.Count,
                changedDecisions,
                request.FetchedCount),
            _canonicalSession.CreateDurablePatch(
                replacePlanStateJson: true,
                replaceMarketEvidence: true,
                replaceProcurementRoute: true,
                replaceContext: true));
    }

    private static void RemoveExpiredMarketEvidencePublications()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var operationId in PendingMarketEvidencePublications
                     .Where(pair => pair.Value.CreatedAtUtc < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            PendingMarketEvidencePublications.Remove(operationId);
        }
    }

    private static WorkerSessionResultEnvelope PublishMarketItemEvidence(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerMarketItemEvidencePublicationRequest>(
                WireJsonOptions)
            ?? throw new InvalidOperationException("Market-item evidence publication is empty.");
        var session = _canonicalSession.Session;
        var plan = session.BorrowActivePlan()
            ?? throw new InvalidOperationException(
                "Build a recipe plan before publishing market evidence.");
        var evidence = session.BorrowMarketEvidence();
        var compactAnalysis =
            WorkerSessionCoordinator.CloneAndCompactMarketAnalysisForPublication(
                request.ItemAnalysis);
        var analyses = MarketEvidenceCollectionMerger.MergeAnalyses(
            evidence.ItemAnalyses,
            [compactAnalysis]);
        var shoppingPlans = MarketEvidenceCollectionMerger.MergeShoppingPlans(
            evidence.ShoppingPlans ?? [],
            [request.ShoppingPlan]);
        var unavailableItemIds = evidence.UnavailableMarketItemIds
            .Where(itemId => itemId != request.ItemId)
            .ToHashSet();
        var planSessionVersion = session.PlanSessionVersion;
        var stamp = session.CaptureVersionStamp();
        var changedDecisions = AcquisitionPlanningService.ReconcileAcquisitionDecisions(
            plan,
            shoppingPlans);
        if (!session.TryPublishMarketAnalysis(
                stamp,
                plan,
                planSessionVersion,
                analyses,
                shoppingPlans,
                changedDecisions > 0,
                "main-thread item evidence accepted by Worker",
                unavailableItemIds,
                evidence.RecommendationMode,
                request.Lens,
                evidence.RecipeBasis,
                publicationContext: evidence.PublicationContext))
        {
            throw new InvalidOperationException(
                "Market evidence became stale before the Worker could publish it.");
        }

        session.ReplaceActiveContext(
            new CraftSessionActiveContext(
                request.SelectedRegion,
                request.SelectedDataCenter,
                session.ActiveContext.World,
                request.Scope),
            "market item context published");
        _canonicalSession.InvalidateLegacyProcurementRoute();
        _sessionRevision++;
        return CreateMutationResult(
            command.CommandKind,
            new WorkerMarketItemRefreshOutcome(
                CoreProcurementItemRefreshStatus.Refreshed,
                request.ShoppingPlan.Name,
                CaptureMarketProjection(
                    includeDetails: true,
                    worldDetailItemId: request.ItemId)),
            _canonicalSession.CreateDurablePatch(
                replacePlanStateJson: true,
                replaceMarketEvidence: true,
                replaceProcurementRoute: true,
                replaceContext: true));
    }

    private static async Task<WorkerSessionResultEnvelope> ApplyMarketLensAsync(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerMarketLensMutation>(WireJsonOptions)
            ?? throw new InvalidOperationException("Market-lens request is empty.");
        var result = await CreateMarketWorkflow(_canonicalSession.Session)
            .ApplyLensAsync(new CoreApplyMarketAnalysisLensRequest(request.Lens));
        if (!result.Published)
        {
            throw new InvalidOperationException(
                "The market lens cannot be applied until Market Analysis has evidence.");
        }

        _canonicalSession.InvalidateLegacyProcurementRoute();
        _sessionRevision++;
        return CreateMutationResult(
            command.CommandKind,
            CaptureMarketProjection(includeDetails: false),
            _canonicalSession.CreateDurablePatch(
                replacePlanStateJson: true,
                replaceMarketEvidence: true,
                replaceProcurementRoute: true,
                replaceContext: true));
    }

    private static async Task<WorkerSessionResultEnvelope> RefreshMarketItemAsync(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerMarketItemRefreshRequest>(
                WireJsonOptions)
            ?? throw new InvalidOperationException("Market-item refresh request is empty.");
        var session = _canonicalSession.Session;
        if (session.ActivePlan is null)
        {
            throw new InvalidOperationException(
                "Build a recipe plan before refreshing market evidence.");
        }

        var execution = new MarketAnalysisExecutionService(
            SessionMarketCache,
            SessionMarketLadder);
        var reconciliation = new MarketEvidenceReconciliationService(
            execution,
            SessionMarketCache,
            SessionUniversalis,
            SessionMarketLadder);
        var shopping = new MarketShoppingService(SessionMarketCache);
        var worldData = await SessionUniversalis.GetWorldDataAsync();
        shopping.SetWorldNameToIdMapping(
            worldData.WorldIdToName.ToDictionary(pair => pair.Value, pair => pair.Key));
        var workflow = new CoreProcurementWorkflowService(
            session,
            new ProcurementRouteExecutionService(reconciliation, shopping),
            reconciliation,
            new WorkerRecipeLayerWorkflow(_canonicalSession),
            new CraftOperationCoordinator(session, new CraftOperationState()));
        var requestedDataCenters = request.RequestedDataCenters is { Count: > 0 }
            ? request.RequestedDataCenters
            : MarketFetchScopeResolver.GetDataCenters(
                request.Scope,
                request.SelectedDataCenter,
                request.SelectedRegion);
        var expectedWorlds = requestedDataCenters
            .Where(dataCenter => worldData.DataCenterToWorlds.ContainsKey(dataCenter))
            .ToDictionary(
                dataCenter => dataCenter,
                dataCenter => (IReadOnlyList<string>)worldData.DataCenterToWorlds[dataCenter],
                StringComparer.OrdinalIgnoreCase);
        var result = await workflow.RefreshItemMarketDataAsync(
            new CoreProcurementItemRefreshWorkflowRequest(
                request.ItemId,
                request.ItemName,
                request.Scope,
                request.SelectedDataCenter,
                request.SelectedRegion,
                request.Lens,
                expectedWorlds,
                ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous,
                TargetDataCenter: request.TargetDataCenter,
                TargetWorldName: request.TargetWorldName,
                ObservedEvidence: request.ObservedEvidence,
                RequestedDataCenters: requestedDataCenters));
        if (result.Status != CoreProcurementItemRefreshStatus.Refreshed)
        {
            throw new InvalidOperationException(
                $"Market evidence for {request.ItemName} was not refreshed ({result.Status}).");
        }

        var refreshedEvidence = session.BorrowMarketEvidence();
        var detailedAnalysis = refreshedEvidence.ItemAnalyses
            .FirstOrDefault(analysis => analysis.ItemId == request.ItemId);
        var detailedShoppingPlan = refreshedEvidence.ShoppingPlans?
            .FirstOrDefault(plan => plan.ItemId == request.ItemId);
        var detachedDetailedAnalysis = detailedAnalysis is null
            ? null
            : JsonSerializer.SerializeToElement(detailedAnalysis, WireJsonOptions)
                .Deserialize<MarketItemAnalysis>(WireJsonOptions);
        foreach (var analysis in refreshedEvidence.ItemAnalyses)
        {
            WorkerSessionCoordinator.CompactMarketAnalysisForPublication(analysis);
        }
        _canonicalSession.InvalidateLegacyProcurementRoute();
        SessionMarketCache.Clear();
        _sessionRevision++;
        var market = CaptureMarketProjection(
            includeDetails: true,
            worldDetailItemId: request.ItemId);
        if (detachedDetailedAnalysis is not null && detailedShoppingPlan is not null)
        {
            market = market with
            {
                ShoppingPlans = [detailedShoppingPlan],
                ItemAnalyses = [detachedDetailedAnalysis]
            };
        }
        return CreateMutationResult(
            command.CommandKind,
            new WorkerMarketItemRefreshOutcome(
                result.Status,
                result.ItemName,
                market),
            _canonicalSession.CreateDurablePatch(
                replacePlanStateJson: true,
                replaceMarketEvidence: true,
                replaceProcurementRoute: true,
                replaceContext: true));
    }

    private static async Task<WorkerSessionResultEnvelope> RunProcurementAsync(
        WorkerSessionCommandEnvelope command)
    {
        var timing = Stopwatch.StartNew();
        var request = command.Payload.Deserialize<WorkerProcurementRequest>(WireJsonOptions)
            ?? throw new InvalidOperationException("Procurement request is empty.");
        var session = _canonicalSession.Session;
        if (session.BorrowActivePlan() is null)
        {
            throw new InvalidOperationException("Build a recipe plan before generating a route.");
        }
        if (session.BorrowMarketEvidence().ShoppingPlans is not { Count: > 0 } sourcePlans)
        {
            throw new InvalidOperationException(
                "Run Market Analysis before generating a procurement route.");
        }

        var execution = new MarketAnalysisExecutionService(
            SessionMarketCache,
            SessionMarketLadder);
        var reconciliation = new MarketEvidenceReconciliationService(
            execution,
            SessionMarketCache,
            SessionUniversalis,
            SessionMarketLadder);
        var shopping = new MarketShoppingService(SessionMarketCache);
        var worldData = await SessionUniversalis.GetWorldDataAsync();
        var worldDataMilliseconds = timing.ElapsedMilliseconds;
        shopping.SetWorldNameToIdMapping(
            worldData.WorldIdToName.ToDictionary(pair => pair.Value, pair => pair.Key));
        var workflow = new CoreProcurementWorkflowService(
            session,
            new ProcurementRouteExecutionService(reconciliation, shopping),
            reconciliation,
            new WorkerRecipeLayerWorkflow(_canonicalSession),
            new CraftOperationCoordinator(session, new CraftOperationState()));
        var expectedWorlds = MarketFetchScopeResolver
            .GetDataCenters(request.Scope, request.SelectedDataCenter, request.SelectedRegion)
            .Where(dataCenter => worldData.DataCenterToWorlds.ContainsKey(dataCenter))
            .ToDictionary(
                dataCenter => dataCenter,
                dataCenter => (IReadOnlyList<string>)worldData.DataCenterToWorlds[dataCenter],
                StringComparer.OrdinalIgnoreCase);
        var preparationCompletedAt = 0L;
        var reconciliationCompletedAt = 0L;
        var workflowProgress = new ImmediateProgress<string>(message =>
        {
            if (preparationCompletedAt == 0 &&
                (message.StartsWith("Reconciling ", StringComparison.Ordinal) ||
                 message.StartsWith("Preparing authoritative ", StringComparison.Ordinal)))
            {
                preparationCompletedAt = timing.ElapsedMilliseconds;
            }
            else if (reconciliationCompletedAt == 0 &&
                     message.StartsWith("Optimizing ", StringComparison.Ordinal))
            {
                reconciliationCompletedAt = timing.ElapsedMilliseconds;
            }
        });
        var result = await workflow.RunAnalysisAsync(
            new CoreProcurementWorkflowRequest(
                request.Scope,
                request.SelectedDataCenter,
                request.SelectedRegion,
                request.Lens,
                new MarketAnalysisConfig
                {
                    TravelTolerance = request.TravelTolerance,
                    EnableSplitWorld = request.IncludeSplitPurchases,
                    StartFromHomeDataCenter = request.StartFromHomeDataCenter,
                    HomeDataCenter = request.SelectedDataCenter,
                    TravelPriority = request.TravelPriority
                },
                request.IncludeSplitPurchases,
                sourcePlans,
                request.ExcludedWorlds?.ToHashSet() ?? new HashSet<MarketWorldKey>(),
                request.ExcludedItemWorlds?.ToHashSet() ?? new HashSet<MarketItemWorldKey>(),
                expectedWorlds,
                ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous),
            workflowProgress);
        var workflowMilliseconds = timing.ElapsedMilliseconds - worldDataMilliseconds;
        if (result.Status != CoreProcurementWorkflowStatus.Published)
        {
            throw new InvalidOperationException(
                $"Procurement route was not published ({result.Status}).");
        }

        SessionMarketCache.Clear();
        _canonicalSession.InvalidateLegacyProcurementRoute();
        _sessionRevision++;
        var projectionStarted = timing.ElapsedMilliseconds;
        var diagnostics = new WorkerProcurementDiagnostics(
            worldDataMilliseconds,
            Math.Max(0, preparationCompletedAt - worldDataMilliseconds),
            Math.Max(0, reconciliationCompletedAt - preparationCompletedAt),
            Math.Max(0, timing.ElapsedMilliseconds - reconciliationCompletedAt),
            workflowMilliseconds);
        var mutation = CreateMutationResult(
            command.CommandKind,
            new WorkerProcurementOutcome(
                result.Status,
                result.ShoppingPlanCount,
                CaptureProcurementProjection(),
                diagnostics),
            new WorkerSessionDurablePatch(
                ReplaceProcurementRoute: true,
                ProcurementRouteJson: _canonicalSession.ExportProcurementRoute(),
                ProcurementTravelTolerance: request.TravelTolerance));
        Console.WriteLine(
            $"[EngineSession] procurement world-data={worldDataMilliseconds}ms " +
            $"workflow={workflowMilliseconds}ms projection-and-patch=" +
            $"{timing.ElapsedMilliseconds - projectionStarted}ms total={timing.ElapsedMilliseconds}ms");
        return mutation;
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static WorkerSessionResultEnvelope MutateProcurementTolerance(
        WorkerSessionCommandEnvelope command)
    {
        var request =
            command.Payload.Deserialize<WorkerProcurementToleranceMutation>(WireJsonOptions)
            ?? throw new InvalidOperationException("Procurement tolerance request is empty.");
        if (!_canonicalSession.Session.TrySelectProcurementTravelTolerance(
                request.TravelTolerance))
        {
            throw new InvalidOperationException(
                "This route does not contain a precomputed selection for that tolerance.");
        }

        _canonicalSession.InvalidateLegacyProcurementRoute();
        _sessionRevision++;
        return CreateMutationResult(
            command.CommandKind,
            CaptureProcurementProjection(),
            new WorkerSessionDurablePatch(
                ProcurementTravelTolerance: request.TravelTolerance));
    }

    private static CoreMarketAnalysisWorkflowService CreateMarketWorkflow(
        CraftSessionState session)
    {
        var execution = new MarketAnalysisExecutionService(
            SessionMarketCache,
            SessionMarketLadder);
        var reconciliation = new MarketEvidenceReconciliationService(
            execution,
            SessionMarketCache,
            SessionUniversalis,
            SessionMarketLadder);
        var operations = new CraftOperationCoordinator(
            session,
            new CraftOperationState());
        return new CoreMarketAnalysisWorkflowService(
            session,
            reconciliation,
            SessionMarketLadder,
            new WorkerRecipeLayerWorkflow(_canonicalSession),
            operations);
    }

    private static WorkerSessionResultEnvelope CompleteMutation(
        string commandKind,
        Func<object> capturePublicProjection,
        WorkerSessionDurablePatch durablePatch)
    {
        _sessionRevision++;
        return CreateMutationResult(commandKind, capturePublicProjection(), durablePatch);
    }

    private static WorkerSessionResultEnvelope CreateMutationResult(
        string commandKind,
        object publicProjection,
        WorkerSessionDurablePatch? durablePatch = null)
    {
        try
        {
            var durable = durablePatch is null
                ? _canonicalSession.Export(
                      "autosave",
                      "Autosave",
                      includeSourcePlanIdentity: true)
                  ?? new StoredPlan { Id = "autosave", Name = "Autosave" }
                : null;
            var carrier = new WorkerSessionMutationProjection(
                CaptureShellProjection(),
                durable,
                durablePatch,
                JsonSerializer.SerializeToElement(publicProjection, WireJsonOptions));
            return CreateSessionResult(
                commandKind,
                accepted: true,
                null,
                null,
                carrier);
        }
        catch
        {
            // A rejected mutation must never strand the main thread behind a
            // revision that was not durably committed by engine-worker.js.
            _sessionRevision--;
            throw;
        }
    }

    private static WorkerRecipePlannerProjection CaptureRecipeProjection()
    {
        var session = _canonicalSession.Session;
        var plan = session.BorrowActivePlan();
        var evidence = session.BorrowMarketEvidence();
        var shoppingPlans = evidence.ShoppingPlans ?? Array.Empty<DetailedShoppingPlan>();
        var displayStates = RecipePlanTreeDisplayBuilder.Build(
            plan,
            shoppingPlans,
            RecipePlanAcquisitionQuoteBasis.MarketAnalysis,
            isRefreshing: false,
            evidencePublishedAtUtc: null);
        var route = session.BorrowProcurementOverlay();
        var routeSummaries = route?.ShoppingPlans?.Count > 0
            ? RecipePlanProcurementRouteSummaryBuilder.Build(
                route.ShoppingPlans,
                session.ActiveContext.DataCenter ?? "Aether")
            : new Dictionary<int, RecipePlanProcurementRouteSummary>();
        return new WorkerRecipePlannerProjection(
            _sessionRevision,
            session.Identity.SourcePlanId,
            session.Identity.SourcePlanName ?? plan?.Name ?? session.Identity.Name,
            session.ActiveContext.DataCenter ?? plan?.DataCenter ?? "Aether",
            session.ActiveContext.Region ?? "North America",
            session.ProjectItems,
            plan?.RootItems.Select(node =>
                ProjectRecipeNode(node, displayStates, routeSummaries)).ToArray()
                ?? Array.Empty<WorkerRecipeNodeProjection>(),
            evidence.ItemAnalyses.Count > 0 || shoppingPlans.Count > 0,
            route?.RouteDecision is not null);
    }

    private static WorkerMarketProjection CaptureMarketProjection(
        bool includeDetails = true,
        int? worldDetailItemId = null) =>
        CaptureMarketProjection(new WorkerMarketProjectionRequest(
            includeDetails,
            worldDetailItemId));

    private static WorkerMarketProjection CaptureMarketProjection(
        WorkerMarketProjectionRequest? request)
    {
        request ??= new WorkerMarketProjectionRequest();
        var includeDetails = request.IncludeDetails;
        var session = _canonicalSession.Session;
        var evidence = session.BorrowMarketEvidence();
        var analyses = evidence.ItemAnalyses.ToDictionary(analysis => analysis.ItemId);
        var shoppingPlans = evidence.ShoppingPlans ?? Array.Empty<DetailedShoppingPlan>();
        var defaultWorldDetailItemId = includeDetails
            ? null
            : shoppingPlans
                .OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(plan => plan.ItemId)
                .Select(plan => (int?)plan.ItemId)
                .FirstOrDefault();
        var worldDetailItemId = request.WorldDetailItemId ?? defaultWorldDetailItemId;
        var items = shoppingPlans
            .Select(plan =>
            {
                analyses.TryGetValue(plan.ItemId, out var analysis);
                var totalCost = plan.SplitTotalCost ??
                                plan.RecommendedWorld?.TotalCost ??
                                0;
                var worldName = plan.RequiresSplitPurchase
                    ? $"{plan.RecommendedSplit!.Count} world split"
                    : plan.RecommendedWorld?.WorldName ?? "Unavailable";
                var includeWorlds =
                    (includeDetails && !worldDetailItemId.HasValue) ||
                    plan.ItemId == worldDetailItemId;
                var worlds = includeWorlds
                    ? plan.WorldOptions
                        .OrderBy(world => world.LensRank)
                        .ThenBy(world => world.TotalCost)
                        .Select(world => new WorkerMarketWorldProjection(
                            world.DataCenter,
                            world.WorldName,
                            world.TotalQuantityPurchased,
                            world.TotalCost,
                            world.AveragePricePerUnit,
                            world.HasSufficientStock,
                            world.MarketDataQualityBucket,
                            world.MarketDataAge))
                        .ToArray()
                    : Array.Empty<WorkerMarketWorldProjection>();
                return new WorkerMarketItemProjection(
                    plan.ItemId,
                    plan.Name,
                    plan.IconId,
                    plan.QuantityNeeded,
                    plan.HasOptions && string.IsNullOrWhiteSpace(plan.Error),
                    plan.HasSufficientStock,
                    plan.TotalAvailableQuantity,
                    totalCost,
                    plan.QuantityNeeded > 0 ? totalCost / (decimal)plan.QuantityNeeded : 0,
                    worldName,
                    plan.WorldOptions.Count,
                    analysis?.WorstDataQualityBucket ?? MarketDataQualityBucket.Missing,
                    plan.Error ?? plan.MarketDataWarning ?? analysis?.Warning,
                    worlds);
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var context = session.ActiveContext;
        var candidateItems = new WorkerRecipeLayerWorkflow(_canonicalSession)
            .BuildMarketAnalysisCandidates(session.BorrowActivePlan())
            .ToArray();
        var candidateCount = candidateItems.Length;
        return new WorkerMarketProjection(
            _sessionRevision,
            session.BorrowActivePlan() is not null,
            items.Length > 0,
            context.MarketFetchScope ?? MarketFetchScope.EntireRegion,
            context.DataCenter ?? "Aether",
            context.Region ?? "North America",
            evidence.Lens,
            candidateCount,
            items.Count(item => item.IsAvailable),
            Math.Max(0, candidateCount - items.Count(item => item.IsAvailable)),
            items.Sum(item => item.EstimatedTotalCost),
            items,
            candidateItems,
            includeDetails
                ? (evidence.ShoppingPlans ?? Array.Empty<DetailedShoppingPlan>())
                    .Where(plan =>
                        !worldDetailItemId.HasValue ||
                        plan.ItemId == worldDetailItemId.Value)
                    .ToArray()
                : Array.Empty<DetailedShoppingPlan>(),
            includeDetails
                ? evidence.ItemAnalyses
                    .Where(analysis =>
                        !worldDetailItemId.HasValue ||
                        analysis.ItemId == worldDetailItemId.Value)
                    .ToArray()
                : Array.Empty<MarketItemAnalysis>(),
            evidence.PublicationContext?.RequestedDataCenters);
    }

    private static WorkerProcurementProjection CaptureProcurementProjection()
    {
        var session = _canonicalSession.Session;
        var plan = session.BorrowActivePlan();
        var evidence = session.BorrowMarketEvidence();
        var overlay = session.BorrowProcurementOverlay();
        var decision = overlay?.RouteDecision;
        var activeItems = new WorkerRecipeLayerWorkflow(_canonicalSession)
            .BuildActiveProcurementItems(plan);
        var worlds = (overlay?.RouteCards ?? Array.Empty<WorldProcurementCardModel>())
            .Select(world => new WorkerProcurementWorldProjection(
                world.DataCenter,
                world.WorldName,
                world.IsVendor,
                world.IsCongested,
                world.CongestedWarning,
                world.Classification,
                world.TotalCost,
                world.ItemCount,
                world.TotalQuantity,
                world.Vendors,
                world.SelectedVendorName,
                world.Items.Select(item => new WorkerProcurementItemProjection(
                    item.ItemId,
                    item.ItemName,
                    item.IconId,
                    item.QuantityOnThisWorld,
                    item.TotalQuantityNeeded,
                    item.PricePerUnit,
                    item.PriceIsEffectiveCost,
                    item.TotalCost,
                    item.IsSplitPurchase,
                    item.TravelContext,
                    item.Vendor)).ToArray()))
            .OrderBy(world => world.IsVendor ? 1 : 0)
            .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fixedCost = decision?.FixedAcquisitionGilCost ?? 0;
        var tolerance = decision?.TravelTolerance ?? 0;
        return new WorkerProcurementProjection(
            _sessionRevision,
            plan is not null,
            evidence.ShoppingPlans is { Count: > 0 },
            decision is not null,
            session.ActiveContext.MarketFetchScope ?? MarketFetchScope.EntireRegion,
            session.ActiveContext.DataCenter ?? "Aether",
            session.ActiveContext.Region ?? "North America",
            evidence.Lens,
            activeItems.Count,
            tolerance,
            MarketRouteScoring.GetToleranceLabel(tolerance),
            decision?.TravelPriority ?? MarketTravelPriority.DataCenterTransfersFirst,
            session.ActiveContext.MarketFetchScope == MarketFetchScope.EntireRegion,
            decision?.IncludeSplitPurchases,
            (decision?.SelectedGilCost ?? 0) + fixedCost,
            (decision?.CheapestGilCost ?? 0) + fixedCost,
            decision?.PremiumGil ?? 0,
            decision?.SelectedWorldStops ?? 0,
            decision?.SelectedDataCenterTransfers ?? 0,
            decision?.RouteSearchWasTruncated ?? false,
            decision?.ToleranceSelections.Select(selection =>
                new WorkerProcurementToleranceProjection(
                    selection.MinimumTolerance,
                    selection.MaximumTolerance,
                    selection.GilCost + selection.FixedAcquisitionGilCost,
                    selection.WorldStops,
                    selection.DataCenterTransfers)).ToArray()
                ?? Array.Empty<WorkerProcurementToleranceProjection>(),
            worlds,
            overlay?.ShoppingPlans?
                .Where(plan =>
                    !string.IsNullOrWhiteSpace(plan.Error) ||
                    !string.IsNullOrWhiteSpace(plan.MarketDataWarning))
                .Select(plan => new WorkerProcurementIssueProjection(
                    plan.ItemId,
                    plan.Name,
                    plan.Error,
                    plan.MarketDataWarning))
                .ToArray()
                ?? Array.Empty<WorkerProcurementIssueProjection>(),
            Array.Empty<DetailedShoppingPlan>(),
            CompactProcurementDecision(decision));
    }

    private static async Task<WorkerTradeProjection> CaptureTradeProjectionAsync(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerTradeProjectionRequest>(WireJsonOptions)
            ?? new WorkerTradeProjectionRequest();
        var session = _canonicalSession.Session;
        var plan = session.BorrowActivePlan();
        if (plan is null)
        {
            return new WorkerTradeProjection(
                _sessionRevision,
                HasPlan: false,
                session.Identity.SourcePlanId,
                session.Identity.SourcePlanName ?? "Active craft plan",
                session.ActiveContext.DataCenter ?? "Aether",
                session.ActiveContext.Region ?? "North America",
                session.ActiveContext.MarketFetchScope ?? MarketFetchScope.EntireRegion,
                Array.Empty<string>(),
                session.BorrowMarketEvidence().Lens,
                session.PlanSessionVersion,
                session.Versions.MarketAnalysis,
                Array.Empty<ProjectItem>(),
                Array.Empty<TradeOrderRootItemSnapshot>(),
                Array.Empty<CommissionPayrollInputLine>(),
                Array.Empty<MaterialAggregate>(),
                Array.Empty<WorkerAcquisitionRowProjection>(),
                Array.Empty<TradeOrderCraftLaborSnapshot>(),
                Array.Empty<string>());
        }

        var recipeLayer = new WorkerRecipeLayerWorkflow(_canonicalSession);
        var demand = recipeLayer.BuildDemandProjection(plan);
        var activeDemand = demand.ActiveProcurementDemand
            .Where(row => row.Quantity > 0)
            .ToArray();
        var activeItems = demand.ToActiveProcurementMaterialAggregates()
            .Where(item => item.TotalQuantity > 0)
            .ToArray();
        var evidence = session.BorrowMarketEvidence();
        var acquisition = CaptureAcquisitionProjection(new WorkerSessionCommandEnvelope(
            WorkerSessionProtocol.ContractVersion,
            WorkerSessionCommandKinds.AcquisitionProjection,
            _sessionRevision,
            JsonSerializer.SerializeToElement(
                new WorkerAcquisitionProjectionRequest("All"),
                WireJsonOptions)));
        var materialLines = ApplyOnHandReferenceValues(
            new CommissionCostBasisResolver().BuildSelectedSourceLines(
                activeDemand,
                evidence.ItemAnalyses,
                evidence.ShoppingPlans ?? Array.Empty<DetailedShoppingPlan>()),
            acquisition.Rows);

        var warnings = new List<string>();
        if (evidence.ItemAnalyses.Count == 0)
        {
            warnings.Add(
                "No market-analysis evidence is loaded. Payment uses plan prices where available and may be incomplete.");
        }
        warnings.AddRange(materialLines.SelectMany(line => line.Warnings));

        var craftLabor = Array.Empty<TradeOrderCraftLaborSnapshot>();
        if (request.IncludeCraftLabor)
        {
            var snapshotService = new RecipeOperationSnapshotService(
                SessionGarland,
                new RecipeResolutionService(),
                NullLogger<RecipeOperationSnapshotService>.Instance);
            var snapshot = await snapshotService.BuildAsync(plan);
            craftLabor = snapshot.GetRequiredCrafts()
                .Where(craft => craft.CraftCount > 0)
                .Select(craft => new TradeOrderCraftLaborSnapshot(
                    craft.NodeId,
                    craft.ResultItemId,
                    craft.ResultItemName,
                    craft.RequestedQuantity,
                    craft.CraftCount,
                    craft.JobName,
                    craft.RecipeLevel,
                    craft.HasStructuralDiagnostics
                        ? [$"Recipe-operation diagnostics exist for {craft.ResultItemName}."]
                        : []))
                .ToArray();
            var unresolvedCount = snapshot.GetUnresolvedRequiredCrafts().Count();
            if (unresolvedCount > 0)
            {
                warnings.Add(
                    $"Labor-standard evidence is incomplete: {unresolvedCount:N0} active crafts could not be resolved.");
            }
            if (craftLabor.Length == 0)
            {
                warnings.Add(
                    "Labor-standard evidence is unavailable. No active craft synths were resolved for this order.");
            }
        }

        var rootItems = plan.RootItems
            .Select(node =>
            {
                var unitPrice = node.MustBeHq && node.HqMarketPrice > 0
                    ? node.HqMarketPrice
                    : node.MarketPrice;
                return new TradeOrderRootItemSnapshot(
                    node.ItemId,
                    node.Name,
                    node.Quantity,
                    node.MustBeHq,
                    unitPrice * node.Quantity);
            })
            .ToArray();
        return new WorkerTradeProjection(
            _sessionRevision,
            HasPlan: true,
            session.Identity.SourcePlanId,
            session.Identity.SourcePlanName ?? plan.Name ?? "Active craft plan",
            session.ActiveContext.DataCenter ?? plan.DataCenter ?? "Aether",
            session.ActiveContext.Region ?? "North America",
            session.ActiveContext.MarketFetchScope ?? MarketFetchScope.EntireRegion,
            ResolveTradeRequestedDataCenters(
                evidence.PublicationContext,
                session.ActiveContext.MarketFetchScope ?? MarketFetchScope.EntireRegion,
                session.ActiveContext.DataCenter ?? plan.DataCenter ?? "Aether",
                session.ActiveContext.Region ?? "North America"),
            evidence.Lens,
            session.PlanSessionVersion,
            session.Versions.MarketAnalysis,
            session.ProjectItems,
            rootItems,
            materialLines,
            activeItems,
            acquisition.Rows,
            craftLabor,
            warnings
                .Where(warning => !string.IsNullOrWhiteSpace(warning))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static IReadOnlyList<string> ResolveTradeRequestedDataCenters(
        MarketIntelligencePublicationContext? publication,
        MarketFetchScope scope,
        string selectedDataCenter,
        string selectedRegion)
    {
        var recorded = publication?.RequestedDataCenters
            .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return recorded is { Length: > 0 }
            ? recorded
            : MarketFetchScopeResolver.GetDataCenters(scope, selectedDataCenter, selectedRegion);
    }

    private static MarketRouteDecision? CompactProcurementDecision(
        MarketRouteDecision? decision) =>
        decision is null
            ? null
            : decision with
            {
                ToleranceSelections = decision.ToleranceSelections
                    .Select(selection => selection with
                    {
                        ShoppingPlans = Array.Empty<DetailedShoppingPlan>()
                    })
                    .ToArray()
            };

    private static WorkerAcquisitionProjection CaptureAcquisitionProjection(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerAcquisitionProjectionRequest>(WireJsonOptions)
            ?? new WorkerAcquisitionProjectionRequest("All");
        var filter = Enum.TryParse<CoreAcquisitionFilter>(
            request.Filter,
            ignoreCase: true,
            out var parsedFilter)
            ? parsedFilter
            : CoreAcquisitionFilter.All;
        var session = _canonicalSession.Session;
        var plan = session.ActivePlan;
        if (plan is null)
        {
            return new WorkerAcquisitionProjection(
                _sessionRevision,
                filter.ToString(),
                HasPlan: false,
                RootItemCount: 0,
                PricedItemCount: 0,
                UnavailableItemCount: 0,
                Rows: Array.Empty<WorkerAcquisitionRowProjection>(),
                MarketCandidateCount: 0,
                ActiveProcurementCount: 0,
                HasProcurementRoute: false,
                ActiveProcurementItems: Array.Empty<MaterialAggregate>(),
                UnavailableMarketItems: Array.Empty<CoreMarketDataUnavailableItem>());
        }

        var evidence = session.MarketEvidence;
        var demand = new RecipeDemandProjectionService().Build(plan, snapshot: null);
        var snapshot = CoreAcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            evidence.ShoppingPlans ?? Array.Empty<DetailedShoppingPlan>(),
            evidence.UnavailableMarketItemIds,
            filter,
            demand);
        var rows = snapshot.VisibleRows
            .Select(row => ProjectAcquisitionRow(row, snapshot.CostContext, evidence))
            .ToArray();
        var unavailableItems = evidence.UnavailableMarketItemIds
            .Select(itemId =>
            {
                var name = snapshot.Rows
                    .FirstOrDefault(row => row.ItemId == itemId)
                    ?.ItemName ?? $"Item {itemId}";
                return new CoreMarketDataUnavailableItem(itemId, name);
            })
            .ToArray();
        return new WorkerAcquisitionProjection(
            _sessionRevision,
            filter.ToString(),
            HasPlan: true,
            RootItemCount: plan.RootItems.Count,
            PricedItemCount: evidence.ShoppingPlans?.Count ?? 0,
            UnavailableItemCount: evidence.UnavailableMarketItemIds.Count,
            Rows: rows,
            MarketCandidateCount: snapshot.MarketAnalysisCandidates.Count,
            ActiveProcurementCount: snapshot.ActiveProcurementItems.Count,
            HasProcurementRoute: session.BorrowProcurementOverlay()?.RouteDecision is not null,
            ActiveProcurementItems: snapshot.ActiveProcurementItems,
            UnavailableMarketItems: unavailableItems);
    }

    private static WorkerAcquisitionRowProjection ProjectAcquisitionRow(
        CoreDecisionRow row,
        AcquisitionCostContext costContext,
        CraftSessionMarketEvidence evidence)
    {
        var availableSources = AcquisitionPlanningService.GetAvailableSources(row.Node);
        if (!availableSources.Contains(row.Source))
        {
            availableSources.Insert(0, row.Source);
        }

        var hasCalculatedCost = CoreAcquisitionEvaluationCostCalculator.TryGetCost(
            row,
            row.Source,
            costContext,
            out var calculatedTotalCost);

        return new WorkerAcquisitionRowProjection(
            row.NodeId,
            row.ItemId,
            row.ItemName,
            row.IconId,
            row.Source,
            row.SourceReason,
            row.MustBeHq,
            row.HasChildren,
            row.CanCraft,
            row.CanBeHq,
            row.CanBuyFromMarket,
            row.CanBuyFromVendor,
            row.TotalQuantity,
            row.ActiveQuantity,
            row.UsedIn,
            row.HasSuppressedOccurrences,
            row.IsFullySuppressed,
            row.SuppressedBy,
            row.IsActiveProcurement,
            row.HasEditableOccurrences,
            row.IsMarketCandidate,
            row.MarketEvidence,
            row.EstimatedCost,
            evidence.UnavailableMarketItemIds.Contains(row.ItemId),
            row.UnitPrice,
            hasCalculatedCost ? calculatedTotalCost : 0m,
            availableSources,
            BuildAcquisitionOptions(row, costContext));
    }

    private static IReadOnlyList<WorkerAcquisitionOptionProjection> BuildAcquisitionOptions(
        CoreDecisionRow row,
        AcquisitionCostContext costContext)
    {
        var options = new List<WorkerAcquisitionOptionProjection>();
        if (row.HasChildren && row.CanCraft)
        {
            var hasCost = CoreAcquisitionEvaluationCostCalculator.TryGetCost(
                row,
                AcquisitionSource.Craft,
                costContext,
                out var cost);
            options.Add(new WorkerAcquisitionOptionProjection(
                AcquisitionSource.Craft,
                "Craft",
                "Uses the recipe tree with current evidence for child purchases.",
                hasCost ? $"{cost:N0}g" : "-",
                IsAvailable: true,
                IsProjectedUnsupported: false,
                TotalCost: hasCost ? cost : null));
        }

        if (row.CanBuyFromMarket && !row.MustBeHq)
        {
            options.Add(BuildMarketOption(
                row,
                costContext,
                AcquisitionSource.MarketBuyNq,
                "Buy NQ",
                hqOnly: false));
        }
        if (row.CanBuyFromMarket && row.CanBeHq)
        {
            options.Add(BuildMarketOption(
                row,
                costContext,
                AcquisitionSource.MarketBuyHq,
                "Buy HQ",
                hqOnly: true));
        }
        if (row.CanBuyFromVendor)
        {
            var hasCost = CoreAcquisitionEvaluationCostCalculator.TryGetCost(
                row,
                AcquisitionSource.VendorBuy,
                costContext,
                out var cost);
            var vendor = row.VendorOptions
                .Where(option => option.IsGilVendor)
                .OrderBy(option => option.Price)
                .FirstOrDefault();
            options.Add(new WorkerAcquisitionOptionProjection(
                AcquisitionSource.VendorBuy,
                "Vendor",
                vendor is null
                    ? "No gil vendor price loaded."
                    : $"{vendor.Name} - {vendor.Location}",
                hasCost ? $"{cost:N0}g" : "-",
                hasCost,
                IsProjectedUnsupported: false,
                TotalCost: hasCost ? cost : null));
        }
        options.Add(new WorkerAcquisitionOptionProjection(
            AcquisitionSource.OnHand,
            "On hand",
            "Use stock already held outside this plan.",
            "0g",
            IsAvailable: true,
            IsProjectedUnsupported: false,
            TotalCost: 0m));
        if (!row.CanBuyFromMarket && !row.CanBuyFromVendor && !row.HasChildren)
        {
            options.Add(new WorkerAcquisitionOptionProjection(
                AcquisitionSource.UnknownSource,
                "Figure it out",
                "No supported craft, market, or vendor source is known.",
                "-",
                IsAvailable: true,
                IsProjectedUnsupported: false,
                TotalCost: null));
        }
        return options;
    }

    private static WorkerAcquisitionOptionProjection BuildMarketOption(
        CoreDecisionRow row,
        AcquisitionCostContext costContext,
        AcquisitionSource source,
        string name,
        bool hqOnly)
    {
        costContext.TryGetShoppingPlan(row.ItemId, out var marketPlan);
        var estimate = MarketPurchaseCostProjectionService.Estimate(
            marketPlan,
            row.TotalQuantity,
            hqOnly,
            includeVendor: false);
        var hasCost = CoreAcquisitionEvaluationCostCalculator.TryGetCost(
            row,
            source,
            costContext,
            out var cost);
        var detail = estimate.IsUnsupportedProjection
            ? "Projected cost; current search scope cannot fill this purchase."
            : estimate.World is not null
                ? $"{estimate.World.WorldName} can cover {estimate.World.TotalQuantityPurchased}/{marketPlan?.QuantityNeeded}."
                : marketPlan?.RecommendedSplit?.Sum(split => split.QuantityToBuy) >= row.TotalQuantity
                    ? $"{marketPlan.RecommendedSplit.Count} world split can cover market purchase."
                    : !string.IsNullOrWhiteSpace(marketPlan?.Error)
                        ? marketPlan.Error
                        : "Run Market Analysis for actionable market evidence.";
        return new WorkerAcquisitionOptionProjection(
            source,
            name,
            detail,
            hasCost ? $"{cost:N0}g" : "-",
            hasCost && !estimate.IsUnsupportedProjection,
            estimate.IsUnsupportedProjection,
            TotalCost: hasCost ? cost : null);
    }

    private static IReadOnlyList<CommissionPayrollInputLine> ApplyOnHandReferenceValues(
        IReadOnlyList<CommissionPayrollInputLine> lines,
        IReadOnlyList<WorkerAcquisitionRowProjection> acquisitionRows)
    {
        var rows = acquisitionRows
            .GroupBy(row => (row.ItemId, row.MustBeHq))
            .ToDictionary(group => group.Key, group => group.First());

        return lines.Select(line =>
        {
            if (!string.Equals(
                    line.EvidenceSource,
                    TradeOrderWorkflow.OnHandEvidenceSource,
                    StringComparison.OrdinalIgnoreCase) ||
                line.Quantity <= 0 ||
                !rows.TryGetValue((line.ItemId, line.RequiresHq), out var row))
            {
                return line;
            }

            var reference = row.Options
                .Where(option => option.Source is not
                    (AcquisitionSource.OnHand or AcquisitionSource.UnknownSource))
                .Where(option =>
                    option.IsAvailable &&
                    !option.IsProjectedUnsupported &&
                    option.TotalCost > 0)
                .OrderBy(option => option.TotalCost)
                .FirstOrDefault();
            if (reference?.TotalCost is not > 0)
            {
                return line;
            }

            var unitCost = reference.TotalCost.Value / line.Quantity;
            return line with
            {
                UnitCost = unitCost,
                UnitCostExplanation =
                    $"Existing stock is not reimbursed. Material value uses the cheapest normal route: " +
                    $"{reference.Name} at {reference.TotalCost.Value:N0}g."
            };
        }).ToArray();
    }

    private static WorkerRecipeNodeProjection ProjectRecipeNode(
        PlanNode node,
        IReadOnlyDictionary<string, RecipeNodeDisplayState> displayStates,
        IReadOnlyDictionary<int, RecipePlanProcurementRouteSummary> routeSummaries)
    {
        var display = displayStates.TryGetValue(node.NodeId, out var projectedDisplay)
            ? projectedDisplay
            : RecipePlanTreeDisplayBuilder.BuildWithoutCost(node);
        routeSummaries.TryGetValue(node.ItemId, out var route);
        return new WorkerRecipeNodeProjection(
            node.NodeId,
            node.ItemId,
            node.Name,
            node.IconId,
            node.Quantity,
            node.Source,
            node.MustBeHq,
            node.CanBeHq,
            node.IsCircularReference,
            display,
            route,
            node.Children
                .Select(child => ProjectRecipeNode(child, displayStates, routeSummaries))
                .ToArray());
    }

    private static WorkerSessionResultEnvelope CreateSessionResult(
        string commandKind,
        bool accepted,
        string? rejectionCode,
        string? message,
        object projection,
        WorkerSessionDurablePatch? durableRepairPatch = null) =>
        new(
            WorkerSessionProtocol.ContractVersion,
            commandKind,
            _sessionRevision,
            accepted,
            rejectionCode,
            message,
            JsonSerializer.SerializeToElement(projection, WireJsonOptions),
            durableRepairPatch);

    private static WorkerSessionShellProjection CaptureShellProjection()
    {
        var session = _canonicalSession.Session;
        var plan = session.BorrowActivePlan();
        var context = session.ActiveContext;
        var evidence = session.BorrowMarketEvidence();
        var versions = session.Versions;
        return new WorkerSessionShellProjection(
            _sessionRevision,
            plan is not null || session.ProjectItems.Count > 0,
            session.Identity.SourcePlanId,
            session.Identity.SourcePlanName ?? plan?.Name ?? session.Identity.Name,
            context.DataCenter ?? plan?.DataCenter ?? "Aether",
            context.Region ?? "North America",
            session.ProjectItems.Count,
            plan?.RootItems.Count ?? 0,
            CountPlanNodes(plan),
            evidence.ItemAnalyses.Count,
            evidence.ShoppingPlans?.Count ?? 0,
            session.BorrowProcurementOverlay()?.RouteDecision is not null,
            session.PlanSessionVersion,
            new AppStateVersionSnapshot(
                versions.PlanCore,
                versions.PlanDecision,
                versions.PlanPrice,
                versions.PlanCore,
                versions.MarketAnalysis,
                versions.Procurement,
                versions.SettingsContext,
                versions.ViewState),
            _sessionRestoreWarning,
            _sessionMigratedFromLegacy,
            CaptureOperationProjection());
    }

    private static WorkerSessionOperationProjection? CaptureOperationProjection()
    {
        ExpireOperationLease();
        return _operationProjection;
    }

    private static int CountPlanNodes(CraftingPlan? plan)
    {
        if (plan is null)
        {
            return 0;
        }

        var count = 0;
        var pending = new Stack<PlanNode>(plan.RootItems);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            count++;
            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }
        return count;
    }

    private static ProjectItem CloneProjectItem(ProjectItem item) =>
        new()
        {
            Id = item.Id,
            Name = item.Name,
            IconId = item.IconId,
            Quantity = item.Quantity,
            MustBeHq = item.MustBeHq
        };

    private sealed class PendingMarketEvidencePublication
    {
        public PendingMarketEvidencePublication(
            Guid operationId,
            long baseRevision,
            MarketFetchScope scope,
            string selectedDataCenter,
            string selectedRegion,
            MarketAcquisitionLens lens,
            IReadOnlyList<string> requestedDataCenters)
        {
            OperationId = operationId;
            BaseRevision = baseRevision;
            Scope = scope;
            SelectedDataCenter = selectedDataCenter;
            SelectedRegion = selectedRegion;
            Lens = lens;
            RequestedDataCenters = requestedDataCenters;
            CreatedAtUtc = DateTime.UtcNow;
        }

        public Guid OperationId { get; }
        public long BaseRevision { get; }
        public MarketFetchScope Scope { get; }
        public string SelectedDataCenter { get; }
        public string SelectedRegion { get; }
        public MarketAcquisitionLens Lens { get; }
        public IReadOnlyList<string> RequestedDataCenters { get; }
        public DateTime CreatedAtUtc { get; }
        public List<MarketItemAnalysis> ItemAnalyses { get; } = [];
        public List<DetailedShoppingPlan> ShoppingPlans { get; } = [];
        public HashSet<int> UnavailableItemIds { get; } = [];
        public int FetchedCount { get; set; }

        public void Validate(WorkerMarketEvidencePublicationRequest request)
        {
            if (request.OperationId != OperationId ||
                request.BaseRevision != BaseRevision ||
                request.Scope != Scope ||
                request.Lens != Lens ||
                !(request.RequestedDataCenters ?? Array.Empty<string>())
                    .SequenceEqual(
                        RequestedDataCenters,
                        StringComparer.OrdinalIgnoreCase) ||
                !string.Equals(
                    request.SelectedDataCenter,
                    SelectedDataCenter,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    request.SelectedRegion,
                    SelectedRegion,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Market-evidence publication staging context changed before completion.");
            }
        }
    }

    private sealed class ActiveWorkerSessionOperation(
        Guid operationId,
        WorkerSessionOperationKind kind,
        string intentKey,
        long baseRevision,
        string statusMessage,
        DateTime lastRenewedUtc)
    {
        public Guid OperationId { get; } = operationId;
        public WorkerSessionOperationKind Kind { get; } = kind;
        public string IntentKey { get; } = intentKey;
        public long BaseRevision { get; } = baseRevision;
        public string StatusMessage { get; } = statusMessage;
        public DateTime LastRenewedUtc { get; set; } = lastRenewedUtc;
    }

    private sealed class WorkerRecipeLayerWorkflow : ICoreRecipeLayerWorkflowService
    {
        private readonly WorkerCanonicalSession _canonicalSession;
        private readonly CraftSessionState _session;
        private readonly RecipeDemandProjectionService _projection = new();

        public WorkerRecipeLayerWorkflow(WorkerCanonicalSession canonicalSession)
        {
            _canonicalSession = canonicalSession;
            _session = canonicalSession.Session;
        }

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity()
        {
            var versions = _session.CaptureVersionStamp();
            return new RecipeOperationSnapshotIdentity(
                _session.PlanSessionVersion,
                versions.PlanCore,
                versions.PlanDecision,
                versions.PlanPrice,
                versions.SettingsContext,
                "worker-canonical-plan");
        }

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan) =>
            _projection.Build(plan, snapshot: null);

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(
            CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();

        public StoredRecipeOperationSnapshot BuildMarketAnalysisRecipeBasis(
            CraftingPlan? plan,
            IReadOnlySet<int> unavailableItemIds)
        {
            var candidates = BuildMarketAnalysisCandidates(plan);
            var versions = _session.CaptureVersionStamp();
            return new StoredRecipeOperationSnapshot
            {
                Metadata = new StoredRecipeOperationMetadata
                {
                    PlanSessionVersion = _session.PlanSessionVersion,
                    PlanStructureVersion = versions.PlanCore,
                    PlanDecisionVersion = versions.PlanDecision,
                    PlanPriceVersion = versions.PlanPrice,
                    SettingsVersion = versions.SettingsContext,
                    RecipeDataIdentity = "worker-canonical-plan",
                    CompletedAtUtc = DateTime.UtcNow,
                    UniqueItemIdCount = candidates.Count
                },
                MarketAnalysisDemandItems = candidates.Select(candidate =>
                    new StoredMarketAnalysisDemandItem
                    {
                        ItemId = candidate.ItemId,
                        Name = candidate.Name,
                        IconId = candidate.IconId,
                        TotalQuantity = candidate.TotalQuantity,
                        RequiresHq = candidate.RequiresHq
                    }).ToList(),
                UnavailableMarketItemIds = unavailableItemIds.ToHashSet()
            };
        }

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(
            CraftingPlan? plan) =>
            _canonicalSession.GetActiveProcurementItems(
                () => BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates());

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<RecipeDemandProjection?>(
                BuildDemandProjection(plan));
        }

        public Task<IReadOnlyList<MaterialAggregate>?>
            BuildCurrentMarketAnalysisCandidatesAsync(
                CraftingPlan? plan,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(
                BuildMarketAnalysisCandidates(plan));
        }

        public Task<CoreMarketAnalysisCandidateBuildResult?>
            BuildCurrentMarketAnalysisCandidateResultAsync(
                CraftingPlan? plan,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CoreMarketAnalysisCandidateBuildResult?>(
                new CoreMarketAnalysisCandidateBuildResult(
                    BuildMarketAnalysisCandidates(plan),
                    RecipeBasis: null));
        }

        public Task<IReadOnlyList<MaterialAggregate>?>
            BuildCurrentActiveProcurementItemsAsync(
                CraftingPlan? plan,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(
                BuildActiveProcurementItems(plan));
        }
    }

}
