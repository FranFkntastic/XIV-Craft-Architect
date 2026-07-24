using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

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
        RecipePlannerCommandMessageLevel level = RecipePlannerCommandMessageLevel.Warning) =>
        new(status, null, message, level, 0, 0, 0, 0);
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
    private readonly WorkerSessionCoordinator _worker;
    private readonly WorkerProjectionStore _projections;
    private readonly WebPlanPersistenceService _planPersistence;
    private readonly AppState _viewSettings;
    private readonly CancellableOperationService _operations;

    public TradeOrderPricingWorkflowService(
        WorkerSessionCoordinator worker,
        WorkerProjectionStore projections,
        WebPlanPersistenceService planPersistence,
        AppState viewSettings,
        CancellableOperationService operations)
    {
        _worker = worker;
        _projections = projections;
        _planPersistence = planPersistence;
        _viewSettings = viewSettings;
        _operations = operations;
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

        using var operation = _operations.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Pricing",
            "Building order craft plan...",
            ct);
        try
        {
            var roots = GetOrderRootItems(order);
            var projectItems = roots
                .Where(item => item.Quantity > 0)
                .Select(ToProjectItem)
                .ToArray();
            if (projectItems.Length == 0)
            {
                return TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                    "The order has no requested outputs to build.");
            }

            var build = await _worker.BuildRecipeAsync(
                new WorkerRecipeBuildRequest(
                    projectItems,
                    options.DataCenter,
                    _projections.Shell.SelectedRegion,
                    _viewSettings.DefaultMarketFetchScope),
                operation.Token);
            if (!operation.IsCurrent || !build.Built)
            {
                return CanceledResult();
            }

            var orderToSave = TradeOrderWorkflow.CopyOrder(order);
            var savedAt = DateTime.UtcNow;
            var link = TradeOrderWorkflow.CreateGeneratedCraftPlanLinkDraft(
                orderToSave,
                replaceExistingPlan: true);
            await _worker.MutatePlanIdentityAsync(
                link.PlanId,
                link.PlanName,
                operation.Token);
            var source = await _worker.GetTradeProjectionAsync(
                cancellationToken: operation.Token)
                ?? throw new InvalidOperationException(
                    "The Worker did not publish the rebuilt Trade plan.");

            TradeOrderWorkflow.ApplyGeneratedCraftPlanLink(
                orderToSave,
                link.PlanId,
                link.PlanName,
                source.ActiveProcurementItems,
                GetOrderOutputs(orderToSave),
                savedAt);
            TradeOrderWorkflow.AppendCraftPlanLinkedHistory(
                orderToSave,
                link,
                savedAt);
            return await PriceAndPersistAsync(
                orderToSave,
                new MarketRefreshRequest(options.ForceRefreshMarketData),
                operation,
                savedAt);
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            operation.Complete("Trade order pricing failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Trade order pricing failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error);
        }
        catch (OperationCanceledException)
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

        using var operation = _operations.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Pricing",
            "Loading linked order plan...",
            ct);
        try
        {
            var stored = await _planPersistence.LoadPlanPayloadAsync(order.CraftPlanId);
            if (stored == null)
            {
                return TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanLoadFailed,
                    "Linked Craft Architect plan could not be loaded.");
            }

            await _worker.ReplaceStoredPlanAsync(
                stored,
                trackStoredPlanIdentity: true,
                operation.Token);
            return await PriceAndPersistAsync(
                TradeOrderWorkflow.CopyOrder(order),
                new MarketRefreshRequest(options.ForceRefreshMarketData),
                operation,
                DateTime.UtcNow,
                string.IsNullOrWhiteSpace(_projections.Shell.RestoreWarning)
                    ? []
                    : [_projections.Shell.RestoreWarning]);
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            operation.Complete("Trade order pricing failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Trade order pricing failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error);
        }
        catch (OperationCanceledException)
        {
            return CanceledResult();
        }
    }

    public async Task<TradeOrderPricingWorkflowResult> RepriceActivePlanAsync(
        TradeOrder order,
        IReadOnlyCollection<int>? marketItemIdsToRefresh,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.ArchivedOrder,
                "Reopen archived orders before repricing.");
        }

        using var operation = _operations.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Pricing",
            "Repricing changed acquisition source...",
            ct);
        try
        {
            return await PriceAndPersistAsync(
                TradeOrderWorkflow.CopyOrder(order),
                marketItemIdsToRefresh is null
                    ? MarketRefreshRequest.Skip
                    : new MarketRefreshRequest(marketItemIdsToRefresh),
                operation,
                DateTime.UtcNow);
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            operation.Complete("Trade order pricing failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Trade order pricing failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error);
        }
        catch (OperationCanceledException)
        {
            return CanceledResult();
        }
    }

    private async Task<TradeOrderPricingWorkflowResult> PriceAndPersistAsync(
        TradeOrder order,
        MarketRefreshRequest refresh,
        CancellableOperationLease operation,
        DateTime refreshedAt,
        IReadOnlyList<string>? initialWarnings = null)
    {
        var source = await _worker.GetTradeProjectionAsync(
            cancellationToken: operation.Token);
        if (source is not { HasPlan: true })
        {
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.PlanLoadFailed,
                "No active Craft Architect plan is available for pricing.");
        }

        var warnings = new List<string>(initialWarnings ?? []);
        operation.ReportStatus("Analyzing market prices...", progress: 45);
        var market = await RefreshMarketEvidenceAsync(
            source,
            refresh,
            operation);
        warnings.AddRange(market.Warnings);
        if (!operation.IsCurrent)
        {
            return CanceledResult();
        }

        operation.ReportStatus("Resolving procurement route...", progress: 70);
        try
        {
            if (market.HasMarketCandidates && market.Published)
            {
                await _worker.RunProcurementAsync(
                    new WorkerProcurementRequest(
                        source.MarketFetchScope,
                        source.SelectedDataCenter,
                        source.SelectedRegion,
                        source.MarketLens,
                        _viewSettings.ProcurementTravelTolerance,
                        _viewSettings.ProcurementEnableSplitWorldPurchases,
                        _viewSettings.ProcurementStartFromHomeDataCenter,
                        _viewSettings.ProcurementTravelPriority),
                    operation.Token);
            }
        }
        catch (InvalidOperationException ex)
        {
            warnings.Add($"Procurement route was not published: {ex.Message}");
        }
        if (!operation.IsCurrent)
        {
            return CanceledResult();
        }

        operation.ReportStatus("Updating order payment evidence...", progress: 90);
        source = await _worker.GetTradeProjectionAsync(
            includeCraftLabor: true,
            cancellationToken: operation.Token)
            ?? throw new InvalidOperationException(
                "The Worker did not publish Trade pricing evidence.");
        warnings.AddRange(source.Warnings);
        var materials = TradeOrderMaterialEvidenceMapper.ToMaterialSnapshots(
            source.MaterialLines);
        var pricedCount = materials.Count(material =>
            material.UnitCost > 0 && material.TotalCost > 0);
        if (pricedCount < source.ActiveProcurementItems.Count)
        {
            warnings.Add(
                $"Order pricing is incomplete: {pricedCount:N0} of {source.ActiveProcurementItems.Count:N0} active procurement items are priced.");
        }

        order.SourceSnapshot.SourcePlanId = order.CraftPlanId;
        order.SourceSnapshot.SourcePlanName =
            order.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(order);
        order.SourceSnapshot.DataCenter = source.SelectedDataCenter;
        order.SourceSnapshot.PlanSessionVersion = source.PlanSessionVersion;
        order.SourceSnapshot.MarketAnalysisVersion = source.MarketAnalysisVersion;
        order.SourceSnapshot.Materials = materials;
        order.SourceSnapshot.CraftLabor = source.CraftLabor;
        order.SourceSnapshot.Warnings = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        order.SourceSnapshot.ImportedAtUtc = refreshedAt;
        order.UpdatedAtUtc = refreshedAt;
        TradeOrderWorkflow.AppendPricingEvidenceHistory(
            order,
            materials.Count,
            market.FetchedCount,
            refreshedAt);

        if (!string.IsNullOrWhiteSpace(order.CraftPlanId))
        {
            var snapshot = await _worker.ExportStoredPlanAsync(
                order.CraftPlanId,
                order.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(order),
                includeSourcePlanIdentity: true,
                operation.Token);
            if (snapshot == null || !await _planPersistence.SaveSnapshotAsync(snapshot))
            {
                return TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                    "Order pricing updated, but failed to save the linked Craft Architect plan.",
                    RecipePlannerCommandMessageLevel.Error);
            }
        }

        var complete = pricedCount == source.ActiveProcurementItems.Count &&
            market.Published;
        var message = complete
            ? "Order pricing ready"
            : "Order pricing updated with incomplete evidence.";
        operation.Complete(message);
        return new TradeOrderPricingWorkflowResult(
            complete
                ? TradeOrderPricingWorkflowStatus.Completed
                : TradeOrderPricingWorkflowStatus.OrderEvidenceIncomplete,
            order,
            message,
            complete
                ? RecipePlannerCommandMessageLevel.Success
                : RecipePlannerCommandMessageLevel.Warning,
            market.AnalyzedCount,
            market.FetchedCount,
            pricedCount,
            source.ActiveProcurementItems.Count);
    }

    private async Task<MarketRefreshResult> RefreshMarketEvidenceAsync(
        WorkerTradeProjection source,
        MarketRefreshRequest refresh,
        CancellableOperationLease operation)
    {
        var acquisition = await _worker.GetAcquisitionProjectionAsync(
            "All",
            operation.Token);
        var candidates = acquisition?.Rows
            .Where(row => row.IsMarketCandidate)
            .ToArray() ?? [];
        if (candidates.Length == 0)
        {
            return new MarketRefreshResult(
                Published: true,
                HasMarketCandidates: false,
                AnalyzedCount: 0,
                FetchedCount: 0,
                Warnings: []);
        }

        if (refresh.SkipRefresh)
        {
            var current = await _worker.GetMarketProjectionAsync(operation.Token);
            var published = current?.HasAnalysis == true;
            return new MarketRefreshResult(
                Published: published,
                HasMarketCandidates: true,
                AnalyzedCount: current?.AvailableCount ?? 0,
                FetchedCount: 0,
                Warnings: published
                    ? []
                    : ["Market evidence is not available. Run Market Analysis before using payment totals."]);
        }

        if (refresh.ItemIds.Count > 0)
        {
            var refreshed = 0;
            var warnings = new List<string>();
            foreach (var itemId in refresh.ItemIds.Distinct())
            {
                var item = candidates.FirstOrDefault(candidate =>
                    candidate.ItemId == itemId);
                if (item == null)
                {
                    continue;
                }

                try
                {
                    await _worker.RefreshMarketItemAsync(
                        new WorkerMarketItemRefreshRequest(
                            item.ItemId,
                            item.ItemName,
                            source.MarketFetchScope,
                            source.SelectedDataCenter,
                            source.SelectedRegion,
                            source.MarketLens),
                        operation.Token);
                    refreshed++;
                }
                catch (InvalidOperationException ex)
                {
                    warnings.Add(
                        $"Market refresh for {item.ItemName} did not publish: {ex.Message}");
                }
            }
            return new MarketRefreshResult(
                Published: warnings.Count == 0,
                HasMarketCandidates: true,
                AnalyzedCount: refreshed,
                FetchedCount: refreshed,
                Warnings: warnings);
        }

        var result = await _worker.RunMarketAnalysisAsync(
            new WorkerMarketAnalysisRequest(
                refresh.ForceRefresh,
                source.MarketFetchScope,
                source.SelectedDataCenter,
                source.SelectedRegion,
                source.MarketLens),
            operation.Token);
        return new MarketRefreshResult(
            result.Published,
            HasMarketCandidates: true,
            result.AnalyzedCount,
            result.FetchedCount,
            Warnings: []);
    }

    private static ProjectItem ToProjectItem(TradeOrderRootItemSnapshot item) =>
        new()
        {
            Id = item.ItemId,
            Name = item.Name,
            Quantity = item.Quantity,
            MustBeHq = item.MustBeHq
        };

    private static IReadOnlyList<TradeOrderRootItemSnapshot> GetOrderRootItems(
        TradeOrder order) =>
        order.SourceSnapshot?.RootItems ?? [];

    private static IReadOnlyList<TradeRequestedOrderOutput> GetOrderOutputs(
        TradeOrder order) =>
        GetOrderRootItems(order)
            .Select(item => new TradeRequestedOrderOutput(
                item.ItemId,
                item.Name,
                item.Quantity,
                item.MustBeHq,
                item.EstimatedSaleValue))
            .ToArray();

    private static TradeOrderPricingWorkflowResult CanceledResult() =>
        TradeOrderPricingWorkflowResult.Noop(
            TradeOrderPricingWorkflowStatus.Canceled,
            "Trade order pricing was canceled.",
            RecipePlannerCommandMessageLevel.Info);

    private sealed record MarketRefreshRequest
    {
        public static MarketRefreshRequest Skip { get; } = new();

        private MarketRefreshRequest()
        {
            SkipRefresh = true;
            ItemIds = [];
        }

        public MarketRefreshRequest(bool forceRefresh)
        {
            ForceRefresh = forceRefresh;
            ItemIds = [];
        }

        public MarketRefreshRequest(IReadOnlyCollection<int> itemIds)
        {
            ForceRefresh = true;
            ItemIds = itemIds;
        }

        public bool ForceRefresh { get; }
        public IReadOnlyCollection<int> ItemIds { get; }
        public bool SkipRefresh { get; }
    }

    private sealed record MarketRefreshResult(
        bool Published,
        bool HasMarketCandidates,
        int AnalyzedCount,
        int FetchedCount,
        IReadOnlyList<string> Warnings);
}
