using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record MarketAnalysisSubsetRefreshWorkflowRequest(
    IReadOnlyCollection<int> ItemIds,
    Func<bool>? IsCurrentOperation = null,
    MarketAnalysisExecutionOptions? ExecutionOptions = null)
{
    public MarketFetchScope Scope { get; init; } = MarketFetchScope.SelectedDataCenter;

    public Func<bool>? IsCurrentConfiguration { get; init; }

    public string? TargetDataCenter { get; init; }

    public string? TargetWorldName { get; init; }

    public MarketWorldEvidenceSnapshot? ObservedEvidence { get; init; }
}

public sealed record MarketAnalysisSubsetRefreshWorkflowResult(
    MarketAnalysisSubsetRefreshStatus Status,
    IReadOnlyList<int> RequestedItemIds,
    IReadOnlyList<int> RefreshedItemIds,
    IReadOnlyDictionary<int, string> RefreshedItemNamesById,
    IReadOnlyList<int> MissingCandidateItemIds,
    IReadOnlyList<int> NoDataItemIds,
    int AnalyzedCount,
    int FetchedCount,
    bool Published)
{
    public static MarketAnalysisSubsetRefreshWorkflowResult Noop(
        MarketAnalysisSubsetRefreshStatus status,
        IReadOnlyList<int>? requestedItemIds = null,
        IReadOnlyList<int>? missingCandidateItemIds = null,
        IReadOnlyList<int>? noDataItemIds = null)
    {
        return new MarketAnalysisSubsetRefreshWorkflowResult(
            status,
            requestedItemIds ?? [],
            RefreshedItemIds: [],
            RefreshedItemNamesById: new Dictionary<int, string>(),
            missingCandidateItemIds ?? [],
            noDataItemIds ?? [],
            AnalyzedCount: 0,
            FetchedCount: 0,
            Published: false);
    }
}

public enum MarketAnalysisSubsetRefreshStatus
{
    NoPlan,
    NotFound,
    NoData,
    Refreshed,
    StaleDecision,
    StaleConfiguration,
    StalePlan,
    Superseded
}

public sealed class MarketAnalysisSubsetRefreshService
{
    private readonly AppState _appState;
    private readonly IMarketEvidenceReconciliationService _marketEvidenceReconciliation;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly WebPlanPersistenceService _planPersistence;
    private readonly IndexedDbService _indexedDb;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;

    public MarketAnalysisSubsetRefreshService(
        AppState appState,
        IMarketEvidenceReconciliationService marketEvidenceReconciliation,
        MarketShoppingService marketShoppingService,
        WebPlanPersistenceService planPersistence,
        IndexedDbService indexedDb,
        IRecipeLayerWorkflowService recipeLayerWorkflow)
    {
        _appState = appState;
        _marketEvidenceReconciliation = marketEvidenceReconciliation;
        _marketShoppingService = marketShoppingService;
        _planPersistence = planPersistence;
        _indexedDb = indexedDb;
        _recipeLayerWorkflow = recipeLayerWorkflow;
    }

