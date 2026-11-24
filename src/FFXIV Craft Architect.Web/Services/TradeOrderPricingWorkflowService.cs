using System.Diagnostics.CodeAnalysis;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record TradeOrderPricingWorkflowOptions(
    bool ForceRefreshMarketData,
    TradeMaterialPricingPolicy? MaterialPricingPolicy = null,
    // When the active Worker plan already has exactly the order's requested outputs,
    // price it in place (keeping its recipe tree and acquisition choices) instead of
    // rebuilding the tree from scratch. Market evidence is still reconciled so the
    // order never prices from evidence older than the reusable-cache window.
    bool ReuseMatchingActivePlan = false,
    // Company-provided materials are excluded from the crafter's executable route quote.
    IReadOnlyList<TradePayrollResponsibilityLine>? MaterialResponsibilities = null,
    // Template supply choices applied to a freshly built plan before it is priced.
    IReadOnlyList<TradeCommissionTemplateSupplyChoice>? InitialSupplyChoices = null);

/// <summary>
/// Builds an order's plan as an unpriced skeleton: recipe tree, labor and supply choices,
/// but no market fetch and no quote.
/// </summary>
public sealed record TradeOrderInitializePlanOptions(
    IReadOnlyList<TradeCommissionTemplateSupplyChoice>? InitialSupplyChoices = null,
    IReadOnlyList<TradePayrollResponsibilityLine>? MaterialResponsibilities = null);

/// <summary>
/// Re-quotes a priced order against its active linked plan after a supply edit: a
/// Crafter/Company responsibility toggle (no plan change) and/or one acquisition mutation
/// (e.g. a precraft switched to company-supplied on hand).
/// </summary>
public sealed record TradeOrderRequoteOptions(
    TradeMaterialPricingPolicy? MaterialPricingPolicy = null,
    IReadOnlyList<TradePayrollResponsibilityLine>? MaterialResponsibilities = null,
    WorkerAcquisitionMutation? AcquisitionMutation = null);

/// <summary>
/// Builds one plan for a batch of draft orders with identical requests and stamps it onto
/// every order. <see cref="Price"/> prices that plan once; otherwise every order is an
/// unpriced skeleton.
/// </summary>
public sealed record TradeOrderBatchMaterializeOptions(
    bool Price,
    TradeMaterialPricingPolicy? MaterialPricingPolicy = null,
    IReadOnlyList<TradeCommissionTemplateSupplyChoice>? InitialSupplyChoices = null,
    IReadOnlyList<TradePayrollResponsibilityLine>? MaterialResponsibilities = null);

public sealed record TradeOrderBatchMaterializeResult(
    TradeOrderPricingWorkflowStatus Status,
    IReadOnlyList<TradeOrder> UpdatedOrders,
    string Message,
    RecipePlannerCommandMessageLevel MessageLevel,
    WorkerPlanOwnershipFence? ActivePlanFence = null)
{
    public bool HasUpdatedOrders => UpdatedOrders.Count > 0;

    public static TradeOrderBatchMaterializeResult From(TradeOrderPricingWorkflowResult result) =>
        new(result.Status, [], result.Message, result.MessageLevel, result.ActivePlanFence);
}

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
    Failed,
    CompanyPricingScopeRequired
}

public sealed class TradeOrderPricingWorkflowService
{
    private readonly WorkerSessionCoordinator _worker;
    private readonly PlanLifecycleWorkflowService _planLifecycle;
    private readonly WorkerProjectionStore _projections;
    private readonly WebPlanPersistenceService _planPersistence;
    private readonly CancellableOperationService _operations;
    private readonly TradeOperationsPersistenceService _companies;
    private readonly TradeCompany.CompanyHubClient _companyHub;
    private readonly ProfileHosting.ProfileSyncService _profileSync;

    public TradeOrderPricingWorkflowService(
        WorkerSessionCoordinator worker,
        PlanLifecycleWorkflowService planLifecycle,
        WorkerProjectionStore projections,
        WebPlanPersistenceService planPersistence,
        CancellableOperationService operations,
        TradeOperationsPersistenceService companies,
        TradeCompany.CompanyHubClient companyHub,
        ProfileHosting.ProfileSyncService profileSync)
    {
        _worker = worker;
        _planLifecycle = planLifecycle;
        _projections = projections;
        _planPersistence = planPersistence;
        _operations = operations;
        _companies = companies;
        _companyHub = companyHub;
        _profileSync = profileSync;
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
            var (pricingScope, laborGilPerSynth) = await LoadPricingScopeAsync(order, operation.Token);
            if (pricingScope is not { IsValid: true })
            {
                return PricingScopeRequired();
            }
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

            var reusedActivePlan = options.ReuseMatchingActivePlan &&
                await ActivePlanMatchesRootsAsync(roots, operation.Token);
            if (reusedActivePlan)
            {
                _planLifecycle.Cancel();
                operation.ReportStatus("Pricing the active craft plan...");
            }
            else
            {
                var build = await _planLifecycle.BuildRecipeAsync(
                    new WorkerRecipeBuildRequest(
                        projectItems,
                        pricingScope.EffectiveDataCenter,
                        pricingScope.Region,
                        pricingScope.MarketFetchScope),
                    PlanDerivationDispatch.Deferred,
                    operation.Token,
                    workerOperation.OperationId);
                if (!operation.IsCurrent || !build.Built)
                {
                    return CanceledResult(CaptureActivePlanFence(order.CraftPlanId));
                }
            }

            var orderToSave = TradeOrderWorkflow.CopyOrder(order);
            stagedOrder = orderToSave;
            var savedAt = DateTime.UtcNow;
            var link = await BeginLinkedPlanRevisionAsync(
                orderToSave,
                operation.Token,
                workerOperation.OperationId,
                GetPlanLaborRateStamp(order, laborGilPerSynth, planRebuilt: !reusedActivePlan));
            var source = await _worker.GetTradeProjectionAsync(
                cancellationToken: operation.Token)
                ?? throw new InvalidOperationException(
                    "The Worker did not publish the rebuilt Trade plan.");
            var effectiveResponsibilities = MergeResponsibilities(
                options.InitialSupplyChoices,
                options.MaterialResponsibilities);
            var supply = await ApplyInitialSupplyChoicesAsync(
                source,
                options.InitialSupplyChoices,
                effectiveResponsibilities,
                operation.Token,
                workerOperation.OperationId);
            if (supply.Applied > 0)
            {
                source = await _worker.GetTradeProjectionAsync(
                    cancellationToken: operation.Token)
                    ?? throw new InvalidOperationException(
                        "The Worker did not publish the rebuilt Trade plan.");
            }

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
                pricingScope,
                reusedActivePlan && !options.ForceRefreshMarketData
                    ? MarketRefreshRequest.ReconcilePublished
                    : new MarketRefreshRequest(options.ForceRefreshMarketData),
                operation,
                workerOperation,
                savedAt,
                useCurrentSettingsContext: true,
                materialPricingPolicy: options.MaterialPricingPolicy,
                materialResponsibilities: effectiveResponsibilities,
                initialWarnings: supply.Warnings);
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
            operation.Complete("Ready");
            if (workerOperation is not null)
            {
                await workerOperation.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Builds the order's linked plan without touching the market: recipe tree, labor rate and
    /// supply choices only. The saved order is an unpriced skeleton (no cost basis, no quote)
    /// whose material rows are ready for responsibility editing before the first pricing run.
    /// </summary>
    public async Task<TradeOrderPricingWorkflowResult> InitializePlanAsync(
        TradeOrder order,
        TradeOrderInitializePlanOptions options,
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
            "Trade Order Plan",
            "Building the order's craft plan...",
            ct);
        WorkerSessionOperationLease? workerOperation = null;
        TradeOrder? stagedOrder = null;
        try
        {
            var (pricingScope, laborGilPerSynth) = await LoadPricingScopeAsync(order, operation.Token);
            if (pricingScope is not { IsValid: true })
            {
                return PricingScopeRequired();
            }
            workerOperation = await _worker.BeginOperationAsync(
                WorkerSessionOperationKind.TradeOrderPricing,
                $"trade-pricing:{order.Id}",
                "Building the Trade order plan...",
                operation.Token);
            var projectItems = GetOrderRootItems(order)
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
                    pricingScope.EffectiveDataCenter,
                    pricingScope.Region,
                    pricingScope.MarketFetchScope),
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
                workerOperation.OperationId,
                GetPlanLaborRateStamp(order, laborGilPerSynth, planRebuilt: true));
            var initial = await _worker.GetTradeProjectionAsync(
                cancellationToken: operation.Token)
                ?? throw new InvalidOperationException(
                    "The Worker did not publish the built Trade plan.");
            var effectiveResponsibilities = MergeResponsibilities(
                options.InitialSupplyChoices,
                options.MaterialResponsibilities);
            var supply = await ApplyInitialSupplyChoicesAsync(
                initial,
                options.InitialSupplyChoices,
                effectiveResponsibilities,
                operation.Token,
                workerOperation.OperationId);

