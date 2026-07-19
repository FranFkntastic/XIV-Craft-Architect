using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record MarketAnalysisWorkflowRequest(
    bool ForceRefreshData,
    bool PreserveExistingEvidence = false);

public sealed record MarketAnalysisWorkflowResult(
    bool Published,
    int AnalyzedCount,
    int ChangedDecisionCount,
    int FetchedCount);

public sealed class MarketAnalysisWorkflowService
{
    private readonly AppState _appState;
    private readonly IMarketEvidenceReconciliationService _marketEvidenceReconciliation;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly IMarketPriceLadderAnalysisService _marketPriceLadderAnalysis;
    private readonly IMarketAnalysisPersistence _marketAnalysisPersistence;
    private readonly IndexedDbService _indexedDb;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly ILogger<MarketAnalysisWorkflowService> _logger;

    public MarketAnalysisWorkflowService(
        AppState appState,
        IMarketEvidenceReconciliationService marketEvidenceReconciliation,
        MarketShoppingService marketShoppingService,
        IMarketPriceLadderAnalysisService marketPriceLadderAnalysis,
        IMarketAnalysisPersistence marketAnalysisPersistence,
        IndexedDbService indexedDb,
        IRecipeLayerWorkflowService recipeLayerWorkflow,
        ILogger<MarketAnalysisWorkflowService> logger)
    {
        _appState = appState;
        _marketEvidenceReconciliation = marketEvidenceReconciliation;
        _marketShoppingService = marketShoppingService;
        _marketPriceLadderAnalysis = marketPriceLadderAnalysis;
        _marketAnalysisPersistence = marketAnalysisPersistence;
        _indexedDb = indexedDb;
        _recipeLayerWorkflow = recipeLayerWorkflow;
        _logger = logger;
    }

