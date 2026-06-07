using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Builds a fresh plan. Plan construction always refreshes vendor and market prices for the new plan.
/// </summary>
public sealed record CoreBuildRecipePlanRequest(
    IReadOnlyList<ProjectItem> ProjectItems,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketFetchScope PriceFetchScope);

public sealed record CoreBuildRecipePlanResult(
    bool Built,
    PlanNode? SelectedNode,
    CoreMarketPriceRefreshResult PriceRefresh,
    int ChangedDefaultDecisions,
    string Message,
    CoreRecipePlannerCommandMessageLevel MessageLevel);

public sealed record CoreImportProjectItemsRequest(
    IReadOnlyList<ProjectItem> ProjectItems,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketFetchScope PriceFetchScope);

public sealed record CoreImportProjectItemsResult(
    CoreBuildRecipePlanResult BuildResult,
    string Message,
    CoreRecipePlannerCommandMessageLevel MessageLevel);

public sealed record CoreRefreshRecipePlanPricesRequest(
    MarketFetchScope PriceFetchScope,
    string SelectedDataCenter,
    string SelectedRegion,
    bool ForceRefreshData = false);

public sealed record CoreApplyPlanEditorEditRequest(
    IReadOnlyCollection<string> NodeIds,
    PlanBulkEditOptions Options,
    bool RequireHqMaterials);

public sealed record CoreApplyPlanEditorEditResult(
    PlanBulkEditResult EditResult,
    string Message);

public sealed record CoreActivateRecipePlanRequest(
    CraftingPlan Plan,
    bool ClearCurrentPlanId,
    bool RefreshVendorPricesOnActivation,
    bool RefreshMarketPricesOnActivation,
    MarketFetchScope PriceFetchScope,
    string SelectedDataCenter,
    string SelectedRegion);

public sealed record CoreActivateRecipePlanResult(
    PlanNode? SelectedNode,
    CoreMarketPriceRefreshResult PriceRefresh,
    string Message,
    CoreRecipePlannerCommandMessageLevel MessageLevel);

public enum CoreRecipePlannerCommandMessageLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed class CoreRecipePlannerCommandService
{
    private readonly CraftSessionState _session;
    private readonly ICoreRecipePlanBuilder _recipePlanBuilder;
    private readonly IMarketCacheService _marketCache;
    private readonly ICoreRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly ICraftOperationCoordinator _operationCoordinator;