            operation.ReportStatus("Listing the plan's materials...", progress: 80);
            var source = await _worker.GetTradeProjectionAsync(
                includeCraftLabor: true,
                cancellationToken: operation.Token,
                materialResponsibilities: effectiveResponsibilities)
                ?? throw new InvalidOperationException(
                    "The Worker did not publish the built Trade plan.");

            TradeOrderWorkflow.ApplyGeneratedCraftPlanLink(
                orderToSave,
                link.PlanId,
                link.PlanName,
                source.ActiveProcurementItems,
                GetOrderOutputs(orderToSave),
                savedAt);
            TradeOrderWorkflow.AppendCraftPlanLinkedHistory(orderToSave, link, savedAt);
            ApplySkeletonSnapshot(orderToSave, source, savedAt, supply.Warnings, pricingScope);

            var sealFailure = await SealLinkedPlanAsync(orderToSave, operation, workerOperation);
            if (sealFailure is not null)
            {
                return sealFailure;
            }

            const string message = "Plan initialized. Review supply choices, then price the plan.";
            await workerOperation.CompleteAsync(operation.Token);
            operation.Complete(message);
            return new TradeOrderPricingWorkflowResult(
                TradeOrderPricingWorkflowStatus.Completed,
                orderToSave,
                message,
                RecipePlannerCommandMessageLevel.Success,
                0,
                0,
                0,
                orderToSave.SourceSnapshot.Materials.Count,
                CaptureActivePlanFence(orderToSave.CraftPlanId));
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            var activePlanFence = CaptureActivePlanFence(stagedOrder?.CraftPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            operation.Complete("Trade order plan failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Building the order plan failed: {ex.Message}",
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
            operation.Complete("Ready");
            if (workerOperation is not null)
            {
                await workerOperation.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Changes one acquisition source on an unpriced skeleton without pricing it: the Worker
    /// plan is mutated and the material rows re-listed, but no market data is fetched and the
    /// order stays unpriced.
    /// </summary>
    public async Task<TradeOrderPricingWorkflowResult> ReviseSkeletonAcquisitionAsync(
        TradeOrder order,
        WorkerAcquisitionMutation mutation,
        IReadOnlyList<TradePayrollResponsibilityLine>? materialResponsibilities = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(mutation);
        if (TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.ArchivedOrder,
                "Reopen archived orders before changing the plan.");
        }

        using var operation = _operations.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Plan",
            "Updating the order's craft plan...",
            ct);
        WorkerSessionOperationLease? workerOperation = null;
        TradeOrder? stagedOrder = null;
        try
        {
            var (pricingScope, laborGilPerSynth) = await LoadPricingScopeAsync(order, operation.Token);
            if (pricingScope is not { IsValid: true })
            {
                return PricingScopeRequired();
            }
            workerOperation = await _worker.BeginOperationAsync(
                WorkerSessionOperationKind.TradeOrderPricing,
                $"trade-pricing:{order.Id}",
                "Updating the Trade order plan...",
                operation.Token);
            var active = await _worker.GetTradeProjectionAsync(
                cancellationToken: operation.Token);
            if (active is not { HasPlan: true } ||
                !string.Equals(active.PlanId, order.CraftPlanId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The linked plan changed before the acquisition edit could begin.");
            }

            var orderToSave = TradeOrderWorkflow.CopyOrder(order);
            stagedOrder = orderToSave;
            await BeginLinkedPlanRevisionAsync(
                orderToSave,
                operation.Token,
                workerOperation.OperationId,
                GetPlanLaborRateStamp(order, laborGilPerSynth, planRebuilt: false));
            await _worker.MutateAcquisitionAsync(
                mutation,
                operation.Token,
                workerOperation.OperationId);
            var source = await _worker.GetTradeProjectionAsync(
                includeCraftLabor: true,
                cancellationToken: operation.Token,
                materialResponsibilities: materialResponsibilities)
                ?? throw new InvalidOperationException(
                    "The Worker did not publish the updated Trade plan.");

            var savedAt = DateTime.UtcNow;
            ApplySkeletonSnapshot(orderToSave, source, savedAt, [], pricingScope);
            var sealFailure = await SealLinkedPlanAsync(orderToSave, operation, workerOperation);
            if (sealFailure is not null)
            {
                return sealFailure;
            }

            const string message = "Plan updated. Price the plan when supply choices are final.";
            await workerOperation.CompleteAsync(operation.Token);
            operation.Complete(message);
            return new TradeOrderPricingWorkflowResult(
                TradeOrderPricingWorkflowStatus.Completed,
                orderToSave,
                message,
                RecipePlannerCommandMessageLevel.Success,
                0,
                0,
                0,
                orderToSave.SourceSnapshot.Materials.Count,
                CaptureActivePlanFence(orderToSave.CraftPlanId));
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            var activePlanFence = CaptureActivePlanFence(stagedOrder?.CraftPlanId ?? order.CraftPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            operation.Complete("Trade order plan update failed.");
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Updating the order plan failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error,
                activePlanFence);
        }
        catch (OperationCanceledException)
        {
            var activePlanFence = CaptureActivePlanFence(stagedOrder?.CraftPlanId ?? order.CraftPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            return CanceledResult(activePlanFence);
        }
        finally
        {
            operation.Complete("Ready");
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
        if (!IsExactLinkedRevision(order, stored))
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
            var (pricingScope, laborGilPerSynth) = await LoadPricingScopeAsync(order, operation.Token);
            if (pricingScope is not { IsValid: true })
            {
                return PricingScopeRequired();
            }
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
                workerOperation.OperationId,
                GetPlanLaborRateStamp(order, laborGilPerSynth, planRebuilt: false));
            return await PriceAndPersistAsync(
                orderToSave,
                pricingScope,
                new MarketRefreshRequest(options.ForceRefreshMarketData),
                operation,
                workerOperation,
                DateTime.UtcNow,
                useCurrentSettingsContext: true,
                materialPricingPolicy: options.MaterialPricingPolicy,
                materialResponsibilities: options.MaterialResponsibilities,
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
            operation.Complete("Ready");
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
        TradeMaterialPricingPolicy? materialPricingPolicy = null,
        CancellationToken ct = default,
        IReadOnlyList<TradePayrollResponsibilityLine>? materialResponsibilities = null)
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
            var (pricingScope, laborGilPerSynth) = await LoadPricingScopeAsync(order, operation.Token);
            if (pricingScope is not { IsValid: true })
            {
                return PricingScopeRequired();
            }
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
                workerOperation.OperationId,
                GetPlanLaborRateStamp(order, laborGilPerSynth, planRebuilt: false));
            await _worker.MutateAcquisitionAsync(
                mutation,
                operation.Token,
                workerOperation.OperationId);
            return await PriceAndPersistAsync(
                order,
                pricingScope,
                marketItemIdsToRefresh is null
                    ? MarketRefreshRequest.Skip
                    : new MarketRefreshRequest(marketItemIdsToRefresh),
                operation,
                workerOperation,
                DateTime.UtcNow,
                useCurrentSettingsContext: false,
                materialPricingPolicy: materialPricingPolicy,
                materialResponsibilities: materialResponsibilities);
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
            operation.Complete("Ready");
            if (workerOperation is not null)
            {
                await workerOperation.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Re-quotes an order against its linked plan after a supply edit without touching the
    /// network when the live session already holds every listing the new quote needs.
    /// A pure Crafter/Company responsibility toggle (no <see cref="TradeOrderRequoteOptions.AcquisitionMutation"/>)
    /// keeps the linked plan revision as is; an acquisition mutation forks and seals a new
    /// revision. When a newly needed item has no loaded listings (or the pricing scope
    /// changed), only that evidence is fetched, from the local cache first.
    /// </summary>
    public async Task<TradeOrderPricingWorkflowResult> RequoteActivePlanAsync(
        TradeOrder order,
        TradeOrderRequoteOptions options,
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
            "Updating the order payment...",
            ct);
        WorkerSessionOperationLease? workerOperation = null;
        TradeOrder? stagedOrder = null;
        try
        {
            var (pricingScope, laborGilPerSynth) = await LoadPricingScopeAsync(order, operation.Token);
            if (pricingScope is not { IsValid: true })
            {
                return PricingScopeRequired();
            }
            workerOperation = await _worker.BeginOperationAsync(
                WorkerSessionOperationKind.TradeOrderPricing,
                $"trade-pricing:{order.Id}",
                "Pricing the Trade order...",
                operation.Token);

            var initialWarnings = new List<string>();
            var active = await _worker.GetTradeProjectionAsync(
                cancellationToken: operation.Token);
            if (active is not { HasPlan: true } ||
                !string.Equals(active.PlanId, order.CraftPlanId, StringComparison.Ordinal))
            {
                // The linked plan is not live (another plan is open): restore its exact
                // sealed revision so the caller never handles this case separately.
                var stored = await _planPersistence.LoadPlanPayloadAsync(order.CraftPlanId);
                if (!IsExactLinkedRevision(order, stored))
                {
                    return TradeOrderPricingWorkflowResult.Noop(
                        TradeOrderPricingWorkflowStatus.PlanLoadFailed,
                        "The exact linked Craft Architect plan revision could not be loaded.");
                }
                await _planLifecycle.ReplaceStoredPlanAsync(
                    stored,
                    trackStoredPlanIdentity: true,
                    derivation: PlanDerivationDispatch.Deferred,
                    cancellationToken: operation.Token,
                    operationId: workerOperation.OperationId);
                if (!string.IsNullOrWhiteSpace(_projections.Shell.RestoreWarning))
                {
                    initialWarnings.Add(_projections.Shell.RestoreWarning);
                }
            }

            var orderToSave = TradeOrderWorkflow.CopyOrder(order);
            stagedOrder = orderToSave;
            var planForked = false;
            if (options.AcquisitionMutation is { } mutation)
            {
                // The plan itself changes (e.g. a precraft switched to company-supplied on
                // hand in one pass), so it gets a fresh immutable linked revision.
                await BeginLinkedPlanRevisionAsync(
                    orderToSave,
                    operation.Token,
                    workerOperation.OperationId,
                    GetPlanLaborRateStamp(order, laborGilPerSynth, planRebuilt: false));
                planForked = true;
                await _worker.MutateAcquisitionAsync(
                    mutation,
                    operation.Token,
                    workerOperation.OperationId);
            }

            if (TradeOrderWorkflow.IsUnpricedSkeleton(order))
            {
                // An unpriced skeleton stays unpriced until the operator prices it: re-list
                // its materials under the new responsibilities, never fetch listings.
                return await CompleteSkeletonRequoteAsync(
                    orderToSave,
                    options.MaterialResponsibilities,
                    pricingScope,
                    planForked,
                    operation,
                    workerOperation);
            }

            var current = await _worker.GetTradeProjectionAsync(
                cancellationToken: operation.Token)
                ?? throw new InvalidOperationException(
                    "The Worker did not publish the linked Trade plan.");
            var targetItemIds = IsPublishedCommission(orderToSave)
                ? null
                : ComputeTargetItemIds(current, options.MaterialResponsibilities);
            var evidence = await CheckLoadedMarketEvidenceAsync(
                pricingScope,
                targetItemIds,
                operation.Token);
            if (evidence.Complete)
            {
                return await CompleteLoadedEvidenceRequoteAsync(
                    orderToSave,
                    options,
                    evidence,
                    planForked,
                    initialWarnings,
                    operation,
                    workerOperation);
            }

            // Slow path: a newly needed item has no loaded listings or the pricing scope
            // changed. The fetched evidence seals into a fresh immutable linked revision.
            if (!planForked)
            {
                await BeginLinkedPlanRevisionAsync(
                    orderToSave,
                    operation.Token,
                    workerOperation.OperationId,
                    GetPlanLaborRateStamp(order, laborGilPerSynth, planRebuilt: false));
            }
            return await PriceAndPersistAsync(
                orderToSave,
                pricingScope,
                MarketRefreshRequest.Skip,
                operation,
                workerOperation,
                DateTime.UtcNow,
                useCurrentSettingsContext: false,
                materialPricingPolicy: options.MaterialPricingPolicy,
                materialResponsibilities: options.MaterialResponsibilities,
                initialWarnings: initialWarnings);
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            var activePlanFence = CaptureActivePlanFence(stagedOrder?.CraftPlanId ?? order.CraftPlanId);
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
            var activePlanFence = CaptureActivePlanFence(stagedOrder?.CraftPlanId ?? order.CraftPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            return CanceledResult(activePlanFence);
        }
        finally
        {
            operation.Complete("Ready");
            if (workerOperation is not null)
            {
                await workerOperation.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Builds the plan once for a batch of draft orders with the same request
    /// ("Duplicate xN"), optionally prices it once, and stamps it onto every order: each
    /// order gets its own linked plan id and sealed plan revision. The first order's revision
    /// stays live in the Worker so selecting it needs no plan reload.
    /// </summary>
    public async Task<TradeOrderBatchMaterializeResult> MaterializeBatchAsync(
        IReadOnlyList<TradeOrder> orders,
        TradeOrderBatchMaterializeOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(options);
        if (orders.Count == 0)
        {
            return TradeOrderBatchMaterializeResult.From(TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                "There are no orders to build."));
        }
        if (orders.Any(order => order is null))
        {
            throw new ArgumentException("The batch contains a missing order.", nameof(orders));
        }
        if (orders.Any(order => TradeOrderStatusWorkflow.IsArchived(order.Status)))
        {
            return TradeOrderBatchMaterializeResult.From(TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.ArchivedOrder,
                "Reopen archived orders before updating their linked craft plans."));
        }

        var first = orders[0];
        using var operation = _operations.Start(
            CancellableOperationWorkflow.TradeOrderPricing,
            "Trade Order Plans",
            options.Price
                ? "Building and pricing the orders' craft plan..."
                : "Building the orders' craft plan...",
            ct);
        WorkerSessionOperationLease? workerOperation = null;
        string? firstPlanId = null;
        try
        {
            var (pricingScope, laborGilPerSynth) = await LoadPricingScopeAsync(first, operation.Token);
            if (pricingScope is not { IsValid: true })
            {
                return TradeOrderBatchMaterializeResult.From(PricingScopeRequired());
            }
            workerOperation = await _worker.BeginOperationAsync(
                WorkerSessionOperationKind.TradeOrderPricing,
                $"trade-pricing:{first.Id}",
                "Building the Trade order plans...",
                operation.Token);
            var projectItems = GetOrderRootItems(first)
                .Where(item => item.Quantity > 0)
                .Select(ToProjectItem)
                .ToArray();
            if (projectItems.Length == 0)
            {
                return TradeOrderBatchMaterializeResult.From(TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                    "The orders have no requested outputs to build."));
            }

            var build = await _planLifecycle.BuildRecipeAsync(
                new WorkerRecipeBuildRequest(
                    projectItems,
                    pricingScope.EffectiveDataCenter,
                    pricingScope.Region,
                    pricingScope.MarketFetchScope),
                PlanDerivationDispatch.Deferred,
                operation.Token,
                workerOperation.OperationId);
            if (!operation.IsCurrent || !build.Built)
            {
                return TradeOrderBatchMaterializeResult.From(
                    CanceledResult(CaptureActivePlanFence(first.CraftPlanId)));
            }

            // The live session takes the first order's plan identity and labor rate; every
            // other order gets its own plan id when the sealed snapshot is cloned below.
            var firstOrder = TradeOrderWorkflow.CopyOrder(first);
            var firstLink = await BeginLinkedPlanRevisionAsync(
                firstOrder,
                operation.Token,
                workerOperation.OperationId,
                GetPlanLaborRateStamp(first, laborGilPerSynth, planRebuilt: true));
            firstPlanId = firstLink.PlanId;
            var initial = await _worker.GetTradeProjectionAsync(
                cancellationToken: operation.Token)
                ?? throw new InvalidOperationException(
                    "The Worker did not publish the built Trade plan.");
            var effectiveResponsibilities = MergeResponsibilities(
                options.InitialSupplyChoices,
                options.MaterialResponsibilities);
            var supply = await ApplyInitialSupplyChoicesAsync(
                initial,
                options.InitialSupplyChoices,
                effectiveResponsibilities,
                operation.Token,
                workerOperation.OperationId);
            var warnings = new List<string>(supply.Warnings);

            WorkerTradeProjection source;
            PlanDerivationResult? derivation = null;
            if (options.Price)
            {
                var current = supply.Applied > 0
                    ? await _worker.GetTradeProjectionAsync(cancellationToken: operation.Token)
                        ?? throw new InvalidOperationException(
                            "The Worker did not publish the built Trade plan.")
                    : initial;
                derivation = await _planLifecycle.EnsureDerivedAsync(
                    new PlanDerivationRequest(
                        ForceRefreshMarketData: false,
                        UseCurrentSettingsContext: false,
                        CompanyPricingScope: pricingScope,
                        TargetItemIds: ComputeTargetItemIds(current, effectiveResponsibilities),
                        SkipProcurement: true),
                    operation.Token,
                    (message, progress) => operation.ReportStatus(
                        message,
                        progress: progress),
                    () => operation.IsCurrent && workerOperation.IsCurrent,
                    workerOperation.OperationId);
                warnings.AddRange(derivation.Warnings);
                if (!operation.IsCurrent)
                {
                    return TradeOrderBatchMaterializeResult.From(
                        CanceledResult(CaptureActivePlanFence(firstPlanId)));
                }

                operation.ReportStatus("Calculating the order payment...", progress: 85);
                source = await _worker.GetTradeProjectionAsync(
                    includeCraftLabor: true,
                    materialPricingPolicy: options.MaterialPricingPolicy,
                    cancellationToken: operation.Token,
                    materialResponsibilities: effectiveResponsibilities)
                    ?? throw new InvalidOperationException(
                        "The Worker did not publish Trade pricing evidence.");
            }
            else
            {
                operation.ReportStatus("Listing the plan's materials...", progress: 80);
                source = await _worker.GetTradeProjectionAsync(
                    includeCraftLabor: true,
                    cancellationToken: operation.Token,
                    materialResponsibilities: effectiveResponsibilities)
                    ?? throw new InvalidOperationException(
                        "The Worker did not publish the built Trade plan.");
            }

            operation.ReportStatus("Saving the orders' linked plans...", progress: 92);
            var preExportRevision = _projections.Shell.Revision;
            var baseSnapshot = await _worker.ExportStoredPlanAsync(
                firstLink.PlanId,
                firstLink.PlanName,
                includeSourcePlanIdentity: true,
                operation.Token);
            if (baseSnapshot is null)
            {
                return TradeOrderBatchMaterializeResult.From(TradeOrderPricingWorkflowResult.Noop(
                    TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                    "The orders' craft plan was built, but failed to save the linked Craft Architect plan.",
                    RecipePlannerCommandMessageLevel.Error,
                    CaptureActivePlanFence(firstPlanId)));
            }

            var updatedOrders = new List<TradeOrder>(orders.Count);
            StoredPlan? firstSealed = null;
            var complete = true;
            var previousSavedAt = DateTime.MinValue;
            for (var index = 0; index < orders.Count; index++)
            {
                operation.Token.ThrowIfCancellationRequested();
                var orderCopy = index == 0 ? firstOrder : TradeOrderWorkflow.CopyOrder(orders[index]);
                var link = index == 0
                    ? firstLink
                    : TradeOrderWorkflow.CreateGeneratedCraftPlanLinkDraft(orderCopy);
                orderCopy.CraftPlanId = link.PlanId;
                orderCopy.CraftPlanName = link.PlanName;
                orderCopy.CraftPlanSavedAtUtc = null;
                orderCopy.CraftPlanLinkKind = TradeOrderCraftPlanLinkKind.OrderGenerated;

                // Every copy is its own immutable revision, so each gets a distinct SavedAt.
                var savedAt = DateTime.UtcNow;
                if (savedAt <= previousSavedAt)
                {
                    savedAt = previousSavedAt.AddMilliseconds(1);
                }
                previousSavedAt = savedAt;

                TradeOrderWorkflow.AppendCraftPlanLinkedHistory(orderCopy, link, savedAt);
                if (options.Price)
                {
                    var priced = ApplyPricedSnapshot(
                        orderCopy,
                        source,
                        effectiveResponsibilities,
                        warnings,
                        savedAt,
                        quotePlanId: link.PlanId);
                    TradeOrderWorkflow.AppendPricingEvidenceHistory(
                        orderCopy,
                        priced.MaterialCount,
                        derivation!.MarketEntriesFetched,
                        savedAt);
                    complete &= source.MaterialQuote != null &&
                        priced.PricedCount == priced.CrafterActiveCount &&
                        derivation.MarketPublished &&
                        derivation.ProcurementPublished;
                }
                else
                {
                    ApplySkeletonSnapshot(orderCopy, source, savedAt, warnings, pricingScope);
                }

                var planCopy = CloneStoredPlan(baseSnapshot);
                planCopy.Id = link.PlanId;
                planCopy.Name = link.PlanName;
                planCopy.SourcePlanId = link.PlanId;
                planCopy.SourcePlanName = link.PlanName;
                planCopy.LinkedOrderId = orderCopy.Id;
                planCopy.SourceOrderId = null;
                planCopy.SavedAt = savedAt;
                if (!await _planPersistence.SaveSnapshotAsync(planCopy))
                {
                    return TradeOrderBatchMaterializeResult.From(TradeOrderPricingWorkflowResult.Noop(
                        TradeOrderPricingWorkflowStatus.PlanBuildFailed,
                        $"Failed to seal the linked plan for \"{orderCopy.Title}\"; no orders were updated.",
                        RecipePlannerCommandMessageLevel.Error,
                        CaptureActivePlanFence(firstPlanId)));
                }

                orderCopy.CraftPlanSavedAtUtc = planCopy.SavedAt;
                firstSealed ??= planCopy;
                updatedOrders.Add(orderCopy);
            }

            // The first order's sealed revision becomes the live session identity, so the
            // Plan tab can show orders[0] immediately without re-importing its plan.
            _planLifecycle.Cancel();
            await _worker.AdoptStoredPlanRevisionAsync(
                firstSealed!,
                operation.Token,
                workerOperation.OperationId,
                preExportRevision);

            var count = updatedOrders.Count;
            var status = !options.Price || complete
                ? TradeOrderPricingWorkflowStatus.Completed
                : TradeOrderPricingWorkflowStatus.OrderEvidenceIncomplete;
            var message = !options.Price
                ? $"{count:N0} order plans initialized. Review supply choices, then price the plans."
                : complete
                    ? $"{count:N0} orders priced."
                    : $"{count:N0} orders priced with incomplete evidence.";
            await workerOperation.CompleteAsync(operation.Token);
            operation.Complete(message);
            return new TradeOrderBatchMaterializeResult(
                status,
                updatedOrders,
                message,
                status == TradeOrderPricingWorkflowStatus.Completed
                    ? RecipePlannerCommandMessageLevel.Success
                    : RecipePlannerCommandMessageLevel.Warning,
                CaptureActivePlanFence(firstPlanId));
        }
        catch (Exception ex) when (operation.ShouldReportError(ex))
        {
            var activePlanFence = CaptureActivePlanFence(firstPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            operation.Complete("Trade order plans failed.");
            return TradeOrderBatchMaterializeResult.From(TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.Failed,
                $"Building the order plans failed: {ex.Message}",
                RecipePlannerCommandMessageLevel.Error,
                activePlanFence));
        }
        catch (OperationCanceledException)
        {
            var activePlanFence = CaptureActivePlanFence(firstPlanId);
            if (workerOperation is not null)
            {
                await workerOperation.AbortAsync(CancellationToken.None);
            }
            return TradeOrderBatchMaterializeResult.From(CanceledResult(activePlanFence));
        }
        finally
        {
            operation.Complete("Ready");
            if (workerOperation is not null)
            {
                await workerOperation.DisposeAsync();
            }
        }
    }

    /// <param name="laborGilPerSynth">
    /// The order's effective per-synth labor rate. When set, it is stamped on the linked plan so
    /// automatic craft-vs-buy defaults count the labor each precraft adds to the payment.
    /// </param>
    public async Task<TradeOrderCraftPlanLinkDraft> BeginLinkedPlanRevisionAsync(
        TradeOrder order,
        CancellationToken cancellationToken = default,
        Guid? operationId = null,
        decimal? laborGilPerSynth = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        var link = TradeOrderWorkflow.CreateGeneratedCraftPlanLinkDraft(order);
        await _worker.MutatePlanIdentityAsync(
            link.PlanId,
            link.PlanName,
            cancellationToken,
            operationId,
            laborGilPerSynth);
        order.CraftPlanId = link.PlanId;
        order.CraftPlanName = link.PlanName;
        order.CraftPlanSavedAtUtc = null;
        order.CraftPlanLinkKind = TradeOrderCraftPlanLinkKind.OrderGenerated;
        return link;
    }

    private async Task<TradeOrderPricingWorkflowResult> PriceAndPersistAsync(
        TradeOrder order,
        TradeCompanyPricingScope pricingScope,
        MarketRefreshRequest refresh,
        CancellableOperationLease operation,
        WorkerSessionOperationLease workerOperation,
        DateTime refreshedAt,
        bool useCurrentSettingsContext,
        TradeMaterialPricingPolicy? materialPricingPolicy = null,
        IReadOnlyList<TradePayrollResponsibilityLine>? materialResponsibilities = null,
        IReadOnlyList<string>? initialWarnings = null)
    {
        // The caller resolved and validated the company pricing scope before taking
        // the Worker operation; resolving it again would repeat a hosted round trip.
        var source = await _worker.GetTradeProjectionAsync(
            cancellationToken: operation.Token);
        if (source is not { HasPlan: true })
        {
            return TradeOrderPricingWorkflowResult.Noop(
                TradeOrderPricingWorkflowStatus.PlanLoadFailed,
                "No active Craft Architect plan is available for pricing.");
        }

        var warnings = new List<string>(initialWarnings ?? []);
        // Draft orders fetch only what the crafter will actually buy. Published terms keep
        // full market evidence so their provided-material values never change underneath.
        var targetItemIds = IsPublishedCommission(order)
            ? null
            : ComputeTargetItemIds(source, materialResponsibilities);
        var derivation = await _planLifecycle.EnsureDerivedAsync(
            new PlanDerivationRequest(
                refresh.ForceRefresh,
                refresh.ItemIds,
                refresh.SkipRefresh,
                UseCurrentSettingsContext: false,
                CompanyPricingScope: pricingScope,
                ReconcileMarketEvidence: refresh.Reconcile,
                TargetItemIds: targetItemIds,
                SkipProcurement: true),
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
            materialPricingPolicy: materialPricingPolicy,
            cancellationToken: operation.Token,
            materialResponsibilities: materialResponsibilities)
            ?? throw new InvalidOperationException(
                "The Worker did not publish Trade pricing evidence.");
        var priced = ApplyPricedSnapshot(
            order,
            source,
            materialResponsibilities,
            warnings,
            refreshedAt,
            quotePlanId: source.PlanId);
        TradeOrderWorkflow.AppendPricingEvidenceHistory(
            order,
            priced.MaterialCount,
            derivation.MarketEntriesFetched,
            refreshedAt);

        var sealFailure = await SealLinkedPlanAsync(order, operation, workerOperation);
        if (sealFailure is not null)
        {
            return sealFailure;
        }

        var pricedCount = priced.PricedCount;
        var crafterActiveCount = priced.CrafterActiveCount;
        var complete = source.MaterialQuote != null &&
            pricedCount == crafterActiveCount &&
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
            crafterActiveCount,
            CaptureActivePlanFence(order.CraftPlanId));
    }

    private readonly record struct PricedSnapshotSummary(
        int MaterialCount,
        int PricedCount,
        int CrafterActiveCount);

    /// <summary>
    /// Records a priced Trade projection on the order's snapshot: route cost basis, market
    /// context, materials, labor, quote and warnings. Company-provided materials are never
    /// bought by the crafter, so only the crafter's own active materials decide completeness.
    /// </summary>
    private static PricedSnapshotSummary ApplyPricedSnapshot(
        TradeOrder order,
        WorkerTradeProjection source,
        IReadOnlyList<TradePayrollResponsibilityLine>? materialResponsibilities,
        IEnumerable<string> initialWarnings,
        DateTime refreshedAt,
        string? quotePlanId)
    {
        var warnings = new List<string>(initialWarnings);
        warnings.AddRange(source.Warnings);
        var materials = TradeOrderMaterialEvidenceMapper.ToMaterialSnapshots(
            source.MaterialLines);
        var companyProvided = ToCompanyProvidedKeys(materialResponsibilities);
        var crafterActiveCount = source.ActiveProcurementItems
            .Count(item => !companyProvided.Contains((item.ItemId, item.RequiresHq)));
        var pricedCount = materials
            .Where(material => !companyProvided.Contains((material.ItemId, material.RequiresHq)))
            .Count(material => TradeOrderWorkflow.IsResolvedMaterialEvidence(
                material.UnitCost,
                material.TotalCost,
                material.EvidenceSource));
        if (pricedCount < crafterActiveCount)
        {
            warnings.Add(
                $"Order supply evidence is incomplete: {pricedCount:N0} of {crafterActiveCount:N0} active materials are resolved.");
        }

        order.SourceSnapshot ??= new TradeOrderSourceSnapshot();
        var snapshot = order.SourceSnapshot;
        snapshot.SourcePlanId = order.CraftPlanId;
        snapshot.SourcePlanName =
            order.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(order);
        snapshot.CostBasis = CommissionCostBasis.ProcurementRoute;
        snapshot.MarketFetchScope = source.MarketFetchScope;
        snapshot.Region = source.SelectedRegion;
        snapshot.DataCenter = source.SelectedDataCenter;
        snapshot.RequestedDataCenters = source.RequestedDataCenters.ToArray();
        snapshot.PlanSessionVersion = source.PlanSessionVersion;
        snapshot.MarketAnalysisVersion = source.MarketAnalysisVersion;
        snapshot.Materials = materials;
        snapshot.CraftLabor = source.CraftLabor;
        snapshot.MaterialQuote = source.MaterialQuote is null
            ? null
            : source.MaterialQuote with
            {
                CompanyProfileId = order.CompanyProfileId,
                SourcePlanId = quotePlanId,
                PlanSessionVersion = source.PlanSessionVersion,
                MarketAnalysisVersion = source.MarketAnalysisVersion
            };
        snapshot.MaterialQuoteFailureReason =
            source.MaterialQuote is null ? source.MaterialQuoteFailureReason : null;
        snapshot.Warnings = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        snapshot.ImportedAtUtc = refreshedAt;
        order.UpdatedAtUtc = refreshedAt;
        return new PricedSnapshotSummary(materials.Count, pricedCount, crafterActiveCount);
    }

    private readonly record struct LoadedMarketEvidence(bool Complete, int AnalyzedCount);

    /// <summary>
    /// True when the live session's market evidence was published for the company pricing
    /// scope and already covers every market candidate the quote needs (targets, or every
    /// candidate when <paramref name="targetItemIds"/> is null). Reads Worker projections
    /// only: no network and no market-cache reads.
    /// </summary>
    private async Task<LoadedMarketEvidence> CheckLoadedMarketEvidenceAsync(
        TradeCompanyPricingScope pricingScope,
        IReadOnlyCollection<int>? targetItemIds,
        CancellationToken cancellationToken)
    {
        var acquisition = await _worker.GetAcquisitionProjectionAsync("All", cancellationToken);
        var market = await _worker.GetMarketProjectionAsync(cancellationToken);
        if (acquisition is not { HasPlan: true } ||
            market is null ||
            !MarketContextMatches(market, pricingScope))
        {
            return new LoadedMarketEvidence(false, 0);
        }

        var needed = acquisition.Rows
            .Where(row => row.IsMarketCandidate &&
                (targetItemIds is null || targetItemIds.Contains(row.ItemId)))
            .Select(row => row.ItemId)
            .ToHashSet();
        if (needed.Count == 0)
        {
            return new LoadedMarketEvidence(true, 0);
        }
        if (!market.HasAnalysis)
        {
            return new LoadedMarketEvidence(false, 0);
        }

        // Items lists every published shopping plan; unavailable items were already
        // checked and found unlisted. Same rule as the derivation's missing-evidence check.
        var known = market.Items
            .Select(item => item.ItemId)
            .Concat(acquisition.UnavailableMarketItems.Select(item => item.ItemId))
            .ToHashSet();
        return needed.All(known.Contains)
            ? new LoadedMarketEvidence(true, market.AvailableCount)
            : new LoadedMarketEvidence(false, 0);
    }

    private static bool MarketContextMatches(
        WorkerMarketProjection market,
        TradeCompanyPricingScope pricingScope)
    {
        var requestedDataCenters = MarketFetchScopeResolver.GetDataCenters(
            pricingScope.MarketFetchScope,
            pricingScope.EffectiveDataCenter,
            pricingScope.Region,
            [pricingScope.Region]);
        return market.Scope == pricingScope.MarketFetchScope &&
            string.Equals(
                market.SelectedDataCenter,
                pricingScope.EffectiveDataCenter,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                market.SelectedRegion,
                pricingScope.Region,
                StringComparison.OrdinalIgnoreCase) &&
            (market.RequestedDataCenters ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(requestedDataCenters);
    }

    /// <summary>
    /// The requote fast path: re-reads the Trade projection under the new responsibilities
    /// from evidence the session already holds. The linked plan is sealed again only when
    /// the requote changed the plan (and so forked its identity).
    /// </summary>
    private async Task<TradeOrderPricingWorkflowResult> CompleteLoadedEvidenceRequoteAsync(
        TradeOrder order,
        TradeOrderRequoteOptions options,
        LoadedMarketEvidence evidence,
        bool planForked,
        IReadOnlyList<string> initialWarnings,
        CancellableOperationLease operation,
        WorkerSessionOperationLease workerOperation)
    {
        operation.ReportStatus("Calculating the order payment...", progress: 90);
        var source = await _worker.GetTradeProjectionAsync(
            includeCraftLabor: true,
            materialPricingPolicy: options.MaterialPricingPolicy,
            cancellationToken: operation.Token,
            materialResponsibilities: options.MaterialResponsibilities)
            ?? throw new InvalidOperationException(
                "The Worker did not publish Trade pricing evidence.");
        var priced = ApplyPricedSnapshot(
            order,
            source,
            options.MaterialResponsibilities,
            initialWarnings,
            DateTime.UtcNow,
            quotePlanId: order.CraftPlanId);

        if (planForked)
        {
            var sealFailure = await SealLinkedPlanAsync(order, operation, workerOperation);
            if (sealFailure is not null)
            {
                return sealFailure;
            }
        }

        var complete = source.MaterialQuote != null &&
            priced.PricedCount == priced.CrafterActiveCount;
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
            evidence.AnalyzedCount,
            0,
            priced.PricedCount,
            priced.CrafterActiveCount,
            CaptureActivePlanFence(order.CraftPlanId));
    }

    /// <summary>Re-lists an unpriced skeleton's materials without any market access.</summary>
    private async Task<TradeOrderPricingWorkflowResult> CompleteSkeletonRequoteAsync(
        TradeOrder order,
        IReadOnlyList<TradePayrollResponsibilityLine>? materialResponsibilities,
        TradeCompanyPricingScope pricingScope,
        bool planForked,
        CancellableOperationLease operation,
        WorkerSessionOperationLease workerOperation)
    {
        var source = await _worker.GetTradeProjectionAsync(
            includeCraftLabor: true,
            cancellationToken: operation.Token,
            materialResponsibilities: materialResponsibilities)
            ?? throw new InvalidOperationException(
                "The Worker did not publish the updated Trade plan.");
        ApplySkeletonSnapshot(order, source, DateTime.UtcNow, order.SourceSnapshot.Warnings, pricingScope);
        if (planForked)
        {
            var sealFailure = await SealLinkedPlanAsync(order, operation, workerOperation);
            if (sealFailure is not null)
            {
                return sealFailure;
            }
        }

        const string message = "Plan updated. Price the plan when supply choices are final.";
        await workerOperation.CompleteAsync(operation.Token);
        operation.Complete(message);
        return new TradeOrderPricingWorkflowResult(
            TradeOrderPricingWorkflowStatus.Completed,
            order,
            message,
            RecipePlannerCommandMessageLevel.Success,
            0,
            0,
            0,
            order.SourceSnapshot.Materials.Count,
            CaptureActivePlanFence(order.CraftPlanId));
    }

    private static bool IsExactLinkedRevision(TradeOrder order, [NotNullWhen(true)] StoredPlan? stored) =>
        stored != null &&
        order.CraftPlanSavedAtUtc.HasValue &&
        stored.SavedAt == order.CraftPlanSavedAtUtc.Value &&
        !(stored.LinkedOrderId is { } linkedOrderId && linkedOrderId != order.Id);

    private static StoredPlan CloneStoredPlan(StoredPlan source) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            CreatedAt = source.CreatedAt,
            ModifiedAt = source.ModifiedAt,
            SavedAt = source.SavedAt,
            DataCenter = source.DataCenter,
            ProjectItems = source.ProjectItems
                .Select(item => new StoredProjectItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    IconId = item.IconId,
                    Quantity = item.Quantity,
                    MustBeHq = item.MustBeHq
                })
                .ToList(),
            PlanJson = source.PlanJson,
            PlanStateJson = source.PlanStateJson,
            MarketPlansJson = source.MarketPlansJson,
            MarketIntelligenceJson = source.MarketIntelligenceJson,
            ProcurementRouteJson = source.ProcurementRouteJson,
            ProcurementTravelTolerance = source.ProcurementTravelTolerance,
            MarketItemAnalysesJson = source.MarketItemAnalysesJson,
            MarketAnalysisRecipeBasisJson = source.MarketAnalysisRecipeBasisJson,
            MarketAnalysisScopeSnapshotJson = source.MarketAnalysisScopeSnapshotJson,
            SavedRecommendationMode = source.SavedRecommendationMode,
            SavedMarketAnalysisLens = source.SavedMarketAnalysisLens,
            SourcePlanId = source.SourcePlanId,
            SourcePlanName = source.SourcePlanName,
            LinkedOrderId = source.LinkedOrderId,
            SourceOrderId = source.SourceOrderId
        };

    /// <summary>
    /// Exports the live plan, seals it to plan storage as the order's linked revision, and
    /// stamps that identity on the session. Returns a failure result, or null on success.
    /// </summary>
    private async Task<TradeOrderPricingWorkflowResult?> SealLinkedPlanAsync(
        TradeOrder order,
        CancellableOperationLease operation,
        WorkerSessionOperationLease workerOperation)
    {
        if (string.IsNullOrWhiteSpace(order.CraftPlanId))
        {
            return null;
        }

        var preExportRevision = _projections.Shell.Revision;
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
        // The snapshot was exported from this exact live session, so re-importing
        // it (and re-fetching every projection) would only reproduce the same
        // state. Stamp the sealed revision identity instead: the session and its
        // durable record now track this SavedAt/LinkedOrderId, keeping
        // CraftPlanSavedAtUtc and the active-plan fence aligned.
        _planLifecycle.Cancel();
        await _worker.AdoptStoredPlanRevisionAsync(
            snapshot,
            operation.Token,
            workerOperation.OperationId,
            preExportRevision);
        return null;
    }

    /// <summary>Records an unpriced skeleton: material rows and labor, but no pricing evidence.</summary>
    private static void ApplySkeletonSnapshot(
        TradeOrder order,
        WorkerTradeProjection source,
        DateTime savedAt,
        IReadOnlyList<string> warnings,
        TradeCompanyPricingScope pricingScope)
    {
        order.SourceSnapshot ??= new TradeOrderSourceSnapshot();
        var snapshot = order.SourceSnapshot;
        snapshot.SourcePlanId = order.CraftPlanId;
        snapshot.SourcePlanName =
            order.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(order);
        snapshot.CostBasis = null;
        snapshot.MarketFetchScope = pricingScope.MarketFetchScope;
        snapshot.Region = pricingScope.Region;
        snapshot.DataCenter = pricingScope.EffectiveDataCenter;
        snapshot.RequestedDataCenters = MarketFetchScopeResolver.GetDataCenters(
                pricingScope.MarketFetchScope,
                pricingScope.EffectiveDataCenter,
                pricingScope.Region,
                [pricingScope.Region])
            .ToArray();
        snapshot.PlanSessionVersion = source.PlanSessionVersion;
        snapshot.MarketAnalysisVersion = 0;
        snapshot.Materials = TradeOrderMaterialEvidenceMapper.ToMaterialSnapshots(source.MaterialLines);
        snapshot.CraftLabor = source.CraftLabor;
        snapshot.MaterialQuote = null;
        snapshot.MaterialQuoteFailureReason = null;
        // Skeleton warnings are only supply-choice problems; missing-price warnings from the
        // unpriced projection would be noise until the plan is priced.
        snapshot.Warnings = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        snapshot.ImportedAtUtc = savedAt;
        order.UpdatedAtUtc = savedAt;
    }

    private sealed record SupplyChoiceApplication(int Applied, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Applies template supply choices to the live plan before it is priced: explicit
    /// acquisition sources first, then every company-provided precraft still set to craft is
    /// marked on hand so its subtree drops out of the crafter's demand (and market scope).
    /// Commissioned outputs always stay crafted.
    /// </summary>
    private async Task<SupplyChoiceApplication> ApplyInitialSupplyChoicesAsync(
        WorkerTradeProjection source,
        IReadOnlyList<TradeCommissionTemplateSupplyChoice>? supplyChoices,
        IReadOnlyList<TradePayrollResponsibilityLine>? responsibilities,
        CancellationToken ct,
        Guid operationId)
    {
        if (supplyChoices is not { Count: > 0 } && responsibilities is not { Count: > 0 })
        {
            return new SupplyChoiceApplication(0, []);
        }

        var rootItemIds = source.RootItems.Select(root => root.ItemId).ToHashSet();
        var rowsByItem = source.AcquisitionRows
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        WorkerAcquisitionRowProjection? FindRow(int itemId, bool requiresHq) =>
            rowsByItem.TryGetValue(itemId, out var rows)
                ? rows.FirstOrDefault(row => row.MustBeHq == requiresHq) ?? rows[0]
                : null;

        var warnings = new List<string>();
        var desired = new Dictionary<int, (AcquisitionSource Source, string Name)>();
        foreach (var choice in supplyChoices ?? [])
        {
            if (choice.AcquisitionSource is not { } requested || rootItemIds.Contains(choice.ItemId))
            {
                continue;
            }

            var row = FindRow(choice.ItemId, choice.RequiresHq);
            if (row is null)
            {
                warnings.Add($"Template supply choice for {choice.Name} no longer matches the plan.");
                continue;
            }
            if (!IsApplicableSource(row, requested))
            {
                warnings.Add($"{choice.Name} can no longer be acquired as {requested}; the plan default was kept.");
                continue;
            }

            desired[choice.ItemId] = (requested, row.ItemName);
        }

        var provided = (supplyChoices ?? [])
            .Where(choice => choice.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(choice => (choice.ItemId, choice.RequiresHq))
            .Concat(ToCompanyProvidedKeys(responsibilities))
            .Distinct();
        foreach (var (itemId, requiresHq) in provided)
        {
            if (rootItemIds.Contains(itemId) || desired.ContainsKey(itemId))
            {
                continue;
            }

            var row = FindRow(itemId, requiresHq);
            if (row is { HasChildren: true, Source: AcquisitionSource.Craft })
            {
                desired[itemId] = (AcquisitionSource.OnHand, row.ItemName);
            }
        }

        var applied = 0;
        foreach (var (itemId, (requested, name)) in desired)
        {
            if (rowsByItem[itemId].All(row => row.Source == requested))
            {
                continue;
            }

            try
            {
                await _worker.MutateAcquisitionAsync(
                    new WorkerAcquisitionMutation(itemId, requested, MustBeHq: null),
                    ct,
                    operationId);
                applied++;
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add($"Supply choice for {name} was not applied: {ex.Message}");
            }
        }

        return new SupplyChoiceApplication(applied, warnings);
    }

    private static bool IsApplicableSource(WorkerAcquisitionRowProjection row, AcquisitionSource source) =>
        source switch
        {
            AcquisitionSource.OnHand => true,
            AcquisitionSource.Craft => row.CanCraft,
            _ => row.AvailableSources.Contains(source)
        };

    /// <summary>
    /// Template supply choices become responsibility lines; explicit responsibilities (the
    /// order's own payroll draft) win for the same item.
    /// </summary>
    private static IReadOnlyList<TradePayrollResponsibilityLine>? MergeResponsibilities(
        IReadOnlyList<TradeCommissionTemplateSupplyChoice>? supplyChoices,
        IReadOnlyList<TradePayrollResponsibilityLine>? responsibilities)
    {
        if (supplyChoices is not { Count: > 0 })
        {
            return responsibilities;
        }

        var merged = new Dictionary<(int ItemId, bool RequiresHq), TradePayrollResponsibilityLine>();
        foreach (var choice in supplyChoices)
        {
            merged[(choice.ItemId, choice.RequiresHq)] = new TradePayrollResponsibilityLine(
                choice.ItemId,
                choice.RequiresHq,
                choice.Responsibility);
        }
        foreach (var line in responsibilities ?? [])
        {
            merged[(line.ItemId, line.RequiresHq)] = line;
        }

        return merged.Values.ToArray();
    }

    private static HashSet<(int ItemId, bool RequiresHq)> ToCompanyProvidedKeys(
        IReadOnlyList<TradePayrollResponsibilityLine>? responsibilities) =>
        (responsibilities ?? [])
            .Where(line => line.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(line => (line.ItemId, line.RequiresHq))
            .ToHashSet();

    /// <summary>
    /// The market items this order's pricing needs, or null (every candidate) when the
    /// projection carries no occurrence ancestry to prune with.
    /// </summary>
    private static IReadOnlyCollection<int>? ComputeTargetItemIds(
        WorkerTradeProjection source,
        IReadOnlyList<TradePayrollResponsibilityLine>? materialResponsibilities)
    {
        if (source.PlanOccurrences is not { Count: > 0 } occurrences)
        {
            return null;
        }

        return TradePlanMarketScopeFilter.ComputeTradeRequiredMarketItemIds(
            source.RootItems.Select(root => (root.ItemId, root.MustBeHq)),
            occurrences
                .Select(occurrence => new TradePlanMarketScopeOccurrence(
                    occurrence.NodeId,
                    occurrence.ParentNodeId,
                    occurrence.ItemId,
                    occurrence.MustBeHq,
                    occurrence.Source,
                    occurrence.IsSuppressed,
                    occurrence.SourceReason))
                .ToArray(),
            source.AcquisitionRows
                .Where(row => row.CanBuyFromMarket)
                .Select(row => row.ItemId)
                .ToHashSet(),
            materialResponsibilities);
    }

    private static bool IsPublishedCommission(TradeOrder order) =>
        order.CompanyCommission is { } commission &&
        commission.PublicMetadata.ViewState != CompanyCommissionPublicViewState.Draft;

    private WorkerPlanOwnershipFence? CaptureActivePlanFence(string? expectedPlanId = null)
    {
        var shell = _projections.Shell;
        return !string.IsNullOrWhiteSpace(shell.PlanId) &&
               (string.IsNullOrWhiteSpace(expectedPlanId) ||
                string.Equals(shell.PlanId, expectedPlanId, StringComparison.Ordinal))
            ? new WorkerPlanOwnershipFence(shell.PlanId, shell.Revision)
            : null;
    }

    private async Task<bool> ActivePlanMatchesRootsAsync(
        IReadOnlyList<TradeOrderRootItemSnapshot> roots,
        CancellationToken cancellationToken)
    {
        var active = await _worker.GetTradeProjectionAsync(
            cancellationToken: cancellationToken);
        if (active is not { HasPlan: true } || active.RootItems.Count == 0)
        {
            return false;
        }

        static string Key(int itemId, int quantity, bool mustBeHq) =>
            $"{itemId}:{quantity}:{mustBeHq}";
        var expected = roots
            .Where(item => item.Quantity > 0)
            .Select(item => Key(item.ItemId, item.Quantity, item.MustBeHq))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var actual = active.RootItems
            .Where(item => item.Quantity > 0)
            .Select(item => Key(item.ItemId, item.Quantity, item.MustBeHq))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        return expected.Length > 0 && expected.SequenceEqual(actual, StringComparer.Ordinal);
    }

    private async Task<(TradeCompanyPricingScope? Scope, decimal LaborGilPerSynth)> LoadPricingScopeAsync(
        TradeOrder order, CancellationToken cancellationToken)
    {
        // Same hosted-then-local resolution the Orders page uses, so an unhosted company
        // still prices from its own scope while profile sync is connected. The labor rate
        // comes from the same resolution: the order's payment override, else company policy.
        var resolved = await new TradeCompany.TradeWorkspaceProfileResolver(_companies, _companyHub)
            .ResolveAsync(order.CompanyProfileId, cancellationToken);
        var paymentPolicy = TradeOrderWorkflow.ResolvePaymentPolicy(order, resolved.Profile?.PaymentPolicy);
        return (resolved.Profile?.CommissionPricingScope, paymentPolicy.LaborGilPerSynth);
    }

    /// <summary>
    /// The rate to stamp on the order's plan, or null to leave the plan's own rate alone. A
    /// published commission keeps the rate its plan was priced with, so editing one route in a
    /// terms revision never re-decides the other routes at a newer rate; a plan rebuilt from
    /// scratch has no rate yet and takes the order's.
    /// </summary>
    private static decimal? GetPlanLaborRateStamp(TradeOrder order, decimal laborGilPerSynth, bool planRebuilt)
    {
        var published = order.CompanyCommission is { } commission &&
            commission.PublicMetadata.ViewState != CompanyCommissionPublicViewState.Draft;
        return !published || planRebuilt ? laborGilPerSynth : null;
    }

    private static TradeOrderPricingWorkflowResult PricingScopeRequired() =>
        TradeOrderPricingWorkflowResult.Noop(
            TradeOrderPricingWorkflowStatus.CompanyPricingScopeRequired,
            "The company needs a commission pricing region. Owners and Operators can configure it in Settings > Company administration > Commission pricing.");

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

        public static MarketRefreshRequest ReconcilePublished { get; } =
            new(forceRefresh: false) { Reconcile = true };

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
        public bool Reconcile { get; private init; }
    }
}
