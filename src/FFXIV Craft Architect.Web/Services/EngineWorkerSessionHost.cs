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
    private static PendingMarketEvidencePublication? _pendingMarketEvidencePublication;
    private static long _sessionRevision;
    private static string? _sessionRestoreWarning;
    private static bool _sessionMigratedFromLegacy;

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
            return command.CommandKind switch
            {
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
                    CaptureMarketProjection(
                        command.Payload
                            .Deserialize<WorkerMarketProjectionRequest>(WireJsonOptions)?
                            .IncludeDetails ?? true)),
                WorkerSessionCommandKinds.ProcurementProjection => CreateSessionResult(
                    command.CommandKind,
                    accepted: true,
                    null,
                    null,
                    CaptureProcurementProjection()),
                WorkerSessionCommandKinds.ProjectItemsMutation =>
                    MutateProjectItems(command),
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
        var warning = replacement.Restore(
            restore.StoredPlan,
            restore.TrackStoredPlanIdentity);
        _canonicalSession = replacement;
        _sessionRevision = restore.Revision;
        _sessionRestoreWarning = warning;
        _sessionMigratedFromLegacy = restore.MigratedFromLegacy;
        return CreateSessionResult(
            command.CommandKind,
            accepted: true,
            null,
            warning,
            CaptureShellProjection());
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
                request.IncludeSourcePlanIdentity,
                request.IncludeLegacyMarketAnalysisFields));
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
                return CompleteMutation(command.CommandKind, CaptureRecipeProjection);
            default:
                throw new InvalidOperationException(
                    $"Unknown project-items operation '{mutation.Operation}'.");
        }

        _canonicalSession.Session.ReplaceProjectItems(items);
        _canonicalSession.InvalidateLegacyProcurementRoute();
        return CompleteMutation(command.CommandKind, CaptureRecipeProjection);
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
        return CompleteMutation(command.CommandKind, CaptureRecipeProjection);
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
                recipe));
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
        var expectedWorlds = MarketFetchScopeResolver
            .GetDataCenters(request.Scope, request.SelectedDataCenter, request.SelectedRegion)
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
                MarketAnalysisExecutionOptions.Synchronous));
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
                CaptureMarketProjection()));
    }

    private static WorkerSessionResultEnvelope PublishMarketEvidence(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerMarketEvidencePublicationRequest>(
                WireJsonOptions)
            ?? throw new InvalidOperationException("Market-evidence publication is empty.");
        if (request.ResetStaging)
        {
            _pendingMarketEvidencePublication = new PendingMarketEvidencePublication(
                request.Scope,
                request.SelectedDataCenter,
                request.SelectedRegion,
                request.Lens);
        }

        var staging = _pendingMarketEvidencePublication
            ?? throw new InvalidOperationException(
                "Market-evidence publication staging was not initialized.");
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
            staging.Scope,
            staging.SelectedDataCenter,
            staging.SelectedRegion,
            staging.Lens,
            staging.ItemAnalyses,
            staging.ShoppingPlans,
            staging.UnavailableItemIds,
            staging.FetchedCount,
            ResetStaging: true,
            CompleteStaging: true);
        _pendingMarketEvidencePublication = null;
        var session = _canonicalSession.Session;
        var plan = session.BorrowActivePlan()
            ?? throw new InvalidOperationException(
                "Build a recipe plan before publishing market evidence.");
        var planSessionVersion = session.PlanSessionVersion;
        var stamp = session.CaptureVersionStamp();
        var recipeLayer = new WorkerRecipeLayerWorkflow(session);
        var recipeBasis = recipeLayer.BuildMarketAnalysisRecipeBasis(
            plan,
            request.UnavailableItemIds);
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
                recipeBasis: recipeBasis))
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
                request.FetchedCount));
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
        var analyses = MarketEvidenceCollectionMerger.MergeAnalyses(
            evidence.ItemAnalyses,
            [request.ItemAnalysis]);
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
                evidence.RecipeBasis))
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
                CaptureMarketProjection()));
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
        return CreateMutationResult(command.CommandKind, CaptureMarketProjection());
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
            new WorkerRecipeLayerWorkflow(session),
            new CraftOperationCoordinator(session, new CraftOperationState()));
        var expectedWorlds = MarketFetchScopeResolver
            .GetDataCenters(
                request.Scope,
                request.SelectedDataCenter,
                request.SelectedRegion)
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
                ObservedEvidence: request.ObservedEvidence));
        if (result.Status != CoreProcurementItemRefreshStatus.Refreshed)
        {
            throw new InvalidOperationException(
                $"Market evidence for {request.ItemName} was not refreshed ({result.Status}).");
        }

        _canonicalSession.InvalidateLegacyProcurementRoute();
        SessionMarketCache.Clear();
        _sessionRevision++;
        return CreateMutationResult(
            command.CommandKind,
            new WorkerMarketItemRefreshOutcome(
                result.Status,
                result.ItemName,
                CaptureMarketProjection()));
    }

    private static async Task<WorkerSessionResultEnvelope> RunProcurementAsync(
        WorkerSessionCommandEnvelope command)
    {
        var request = command.Payload.Deserialize<WorkerProcurementRequest>(WireJsonOptions)
            ?? throw new InvalidOperationException("Procurement request is empty.");
        var session = _canonicalSession.Session;
        if (session.ActivePlan is null)
        {
            throw new InvalidOperationException("Build a recipe plan before generating a route.");
        }
        if (session.MarketEvidence.ShoppingPlans is not { Count: > 0 } sourcePlans)
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
        shopping.SetWorldNameToIdMapping(
            worldData.WorldIdToName.ToDictionary(pair => pair.Value, pair => pair.Key));
        var workflow = new CoreProcurementWorkflowService(
            session,
            new ProcurementRouteExecutionService(reconciliation, shopping),
            reconciliation,
            new WorkerRecipeLayerWorkflow(session),
            new CraftOperationCoordinator(session, new CraftOperationState()));
        var expectedWorlds = MarketFetchScopeResolver
            .GetDataCenters(request.Scope, request.SelectedDataCenter, request.SelectedRegion)
            .Where(dataCenter => worldData.DataCenterToWorlds.ContainsKey(dataCenter))
            .ToDictionary(
                dataCenter => dataCenter,
                dataCenter => (IReadOnlyList<string>)worldData.DataCenterToWorlds[dataCenter],
                StringComparer.OrdinalIgnoreCase);
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
                ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous));
        if (result.Status != CoreProcurementWorkflowStatus.Published)
        {
            throw new InvalidOperationException(
                $"Procurement route was not published ({result.Status}).");
        }

        SessionMarketCache.Clear();
        _canonicalSession.InvalidateLegacyProcurementRoute();
        _sessionRevision++;
        return CreateMutationResult(
            command.CommandKind,
            new WorkerProcurementOutcome(
                result.Status,
                result.ShoppingPlanCount,
                CaptureProcurementProjection()));
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
        return CompleteMutation(
            command.CommandKind,
            CaptureProcurementProjection);
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
            new WorkerRecipeLayerWorkflow(session),
            operations);
    }

    private static WorkerSessionResultEnvelope CompleteMutation(
        string commandKind,
        Func<object> capturePublicProjection)
    {
        _sessionRevision++;
        return CreateMutationResult(commandKind, capturePublicProjection());
    }

    private static WorkerSessionResultEnvelope CreateMutationResult(
        string commandKind,
        object publicProjection)
    {
        try
        {
            var durable = _canonicalSession.Export(
                    "autosave",
                    "Autosave",
                    includeSourcePlanIdentity: true,
                    includeLegacyMarketAnalysisFields: false)
                ?? new StoredPlan { Id = "autosave", Name = "Autosave" };
            var carrier = new WorkerSessionMutationProjection(
                CaptureShellProjection(),
                durable,
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
        var route = session.ProcurementOverlay;
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
        bool includeDetails = true)
    {
        var session = _canonicalSession.Session;
        var evidence = session.BorrowMarketEvidence();
        var analyses = evidence.ItemAnalyses.ToDictionary(analysis => analysis.ItemId);
        var items = (evidence.ShoppingPlans ?? Array.Empty<DetailedShoppingPlan>())
            .Select(plan =>
            {
                analyses.TryGetValue(plan.ItemId, out var analysis);
                var totalCost = plan.SplitTotalCost ??
                                plan.RecommendedWorld?.TotalCost ??
                                0;
                var worldName = plan.RequiresSplitPurchase
                    ? $"{plan.RecommendedSplit!.Count} world split"
                    : plan.RecommendedWorld?.WorldName ?? "Unavailable";
                var worlds = plan.WorldOptions
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
                    .ToArray();
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
                    worlds.Length,
                    analysis?.WorstDataQualityBucket ?? MarketDataQualityBucket.Missing,
                    plan.Error ?? plan.MarketDataWarning ?? analysis?.Warning,
                    worlds);
            })
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var context = session.ActiveContext;
        var candidateItems = new WorkerRecipeLayerWorkflow(session)
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
                ? evidence.ShoppingPlans?.ToArray() ?? Array.Empty<DetailedShoppingPlan>()
                : Array.Empty<DetailedShoppingPlan>(),
            includeDetails
                ? evidence.ItemAnalyses.ToArray()
                : Array.Empty<MarketItemAnalysis>());
    }

    private static WorkerProcurementProjection CaptureProcurementProjection()
    {
        var session = _canonicalSession.Session;
        var overlay = session.ProcurementOverlay;
        var decision = overlay?.RouteDecision;
        var activeItems = new WorkerRecipeLayerWorkflow(session)
            .BuildActiveProcurementItems(session.ActivePlan);
        var worlds = (overlay?.RouteCards ?? Array.Empty<WorldProcurementCardModel>())
            .Select(world => new WorkerProcurementWorldProjection(
                world.DataCenter,
                world.WorldName,
                world.IsVendor,
                world.TotalCost,
                world.ItemCount,
                world.TotalQuantity,
                world.Items.Select(item => new WorkerProcurementItemProjection(
                    item.ItemId,
                    item.ItemName,
                    item.IconId,
                    item.QuantityOnThisWorld,
                    item.TotalQuantityNeeded,
                    item.PricePerUnit,
                    item.TotalCost,
                    item.IsSplitPurchase)).ToArray()))
            .OrderBy(world => world.IsVendor ? 1 : 0)
            .ThenBy(world => world.DataCenter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(world => world.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fixedCost = decision?.FixedAcquisitionGilCost ?? 0;
        var tolerance = decision?.TravelTolerance ?? 0;
        return new WorkerProcurementProjection(
            _sessionRevision,
            session.ActivePlan is not null,
            session.MarketEvidence.ShoppingPlans is { Count: > 0 },
            decision is not null,
            activeItems.Count,
            tolerance,
            MarketRouteScoring.GetToleranceLabel(tolerance),
            decision?.TravelPriority ?? MarketTravelPriority.DataCenterTransfersFirst,
            session.ActiveContext.MarketFetchScope == MarketFetchScope.EntireRegion,
            overlay?.ShoppingPlans?.Any(plan => plan.RequiresSplitPurchase) == true,
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
            overlay?.ShoppingPlans?.ToArray() ?? Array.Empty<DetailedShoppingPlan>(),
            decision);
    }

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
            HasPlan: true,
            RootItemCount: plan.RootItems.Count,
            PricedItemCount: evidence.ShoppingPlans?.Count ?? 0,
            UnavailableItemCount: evidence.UnavailableMarketItemIds.Count,
            Rows: rows,
            MarketCandidateCount: snapshot.MarketAnalysisCandidates.Count,
            ActiveProcurementCount: snapshot.ActiveProcurementItems.Count,
            HasProcurementRoute: session.ProcurementOverlay?.RouteDecision is not null,
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
                IsProjectedUnsupported: false));
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
                IsProjectedUnsupported: false));
        }
        if (!row.CanBuyFromMarket && !row.CanBuyFromVendor && !row.HasChildren)
        {
            options.Add(new WorkerAcquisitionOptionProjection(
                AcquisitionSource.UnknownSource,
                "Figure it out",
                "No supported craft, market, or vendor source is known.",
                "-",
                IsAvailable: true,
                IsProjectedUnsupported: false));
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
            estimate.IsUnsupportedProjection);
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
        object projection) =>
        new(
            WorkerSessionProtocol.ContractVersion,
            commandKind,
            _sessionRevision,
            accepted,
            rejectionCode,
            message,
            JsonSerializer.SerializeToElement(projection, WireJsonOptions));

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
            session.ProcurementOverlay?.RouteDecision is not null,
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
            _sessionMigratedFromLegacy);
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
            MarketFetchScope scope,
            string selectedDataCenter,
            string selectedRegion,
            MarketAcquisitionLens lens)
        {
            Scope = scope;
            SelectedDataCenter = selectedDataCenter;
            SelectedRegion = selectedRegion;
            Lens = lens;
        }

        public MarketFetchScope Scope { get; }
        public string SelectedDataCenter { get; }
        public string SelectedRegion { get; }
        public MarketAcquisitionLens Lens { get; }
        public List<MarketItemAnalysis> ItemAnalyses { get; } = [];
        public List<DetailedShoppingPlan> ShoppingPlans { get; } = [];
        public HashSet<int> UnavailableItemIds { get; } = [];
        public int FetchedCount { get; set; }

        public void Validate(WorkerMarketEvidencePublicationRequest request)
        {
            if (request.Scope != Scope ||
                request.Lens != Lens ||
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

    private sealed class WorkerRecipeLayerWorkflow : ICoreRecipeLayerWorkflowService
    {
        private readonly CraftSessionState _session;
        private readonly RecipeDemandProjectionService _projection = new();

        public WorkerRecipeLayerWorkflow(CraftSessionState session)
        {
            _session = session;
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
            BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates();

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