    public CoreRecipePlannerCommandService(
        CraftSessionState session,
        ICoreRecipePlanBuilder recipePlanBuilder,
        IMarketCacheService marketCache,
        ICoreRecipeLayerWorkflowService recipeLayerWorkflow,
        ICraftOperationCoordinator operationCoordinator)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _recipePlanBuilder = recipePlanBuilder ?? throw new ArgumentNullException(nameof(recipePlanBuilder));
        _marketCache = marketCache ?? throw new ArgumentNullException(nameof(marketCache));
        _recipeLayerWorkflow = recipeLayerWorkflow ?? throw new ArgumentNullException(nameof(recipeLayerWorkflow));
        _operationCoordinator = operationCoordinator ?? throw new ArgumentNullException(nameof(operationCoordinator));
    }

    public async Task<CoreBuildRecipePlanResult> BuildPlanAsync(
        CoreBuildRecipePlanRequest request,
        CancellationToken ct = default)
    {
        if (request.ProjectItems.Count == 0)
        {
            return new CoreBuildRecipePlanResult(
                false,
                null,
                CoreMarketPriceAvailability.Empty,
                0,
                "No project items to build.",
                CoreRecipePlannerCommandMessageLevel.Info);
        }

        using var operation = _operationCoordinator.Start(
            CraftOperationWorkflow.RecipeBuild,
            "Fetching Prices",
            "Loading market prices for all materials...");
        var targetItems = request.ProjectItems
            .Select(item => (item.Id, item.Name, item.Quantity, item.MustBeHq))
            .ToList();
        CraftingPlan? publishedPlan = null;
        PlanNode? selectedNode = null;
        long? publishedPlanSessionVersion = null;

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Token);
            var builtPlan = await _recipePlanBuilder.BuildPlanAsync(
                targetItems,
                request.SelectedDataCenter,
                string.Empty,
                linkedCancellation.Token);
            if (!operation.IsCurrent)
            {
                return CanceledBuildResult();
            }

            if (!operation.CompleteIfCurrent(
                () => _session.ActivatePlan(
                    builtPlan,
                    CloneProjectItems(request.ProjectItems),
                    new CraftSessionActiveContext(
                        request.SelectedRegion,
                        request.SelectedDataCenter,
                        string.Empty,
                        request.PriceFetchScope),
                    "recipe plan built"),
                "Plan built."))
            {
                return CanceledBuildResult();
            }

            publishedPlan = builtPlan;
            publishedPlanSessionVersion = _session.PlanSessionVersion;
            selectedNode = builtPlan.RootItems.FirstOrDefault();

            var priceRefresh = await RefreshPricesWithOperationAsync(
                builtPlan,
                publishedPlanSessionVersion.Value,
                request.PriceFetchScope,
                request.SelectedDataCenter,
                request.SelectedRegion,
                forceRefreshData: false,
                linkedCancellation.Token);
            if (!_session.IsCurrentPlanSession(builtPlan, publishedPlanSessionVersion.Value))
            {
                return CanceledBuildResult();
            }

            var changedDefaults = AcquisitionPlanningService.ApplyCheapestAcquisitionDefaults(
                builtPlan,
                Array.Empty<DetailedShoppingPlan>());
            if (changedDefaults > 0)
            {
                var defaultsApplied = false;
                using var defaultsOperation = _operationCoordinator.Start(
                    CraftOperationWorkflow.RecipeBuild,
                    "Reconciling Plan",
                    "Reconciling acquisition defaults...");
                var defaultsCompleted = defaultsOperation.CompleteIfCurrent(
                    () =>
                    {
                        var decisionStamp = _session.CaptureVersionStamp();
                        defaultsApplied = _session.TryReplaceActivePlanDecisions(
                            decisionStamp,
                            builtPlan,
                            publishedPlanSessionVersion.Value,
                            "recipe plan defaults reconciled");
                    },
                    "Plan defaults reconciled.");
                if (!defaultsCompleted || !defaultsApplied)
                {
                    return CanceledBuildResult();
                }
            }

            if (priceRefresh.HasUnavailableItems)
            {
                var warning = CoreMarketPriceAvailability.FormatUnavailableMessage(priceRefresh.UnavailableItems);
                return new CoreBuildRecipePlanResult(
                    true,
                    selectedNode,
                    priceRefresh,
                    changedDefaults,
                    $"Plan built with partial market pricing. {warning}",
                    CoreRecipePlannerCommandMessageLevel.Warning);
            }

            return new CoreBuildRecipePlanResult(
                true,
                selectedNode,
                priceRefresh,
                changedDefaults,
                "Plan built! Prices fetched. Go to Procurement Planner to run market analysis.",
                CoreRecipePlannerCommandMessageLevel.Success);
        }
        catch (OperationCanceledException) when (!operation.IsCurrent || operation.Token.IsCancellationRequested || ct.IsCancellationRequested)
        {
            operation.Cancel();
            if (publishedPlan != null &&
                publishedPlanSessionVersion.HasValue &&
                _session.IsCurrentPlanSession(publishedPlan, publishedPlanSessionVersion.Value))
            {
                return new CoreBuildRecipePlanResult(
                    true,
                    selectedNode,
                    CoreMarketPriceAvailability.Empty,
                    0,
                    "Plan built; price refresh canceled.",
                    CoreRecipePlannerCommandMessageLevel.Info);
            }

            return CanceledBuildResult();
        }
        catch (Exception ex)
        {
            if (!operation.CompleteStatusIfCurrent($"Failed: {ex.Message}"))
            {
                operation.Cancel();
            }

            return new CoreBuildRecipePlanResult(
                false,
                _session.ActivePlan?.RootItems.FirstOrDefault(),
                CoreMarketPriceAvailability.Empty,
                0,
                $"Failed to build plan: {ex.Message}",
                CoreRecipePlannerCommandMessageLevel.Error);
        }

        CoreBuildRecipePlanResult CanceledBuildResult()
        {
            operation.Cancel();
            return new CoreBuildRecipePlanResult(
                false,
                _session.ActivePlan?.RootItems.FirstOrDefault(),
                CoreMarketPriceAvailability.Empty,
                0,
                "Plan build canceled.",
                CoreRecipePlannerCommandMessageLevel.Info);
        }
    }

    public async Task<CoreImportProjectItemsResult> ImportProjectItemsAsync(
        CoreImportProjectItemsRequest request,
        CancellationToken ct = default)
    {
        var importedItems = CloneProjectItems(request.ProjectItems);
        using var importOperation = _operationCoordinator.Start(
            CraftOperationWorkflow.RecipeBuild,
            "Importing Items",
            "Importing project items...");
        if (!importOperation.CompleteIfCurrent(
            () => _session.ActivatePlan(
                null,
                importedItems,
                new CraftSessionActiveContext(
                    request.SelectedRegion,
                    request.SelectedDataCenter,
                    string.Empty,
                    request.PriceFetchScope),
                "project items imported"),
            "Project items imported."))
        {
            var canceledResult = new CoreBuildRecipePlanResult(
                false,
                null,
                CoreMarketPriceAvailability.Empty,
                0,
                "Project item import canceled.",
                CoreRecipePlannerCommandMessageLevel.Info);
            return new CoreImportProjectItemsResult(
                canceledResult,
                "Project item import canceled.",
                CoreRecipePlannerCommandMessageLevel.Info);
        }

        if (importedItems.Count == 0)
        {
            var emptyResult = new CoreBuildRecipePlanResult(
                false,
                null,
                CoreMarketPriceAvailability.Empty,
                0,
                "Imported 0 items.",
                CoreRecipePlannerCommandMessageLevel.Info);
            return new CoreImportProjectItemsResult(
                emptyResult,
                "Imported 0 items.",
                CoreRecipePlannerCommandMessageLevel.Info);
        }

        var buildResult = await BuildPlanAsync(
            new CoreBuildRecipePlanRequest(
                importedItems,
                request.SelectedDataCenter,
                request.SelectedRegion,
                request.PriceFetchScope),
            ct);

        return new CoreImportProjectItemsResult(
            buildResult,
            buildResult.Built
                ? $"Imported {importedItems.Count} items and rebuilt the plan."
                : buildResult.Message,
            buildResult.MessageLevel);
    }

    public async Task<CoreMarketPriceRefreshResult> RefreshPricesAsync(
        CoreRefreshRecipePlanPricesRequest request,
        CancellationToken ct = default)
    {
        var plan = _session.ActivePlan;
        var planSessionVersion = _session.PlanSessionVersion;
        if (plan == null)
        {
            return CoreMarketPriceAvailability.NoPlan;
        }

        var selectedDataCenter = !string.IsNullOrWhiteSpace(plan.DataCenter)
            ? plan.DataCenter
            : request.SelectedDataCenter;
        var selectedRegion = MarketFetchScopeResolver.ResolveRegionForDataCenter(
            selectedDataCenter,
            request.SelectedRegion);

        return await RefreshPricesWithOperationAsync(
            plan,
            planSessionVersion,
            request.PriceFetchScope,
            selectedDataCenter,
            selectedRegion,
            request.ForceRefreshData,
            ct);
    }

    public async Task<CoreActivateRecipePlanResult> ActivatePlanAsync(
        CoreActivateRecipePlanRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request.Plan);

        var selectedNode = request.Plan.RootItems.FirstOrDefault();
        var selectedDataCenter = !string.IsNullOrWhiteSpace(request.Plan.DataCenter)
            ? request.Plan.DataCenter
            : request.SelectedDataCenter;
        var selectedRegion = MarketFetchScopeResolver.ResolveRegionForDataCenter(
            selectedDataCenter,
            request.SelectedRegion);
        using var activationOperation = _operationCoordinator.Start(
            CraftOperationWorkflow.PlanActivation,
            "Loading Plan",
            "Loading plan...");
        if (!activationOperation.CompleteIfCurrent(
            () => _session.ActivatePlan(
                request.Plan,
                CraftPlanStateMapper.GetRootProjectItems(request.Plan),
                new CraftSessionActiveContext(
                    selectedRegion,
                    selectedDataCenter,
                    request.Plan.World,
                    request.PriceFetchScope),
                "recipe plan activated",
                request.ClearCurrentPlanId
                    ? CraftSessionIdentity.CreateNew(request.Plan.Name ?? "New Plan")
                    : null),
            "Plan loaded."))
        {
            return new CoreActivateRecipePlanResult(
                selectedNode,
                CoreMarketPriceAvailability.Empty,
                "Plan load canceled.",
                CoreRecipePlannerCommandMessageLevel.Info);
        }

        var activatedPlanSessionVersion = _session.PlanSessionVersion;

        if (!request.RefreshVendorPricesOnActivation && !request.RefreshMarketPricesOnActivation)
        {
            RestoreSavedMarketPlans(request.Plan, activatedPlanSessionVersion);
            return new CoreActivateRecipePlanResult(
                selectedNode,
                CoreMarketPriceAvailability.Empty,
                "Plan loaded.",
                CoreRecipePlannerCommandMessageLevel.Success);
        }

        using var operation = _operationCoordinator.Start(
            CraftOperationWorkflow.PlanActivation,
            "Rebuilding Plan",
            "Rebuilding plan...");
        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Token);
            CoreMarketPriceRefreshResult priceRefresh;
            if (request.RefreshMarketPricesOnActivation)
            {
                priceRefresh = await RefreshPricesForPlanAsync(
                    request.Plan,
                    activatedPlanSessionVersion,
                    request.PriceFetchScope,
                    selectedDataCenter,
                    selectedRegion,
                    forceRefreshData: false,
                    linkedCancellation.Token);
                if (!_session.IsCurrentPlanSession(request.Plan, activatedPlanSessionVersion))
                {
                    return CanceledActivationResult();
                }

                operation.RefreshSessionStamp();
            }
            else
            {
                await _recipePlanBuilder.FetchVendorPricesAsync(request.Plan, linkedCancellation.Token);
                if (!_session.IsCurrentPlanSession(request.Plan, activatedPlanSessionVersion))
                {
                    return CanceledActivationResult();
                }

                request.Plan.PriceVersion++;
                var stamp = _session.CaptureVersionStamp();
                if (!_session.TryReplaceActivePlanPrices(
                    stamp,
                    request.Plan,
                    activatedPlanSessionVersion,
                    Array.Empty<int>(),
                    "recipe plan vendor prices refreshed"))
                {
                    return CanceledActivationResult();
                }

                priceRefresh = CoreMarketPriceAvailability.Empty;
                operation.RefreshSessionStamp();
            }

            if (!request.RefreshMarketPricesOnActivation)
            {
                RestoreSavedMarketPlans(request.Plan, activatedPlanSessionVersion);
                operation.RefreshSessionStamp();
            }

            operation.CompleteStatusIfCurrent("Plan loaded.");
            return new CoreActivateRecipePlanResult(
                selectedNode,
                priceRefresh,
                "Plan loaded.",
                CoreRecipePlannerCommandMessageLevel.Success);
        }
        catch (OperationCanceledException) when (!operation.IsCurrent || operation.Token.IsCancellationRequested || ct.IsCancellationRequested)
        {
            operation.Cancel();
            return CanceledActivationResult();
        }
        catch (Exception ex)
        {
            if (!operation.CompleteStatusIfCurrent("Plan loaded."))
            {
                operation.Cancel();
            }

            return new CoreActivateRecipePlanResult(
                selectedNode,
                CoreMarketPriceAvailability.Empty,
                $"Plan loaded, but price refresh failed: {ex.Message}",
                CoreRecipePlannerCommandMessageLevel.Warning);
        }

        CoreActivateRecipePlanResult CanceledActivationResult()
        {
            operation.Cancel();
            return new CoreActivateRecipePlanResult(
                selectedNode,
                CoreMarketPriceAvailability.Empty,
                "Plan loaded; price refresh canceled.",
                CoreRecipePlannerCommandMessageLevel.Info);
        }
    }

    public CoreApplyPlanEditorEditResult ApplyPlanEditorEdit(CoreApplyPlanEditorEditRequest request)
    {
        using var operation = _operationCoordinator.Start(
            CraftOperationWorkflow.PlanEdit,
            "Editing Plan",
            "Applying plan edits...");
        try
        {
            var currentPlan = _session.ActivePlan;
            if (currentPlan == null)
            {
                operation.Cancel();
                return new CoreApplyPlanEditorEditResult(
                    new PlanBulkEditResult(),
                    "No active plan.");
            }

            var selectedNodeIds = request.NodeIds.ToHashSet(StringComparer.Ordinal);
            var selectedNodes = PlanBulkEditService
                .FlattenPlanNodes(currentPlan)
                .Where(row => selectedNodeIds.Contains(row.Node.NodeId))
                .Select(row => row.Node)
                .ToList();

            var editResult = PlanBulkEditService.ApplyBulkEdit(selectedNodes, request.Options);
            if (request.RequireHqMaterials)
            {
                foreach (var node in selectedNodes.Where(node => node.Children.Any()))
                {
                    var materialResult = PlanBulkEditService.RequireHqMaterials(node, includeNested: true);
                    editResult.ChangedNodes += materialResult.ChangedNodes;
                    editResult.SkippedNodes += materialResult.SkippedNodes;
                    editResult.SwitchedMarketBuys += materialResult.SwitchedMarketBuys;
                }
            }

            AcquisitionPlanningService.ReconcileAcquisitionDecisions(currentPlan, Array.Empty<DetailedShoppingPlan>());
            var planSessionVersion = _session.PlanSessionVersion;
            var editPublished = false;
            var editCompleted = operation.CompleteIfCurrent(
                () =>
                {
                    var stamp = _session.CaptureVersionStamp();
                    editPublished = _session.TryReplaceActivePlanDecisions(
                        stamp,
                        currentPlan,
                        planSessionVersion,
                        "plan editor edit applied");
                },
                "Plan edit applied.");
            if (!editCompleted || !editPublished)
            {
                operation.Cancel();
                return new CoreApplyPlanEditorEditResult(
                    editResult,
                    "Plan edit canceled.");
            }

            return new CoreApplyPlanEditorEditResult(
                editResult,
                $"Updated {editResult.ChangedNodes} node{(editResult.ChangedNodes == 1 ? "" : "s")}; skipped {editResult.SkippedNodes}.");
        }
        catch (Exception ex)
        {
            if (!operation.CompleteStatusIfCurrent($"Failed: {ex.Message}"))
            {
                operation.Cancel();
            }

            return new CoreApplyPlanEditorEditResult(
                new PlanBulkEditResult(),
                $"Failed to edit plan: {ex.Message}");
        }
    }

    private async Task<CoreMarketPriceRefreshResult> RefreshPricesForPlanAsync(
        CraftingPlan plan,
        long planSessionVersion,
        MarketFetchScope priceFetchScope,
        string selectedDataCenter,
        string selectedRegion,
        bool forceRefreshData,
        CancellationToken ct)
    {
        await _recipePlanBuilder.FetchVendorPricesAsync(plan, ct);
        ct.ThrowIfCancellationRequested();
        if (!_session.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return CoreMarketPriceAvailability.StalePlan;
        }

        var allItemIds = plan.GetAllItemIds();
        var marketItemIds = CoreMarketPriceAvailability.GetMarketListableItemIds(plan, allItemIds);
        var nonMarketItemIds = CoreMarketPriceAvailability.GetNonMarketListableItemIds(plan, allItemIds);
        var marketEntries = await MarketScopedPriceLoader.LoadBestEntriesAsync(
            _marketCache,
            marketItemIds,
            priceFetchScope,
            selectedDataCenter,
            selectedRegion,
            forceRefreshData: forceRefreshData,
            ct: ct);
        ct.ThrowIfCancellationRequested();
        if (!_session.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return CoreMarketPriceAvailability.StalePlan;
        }

        var refreshResult = CoreMarketPriceAvailability.FromCachedMarketData(
            plan,
            marketItemIds,
            marketEntries,
            nonMarketItemIds);
        foreach (var root in plan.RootItems)
        {
            UpdateNodePrices(root, marketEntries);
        }

        if (!_session.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return CoreMarketPriceAvailability.StalePlan;
        }

        plan.PriceVersion++;
        var stamp = _session.CaptureVersionStamp();
        return _session.TryReplaceActivePlanPrices(
            stamp,
            plan,
            planSessionVersion,
            refreshResult.UnavailableItems.Select(item => item.ItemId),
            "recipe plan prices refreshed")
            ? refreshResult
            : CoreMarketPriceAvailability.StalePlan;
    }

    private bool RestoreSavedMarketPlans(CraftingPlan plan, long planSessionVersion)
    {
        if (plan.SavedMarketPlans.Count == 0)
        {
            return false;
        }

        return _session.TryPublishMarketAnalysis(
            _session.CaptureVersionStamp(),
            plan,
            planSessionVersion,
            Array.Empty<MarketItemAnalysis>(),
            plan.SavedMarketPlans,
            acquisitionDecisionsChanged: false,
            "saved market shopping plans restored");
    }

    private async Task<CoreMarketPriceRefreshResult> RefreshPricesWithOperationAsync(
        CraftingPlan plan,
        long planSessionVersion,
        MarketFetchScope priceFetchScope,
        string selectedDataCenter,
        string selectedRegion,
        bool forceRefreshData,
        CancellationToken ct)
    {
        using var operation = _operationCoordinator.Start(
            CraftOperationWorkflow.PriceRefresh,
            "Fetching Prices",
            "Loading market prices for all materials...");

        try
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Token);
            var result = await RefreshPricesForPlanAsync(
                plan,
                planSessionVersion,
                priceFetchScope,
                selectedDataCenter,
                selectedRegion,
                forceRefreshData,
                linkedCancellation.Token);

            if (!_session.IsCurrentPlanSession(plan, planSessionVersion))
            {
                operation.Cancel();
                return CoreMarketPriceAvailability.StalePlan;
            }

            operation.RefreshSessionStamp();
            operation.CompleteStatusIfCurrent("Prices refreshed.");
            return result;
        }
        catch (OperationCanceledException) when (!operation.IsCurrent || operation.Token.IsCancellationRequested || ct.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            if (!operation.CompleteStatusIfCurrent($"Failed: {ex.Message}"))
            {
                operation.Cancel();
            }

            throw;
        }
    }

    private static void UpdateNodePrices(PlanNode node, IReadOnlyDictionary<int, CachedMarketData> marketEntries)
    {
        if (marketEntries.TryGetValue(node.ItemId, out var entry))
        {
            node.MarketPrice = MarketScopedPriceLoader.GetComparableAveragePrice(entry);
            var hqListings = entry.Worlds.SelectMany(world => world.Listings).Where(listing => listing.IsHq).ToList();
            if (hqListings.Any())
            {
                node.HqMarketPrice = (decimal)hqListings.Average(listing => listing.PricePerUnit);
            }
        }

        foreach (var child in node.Children)
        {
            UpdateNodePrices(child, marketEntries);
        }
    }

    private static IReadOnlyList<ProjectItem> CloneProjectItems(IEnumerable<ProjectItem> projectItems) =>
        projectItems
            .Select(item => new ProjectItem
            {
                Id = item.Id,
                Name = item.Name,
                IconId = item.IconId,
                Quantity = item.Quantity,
                MustBeHq = item.MustBeHq
            })
            .ToArray();
}
