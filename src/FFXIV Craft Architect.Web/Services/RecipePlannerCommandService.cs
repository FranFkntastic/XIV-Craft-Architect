using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public interface IRecipePlanBuilder
{
    Task<CraftingPlan> BuildPlanAsync(
        List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default);

    Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default);
}

public sealed class RecipeCalculationPlanBuilder : IRecipePlanBuilder
{
    private readonly RecipeCalculationService _recipeCalculationService;

    public RecipeCalculationPlanBuilder(RecipeCalculationService recipeCalculationService)
    {
        _recipeCalculationService = recipeCalculationService;
    }

    public Task<CraftingPlan> BuildPlanAsync(
        List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default)
    {
        return _recipeCalculationService.BuildPlanAsync(targetItems, dataCenter, world, ct);
    }

    public Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default)
    {
        return _recipeCalculationService.FetchVendorPricesAsync(plan, ct);
    }
}

public sealed record BuildRecipePlanRequest(
    IReadOnlyList<ProjectItem> ProjectItems,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketFetchScope PriceFetchScope,
    bool RefreshPrices = true);

public sealed record BuildRecipePlanResult(
    bool Built,
    PlanNode? SelectedNode,
    MarketPriceRefreshResult PriceRefresh,
    int ChangedDefaultDecisions,
    string Message,
    RecipePlannerCommandMessageLevel MessageLevel);

public sealed record ImportProjectItemsRequest(
    IReadOnlyList<ProjectItem> ProjectItems,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketFetchScope PriceFetchScope);

public sealed record ImportProjectItemsResult(
    BuildRecipePlanResult BuildResult,
    string Message,
    RecipePlannerCommandMessageLevel MessageLevel);

public sealed record RefreshRecipePlanPricesRequest(
    MarketFetchScope PriceFetchScope,
    string SelectedDataCenter,
    string SelectedRegion);

public sealed record ApplyPlanEditorEditRequest(
    IReadOnlyCollection<string> NodeIds,
    PlanBulkEditOptions Options,
    bool RequireHqMaterials);

public sealed record ApplyPlanEditorEditResult(
    PlanBulkEditResult EditResult,
    string Message);

public sealed record ActivateRecipePlanRequest(
    CraftingPlan Plan,
    bool ClearCurrentPlanId,
    bool RefreshVendorPrices,
    bool RefreshMarketPrices,
    MarketFetchScope PriceFetchScope,
    string SelectedDataCenter,
    string SelectedRegion);

public sealed record ActivateRecipePlanResult(
    PlanNode? SelectedNode,
    MarketPriceRefreshResult PriceRefresh,
    string Message,
    RecipePlannerCommandMessageLevel MessageLevel);

public enum RecipePlannerCommandMessageLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed class RecipePlannerCommandService
{
    private readonly AppState _appState;
    private readonly IRecipePlanBuilder _recipePlanBuilder;
    private readonly IMarketCacheService _marketCache;
    private readonly CancellableOperationService _cancellableOperations;

    public RecipePlannerCommandService(
        AppState appState,
        IRecipePlanBuilder recipePlanBuilder,
        IMarketCacheService marketCache,
        CancellableOperationService cancellableOperations)
    {
        _appState = appState;
        _recipePlanBuilder = recipePlanBuilder;
        _marketCache = marketCache;
        _cancellableOperations = cancellableOperations;
    }

    public async Task<BuildRecipePlanResult> BuildPlanAsync(
        BuildRecipePlanRequest request,
        CancellationToken ct = default)
    {
        if (request.ProjectItems.Count == 0)
        {
            return new BuildRecipePlanResult(
                false,
                null,
                new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>()),
                0,
                "No project items to build.",
                RecipePlannerCommandMessageLevel.Info);
        }

        using var operation = _cancellableOperations.Start(
            CancellableOperationWorkflow.RecipeBuild,
            "Fetching Prices",
            "Loading market prices for all materials...",
            ct);
        var targetItems = request.ProjectItems
            .Select(item => (item.Id, item.Name, item.Quantity, item.MustBeHq))
            .ToList();
        CraftingPlan? publishedPlan = null;
        PlanNode? selectedNode = null;
        long? publishedPlanSessionVersion = null;

        try
        {
            var builtPlan = await _recipePlanBuilder.BuildPlanAsync(
                targetItems,
                request.SelectedDataCenter,
                string.Empty,
                operation.Token);
            if (!operation.IsCurrent)
            {
                return CanceledBuildResult();
            }

            _cancellableOperations.CancelPlanDependentOperations();
            _appState.ApplyBuiltRecipePlan(builtPlan);
            var builtPlanSessionVersion = _appState.PlanSessionVersion;
            publishedPlanSessionVersion = builtPlanSessionVersion;
            publishedPlan = builtPlan;
            selectedNode = builtPlan.RootItems.FirstOrDefault();
            var priceRefresh = new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>());
            if (request.RefreshPrices)
            {
                priceRefresh = await RefreshPricesAsync(
                    new RefreshRecipePlanPricesRequest(
                        request.PriceFetchScope,
                        request.SelectedDataCenter,
                        request.SelectedRegion),
                    operation.Token);
                if (!operation.IsCurrent)
                {
                    return CanceledBuildResult();
                }
            }

