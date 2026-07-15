using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record TradeOrderPricingWorkflowOptions(
    string DataCenter,
    string World,
    bool ForceRefreshMarketData);

public sealed record TradeOrderPricingWorkflowResult(
    TradeOrderPricingWorkflowStatus Status,
    TradeOrder? UpdatedOrder,
    string Message,
    RecipePlannerCommandMessageLevel MessageLevel,
    int MarketItemsAnalyzed,
    int MarketEntriesFetched,
    int PricedMaterialLines,
    int TotalMaterialLines)
{
    public bool HasUpdatedOrder => UpdatedOrder != null;

    public static TradeOrderPricingWorkflowResult Noop(
        TradeOrderPricingWorkflowStatus status,
        string message,
        RecipePlannerCommandMessageLevel level = RecipePlannerCommandMessageLevel.Warning)
    {
        return new TradeOrderPricingWorkflowResult(status, null, message, level, 0, 0, 0, 0);
    }
}

public enum TradeOrderPricingWorkflowStatus
{
    Completed,
    ArchivedOrder,
    MissingLinkedPlan,
    PlanBuildFailed,
    PlanLoadFailed,
    MarketAnalysisUnavailable,
    ProcurementUnavailable,
    OrderEvidenceIncomplete,
    Canceled,
    Failed
}

public sealed class TradeOrderPricingWorkflowService
{
    private readonly AppState _appState;
    private readonly TradeOrderCraftPlanBuildService _craftPlanBuildService;
    private readonly WebPlanPersistenceService _planPersistence;
    private readonly MarketAnalysisWorkflowService _marketAnalysisWorkflow;
    private readonly MarketAnalysisSubsetRefreshService _marketAnalysisSubsetRefresh;
    private readonly ProcurementWorkflowService _procurementWorkflow;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly IRecipeOperationSnapshotService _recipeOperationSnapshotService;
    private readonly CommissionCostBasisResolver _costBasisResolver;
    private readonly CancellableOperationService _cancellableOperations;

