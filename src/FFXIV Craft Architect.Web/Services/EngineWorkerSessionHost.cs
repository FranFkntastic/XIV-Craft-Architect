using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;

namespace CraftArchitectEngineWorker;

public static partial class ManagedHost
{
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static readonly HttpClient SessionHttp = new();
    private static readonly RecipeCalculationService SessionRecipeCalculator =
        new(
            new GarlandService(SessionHttp),
            new WorkerVendorCache());
    private static WorkerCanonicalSession _canonicalSession = new();
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
                WorkerSessionCommandKinds.ProjectItemsMutation =>
                    MutateProjectItems(command),
                WorkerSessionCommandKinds.AcquisitionMutation =>
                    MutateAcquisition(command),
                WorkerSessionCommandKinds.RecipeBuild =>
                    await BuildRecipeAsync(command),
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

    private static WorkerRecipePlannerProjection CaptureRecipeProjection()
    {
        var session = _canonicalSession.Session;
        var plan = session.ActivePlan;
        var evidence = session.MarketEvidence;
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
                HasProcurementRoute: false);
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
        return new WorkerAcquisitionProjection(
            _sessionRevision,
            HasPlan: true,
            RootItemCount: plan.RootItems.Count,
            PricedItemCount: evidence.ShoppingPlans?.Count ?? 0,
            UnavailableItemCount: evidence.UnavailableMarketItemIds.Count,
            Rows: rows,
            MarketCandidateCount: snapshot.MarketAnalysisCandidates.Count,
            ActiveProcurementCount: snapshot.ActiveProcurementItems.Count,
            HasProcurementRoute: session.ProcurementOverlay?.RouteDecision is not null);
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
        var plan = session.ActivePlan;
        var context = session.ActiveContext;
        var evidence = session.MarketEvidence;
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

    private sealed class WorkerVendorCache : IVendorCacheService
    {
        public int Count => 0;
        public VendorCacheEntry? Get(int itemId) => null;
        public Task<VendorCacheEntry?> GetOrFetchAsync(
            int itemId,
            CancellationToken ct = default) =>
            Task.FromResult<VendorCacheEntry?>(null);
        public Task<Dictionary<int, VendorCacheEntry>> GetOrFetchBatchAsync(
            IEnumerable<int> itemIds,
            CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<int, VendorCacheEntry>());
        public void Set(int itemId, VendorCacheEntry entry)
        {
        }
        public void Clear()
        {
        }
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
