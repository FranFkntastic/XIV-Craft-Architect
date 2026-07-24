using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Main-thread command facade for the Worker-owned durable session.
/// </summary>
public sealed class WorkerSessionCoordinator : IAsyncDisposable
{
    private readonly CraftArchitectEngineHost _engineHost;
    private readonly WorkerProjectionStore _projections;
    private readonly ExperimentalProcurementEngineCapability _capability;
    private readonly IMarketEvidenceReconciliationService _marketEvidenceReconciliation;
    private readonly IMarketCacheService _marketCache;
    private readonly IUniversalisService _universalis;

    public WorkerSessionCoordinator(
        CraftArchitectEngineHost engineHost,
        WorkerProjectionStore projections,
        ExperimentalProcurementEngineCapability capability,
        IMarketEvidenceReconciliationService marketEvidenceReconciliation,
        IMarketCacheService marketCache,
        IUniversalisService universalis)
    {
        _engineHost = engineHost;
        _projections = projections;
        _capability = capability;
        _marketEvidenceReconciliation = marketEvidenceReconciliation;
        _marketCache = marketCache;
        _universalis = universalis;
    }

    public bool IsEnabled => _capability.IsExecutionEnabled;

    public async Task<WorkerSessionShellProjection?> BootstrapAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.BootstrapSessionAsync(cancellationToken);
        if (!result.Accepted || !_projections.TryPublish(result))
        {
            throw new InvalidOperationException(
                result.Message ?? "The Worker did not publish a valid startup projection.");
        }
        return _projections.Shell;
    }

    public async Task<StoredPlan?> ExportStoredPlanAsync(
        string planId,
        string planName,
        bool includeSourcePlanIdentity = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.ExportSessionAsync(
            _projections.Shell.Revision,
            new WorkerSessionExportRequest(
                planId,
                planName,
                includeSourcePlanIdentity,
                IncludeLegacyMarketAnalysisFields: true),
            cancellationToken);
        var export = result.Projection.Deserialize<WorkerSessionExportProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (!result.Accepted || export is null)
        {
            throw CreateConflict(result);
        }

        return export.StoredPlan;
    }

    public async Task ReplaceStoredPlanAsync(
        StoredPlan storedPlan,
        bool trackStoredPlanIdentity,
        CancellationToken cancellationToken = default) =>
        await ReplaceStoredPlanCoreAsync(
            storedPlan,
            trackStoredPlanIdentity,
            cancellationToken);

    public async Task ClearSessionAsync(
        CancellationToken cancellationToken = default) =>
        await ReplaceStoredPlanCoreAsync(
            storedPlan: null,
            trackStoredPlanIdentity: false,
            cancellationToken);

    private async Task ReplaceStoredPlanCoreAsync(
        StoredPlan? storedPlan,
        bool trackStoredPlanIdentity,
        CancellationToken cancellationToken)
    {
        var result = await _engineHost.ReplaceSessionAsync(
            _projections.Shell.Revision,
            storedPlan,
            trackStoredPlanIdentity,
            cancellationToken);
        if (!result.Accepted || !_projections.TryPublish(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        var recipe = await _engineHost.GetRecipeProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublishRecipe(recipe);
        var market = await _engineHost.GetMarketProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublishMarket(market);
        var procurement = await _engineHost.GetProcurementProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublishProcurement(procurement);
    }

    public async Task<WorkerRecipePlannerProjection?> GetRecipeProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetRecipeProjectionAsync(
            _projections.Shell.Revision,
            cancellationToken);
        if (!_projections.TryPublishRecipe(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            return _projections.Recipe;
        }
        return _projections.Recipe;
    }

    public async Task<WorkerAcquisitionProjection?> GetAcquisitionProjectionAsync(
        string filter,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetAcquisitionProjectionAsync(
            _projections.Shell.Revision,
            filter,
            cancellationToken);
        if (!_projections.TryPublishAcquisition(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            return _projections.Acquisition;
        }
        return _projections.Acquisition;
    }

    public async Task<WorkerMarketProjection?> GetMarketProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetMarketProjectionAsync(
            _projections.Shell.Revision,
            cancellationToken);
        if (!_projections.TryPublishMarket(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            return _projections.Market;
        }
        return _projections.Market;
    }

    public async Task<WorkerProcurementProjection?> GetProcurementProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetProcurementProjectionAsync(
            _projections.Shell.Revision,
            cancellationToken);
        if (!_projections.TryPublishProcurement(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            return _projections.Procurement;
        }
        return _projections.Procurement;
    }

    public async Task<WorkerRecipePlannerProjection> MutateProjectItemsAsync(
        WorkerProjectItemsMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.MutateProjectItemsAsync(
            _projections.Shell.Revision,
            mutation,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerRecipePlannerProjection>(
                result,
                out var projection) ||
            projection is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return projection;
    }

    public async Task<WorkerRecipeBuildOutcome> BuildRecipeAsync(
        WorkerRecipeBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.BuildRecipeAsync(
            _projections.Shell.Revision,
            request,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerRecipeBuildOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return outcome;
    }

    public async Task<WorkerRecipePlannerProjection> MutateAcquisitionAsync(
        WorkerAcquisitionMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.MutateAcquisitionAsync(
            _projections.Shell.Revision,
            mutation,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerRecipePlannerProjection>(
                result,
                out var projection) ||
            projection is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return projection;
    }

    public async Task<WorkerAcquisitionProjection> MutateAcquisitionAndProjectAsync(
        WorkerAcquisitionMutation mutation,
        string filter,
        CancellationToken cancellationToken = default)
    {
        await MutateAcquisitionAsync(mutation, cancellationToken);
        return await GetAcquisitionProjectionAsync(filter, cancellationToken)
            ?? throw new InvalidOperationException("The Worker did not publish acquisition evaluation.");
    }

    public async Task<WorkerMarketAnalysisOutcome> RunMarketAnalysisAsync(
        WorkerMarketAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        var market = _projections.Market ??
            await GetMarketProjectionAsync(cancellationToken) ??
            throw new InvalidOperationException(
                "The Worker did not publish the active market-analysis candidates.");
        if (market.CandidateItems.Count == 0)
        {
            throw new InvalidOperationException(
                "The active plan does not contain any market-analysis candidates.");
        }

        var expectedWorlds = await GetExpectedWorldsAsync(
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion,
            cancellationToken);

        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion);
        var evidenceRequests = market.CandidateItems
            .SelectMany(item => dataCenters.Select(dataCenter => (item.ItemId, dataCenter)))
            .ToList();
        var fetchedCount = request.ForceRefreshData
            ? await _marketCache.RefreshRequestedAsync(
                evidenceRequests,
                ct: cancellationToken)
            : await _marketCache.EnsurePopulatedAsync(
                evidenceRequests,
                ct: cancellationToken);

        // Raw listings are intentionally read and released one item at a time.
        // A regional Crasher plan spans hundreds of item/data-center pairs; loading
        // all of those payloads into WASM at once can exhaust the browser heap.
        var analyses = new List<MarketItemAnalysis>(market.CandidateItems.Count);
        var shoppingPlans = new List<DetailedShoppingPlan>(market.CandidateItems.Count);
        var unavailableItemIds = new HashSet<int>();
        foreach (var item in market.CandidateItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reconciliation = await _marketEvidenceReconciliation.ReconcileAsync(
                new MarketEvidenceReconciliationRequest
                {
                    Items = [item],
                    PublishedAnalyses = request.ForceRefreshData
                        ? []
                        : market.ItemAnalyses
                            .Where(candidate => candidate.ItemId == item.ItemId)
                            .ToArray(),
                    PublishedShoppingPlans = request.ForceRefreshData
                        ? []
                        : market.ShoppingPlans
                            .Where(candidate => candidate.ItemId == item.ItemId)
                            .ToArray(),
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    Lens = request.Lens,
                    ExpectedWorldsByDataCenter = expectedWorlds
                },
                ct: cancellationToken,
                executionOptions: MarketAnalysisExecutionOptions.Interactive);
            analyses.AddRange(reconciliation.Analyses);
            shoppingPlans.AddRange(reconciliation.ShoppingPlans);
            unavailableItemIds.UnionWith(reconciliation.UnavailableItemIds);
        }

        if (shoppingPlans.Count == 0)
        {
            throw new InvalidOperationException(
                $"The market source returned no usable evidence for {market.CandidateItems.Count:N0} items.");
        }

        var result = await _engineHost.PublishMarketEvidenceAsync(
            _projections.Shell.Revision,
            new WorkerMarketEvidencePublicationRequest(
                request.Scope,
                request.SelectedDataCenter,
                request.SelectedRegion,
                request.Lens,
                analyses,
                shoppingPlans,
                unavailableItemIds,
                fetchedCount),
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerMarketAnalysisOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return outcome;
    }

    public async Task<WorkerMarketProjection> ApplyMarketLensAsync(
        MarketAcquisitionLens lens,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.ApplyMarketLensAsync(
            _projections.Shell.Revision,
            new WorkerMarketLensMutation(lens),
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerMarketProjection>(
                result,
                out var market) ||
            market is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return market;
    }

    public async Task<WorkerMarketItemRefreshOutcome> RefreshMarketItemAsync(
        WorkerMarketItemRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        var market = _projections.Market ??
            await GetMarketProjectionAsync(cancellationToken) ??
            throw new InvalidOperationException(
                "The Worker did not publish the active market-analysis candidates.");
        var item = market.CandidateItems.FirstOrDefault(candidate =>
            candidate.ItemId == request.ItemId)
            ?? throw new InvalidOperationException(
                $"{request.ItemName} is no longer part of the active market analysis.");
        var expectedWorlds = await GetExpectedWorldsAsync(
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion,
            cancellationToken);

        MarketItemAnalysis analysis;
        DetailedShoppingPlan shoppingPlan;
        if (!string.IsNullOrWhiteSpace(request.TargetDataCenter) &&
            !string.IsNullOrWhiteSpace(request.TargetWorldName))
        {
            var worldResult = await _marketEvidenceReconciliation.ReconcileWorldAsync(
                new MarketWorldEvidenceReconciliationRequest
                {
                    Item = item,
                    DataCenter = request.TargetDataCenter,
                    WorldName = request.TargetWorldName,
                    ObservedEvidence = request.ObservedEvidence,
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    Lens = request.Lens,
                    ExpectedWorldsByDataCenter = expectedWorlds
                },
                ct: cancellationToken,
                executionOptions: MarketAnalysisExecutionOptions.Interactive);
            analysis = worldResult.Analysis;
            shoppingPlan = worldResult.ShoppingPlan;
        }
        else
        {
            var reconciliation = await _marketEvidenceReconciliation.ReconcileAsync(
                new MarketEvidenceReconciliationRequest
                {
                    Items = [item],
                    PublishedAnalyses = market.ItemAnalyses
                        .Where(candidate => candidate.ItemId == request.ItemId)
                        .ToArray(),
                    PublishedShoppingPlans = market.ShoppingPlans
                        .Where(candidate => candidate.ItemId == request.ItemId)
                        .ToArray(),
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    Lens = request.Lens,
                    ExpectedWorldsByDataCenter = expectedWorlds,
                    Policy = MarketEvidenceReconciliationPolicy.ForcedRefresh()
                },
                ct: cancellationToken,
                executionOptions: MarketAnalysisExecutionOptions.Interactive);
            analysis = reconciliation.Analyses.SingleOrDefault()
                ?? throw new InvalidOperationException(
                    $"The market source returned no usable evidence for {request.ItemName}.");
            shoppingPlan = reconciliation.ShoppingPlans.SingleOrDefault()
                ?? throw new InvalidOperationException(
                    $"The market source returned no purchase plan for {request.ItemName}.");
        }

        var result = await _engineHost.PublishMarketItemEvidenceAsync(
            _projections.Shell.Revision,
            new WorkerMarketItemEvidencePublicationRequest(
                request.ItemId,
                request.Scope,
                request.SelectedDataCenter,
                request.SelectedRegion,
                request.Lens,
                analysis,
                shoppingPlan),
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerMarketItemRefreshOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return outcome;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        GetExpectedWorldsAsync(
            MarketFetchScope scope,
            string selectedDataCenter,
            string selectedRegion,
            CancellationToken cancellationToken)
    {
        var worldData = await _universalis.GetWorldDataAsync(cancellationToken);
        return MarketFetchScopeResolver
            .GetDataCenters(scope, selectedDataCenter, selectedRegion)
            .Where(dataCenter => worldData.DataCenterToWorlds.ContainsKey(dataCenter))
            .ToDictionary(
                dataCenter => dataCenter,
                dataCenter =>
                    (IReadOnlyList<string>)worldData.DataCenterToWorlds[dataCenter],
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task<WorkerProcurementOutcome> RunProcurementAsync(
        WorkerProcurementRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.RunProcurementAsync(
            _projections.Shell.Revision,
            request,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerProcurementOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return outcome;
    }

    public async Task<WorkerProcurementProjection> SelectProcurementToleranceAsync(
        int travelTolerance,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.SelectProcurementToleranceAsync(
            _projections.Shell.Revision,
            travelTolerance,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerProcurementProjection>(
                result,
                out var procurement) ||
            procurement is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        return procurement;
    }

    private async Task RefreshAfterConflictAsync(
        WorkerSessionResultEnvelope result,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(result.RejectionCode, "stale-revision", StringComparison.Ordinal))
        {
            return;
        }

        var shell = await _engineHost.GetShellProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublish(shell);
        var recipe = await _engineHost.GetRecipeProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublishRecipe(recipe);
    }

    private static InvalidOperationException CreateConflict(
        WorkerSessionResultEnvelope result) =>
        new(
            result.Message ??
            "The plan changed before this edit was accepted. The current Worker projection has been restored.");

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