    public TradeOrderPricingWorkflowService(
        AppState appState,
        TradeOrderCraftPlanBuildService craftPlanBuildService,
        WebPlanPersistenceService planPersistence,
        MarketAnalysisWorkflowService marketAnalysisWorkflow,
        MarketAnalysisSubsetRefreshService marketAnalysisSubsetRefresh,
        ProcurementWorkflowService procurementWorkflow,
        IRecipeLayerWorkflowService recipeLayerWorkflow,
        IRecipeOperationSnapshotService recipeOperationSnapshotService,
        CommissionCostBasisResolver costBasisResolver,
        CancellableOperationService cancellableOperations)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _craftPlanBuildService = craftPlanBuildService ?? throw new ArgumentNullException(nameof(craftPlanBuildService));
        _planPersistence = planPersistence ?? throw new ArgumentNullException(nameof(planPersistence));
        _marketAnalysisWorkflow = marketAnalysisWorkflow ?? throw new ArgumentNullException(nameof(marketAnalysisWorkflow));
        _marketAnalysisSubsetRefresh = marketAnalysisSubsetRefresh ?? throw new ArgumentNullException(nameof(marketAnalysisSubsetRefresh));
        _procurementWorkflow = procurementWorkflow ?? throw new ArgumentNullException(nameof(procurementWorkflow));
        _recipeLayerWorkflow = recipeLayerWorkflow ?? throw new ArgumentNullException(nameof(recipeLayerWorkflow));
        _recipeOperationSnapshotService = recipeOperationSnapshotService ?? throw new ArgumentNullException(nameof(recipeOperationSnapshotService));
        _costBasisResolver = costBasisResolver ?? throw new ArgumentNullException(nameof(costBasisResolver));
        _cancellableOperations = cancellableOperations ?? throw new ArgumentNullException(nameof(cancellableOperations));
    }

    public async Task<TradeOrderPricingWorkflowResult> RebuildAndPriceAsync(
        TradeOrder order,
        TradeOrderPricingWorkflowOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(options);

        if (TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.ArchivedOrder,
                "Reopen archived orders before rebuilding the linked craft plan.");
        }

        using var operation = _cancellableOperations.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Pricing",
            "Building order craft plan...",
            ct);

        try
        {
            var buildResult = await _craftPlanBuildService.BuildFromOrderAsync(
                order,
                options.DataCenter,
                options.World,
                operation.Token);
            if (!operation.IsCurrent)
            {
                return CanceledResult();
            }

            if (!buildResult.Built || buildResult.Plan == null)
            {
                operation.Complete(buildResult.UnavailableReason ?? "Could not build the order craft plan.");
                return TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                    buildResult.UnavailableReason ?? "Could not build the order craft plan.");
            }

            var orderToSave = TradeOrderWorkflow.CopyOrder(order);
            var savedAt = DateTime.UtcNow;
            var outputs = GetOrderOutputs(orderToSave);
            var rootItems = GetOrderRootItems(orderToSave);
            var linkDraft = TradeOrderWorkflow.CreateGeneratedCraftPlanLinkDraft(orderToSave, replaceExistingPlan: true);
            ActivatePlan(buildResult.Plan, rootItems, options.DataCenter, buildResult.ActiveProcurementItems);
            _appState.TrackCurrentPlanIdentity(linkDraft.PlanId, linkDraft.PlanName);
            operation.ReportStatus("Saving linked order plan...", progress: 25);

            var savedPlan = await _planPersistence.SaveGeneratedOrderPlanAsync(
                linkDraft.PlanId,
                linkDraft.PlanName,
                buildResult.Plan,
                rootItems,
                savedAt);
            if (!savedPlan)
            {
                operation.Complete("Failed to save linked Craft Architect plan.");
                return TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                    "Failed to save linked Craft Architect plan.",
                    RecipePlannerCommandMessageLevel.Error);
            }

            TradeOrderWorkflow.ApplyGeneratedCraftPlanLink(
                orderToSave,
                linkDraft.PlanId,
                linkDraft.PlanName,
                buildResult.ActiveProcurementItems,
                outputs,
                savedAt);
            TradeOrderWorkflow.AppendCraftPlanLinkedHistory(orderToSave, linkDraft, savedAt);

            var priced = await PriceActiveOrderPlanAsync(
                orderToSave,
                options.ForceRefreshMarketData,
                operation,
                savedAt,
                persistGeneratedPlan: true);
            operation.Complete(priced.Message);
            return priced;
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            operation.Complete("Trade order pricing failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Trade order pricing failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error);
        }
        catch (OperationCanceledException) when (!operation.IsCurrent || operation.Token.IsCancellationRequested)
        {
            return CanceledResult();
        }
    }

    public async Task<TradeOrderPricingWorkflowResult> RepriceAsync(
        TradeOrder order,
        TradeOrderPricingWorkflowOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(options);

        if (TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.ArchivedOrder,
                "Reopen archived orders before repricing.");
        }

        if (string.IsNullOrWhiteSpace(order.CraftPlanId))
        {
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.MissingLinkedPlan,
                "Create a linked craft plan before repricing.");
        }

        using var operation = _cancellableOperations.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Pricing",
            "Loading linked order plan...",
            ct);

        try
        {
            var result = await _planPersistence.LoadPlanIntoSessionAsync(
                order.CraftPlanId,
                trackStoredPlanIdentity: true);
            if (!operation.IsCurrent)
            {
                return CanceledResult();
            }

            if (result == null || result.Plan == null)
            {
                operation.Complete("Linked Craft Architect plan could not be loaded.");
                return TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanLoadFailed,
                    "Linked Craft Architect plan could not be loaded.");
            }

            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                warnings.Add(result.Warning);
            }

            var priced = await PriceActiveOrderPlanAsync(
                TradeOrderWorkflow.CopyOrder(order),
                options.ForceRefreshMarketData,
                operation,
                DateTime.UtcNow,
                persistGeneratedPlan: true,
                additionalWarnings: warnings);
            operation.Complete(priced.Message);
            return priced;
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            operation.Complete("Trade order pricing failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Trade order pricing failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error);
        }
        catch (OperationCanceledException) when (!operation.IsCurrent || operation.Token.IsCancellationRequested)
        {
            return CanceledResult();
        }
    }

    public async Task<TradeOrderPricingWorkflowResult> RepriceActivePlanAsync(
        TradeOrder order,
        IReadOnlyCollection<int> marketItemIdsToRefresh,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(marketItemIdsToRefresh);

        if (TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.ArchivedOrder,
                "Reopen archived orders before repricing.");
        }

        using var operation = _cancellableOperations.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Pricing",
            "Repricing changed acquisition source...",
            ct);

        try
        {
            var priced = await PriceActiveOrderPlanAsync(
                TradeOrderWorkflow.CopyOrder(order),
                new MarketEvidenceRefreshRequest(marketItemIdsToRefresh),
                operation,
                DateTime.UtcNow,
                persistGeneratedPlan: true);
            operation.Complete(priced.Message);
            return priced;
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            operation.Complete("Trade order pricing failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Trade order pricing failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error);
        }
        catch (OperationCanceledException) when (!operation.IsCurrent || operation.Token.IsCancellationRequested)
        {
            return CanceledResult();
        }
    }

    private async Task<TradeOrderPricingWorkflowResult> PriceActiveOrderPlanAsync(
        TradeOrder order,
        bool forceRefreshMarketData,
        CancellableOperationLease operation,
        DateTime refreshedAt,
        bool persistGeneratedPlan,
        IReadOnlyList<string>? additionalWarnings = null)
    {
        return await PriceActiveOrderPlanAsync(
            order,
            new MarketEvidenceRefreshRequest(forceRefreshMarketData),
            operation,
            refreshedAt,
            persistGeneratedPlan,
            additionalWarnings);
    }

    private async Task<TradeOrderPricingWorkflowResult> PriceActiveOrderPlanAsync(
        TradeOrder order,
        MarketEvidenceRefreshRequest marketRefresh,
        CancellableOperationLease operation,
        DateTime refreshedAt,
        bool persistGeneratedPlan,
        IReadOnlyList<string>? additionalWarnings = null)
    {
        var plan = _appState.CurrentPlan;
        var planSessionVersion = _appState.PlanSessionVersion;
        if (plan == null)
        {
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.PlanLoadFailed,
                "No active Craft Architect plan is available for pricing.");
        }

        operation.ReportStatus("Analyzing market prices...", progress: 45);
        var marketResult = await RefreshMarketEvidenceAsync(
            marketRefresh,
            operation,
            plan,
            planSessionVersion);
        if (!operation.IsCurrent)
        {
            return CanceledResult();
        }

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return CanceledResult("Trade order pricing was canceled because the active craft plan changed.");
        }

        var warnings = new List<string>();
        if (additionalWarnings != null)
        {
            warnings.AddRange(additionalWarnings.Where(warning => !string.IsNullOrWhiteSpace(warning)));
        }
        warnings.AddRange(marketResult.Warnings);

        if (!marketResult.Published)
        {
            warnings.Add("Market analysis did not publish fresh evidence for this order plan.");
        }

        operation.ReportStatus("Resolving procurement route...", progress: 70);
        var procurementResult = await _procurementWorkflow.RunAnalysisAsync(
            new ProcurementWorkflowRequest(() => operation.IsCurrent),
            new Progress<string>(message => operation.ReportStatus(message, progress: 75)),
            operation.Token);
        if (!operation.IsCurrent)
        {
            return CanceledResult();
        }

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return CanceledResult("Trade order pricing was canceled because the active craft plan changed.");
        }

        if (procurementResult.Status != ProcurementWorkflowStatus.Published)
        {
            warnings.Add(procurementResult.Message ?? $"Procurement route was not published: {procurementResult.Status}.");
        }

        operation.ReportStatus("Updating order payment evidence...", progress: 90);
        var demandProjection = await _recipeLayerWorkflow.BuildCurrentDemandProjectionAsync(
            _appState.CurrentPlan,
            operation.Token);
        if (!operation.IsCurrent)
        {
            return CanceledResult();
        }

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return CanceledResult("Trade order pricing was canceled because the active craft plan changed.");
        }

        var activeDemandRows = (demandProjection?.ActiveProcurementDemand ?? Array.Empty<RecipeDemandRow>())
            .Where(row => row.Quantity > 0)
            .ToArray();
        var activeItemList = (demandProjection?.ToActiveProcurementMaterialAggregates() ?? Array.Empty<MaterialAggregate>())
            .Where(item => item.TotalQuantity > 0)
            .ToArray();
        var routePlans = _appState.ProcurementShoppingPlans.Any()
            ? _appState.ProcurementShoppingPlans
            : _appState.ShoppingPlans;
        var lines = _costBasisResolver.BuildSelectedSourceLines(
            activeDemandRows,
            _appState.MarketItemAnalyses,
            routePlans);
        warnings.AddRange(lines.SelectMany(line => line.Warnings));
        if (!string.IsNullOrWhiteSpace(_appState.MarketAnalysisScopeWarning))
        {
            warnings.Add(_appState.MarketAnalysisScopeWarning);
        }

        var materials = TradeOrderMaterialEvidenceMapper.ToMaterialSnapshots(lines);
        var pricedCount = materials.Count(material => material.UnitCost > 0 && material.TotalCost > 0);
        if (pricedCount < activeItemList.Length)
        {
            warnings.Add($"Order pricing is incomplete: {pricedCount:N0} of {activeItemList.Length:N0} active procurement items are priced.");
        }

        var laborSnapshot = await _recipeOperationSnapshotService.BuildAsync(_appState.CurrentPlan, operation.Token);
        if (!operation.IsCurrent)
        {
            return CanceledResult();
        }

        var craftLabor = laborSnapshot.GetRequiredCrafts()
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
        var unresolvedCrafts = laborSnapshot.GetUnresolvedRequiredCrafts().ToArray();
        if (unresolvedCrafts.Length > 0)
        {
            warnings.Add($"Labor-standard evidence is incomplete: {unresolvedCrafts.Length:N0} active crafts could not be resolved.");
        }

        if (craftLabor.Length == 0)
        {
            warnings.Add("Labor-standard evidence is unavailable. No active craft synths were resolved for this order.");
        }

        var versions = _appState.CurrentVersions;
        order.SourceSnapshot.SourcePlanId = order.CraftPlanId;
        order.SourceSnapshot.SourcePlanName = order.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(order);
        order.SourceSnapshot.DataCenter = _appState.SelectedDataCenter;
        order.SourceSnapshot.PlanSessionVersion = planSessionVersion;
        order.SourceSnapshot.MarketAnalysisVersion = versions.MarketAnalysisVersion;
        order.SourceSnapshot.Materials = materials;
        order.SourceSnapshot.CraftLabor = craftLabor;
        order.SourceSnapshot.Warnings = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        order.SourceSnapshot.ImportedAtUtc = refreshedAt;
        order.UpdatedAtUtc = refreshedAt;
        TradeOrderWorkflow.AppendPricingEvidenceHistory(order, materials.Count, marketResult.FetchedCount, refreshedAt);

        if (persistGeneratedPlan && !string.IsNullOrWhiteSpace(order.CraftPlanId) && _appState.CurrentPlan != null)
        {
            var persistedVersions = _appState.CurrentVersions;
            var savedPlan = await _planPersistence.SaveGeneratedOrderPlanAsync(
                order.CraftPlanId,
                order.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(order),
                _appState.CurrentPlan,
                GetOrderRootItems(order),
                refreshedAt);
            if (!savedPlan)
            {
                return TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                    "Order pricing updated, but failed to save the linked Craft Architect plan.",
                    RecipePlannerCommandMessageLevel.Error);
            }

            _appState.TrackCurrentPlanIdentity(
                order.CraftPlanId,
                order.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(order));
            _appState.MarkPersisted(
                PersistedStateBucket.PlanCore | PersistedStateBucket.MarketAnalysis,
                persistedVersions);
        }

        var complete = pricedCount == activeItemList.Length && marketResult.Published;
        var message = complete
            ? "Order pricing ready"
            : "Order pricing updated with incomplete evidence.";
        return new TradeOrderPricingWorkflowResult(
            complete ? TradeOrderPricingWorkflowStatus.Completed : TradeOrderPricingWorkflowStatus.OrderEvidenceIncomplete,
            order,
            message,
            complete ? RecipePlannerCommandMessageLevel.Success : RecipePlannerCommandMessageLevel.Warning,
            marketResult.AnalyzedCount,
            marketResult.FetchedCount,
            pricedCount,
            activeItemList.Length);
    }

    private void ActivatePlan(
        CraftingPlan plan,
        IReadOnlyList<TradeOrderRootItemSnapshot> rootItems,
        string dataCenter,
        IReadOnlyList<MaterialAggregate> activeProcurementItems)
    {
        _appState.ActivateRecipePlan(
            plan,
            rootItems.Select(ToProjectItem),
            dataCenter,
            clearCurrentPlanId: true,
            activeProcurementItems);
    }

    private async Task<MarketEvidenceRefreshSummary> RefreshMarketEvidenceAsync(
        MarketEvidenceRefreshRequest request,
        CancellableOperationLease operation,
        CraftingPlan plan,
        long planSessionVersion)
    {
        if (request.IsSubset)
        {
            var itemIds = request.MarketItemIdsToRefresh
                .Where(itemId => itemId > 0)
                .Distinct()
                .OrderBy(itemId => itemId)
                .ToArray();
            if (itemIds.Length == 0)
            {
                operation.ReportStatus("No market evidence refresh needed for this source change.", progress: 55);
                return new MarketEvidenceRefreshSummary(Published: true, AnalyzedCount: 0, FetchedCount: 0, Warnings: []);
            }

            var subsetResult = await _marketAnalysisSubsetRefresh.RefreshMarketDataAsync(
                new MarketAnalysisSubsetRefreshWorkflowRequest(
                    itemIds,
                    IsCurrentOperation: () => operation.IsCurrent),
                new Progress<string>(message => operation.ReportStatus(message, progress: 50)),
                operation.Token);
            var warnings = new List<string>();
            if (subsetResult.MissingCandidateItemIds.Count > 0)
            {
                warnings.Add($"Market refresh skipped {subsetResult.MissingCandidateItemIds.Count:N0} requested item(s) that are no longer market-analysis candidates.");
            }

            if (subsetResult.NoDataItemIds.Count > 0)
            {
                warnings.Add($"Market refresh found no data for {subsetResult.NoDataItemIds.Count:N0} requested item(s).");
            }

            if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
            {
                return new MarketEvidenceRefreshSummary(Published: false, AnalyzedCount: 0, FetchedCount: 0, Warnings: warnings);
            }

            return new MarketEvidenceRefreshSummary(
                subsetResult.Published,
                subsetResult.AnalyzedCount,
                subsetResult.FetchedCount,
                warnings);
        }

        var marketResult = await _marketAnalysisWorkflow.RunAnalysisAsync(
            new MarketAnalysisWorkflowRequest(request.ForceRefreshMarketData),
            new Progress<string>(message => operation.ReportStatus(message, progress: 50)),
            operation.Token);
        return new MarketEvidenceRefreshSummary(
            marketResult.Published,
            marketResult.AnalyzedCount,
            marketResult.FetchedCount,
            Warnings: []);
    }

    private static ProjectItem ToProjectItem(TradeOrderRootItemSnapshot item)
    {
        return new ProjectItem
        {
            Id = item.ItemId,
            Name = item.Name,
            Quantity = item.Quantity,
            MustBeHq = item.MustBeHq
        };
    }

    private static IReadOnlyList<TradeOrderRootItemSnapshot> GetOrderRootItems(TradeOrder order)
    {
        return order.SourceSnapshot?.RootItems ?? Array.Empty<TradeOrderRootItemSnapshot>();
    }

    private static IReadOnlyList<TradeRequestedOrderOutput> GetOrderOutputs(TradeOrder order)
    {
        return GetOrderRootItems(order)
            .Select(item => new TradeRequestedOrderOutput(
                item.ItemId,
                item.Name,
                item.Quantity,
                item.MustBeHq,
                item.EstimatedSaleValue))
            .ToArray();
    }

    private static TradeOrderPricingWorkflowResult CanceledResult(string? message = null)
    {
        return TradeOrderPricingWorkflowResult.Noop(
            TradeOrderPricingWorkflowStatus.Canceled,
            message ?? "Trade order pricing was canceled.",
            RecipePlannerCommandMessageLevel.Info);
    }

    private sealed record MarketEvidenceRefreshRequest
    {
        public MarketEvidenceRefreshRequest(bool forceRefreshMarketData)
        {
            ForceRefreshMarketData = forceRefreshMarketData;
            MarketItemIdsToRefresh = [];
            IsSubset = false;
        }

        public MarketEvidenceRefreshRequest(IReadOnlyCollection<int> marketItemIdsToRefresh)
        {
            ForceRefreshMarketData = true;
            MarketItemIdsToRefresh = marketItemIdsToRefresh;
            IsSubset = true;
        }

        public bool ForceRefreshMarketData { get; }

        public IReadOnlyCollection<int> MarketItemIdsToRefresh { get; }

        public bool IsSubset { get; }
    }

    private sealed record MarketEvidenceRefreshSummary(
        bool Published,
        int AnalyzedCount,
        int FetchedCount,
        IReadOnlyList<string> Warnings);
}