    public async Task<MarketAnalysisSubsetRefreshWorkflowResult> RefreshMarketDataAsync(
        MarketAnalysisSubsetRefreshWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestedItemIds = request.ItemIds
            .Where(itemId => itemId > 0)
            .Distinct()
            .OrderBy(itemId => itemId)
            .ToList();
        if (requestedItemIds.Count == 0)
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.NoData,
                requestedItemIds,
                noDataItemIds: requestedItemIds);
        }

        var plan = _appState.CurrentPlan;
        var planSessionVersion = _appState.PlanSessionVersion;
        if (plan == null)
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.NoPlan,
                requestedItemIds);
        }

        var requestedSet = requestedItemIds.ToHashSet();
        var candidateResult = await _recipeLayerWorkflow.BuildCurrentMarketAnalysisCandidateResultAsync(plan, ct);
        if (candidateResult == null || !_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.StalePlan,
                requestedItemIds);
        }

        var candidates = candidateResult.Candidates
            .Where(item => requestedSet.Contains(item.ItemId))
            .GroupBy(item => item.ItemId)
            .Select(group => group.First())
            .OrderBy(item => item.ItemId)
            .ToList();
        var candidateIds = candidates.Select(item => item.ItemId).ToHashSet();
        var missingCandidateItemIds = requestedItemIds
            .Where(itemId => !candidateIds.Contains(itemId))
            .ToList();
        if (candidates.Count == 0)
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.NotFound,
                requestedItemIds,
                missingCandidateItemIds);
        }

        var capturedPlanId = _appState.CurrentPlanId;
        var capturedDecisionVersion = _appState.CurrentVersions.PlanDecisionVersion;
        var capturedMarketAnalysisVersion = _appState.CurrentVersions.MarketAnalysisVersion;
        var requestSnapshot = new MarketAnalysisSubsetRefreshRequestSnapshot(
            request.Scope,
            _appState.SelectedDataCenter,
            _appState.SelectedRegion,
            _appState.MarketAnalysisLens);
        var guardedProgress = progress == null
            ? null
            : new Progress<string>(message =>
            {
                if (IsCurrentConfiguration(plan, planSessionVersion, capturedMarketAnalysisVersion, requestSnapshot, request) &&
                    (request.IsCurrentOperation?.Invoke() ?? true))
                {
                    progress.Report(message);
                }
            });

        IReadOnlyList<MarketItemAnalysis> reconciledAnalyses;
        IReadOnlyList<DetailedShoppingPlan> reconciledShoppingPlans;
        int fetchedCount;
        if (!string.IsNullOrWhiteSpace(request.TargetWorldName) &&
            !string.IsNullOrWhiteSpace(request.TargetDataCenter))
        {
            if (candidates.Count != 1)
            {
                throw new InvalidOperationException("World reconciliation requires exactly one market item.");
            }

            var worldResult = await _marketEvidenceReconciliation.ReconcileWorldAsync(
                new MarketWorldEvidenceReconciliationRequest
                {
                    Item = candidates[0],
                    DataCenter = request.TargetDataCenter,
                    WorldName = request.TargetWorldName,
                    ObservedEvidence = request.ObservedEvidence,
                    Scope = request.Scope,
                    SelectedDataCenter = _appState.SelectedDataCenter,
                    SelectedRegion = _appState.SelectedRegion,
                    RecommendationMode = RecommendationMode.MinimizeTotalCost,
                    Lens = _appState.MarketAnalysisLens,
                    ExpectedWorldsByDataCenter = _appState.GetExpectedMarketWorlds(request.Scope)
                },
                guardedProgress,
                ct,
                executionOptions: request.ExecutionOptions);
            reconciledAnalyses = [worldResult.Analysis];
            reconciledShoppingPlans = [worldResult.ShoppingPlan];
            fetchedCount = request.ObservedEvidence == null ? 1 : 0;
        }
        else
        {
            var reconciliation = await _marketEvidenceReconciliation.ReconcileAsync(
                new MarketEvidenceReconciliationRequest
                {
                    Items = candidates,
                    PublishedAnalyses = _appState.MarketItemAnalyses
                        .Where(analysis => candidateIds.Contains(analysis.ItemId))
                        .ToList(),
                    PublishedShoppingPlans = _appState.ShoppingPlans
                        .Where(plan => candidateIds.Contains(plan.ItemId))
                        .ToList(),
                    Scope = request.Scope,
                    SelectedDataCenter = _appState.SelectedDataCenter,
                    SelectedRegion = _appState.SelectedRegion,
                    RecommendationMode = RecommendationMode.MinimizeTotalCost,
                    Lens = _appState.MarketAnalysisLens,
                    ExpectedWorldsByDataCenter = _appState.GetExpectedMarketWorlds(request.Scope),
                    Policy = MarketEvidenceReconciliationPolicy.ForcedRefresh()
                },
                guardedProgress,
                ct,
                executionOptions: request.ExecutionOptions);
            reconciledAnalyses = reconciliation.Analyses;
            reconciledShoppingPlans = reconciliation.ShoppingPlans;
            fetchedCount = reconciliation.FetchedCount;
        }
        ct.ThrowIfCancellationRequested();

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.StalePlan,
                requestedItemIds);
        }

        if (_appState.CurrentVersions.PlanDecisionVersion != capturedDecisionVersion)
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.StaleDecision,
                requestedItemIds);
        }

        if (!IsCurrentConfiguration(plan, planSessionVersion, capturedMarketAnalysisVersion, requestSnapshot, request))
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.StaleConfiguration,
                requestedItemIds);
        }

        if (request.IsCurrentOperation?.Invoke() == false)
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.Superseded,
                requestedItemIds);
        }

        var analysisByItemId = reconciledAnalyses
            .Where(analysis => candidateIds.Contains(analysis.ItemId))
            .GroupBy(analysis => analysis.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        var shoppingPlanByItemId = reconciledShoppingPlans
            .Where(shoppingPlan => candidateIds.Contains(shoppingPlan.ItemId))
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        var refreshedItemIds = candidateIds
            .Where(itemId => analysisByItemId.ContainsKey(itemId) && shoppingPlanByItemId.ContainsKey(itemId))
            .OrderBy(itemId => itemId)
            .ToList();
        var noDataItemIds = candidateIds
            .Where(itemId => !refreshedItemIds.Contains(itemId))
            .Concat(missingCandidateItemIds)
            .Distinct()
            .OrderBy(itemId => itemId)
            .ToList();
        if (refreshedItemIds.Count == 0)
        {
            return new MarketAnalysisSubsetRefreshWorkflowResult(
                MarketAnalysisSubsetRefreshStatus.NoData,
                requestedItemIds,
                RefreshedItemIds: [],
                RefreshedItemNamesById: new Dictionary<int, string>(),
                missingCandidateItemIds,
                noDataItemIds,
                AnalyzedCount: 0,
                fetchedCount,
                Published: false);
        }

        var refreshedAnalyses = refreshedItemIds.Select(itemId => analysisByItemId[itemId]).ToList();
        var refreshedItemNamesById = refreshedAnalyses.ToDictionary(
            analysis => analysis.ItemId,
            analysis => analysis.Name);
        var refreshedPlans = refreshedItemIds.Select(itemId => shoppingPlanByItemId[itemId]).ToList();
        _marketShoppingService.ApplyVendorPurchaseOverrides(plan, refreshedPlans);
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.StalePlan,
                requestedItemIds);
        }

        _appState.ReplaceMarketAnalysisItems(refreshedAnalyses, refreshedPlans);

        if (!string.IsNullOrEmpty(capturedPlanId) &&
            _appState.IsCurrentPlanSession(plan, planSessionVersion) &&
            string.Equals(_appState.CurrentPlanId, capturedPlanId, StringComparison.Ordinal))
        {
            await _planPersistence.SaveMarketAnalysisAsync(
                capturedPlanId,
                _appState.ShoppingPlans,
                _appState.MarketItemAnalyses,
                _appState.RecommendationMode,
                _appState.MarketAnalysisLens,
                _appState.MarketAnalysisRecipeBasis,
                _appState.PublishedMarketAnalysisScope,
                _appState.MarketIntelligence);
        }

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.StalePlan,
                requestedItemIds);
        }

        if (request.IsCurrentOperation?.Invoke() == false)
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.Superseded,
                requestedItemIds);
        }

        await _indexedDb.AutoSaveStateAsync(_appState);
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.StalePlan,
                requestedItemIds);
        }

        if (request.IsCurrentOperation?.Invoke() == false)
        {
            return MarketAnalysisSubsetRefreshWorkflowResult.Noop(
                MarketAnalysisSubsetRefreshStatus.Superseded,
                requestedItemIds);
        }

        return new MarketAnalysisSubsetRefreshWorkflowResult(
            MarketAnalysisSubsetRefreshStatus.Refreshed,
            requestedItemIds,
            refreshedItemIds,
            refreshedItemNamesById,
            missingCandidateItemIds,
            noDataItemIds,
            refreshedAnalyses.Count,
            fetchedCount,
            Published: true);
    }

    private bool IsCurrentConfiguration(
        CraftingPlan plan,
        long planSessionVersion,
        long capturedMarketAnalysisVersion,
        MarketAnalysisSubsetRefreshRequestSnapshot requestSnapshot,
        MarketAnalysisSubsetRefreshWorkflowRequest request)
    {
        return _appState.CurrentVersions.MarketAnalysisVersion == capturedMarketAnalysisVersion &&
               IsCurrentRequest(requestSnapshot) &&
               _appState.IsCurrentPlanSession(plan, planSessionVersion) &&
               (request.IsCurrentConfiguration?.Invoke() ?? true);
    }

    private bool IsCurrentRequest(MarketAnalysisSubsetRefreshRequestSnapshot snapshot)
    {
        return string.Equals(snapshot.SelectedDataCenter, _appState.SelectedDataCenter, StringComparison.Ordinal) &&
               string.Equals(snapshot.SelectedRegion, _appState.SelectedRegion, StringComparison.Ordinal) &&
               snapshot.Lens == _appState.MarketAnalysisLens;
    }

    private sealed record MarketAnalysisSubsetRefreshRequestSnapshot(
        MarketFetchScope Scope,
        string SelectedDataCenter,
        string SelectedRegion,
        MarketAcquisitionLens Lens);
}