    public async Task<MarketAnalysisWorkflowResult> RunAnalysisAsync(
        MarketAnalysisWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        var plan = _appState.CurrentPlan;
        var planSessionVersion = _appState.PlanSessionVersion;
        var planDecisionVersion = _appState.CurrentVersions.PlanDecisionVersion;
        var planId = _appState.CurrentPlanId;
        var candidateResult = await _recipeLayerWorkflow.BuildCurrentMarketAnalysisCandidateResultAsync(plan, ct);
        var materials = candidateResult?.Candidates.ToList() ?? [];
        var recipeBasis = candidateResult?.RecipeBasis;
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        if (_appState.CurrentVersions.PlanDecisionVersion != planDecisionVersion)
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        if (plan == null || materials.Count == 0 || string.IsNullOrWhiteSpace(_appState.SelectedDataCenter))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        if (!request.PreserveExistingEvidence)
        {
            _appState.ClearMarketAnalysisState();
        }

        var scope = _appState.SearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var requestSnapshot = new MarketAnalysisRequestSnapshot(
            scope,
            _appState.SelectedDataCenter,
            _appState.SelectedRegion,
            _appState.MarketAnalysisLens);
        var reconciliation = await _marketEvidenceReconciliation.ReconcileAsync(
            new MarketEvidenceReconciliationRequest
            {
                Items = materials,
                Scope = scope,
                SelectedDataCenter = _appState.SelectedDataCenter,
                SelectedRegion = _appState.SelectedRegion,
                RecommendationMode = RecommendationMode.MinimizeTotalCost,
                Lens = _appState.MarketAnalysisLens,
                ExpectedWorldsByDataCenter = _appState.GetExpectedMarketWorlds(scope),
                Policy = request.ForceRefreshData
                    ? MarketEvidenceReconciliationPolicy.ForcedRefresh()
                    : new MarketEvidenceReconciliationPolicy()
            },
            progress,
            ct,
            executionOptions: executionOptions);
        ct.ThrowIfCancellationRequested();

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, reconciliation.FetchedCount);
        }


        if (_appState.CurrentVersions.PlanDecisionVersion != planDecisionVersion)
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, reconciliation.FetchedCount);
        }

        if (!IsCurrentRequest(requestSnapshot))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, reconciliation.FetchedCount);
        }

        var published = await PublishMarketAnalysisAsync(
            plan,
            planSessionVersion,
            planDecisionVersion,
            planId,
            reconciliation.Analyses,
            reconciliation.ShoppingPlans.ToList(),
            recipeBasis,
            _appState.CreateCurrentMarketAnalysisScopeSnapshot(),
            ct);
        if (published == null)
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, reconciliation.FetchedCount);
        }

        return new MarketAnalysisWorkflowResult(
            true,
            _appState.ShoppingPlans.Count,
            published.ChangedDecisionCount,
            reconciliation.FetchedCount);
    }

    public async Task<MarketAnalysisWorkflowResult> ApplyLensAsync(
        MarketAcquisitionLens lens,
        CancellationToken ct = default)
    {
        var changed = _appState.SetMarketAnalysisLens(lens);
        if (!_appState.MarketItemAnalyses.Any())
        {
            if (changed)
            {
                await _indexedDb.AutoSaveStateAsync(_appState);
            }

            return new MarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        var plan = _appState.CurrentPlan;
        var planSessionVersion = _appState.PlanSessionVersion;
        var planDecisionVersion = _appState.CurrentVersions.PlanDecisionVersion;
        var planId = _appState.CurrentPlanId;
        var shoppingPlans = _appState.MarketItemAnalyses
            .Select(analysis => _marketPriceLadderAnalysis.ProjectToShoppingPlan(analysis, lens))
            .ToList();
        var published = await PublishMarketAnalysisAsync(
            plan,
            planSessionVersion,
            planDecisionVersion,
            planId,
            _appState.MarketItemAnalyses,
            shoppingPlans,
            _appState.MarketAnalysisRecipeBasis,
            _appState.CreateCurrentMarketAnalysisScopeSnapshot(),
            ct);
        if (published == null)
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        return new MarketAnalysisWorkflowResult(
            true,
            _appState.ShoppingPlans.Count,
            published.ChangedDecisionCount,
            0);
    }

    private async Task<PublishMarketAnalysisResult?> PublishMarketAnalysisAsync(
        CraftingPlan? plan,
        long planSessionVersion,
        long planDecisionVersion,
        string? planId,
        IEnumerable<MarketItemAnalysis> analyses,
        List<DetailedShoppingPlan> shoppingPlans,
        StoredRecipeOperationSnapshot? recipeBasis,
        PublishedMarketAnalysisScopeSnapshot publishedScope,
        CancellationToken ct)
    {
        if (plan == null ||
            !_appState.IsCurrentPlanSession(plan, planSessionVersion) ||
            _appState.CurrentVersions.PlanDecisionVersion != planDecisionVersion)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        var analysisList = analyses.ToList();
        var changedDecisions = AcquisitionPlanningService.EnsureAutomaticMarketSourcesAreActionable(
            plan,
            shoppingPlans);
        _marketShoppingService.ApplyVendorPurchaseOverrides(plan, shoppingPlans);

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion) ||
            _appState.CurrentVersions.PlanDecisionVersion != planDecisionVersion)
        {
            return null;
        }

        _appState.ApplyMarketAnalysisPublication(
            analysisList,
            shoppingPlans,
            _recipeLayerWorkflow.BuildActiveProcurementItems(plan),
            changedDecisions > 0,
            recipeBasis,
            publishedScope);
        _logger.LogInformation("[stage] hot-state publication applied ({Count} analyses, {PlanCount} plans)", analysisList.Count, shoppingPlans.Count);

        if (!string.IsNullOrEmpty(planId) &&
            _appState.IsCurrentPlanSession(plan, planSessionVersion) &&
            string.Equals(_appState.CurrentPlanId, planId, StringComparison.Ordinal))
        {
            _logger.LogInformation("[stage] analysis persistence starting (plan {PlanId})", planId);
            await _marketAnalysisPersistence.SaveAsync(
                planId,
                shoppingPlans,
                analysisList,
                RecommendationMode.MinimizeTotalCost,
                _appState.MarketAnalysisLens,
                recipeBasis,
                publishedScope,
                _appState.MarketIntelligence);
            _logger.LogInformation("[stage] analysis persistence complete");
        }

        ct.ThrowIfCancellationRequested();
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return null;
        }

        _logger.LogInformation("[stage] autosave starting");
        await _indexedDb.AutoSaveStateAsync(_appState);
        _logger.LogInformation("[stage] autosave complete");
        ct.ThrowIfCancellationRequested();
        return _appState.IsCurrentPlanSession(plan, planSessionVersion)
            ? new PublishMarketAnalysisResult(changedDecisions)
            : null;
    }

    private sealed record PublishMarketAnalysisResult(int ChangedDecisionCount);

    private bool IsCurrentRequest(MarketAnalysisRequestSnapshot snapshot)
    {
        var currentScope = _appState.SearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;

        return snapshot.Scope == currentScope &&
               string.Equals(snapshot.SelectedDataCenter, _appState.SelectedDataCenter, StringComparison.Ordinal) &&
               string.Equals(snapshot.SelectedRegion, _appState.SelectedRegion, StringComparison.Ordinal) &&
               snapshot.Lens == _appState.MarketAnalysisLens;
    }

    private sealed record MarketAnalysisRequestSnapshot(
        MarketFetchScope Scope,
        string SelectedDataCenter,
        string SelectedRegion,
        MarketAcquisitionLens Lens);
}
