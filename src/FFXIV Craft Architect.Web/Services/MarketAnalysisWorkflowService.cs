using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record MarketAnalysisWorkflowRequest(bool ForceRefreshData);

public sealed record MarketAnalysisWorkflowResult(
    bool Published,
    int AnalyzedCount,
    int ChangedDecisionCount,
    int FetchedCount);

public sealed class MarketAnalysisWorkflowService
{
    private readonly AppState _appState;
    private readonly IMarketAnalysisExecutionService _marketAnalysisExecution;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly IMarketPriceLadderAnalysisService _marketPriceLadderAnalysis;
    private readonly WebPlanPersistenceService _planPersistence;
    private readonly IndexedDbService _indexedDb;

    public MarketAnalysisWorkflowService(
        AppState appState,
        IMarketAnalysisExecutionService marketAnalysisExecution,
        MarketShoppingService marketShoppingService,
        IMarketPriceLadderAnalysisService marketPriceLadderAnalysis,
        WebPlanPersistenceService planPersistence,
        IndexedDbService indexedDb)
    {
        _appState = appState;
        _marketAnalysisExecution = marketAnalysisExecution;
        _marketShoppingService = marketShoppingService;
        _marketPriceLadderAnalysis = marketPriceLadderAnalysis;
        _planPersistence = planPersistence;
        _indexedDb = indexedDb;
    }

    public async Task<MarketAnalysisWorkflowResult> RunAnalysisAsync(
        MarketAnalysisWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        var plan = _appState.CurrentPlan;
        var planId = _appState.CurrentPlanId;
        var materials = AcquisitionPlanningService.GetMarketAnalysisCandidates(plan);
        if (plan == null || materials.Count == 0 || string.IsNullOrWhiteSpace(_appState.SelectedDataCenter))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        _appState.ClearMarketAnalysisState();

        var scope = _appState.SearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var executionResult = await _marketAnalysisExecution.ExecuteAsync(
            new MarketAnalysisExecutionRequest
            {
                Items = materials,
                Scope = scope,
                SelectedDataCenter = _appState.SelectedDataCenter,
                SelectedRegion = _appState.SelectedRegion,
                MaxAge = request.ForceRefreshData ? TimeSpan.Zero : (TimeSpan?)null,
                RecommendationMode = RecommendationMode.MinimizeTotalCost,
                Lens = _appState.MarketAnalysisLens,
                ExpectedWorldsByDataCenter = _appState.GetExpectedMarketWorlds(scope)
            },
            progress,
            ct,
            executionOptions: executionOptions);
        ct.ThrowIfCancellationRequested();

        if (!ReferenceEquals(_appState.CurrentPlan, plan))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, executionResult.Evidence.FetchedCount);
        }

        var published = await PublishMarketAnalysisAsync(
            plan,
            planId,
            executionResult.Analyses,
            executionResult.ShoppingPlans,
            ct);
        if (published == null)
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, executionResult.Evidence.FetchedCount);
        }

        return new MarketAnalysisWorkflowResult(
            true,
            _appState.ShoppingPlans.Count,
            published.ChangedDecisionCount,
            executionResult.Evidence.FetchedCount);
    }

    public async Task<MarketAnalysisWorkflowResult> ApplyLensAsync(
        MarketAcquisitionLens lens,
        CancellationToken ct = default)
    {
        _appState.MarketAnalysisLens = lens;
        if (!_appState.MarketItemAnalyses.Any())
        {
            _appState.NotifyShoppingListChanged();
            await _indexedDb.AutoSaveStateAsync(_appState);
            return new MarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        var plan = _appState.CurrentPlan;
        var planId = _appState.CurrentPlanId;
        var shoppingPlans = _appState.MarketItemAnalyses
            .Select(analysis => _marketPriceLadderAnalysis.ProjectToShoppingPlan(analysis, lens))
            .ToList();
        var published = await PublishMarketAnalysisAsync(
            plan,
            planId,
            _appState.MarketItemAnalyses,
            shoppingPlans,
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
        string? planId,
        IEnumerable<MarketItemAnalysis> analyses,
        List<DetailedShoppingPlan> shoppingPlans,
        CancellationToken ct)
    {
        if (plan == null || !ReferenceEquals(_appState.CurrentPlan, plan))
        {
            return null;
        }

        var analysisList = analyses.ToList();
        var changedDecisions = AcquisitionPlanningService.ReconcileAcquisitionDecisions(plan, shoppingPlans);
        _marketShoppingService.ApplyVendorPurchaseOverrides(plan, shoppingPlans);

        using (_appState.BeginStateChangeBatch())
        {
            _appState.ReplaceMarketAnalysis(analysisList, shoppingPlans);
            if (changedDecisions > 0)
            {
                _appState.ReplaceShoppingItemsFromActivePlan();
                _appState.NotifyPlanDecisionChanged();
            }
            else
            {
                _appState.NotifyPlanChanged();
            }
        }

        if (!string.IsNullOrEmpty(planId) && string.Equals(_appState.CurrentPlanId, planId, StringComparison.Ordinal))
        {
            await _planPersistence.SaveMarketAnalysisAsync(
                planId,
                shoppingPlans,
                analysisList,
                RecommendationMode.MinimizeTotalCost,
                _appState.MarketAnalysisLens);
        }

        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_appState.CurrentPlan, plan))
        {
            return null;
        }

        await _indexedDb.AutoSaveStateAsync(_appState);
        ct.ThrowIfCancellationRequested();
        return ReferenceEquals(_appState.CurrentPlan, plan)
            ? new PublishMarketAnalysisResult(changedDecisions)
            : null;
    }

    private sealed record PublishMarketAnalysisResult(int ChangedDecisionCount);
}
