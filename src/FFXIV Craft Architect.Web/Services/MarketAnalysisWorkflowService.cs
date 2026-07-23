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
    private static readonly MarketAnalysisExecutionOptions InteractiveExecution = new()
    {
        YieldEveryItems = 1
    };

    private readonly AppState _appState;
    private readonly IMarketEvidenceReconciliationService _marketEvidenceReconciliation;
    private readonly IMarketPriceLadderAnalysisService _marketPriceLadderAnalysis;
    private readonly IndexedDbService _indexedDb;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly MarketAnalysisPublicationService _publicationService;

    public MarketAnalysisWorkflowService(
        AppState appState,
        IMarketEvidenceReconciliationService marketEvidenceReconciliation,
        IMarketPriceLadderAnalysisService marketPriceLadderAnalysis,
        IMarketAnalysisPersistence marketAnalysisPersistence,
        IndexedDbService indexedDb,
        IRecipeLayerWorkflowService recipeLayerWorkflow,
        ILogger<MarketAnalysisWorkflowService> logger)
    {
        _appState = appState;
        _marketEvidenceReconciliation = marketEvidenceReconciliation;
        _marketPriceLadderAnalysis = marketPriceLadderAnalysis;
        _indexedDb = indexedDb;
        _recipeLayerWorkflow = recipeLayerWorkflow;
        _publicationService = new MarketAnalysisPublicationService(
            appState,
            new MarketAnalysisPublicationStore(appState, marketAnalysisPersistence, indexedDb),
            recipeLayerWorkflow,
            logger);
    }

    public async Task<MarketAnalysisWorkflowResult> RunAnalysisAsync(
        MarketAnalysisWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        var execution = executionOptions ?? InteractiveExecution;
        var plan = _appState.CurrentPlan;
        var planSessionVersion = _appState.PlanSessionVersion;
        var planDecisionVersion = _appState.CurrentVersions.PlanDecisionVersion;
        var planId = _appState.CurrentPlanId;
        var planName = _appState.CurrentPlanName;
        var candidateResult = await _recipeLayerWorkflow.BuildCurrentMarketAnalysisCandidateResultAsync(plan, ct);
        var materials = candidateResult?.Candidates.ToList() ?? [];
        var recipeBasis = candidateResult?.RecipeBasis;
        var publicationProgressMessage = $"[stage] publishing {materials.Count} market analyses...";
        if (!IsCurrentPlanIdentity(plan, planSessionVersion, planId, planName))
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
            executionOptions: execution);
        ct.ThrowIfCancellationRequested();

        if (!IsCurrentPlanIdentity(plan, planSessionVersion, planId, planName))
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

        progress?.Report(publicationProgressMessage);
        var published = await PublishMarketAnalysisAsync(
            plan,
            planSessionVersion,
            planDecisionVersion,
            planId,
            planName,
            reconciliation.Analyses,
            reconciliation.ShoppingPlans.ToList(),
            recipeBasis,
            _appState.CreateCurrentMarketAnalysisScopeSnapshot(),
            ct,
            execution);
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
        var planName = _appState.CurrentPlanName;
        var shoppingPlans = _appState.MarketItemAnalyses
            .Select(analysis => _marketPriceLadderAnalysis.ProjectToShoppingPlan(analysis, lens))
            .ToList();
        var published = await PublishMarketAnalysisAsync(
            plan,
            planSessionVersion,
            planDecisionVersion,
            planId,
            planName,
            _appState.MarketItemAnalyses,
            shoppingPlans,
            _appState.MarketAnalysisRecipeBasis,
            _appState.CreateCurrentMarketAnalysisScopeSnapshot(),
            ct,
            MarketAnalysisExecutionOptions.Interactive);
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

    private async Task<MarketAnalysisPublication?> PublishMarketAnalysisAsync(
        CraftingPlan? plan,
        long planSessionVersion,
        long planDecisionVersion,
        string? planId,
        string? planName,
        IEnumerable<MarketItemAnalysis> analyses,
        List<DetailedShoppingPlan> shoppingPlans,
        StoredRecipeOperationSnapshot? recipeBasis,
        PublishedMarketAnalysisScopeSnapshot publishedScope,
        CancellationToken ct,
        MarketAnalysisExecutionOptions? executionOptions)
    {
        if (plan == null)
        {
            return null;
        }

        return await _publicationService.PublishLegacyAsync(
            new MarketAnalysisPublicationRequest(
                plan,
                planSessionVersion,
                planDecisionVersion,
                planId,
                planName,
                analyses.ToList(),
                shoppingPlans,
                recipeBasis,
                publishedScope),
            ct,
            executionOptions);
    }

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

    private bool IsCurrentPlanIdentity(
        CraftingPlan? plan,
        long planSessionVersion,
        string? planId,
        string? planName) =>
        _appState.IsCurrentPlanSession(plan, planSessionVersion) &&
        string.Equals(_appState.CurrentPlanId, planId, StringComparison.Ordinal) &&
        string.Equals(_appState.CurrentPlanName, planName, StringComparison.Ordinal);

    private sealed record MarketAnalysisRequestSnapshot(
        MarketFetchScope Scope,
        string SelectedDataCenter,
        string SelectedRegion,
        MarketAcquisitionLens Lens);
}
