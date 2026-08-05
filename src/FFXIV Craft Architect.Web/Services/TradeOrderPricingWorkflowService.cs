using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record TradeOrderPricingWorkflowOptions(
    bool ForceRefreshMarketData);

public sealed record TradeOrderPricingWorkflowResult(
    TradeOrderPricingWorkflowStatus Status,
    TradeOrder? UpdatedOrder,
    string Message,
    RecipePlannerCommandMessageLevel MessageLevel,
    int MarketItemsAnalyzed,
    int MarketEntriesFetched,
    int PricedMaterialLines,
    int TotalMaterialLines,
    WorkerPlanOwnershipFence? ActivePlanFence = null)
{
    public bool HasUpdatedOrder => UpdatedOrder != null;

    public static TradeOrderPricingWorkflowResult Noop(
        TradeOrderPricingWorkflowStatus status,
        string message,
        RecipePlannerCommandMessageLevel level = RecipePlannerCommandMessageLevel.Warning,
        WorkerPlanOwnershipFence? activePlanFence = null) =>
        new(status, null, message, level, 0, 0, 0, 0, activePlanFence);
}

public readonly record struct WorkerPlanOwnershipFence(
    string PlanId,
    long Revision);

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
                "Reopen archived orders before updating the linked craft plan.");
        }

        using var operation = _operations.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Pricing",
            "Building the order's craft plan...",
            ct);
        WorkerSessionOperationLease? workerOperation = null;
        TradeOrder? stagedOrder = null;
        try
        {
            workerOperation = await _worker.BeginOperationAsync(
                WorkerSessionOperationKind.TradeOrderPricing,
                $"trade-pricing:{order.Id}",
                "Pricing the Trade order...",
                operation.Token);
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
                    _viewSettings.SelectedDataCenter,
                    _viewSettings.SelectedRegion,
                    _viewSettings.DefaultMarketFetchScope),
                PlanDerivationDispatch.Deferred,
                operation.Token,
                workerOperation.OperationId);
            if (!operation.IsCurrent || !build.Built)
            {
                return CanceledResult(CaptureActivePlanFence(order.CraftPlanId));
            }

            var orderToSave = TradeOrderWorkflow.CopyOrder(order);
            stagedOrder = orderToSave;
            var savedAt = DateTime.UtcNow;
            var link = await BeginLinkedPlanRevisionAsync(
                orderToSave,
                operation.Token,
                workerOperation.OperationId);
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
                workerOperation,
                savedAt,
                useCurrentSettingsContext: true);
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            var activePlanFence = CaptureActivePlanFence(stagedOrder?.CraftPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            operation.Complete("Trade order pricing failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Trade order pricing failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error,
                activePlanFence);
        }
        catch (OperationCanceledException)
        {
            var activePlanFence = CaptureActivePlanFence(stagedOrder?.CraftPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            return CanceledResult(activePlanFence);
        }
        finally
        {
            if (workerOperation is not null)
            {
                await workerOperation.DisposeAsync();
            }
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

        var stored = await _planPersistence.LoadPlanPayloadAsync(order.CraftPlanId);
        if (stored == null ||
            !order.CraftPlanSavedAtUtc.HasValue ||
            stored.SavedAt != order.CraftPlanSavedAtUtc.Value ||
            stored.LinkedOrderId is { } linkedOrderId && linkedOrderId != order.Id)
        {
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.PlanLoadFailed,
                "The exact linked Craft Architect plan revision could not be loaded.");
        }

        using var operation = _operations.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Pricing",
            "Opening the order's craft plan...",
            ct);
        WorkerSessionOperationLease? workerOperation = null;
        TradeOrder? stagedOrder = null;
        try
        {
            workerOperation = await _worker.BeginOperationAsync(
                WorkerSessionOperationKind.TradeOrderPricing,
                $"trade-pricing:{order.Id}",
                "Pricing the Trade order...",
                operation.Token);
            await _planLifecycle.ReplaceStoredPlanAsync(
                stored,
                trackStoredPlanIdentity: true,
                derivation: PlanDerivationDispatch.Deferred,
                cancellationToken: operation.Token,
                operationId: workerOperation.OperationId);
            var orderToSave = TradeOrderWorkflow.CopyOrder(order);
            stagedOrder = orderToSave;
            await BeginLinkedPlanRevisionAsync(
                orderToSave,
                operation.Token,
                workerOperation.OperationId);
            return await PriceAndPersistAsync(
                orderToSave,
                new MarketRefreshRequest(options.ForceRefreshMarketData),
                operation,
                workerOperation,
                DateTime.UtcNow,
                useCurrentSettingsContext: true,
                initialWarnings: string.IsNullOrWhiteSpace(_projections.Shell.RestoreWarning)
                    ? []
                    : [_projections.Shell.RestoreWarning]);
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            var activePlanFence = CaptureActivePlanFence(stagedOrder?.CraftPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            operation.Complete("Trade order pricing failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Trade order pricing failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error,
                activePlanFence);
        }
        catch (OperationCanceledException)
        {
            var activePlanFence = CaptureActivePlanFence(stagedOrder?.CraftPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            return CanceledResult(activePlanFence);
        }
        finally
        {
            if (workerOperation is not null)
            {
                await workerOperation.DisposeAsync();
            }
        }
    }

    public async Task<TradeOrderPricingWorkflowResult> ReviseActiveAcquisitionAsync(
        TradeOrder order,
        WorkerAcquisitionMutation mutation,
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
        WorkerSessionOperationLease? workerOperation = null;
        try
        {
            workerOperation = await _worker.BeginOperationAsync(
                WorkerSessionOperationKind.TradeOrderPricing,
                $"trade-pricing:{order.Id}",
                "Pricing the Trade order...",
                operation.Token);
            var active = await _worker.GetTradeProjectionAsync(
                cancellationToken: operation.Token);
            if (active is not { HasPlan: true } ||
                !string.Equals(active.PlanId, order.CraftPlanId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The linked plan changed before the acquisition edit could begin.");
            }

            await BeginLinkedPlanRevisionAsync(
                order,
                operation.Token,
                workerOperation.OperationId);
            await _worker.MutateAcquisitionAsync(
                mutation,
                operation.Token,
                workerOperation.OperationId);
            return await PriceAndPersistAsync(
                order,
                marketItemIdsToRefresh is null
                    ? MarketRefreshRequest.Skip
                    : new MarketRefreshRequest(marketItemIdsToRefresh),
                operation,
                workerOperation,
                DateTime.UtcNow,
                useCurrentSettingsContext: false);
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            var activePlanFence = CaptureActivePlanFence(order.CraftPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            operation.Complete("Trade order pricing failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Trade order pricing failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error,
                activePlanFence);
        }
        catch (OperationCanceledException)
        {
            var activePlanFence = CaptureActivePlanFence(order.CraftPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            return CanceledResult(activePlanFence);
        }
        finally
        {
            if (workerOperation is not null)
            {
                await workerOperation.DisposeAsync();
            }
        }
    }

    public async Task<TradeOrderCraftPlanLinkDraft> BeginLinkedPlanRevisionAsync(
        TradeOrder order,
        CancellationToken cancellationToken = default,
        Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        var link = TradeOrderWorkflow.CreateGeneratedCraftPlanLinkDraft(order);
        await _worker.MutatePlanIdentityAsync(
            link.PlanId,
            link.PlanName,
            cancellationToken,
            operationId);
        order.CraftPlanId = link.PlanId;
        order.CraftPlanName = link.PlanName;
        order.CraftPlanSavedAtUtc = null;
        order.CraftPlanLinkKind = TradeOrderCraftPlanLinkKind.OrderGenerated;
        return link;
    }

    private async Task<TradeOrderPricingWorkflowResult> PriceAndPersistAsync(
        TradeOrder order,
        MarketRefreshRequest refresh,
        CancellableOperationLease operation,
        WorkerSessionOperationLease workerOperation,
        DateTime refreshedAt,
        bool useCurrentSettingsContext,
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
                refresh.SkipRefresh,
                UseCurrentSettingsContext: useCurrentSettingsContext),
            operation.Token,
            (message, progress) => operation.ReportStatus(
                message,
                progress: progress),
            () => operation.IsCurrent && workerOperation.IsCurrent,
            workerOperation.OperationId);
        warnings.AddRange(derivation.Warnings);
        if (!operation.IsCurrent)
        {
            return CanceledResult(CaptureActivePlanFence(order.CraftPlanId));
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
        var pricedCount = materials.Count(material => TradeOrderWorkflow.IsResolvedMaterialEvidence(
            material.UnitCost,
            material.TotalCost,
            material.EvidenceSource));
        if (pricedCount < source.ActiveProcurementItems.Count)
        {
            warnings.Add(
                $"Order supply evidence is incomplete: {pricedCount:N0} of {source.ActiveProcurementItems.Count:N0} active materials are resolved.");
        }

        order.SourceSnapshot.SourcePlanId = order.CraftPlanId;
        order.SourceSnapshot.SourcePlanName =
            order.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(order);
        order.SourceSnapshot.CostBasis = CommissionCostBasis.SelectedAcquisitionSources;
        order.SourceSnapshot.MarketFetchScope = source.MarketFetchScope;
        order.SourceSnapshot.Region = source.SelectedRegion;
        order.SourceSnapshot.DataCenter = source.SelectedDataCenter;
        order.SourceSnapshot.RequestedDataCenters = source.RequestedDataCenters.ToArray();
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
            if (snapshot == null)
            {
                return TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                    "Order pricing updated, but failed to save the linked Craft Architect plan.",
                    RecipePlannerCommandMessageLevel.Error,
                    CaptureActivePlanFence(order.CraftPlanId));
            }
            snapshot.LinkedOrderId = order.Id;
            if (!await _planPersistence.SaveSnapshotAsync(snapshot))
            {
                return TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                    "Order pricing updated, but failed to seal the linked plan snapshot.",
                    RecipePlannerCommandMessageLevel.Error,
                    CaptureActivePlanFence(order.CraftPlanId));
            }
            order.CraftPlanSavedAtUtc = snapshot.SavedAt;
            await _planLifecycle.ReplaceStoredPlanAsync(
                snapshot,
                trackStoredPlanIdentity: true,
                derivation: PlanDerivationDispatch.Deferred,
                cancellationToken: operation.Token,
                operationId: workerOperation.OperationId);
        }

        var complete = pricedCount == source.ActiveProcurementItems.Count &&
            derivation.MarketPublished &&
            derivation.ProcurementPublished;
        var message = complete
            ? "Order pricing ready"
            : "Order pricing updated with incomplete evidence.";
        await workerOperation.CompleteAsync(operation.Token);
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
            source.ActiveProcurementItems.Count,
            CaptureActivePlanFence(order.CraftPlanId));
    }

    private WorkerPlanOwnershipFence? CaptureActivePlanFence(string? expectedPlanId = null)
    {
        var shell = _projections.Shell;
        return !string.IsNullOrWhiteSpace(shell.PlanId) &&
               (string.IsNullOrWhiteSpace(expectedPlanId) ||
                string.Equals(shell.PlanId, expectedPlanId, StringComparison.Ordinal))
            ? new WorkerPlanOwnershipFence(shell.PlanId, shell.Revision)
            : null;
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

    private static TradeOrderPricingWorkflowResult CanceledResult(
        WorkerPlanOwnershipFence? activePlanFence = null) =>
        TradeOrderPricingWorkflowResult.Noop(
            TradeOrderPricingWorkflowStatus.Canceled,
            "Trade order pricing was canceled.",
            RecipePlannerCommandMessageLevel.Info,
            activePlanFence);

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
