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
        warnings.AddRange(source.Warnings);
        var materials = TradeOrderMaterialEvidenceMapper.ToMaterialSnapshots(
            source.MaterialLines);
        // Company-provided materials are never bought by the crafter and may carry no market
        // evidence; only the crafter's own active materials decide completeness.
        var companyProvided = ToCompanyProvidedKeys(materialResponsibilities);
        var crafterActiveItems = source.ActiveProcurementItems
            .Where(item => !companyProvided.Contains((item.ItemId, item.RequiresHq)))
            .ToArray();
        var pricedCount = materials
            .Where(material => !companyProvided.Contains((material.ItemId, material.RequiresHq)))
            .Count(material => TradeOrderWorkflow.IsResolvedMaterialEvidence(
                material.UnitCost,
                material.TotalCost,
                material.EvidenceSource));
        if (pricedCount < crafterActiveItems.Length)
        {
            warnings.Add(
                $"Order supply evidence is incomplete: {pricedCount:N0} of {crafterActiveItems.Length:N0} active materials are resolved.");
        }

        order.SourceSnapshot.SourcePlanId = order.CraftPlanId;
        order.SourceSnapshot.SourcePlanName =
            order.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(order);
        order.SourceSnapshot.CostBasis = CommissionCostBasis.ProcurementRoute;
        order.SourceSnapshot.MarketFetchScope = source.MarketFetchScope;
        order.SourceSnapshot.Region = source.SelectedRegion;
        order.SourceSnapshot.DataCenter = source.SelectedDataCenter;
        order.SourceSnapshot.RequestedDataCenters = source.RequestedDataCenters.ToArray();
        order.SourceSnapshot.PlanSessionVersion = source.PlanSessionVersion;
        order.SourceSnapshot.MarketAnalysisVersion = source.MarketAnalysisVersion;
        order.SourceSnapshot.Materials = materials;
        order.SourceSnapshot.CraftLabor = source.CraftLabor;
        order.SourceSnapshot.MaterialQuote = source.MaterialQuote is null
            ? null
            : source.MaterialQuote with
            {
                CompanyProfileId = order.CompanyProfileId,
                SourcePlanId = source.PlanId,
                PlanSessionVersion = source.PlanSessionVersion,
                MarketAnalysisVersion = source.MarketAnalysisVersion
            };
        order.SourceSnapshot.MaterialQuoteFailureReason =
            source.MaterialQuote is null ? source.MaterialQuoteFailureReason : null;
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

        var sealFailure = await SealLinkedPlanAsync(order, operation, workerOperation);
        if (sealFailure is not null)
        {
            return sealFailure;
        }

        var complete = source.MaterialQuote != null &&
            pricedCount == crafterActiveItems.Length &&
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
            crafterActiveItems.Length,
            CaptureActivePlanFence(order.CraftPlanId));
    }

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
                    occurrence.IsSuppressed))
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
