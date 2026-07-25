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
    private readonly PlanLifecycleWorkflowService _planLifecycle;
    private readonly WorkerProjectionStore _projections;
    private readonly WebPlanPersistenceService _planPersistence;
    private readonly AppState _viewSettings;
    private readonly CancellableOperationService _operations;

    public TradeOrderPricingWorkflowService(
        WorkerSessionCoordinator worker,
        PlanLifecycleWorkflowService planLifecycle,
        WorkerProjectionStore projections,
        WebPlanPersistenceService planPersistence,
        AppState viewSettings,
        CancellableOperationService operations)
    {
        _worker = worker;
        _planLifecycle = planLifecycle;
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
            "Building the order's craft plan...",
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

            var build = await _planLifecycle.BuildRecipeAsync(
                new WorkerRecipeBuildRequest(
                    projectItems,
                    options.DataCenter,
                    _projections.Shell.SelectedRegion,
                    _viewSettings.DefaultMarketFetchScope),
                PlanDerivationDispatch.Deferred,
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
            "Opening the order's craft plan...",
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

            await _planLifecycle.ReplaceStoredPlanAsync(
                stored,
                trackStoredPlanIdentity: true,
                derivation: PlanDerivationDispatch.Deferred,
                cancellationToken: operation.Token);
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
            "Repricing the changed material...",
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
        var derivation = await _planLifecycle.EnsureDerivedAsync(
            new PlanDerivationRequest(
                refresh.ForceRefresh,
                refresh.ItemIds,
                refresh.SkipRefresh),
            operation.Token,
            (message, progress) => operation.ReportStatus(
                message,
                progress: progress),
            () => operation.IsCurrent);
        warnings.AddRange(derivation.Warnings);
        if (!operation.IsCurrent)
        {
            return CanceledResult();
        }

        operation.ReportStatus("Calculating the order payment...", progress: 90);
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
            derivation.MarketEntriesFetched,
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
            derivation.MarketPublished &&
            derivation.ProcurementPublished;
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
            derivation.MarketItemsAnalyzed,
            derivation.MarketEntriesFetched,
            pricedCount,
            source.ActiveProcurementItems.Count);
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
}
