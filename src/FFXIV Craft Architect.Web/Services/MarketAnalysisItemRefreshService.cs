using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record MarketAnalysisItemRefreshWorkflowRequest(
    int ItemId,
    Func<bool>? IsCurrentOperation = null,
    MarketAnalysisExecutionOptions? ExecutionOptions = null)
{
    public MarketFetchScope Scope { get; init; } = MarketFetchScope.SelectedDataCenter;

    public Func<bool>? IsCurrentConfiguration { get; init; }
}

public sealed record MarketAnalysisItemRefreshWorkflowResult(
    MarketAnalysisItemRefreshStatus Status,
    string? ItemName = null)
{
    public static MarketAnalysisItemRefreshWorkflowResult Noop(MarketAnalysisItemRefreshStatus status)
    {
        return new MarketAnalysisItemRefreshWorkflowResult(status, null);
    }
}

public enum MarketAnalysisItemRefreshStatus
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

public sealed class MarketAnalysisItemRefreshService
{
    private readonly AppState _appState;
    private readonly IMarketAnalysisExecutionService _marketAnalysisExecution;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly WebPlanPersistenceService _planPersistence;
    private readonly IndexedDbService _indexedDb;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;

    public MarketAnalysisItemRefreshService(
        AppState appState,
        IMarketAnalysisExecutionService marketAnalysisExecution,
        MarketShoppingService marketShoppingService,
        WebPlanPersistenceService planPersistence,
        IndexedDbService indexedDb,
        IRecipeLayerWorkflowService recipeLayerWorkflow)
    {
        _appState = appState;
        _marketAnalysisExecution = marketAnalysisExecution;
        _marketShoppingService = marketShoppingService;
        _planPersistence = planPersistence;
        _indexedDb = indexedDb;
        _recipeLayerWorkflow = recipeLayerWorkflow;
    }

    public async Task<MarketAnalysisItemRefreshWorkflowResult> RefreshItemMarketDataAsync(
        MarketAnalysisItemRefreshWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var plan = _appState.CurrentPlan;
        var planSessionVersion = _appState.PlanSessionVersion;
        if (plan == null)
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.NoPlan);
        }

        var candidateResult = await _recipeLayerWorkflow.BuildCurrentMarketAnalysisCandidateResultAsync(plan, ct);
        if (candidateResult == null || !_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.StalePlan);
        }

        var candidate = candidateResult.Candidates.FirstOrDefault(item => item.ItemId == request.ItemId);
        if (candidate == null)
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.NotFound);
        }

        var capturedPlanId = _appState.CurrentPlanId;
        var capturedDecisionVersion = _appState.CurrentVersions.PlanDecisionVersion;
        var capturedMarketAnalysisVersion = _appState.CurrentVersions.MarketAnalysisVersion;
        var requestSnapshot = new MarketAnalysisItemRefreshRequestSnapshot(
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

        var executionResult = await _marketAnalysisExecution.ExecuteAsync(
            new MarketAnalysisExecutionRequest
            {
                Items = [candidate],
                Scope = request.Scope,
                SelectedDataCenter = _appState.SelectedDataCenter,
                SelectedRegion = _appState.SelectedRegion,
                ForceRefreshData = true,
                RecommendationMode = RecommendationMode.MinimizeTotalCost,
                Lens = _appState.MarketAnalysisLens,
                ExpectedWorldsByDataCenter = _appState.GetExpectedMarketWorlds(request.Scope)
            },
            guardedProgress,
            ct,
            executionOptions: request.ExecutionOptions);
        ct.ThrowIfCancellationRequested();

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.StalePlan);
        }

        if (_appState.CurrentVersions.PlanDecisionVersion != capturedDecisionVersion)
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.StaleDecision);
        }

        if (!IsCurrentConfiguration(plan, planSessionVersion, capturedMarketAnalysisVersion, requestSnapshot, request))
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.StaleConfiguration);
        }

        if (request.IsCurrentOperation?.Invoke() == false)
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.Superseded);
        }

        var refreshedAnalysis = executionResult.Analyses.SingleOrDefault();
        if (refreshedAnalysis == null || executionResult.ShoppingPlans.Count != 1)
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.NoData);
        }

        var refreshedPlans = new List<DetailedShoppingPlan> { executionResult.ShoppingPlans.Single() };
        _marketShoppingService.ApplyVendorPurchaseOverrides(plan, refreshedPlans);
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.StalePlan);
        }

        _appState.ReplaceMarketAnalysisItem(refreshedAnalysis, refreshedPlans[0]);

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
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.StalePlan);
        }

        if (request.IsCurrentOperation?.Invoke() == false)
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.Superseded);
        }

        await _indexedDb.AutoSaveStateAsync(_appState);
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.StalePlan);
        }

        if (request.IsCurrentOperation?.Invoke() == false)
        {
            return MarketAnalysisItemRefreshWorkflowResult.Noop(MarketAnalysisItemRefreshStatus.Superseded);
        }

        return new MarketAnalysisItemRefreshWorkflowResult(
            MarketAnalysisItemRefreshStatus.Refreshed,
            candidate.Name);
    }

    private bool IsCurrentConfiguration(
        CraftingPlan plan,
        long planSessionVersion,
        long capturedMarketAnalysisVersion,
        MarketAnalysisItemRefreshRequestSnapshot requestSnapshot,
        MarketAnalysisItemRefreshWorkflowRequest request)
    {
        return _appState.CurrentVersions.MarketAnalysisVersion == capturedMarketAnalysisVersion &&
               IsCurrentRequest(requestSnapshot) &&
               _appState.IsCurrentPlanSession(plan, planSessionVersion) &&
               (request.IsCurrentConfiguration?.Invoke() ?? true);
    }

    private bool IsCurrentRequest(MarketAnalysisItemRefreshRequestSnapshot snapshot)
    {
        return string.Equals(snapshot.SelectedDataCenter, _appState.SelectedDataCenter, StringComparison.Ordinal) &&
               string.Equals(snapshot.SelectedRegion, _appState.SelectedRegion, StringComparison.Ordinal) &&
               snapshot.Lens == _appState.MarketAnalysisLens;
    }

    private sealed record MarketAnalysisItemRefreshRequestSnapshot(
        MarketFetchScope Scope,
        string SelectedDataCenter,
        string SelectedRegion,
        MarketAcquisitionLens Lens);
}