            if (!_appState.IsCurrentPlanSession(builtPlan, builtPlanSessionVersion))
            {
                return CanceledBuildResult();
            }

            var changedDefaults = AcquisitionPlanningService.ApplyCheapestAcquisitionDefaults(
                builtPlan,
                Array.Empty<DetailedShoppingPlan>());

            using (_appState.BeginStateChangeBatch())
            {
                if (changedDefaults > 0)
                {
                    _appState.NotifyPlanDecisionChanged();
                }
                else
                {
                    _appState.NotifyPlanChanged();
                }

                _appState.ReplaceShoppingItemsFromActivePlan();
            }

            if (priceRefresh.HasUnavailableItems)
            {
                var warning = MarketPriceAvailability.FormatUnavailableMessage(priceRefresh.UnavailableItems);
                operation.Complete($"Plan built with partial market pricing. {warning}");
                return new BuildRecipePlanResult(
                    true,
                    selectedNode,
                    priceRefresh,
                    changedDefaults,
                    $"Plan built with partial market pricing. {warning}",
                    RecipePlannerCommandMessageLevel.Warning);
            }

            var message = $"Plan built with {builtPlan.RootItems.Count} items. Go to Procurement Planner to analyze.";
            operation.Complete(message);
            return new BuildRecipePlanResult(
                true,
                selectedNode,
                priceRefresh,
                changedDefaults,
                request.RefreshPrices
                    ? "Plan built! Prices fetched. Go to Procurement Planner to run market analysis."
                    : "Plan built. Go to Procurement Planner to run market analysis.",
                RecipePlannerCommandMessageLevel.Success);
        }
        catch (OperationCanceledException ex) when (!operation.ShouldReportError(ex))
        {
            operation.Cancel();
            if (publishedPlan != null &&
                publishedPlanSessionVersion.HasValue &&
                _appState.IsCurrentPlanSession(publishedPlan, publishedPlanSessionVersion.Value))
            {
                return new BuildRecipePlanResult(
                    true,
                    selectedNode,
                    new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>()),
                    0,
                    "Plan built; price refresh canceled.",
                    RecipePlannerCommandMessageLevel.Info);
            }

            return CanceledBuildResult();
        }
        catch (Exception ex)
        {
            operation.Complete($"Failed: {ex.Message}");
            return new BuildRecipePlanResult(
                false,
                _appState.CurrentPlan?.RootItems.FirstOrDefault(),
                new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>()),
                0,
                $"Failed to build plan: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error);
        }

        BuildRecipePlanResult CanceledBuildResult()
        {
            return new BuildRecipePlanResult(
                false,
                _appState.CurrentPlan?.RootItems.FirstOrDefault(),
                new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>()),
                0,
                "Plan build canceled.",
                RecipePlannerCommandMessageLevel.Info);
        }
    }

    public async Task<ImportProjectItemsResult> ImportProjectItemsAsync(
        ImportProjectItemsRequest request,
        CancellationToken ct = default)
    {
        _appState.ApplyImportedProjectItems(request.ProjectItems.Select(item => new ProjectItem
        {
            Id = item.Id,
            Name = item.Name,
            IconId = item.IconId,
            Quantity = item.Quantity,
            MustBeHq = item.MustBeHq
        }));

        if (!_appState.HasProjectItems)
        {
            var emptyResult = new BuildRecipePlanResult(
                false,
                null,
                new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>()),
                0,
                "Imported 0 items.",
                RecipePlannerCommandMessageLevel.Info);
            return new ImportProjectItemsResult(
                emptyResult,
                "Imported 0 items.",
                RecipePlannerCommandMessageLevel.Info);
        }

        var buildResult = await BuildPlanAsync(
            new BuildRecipePlanRequest(
                _appState.ProjectItems,
                request.SelectedDataCenter,
                request.SelectedRegion,
                request.PriceFetchScope,
                RefreshPrices: false),
            ct);

        return new ImportProjectItemsResult(
            buildResult,
            buildResult.Built
                ? $"Imported {_appState.ProjectItemCount} items and rebuilt the plan."
                : buildResult.Message,
            buildResult.MessageLevel);
    }

    public async Task<MarketPriceRefreshResult> RefreshPricesAsync(
        RefreshRecipePlanPricesRequest request,
        CancellationToken ct = default)
    {
        var plan = _appState.CurrentPlan;
        var planSessionVersion = _appState.PlanSessionVersion;
        if (plan == null)
        {
            return new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>());
        }

        await _recipePlanBuilder.FetchVendorPricesAsync(plan, ct);
        ct.ThrowIfCancellationRequested();
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>());
        }

        var allItemIds = plan.GetAllItemIds();
        var marketItemIds = MarketPriceAvailability.GetMarketListableItemIds(plan, allItemIds);
        var nonMarketItemIds = MarketPriceAvailability.GetNonMarketListableItemIds(plan, allItemIds);
        var marketEntries = await MarketScopedPriceLoader.LoadBestEntriesAsync(
            _marketCache,
            marketItemIds,
            request.PriceFetchScope,
            request.SelectedDataCenter,
            request.SelectedRegion,
            ct: ct);
        ct.ThrowIfCancellationRequested();
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>());
        }

        var refreshResult = MarketPriceAvailability.FromCachedMarketData(
            plan,
            marketItemIds,
            marketEntries,
            nonMarketItemIds);
        _appState.SetUnavailableMarketItems(refreshResult.UnavailableItems);

        foreach (var root in plan.RootItems)
        {
            UpdateNodePrices(root, marketEntries);
        }

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>());
        }

        plan.PriceVersion++;
        _appState.NotifyPlanPriceChanged();
        return refreshResult;
    }

    public async Task<ActivateRecipePlanResult> ActivatePlanAsync(
        ActivateRecipePlanRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request.Plan);

        var selectedNode = request.Plan.RootItems.FirstOrDefault();
        _cancellableOperations.CancelPlanDependentOperations();
        _appState.ActivateRecipePlan(
            request.Plan,
            CraftPlanStateMapper.GetRootProjectItems(request.Plan),
            request.Plan.DataCenter,
            request.ClearCurrentPlanId);
        var activatedPlanSessionVersion = _appState.PlanSessionVersion;

        if (!request.RefreshVendorPrices && !request.RefreshMarketPrices)
        {
            _appState.SetStatus("Ready", busy: false);
            return new ActivateRecipePlanResult(
                selectedNode,
                new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>()),
                "Plan loaded.",
                RecipePlannerCommandMessageLevel.Success);
        }

        using var operation = _cancellableOperations.Start(
            CancellableOperationWorkflow.PlanActivation,
            "Rebuilding Plan",
            "Rebuilding plan...",
            ct);
        try
        {
            var selectedDataCenter = !string.IsNullOrWhiteSpace(_appState.SelectedDataCenter)
                ? _appState.SelectedDataCenter
                : request.SelectedDataCenter;
            MarketPriceRefreshResult priceRefresh;
            if (request.RefreshMarketPrices)
            {
                priceRefresh = await RefreshPricesAsync(
                    new RefreshRecipePlanPricesRequest(
                        request.PriceFetchScope,
                        selectedDataCenter,
                        request.SelectedRegion),
                    operation.Token);
                if (!operation.IsCurrent)
                {
                    return CanceledActivationResult();
                }

                if (!_appState.IsCurrentPlanSession(request.Plan, activatedPlanSessionVersion))
                {
                    return CanceledActivationResult();
                }
            }
            else
            {
                if (!_appState.IsCurrentPlanSession(request.Plan, activatedPlanSessionVersion))
                {
                    return CanceledActivationResult();
                }

                await _recipePlanBuilder.FetchVendorPricesAsync(request.Plan, operation.Token);
                if (!operation.IsCurrent)
                {
                    return CanceledActivationResult();
                }

                if (!_appState.IsCurrentPlanSession(request.Plan, activatedPlanSessionVersion))
                {
                    return CanceledActivationResult();
                }

                request.Plan.PriceVersion++;
                _appState.NotifyPlanPriceChanged();
                priceRefresh = new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>());
            }

            operation.Complete();
            return new ActivateRecipePlanResult(
                selectedNode,
                priceRefresh,
                "Plan loaded.",
                RecipePlannerCommandMessageLevel.Success);
        }
        catch (OperationCanceledException ex) when (!operation.ShouldReportError(ex))
        {
            operation.Cancel();
            return CanceledActivationResult();
        }
        catch (Exception ex)
        {
            operation.Complete();
            return new ActivateRecipePlanResult(
                selectedNode,
                new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>()),
                $"Plan loaded, but price refresh failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Warning);
        }

        ActivateRecipePlanResult CanceledActivationResult()
        {
            return new ActivateRecipePlanResult(
                selectedNode,
                new MarketPriceRefreshResult(0, 0, Array.Empty<MarketDataUnavailableItem>()),
                "Plan loaded; price refresh canceled.",
                RecipePlannerCommandMessageLevel.Info);
        }
    }

    public ApplyPlanEditorEditResult ApplyPlanEditorEdit(ApplyPlanEditorEditRequest request)
    {
        if (_appState.CurrentPlan == null)
        {
            return new ApplyPlanEditorEditResult(
                new PlanBulkEditResult(),
                "No active plan.");
        }

        var selectedNodeIds = request.NodeIds.ToHashSet(StringComparer.Ordinal);
        var selectedNodes = PlanBulkEditService
            .FlattenPlanNodes(_appState.CurrentPlan)
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

        using (_appState.BeginStateChangeBatch())
        {
            AcquisitionPlanningService.ReconcileAcquisitionDecisions(_appState.CurrentPlan, _appState.ShoppingPlans);
            _appState.ReplaceShoppingItemsFromActivePlan();
            _appState.ClearProcurementOverlay();
            _appState.NotifyPlanDecisionChanged();
        }

        return new ApplyPlanEditorEditResult(
            editResult,
            $"Updated {editResult.ChangedNodes} node{(editResult.ChangedNodes == 1 ? "" : "s")}; skipped {editResult.SkippedNodes}.");
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
}
