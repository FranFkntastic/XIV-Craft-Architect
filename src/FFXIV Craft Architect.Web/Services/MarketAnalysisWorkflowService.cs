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
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly IMarketIntelligenceProjectionService _marketIntelligenceProjection;
    private readonly IMarketIntelligenceStore _marketIntelligenceStore;
    private readonly IMarketDataSourceStore _marketDataSourceStore;

    public MarketAnalysisWorkflowService(
        AppState appState,
        IMarketAnalysisExecutionService marketAnalysisExecution,
        MarketShoppingService marketShoppingService,
        IMarketPriceLadderAnalysisService marketPriceLadderAnalysis,
        WebPlanPersistenceService planPersistence,
        IndexedDbService indexedDb,
        IRecipeLayerWorkflowService recipeLayerWorkflow,
        IMarketIntelligenceProjectionService marketIntelligenceProjection,
        IMarketIntelligenceStore marketIntelligenceStore,
        IMarketDataSourceStore marketDataSourceStore)
    {
        _appState = appState;
        _marketAnalysisExecution = marketAnalysisExecution;
        _marketShoppingService = marketShoppingService;
        _marketPriceLadderAnalysis = marketPriceLadderAnalysis;
        _planPersistence = planPersistence;
        _indexedDb = indexedDb;
        _recipeLayerWorkflow = recipeLayerWorkflow;
        _marketIntelligenceProjection = marketIntelligenceProjection;
        _marketIntelligenceStore = marketIntelligenceStore;
        _marketDataSourceStore = marketDataSourceStore;
    }

    public async Task<MarketAnalysisWorkflowResult> RunAnalysisAsync(
        MarketAnalysisWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        var plan = _appState.CurrentPlan;
        var planSessionVersion = _appState.PlanSessionVersion;
        var planId = _appState.CurrentPlanId;
        var candidateResult = await _recipeLayerWorkflow.BuildCurrentMarketAnalysisCandidateResultAsync(plan, ct);
        var materials = candidateResult?.Candidates.ToList() ?? [];
        var recipeBasis = candidateResult?.RecipeBasis;
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        if (plan == null || materials.Count == 0 || string.IsNullOrWhiteSpace(_appState.SelectedDataCenter))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, 0);
        }

        _appState.ClearMarketAnalysisState();

        var scope = _appState.SearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var requestSnapshot = new MarketAnalysisRequestSnapshot(
            scope,
            _appState.SelectedDataCenter,
            _appState.SelectedRegion,
            _appState.MarketAnalysisLens);
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

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, executionResult.Evidence.FetchedCount);
        }

        if (!IsCurrentRequest(requestSnapshot))
        {
            return new MarketAnalysisWorkflowResult(false, 0, 0, executionResult.Evidence.FetchedCount);
        }

        var published = await PublishMarketAnalysisAsync(
            plan,
            planSessionVersion,
            planId,
            executionResult,
            recipeBasis,
            _appState.CreateCurrentMarketAnalysisScopeSnapshot(),
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
        var planId = _appState.CurrentPlanId;
        var shoppingPlans = _appState.MarketItemAnalyses
            .Select(analysis => _marketPriceLadderAnalysis.ProjectToShoppingPlan(analysis, lens))
            .ToList();
        var publishedScope = CreateLensPublicationScope(lens);
        var executionResult = new MarketAnalysisExecutionResult(
            CreateEmptyEvidence(publishedScope),
            _appState.MarketItemAnalyses.ToList(),
            shoppingPlans);
        var published = await PublishMarketAnalysisAsync(
            plan,
            planSessionVersion,
            planId,
            executionResult,
            _appState.MarketAnalysisRecipeBasis,
            publishedScope,
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
        string? planId,
        MarketAnalysisExecutionResult executionResult,
        StoredRecipeOperationSnapshot? recipeBasis,
        PublishedMarketAnalysisScopeSnapshot publishedScope,
        CancellationToken ct)
    {
        if (plan == null || !_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        var analysisList = executionResult.Analyses.ToList();
        var shoppingPlans = executionResult.ShoppingPlans.ToList();
        var changedDecisions = AcquisitionPlanningService.ReconcileAcquisitionDecisions(plan, shoppingPlans);
        _marketShoppingService.ApplyVendorPurchaseOverrides(plan, shoppingPlans);

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return null;
        }

        var projection = _marketIntelligenceProjection.Project(
            new MarketIntelligenceProjectionRequest
            {
                ExecutionResult = new MarketAnalysisExecutionResult(
                    executionResult.Evidence,
                    analysisList,
                    shoppingPlans),
                PublicationContext = ToMarketIntelligencePublicationContext(publishedScope),
                AnalyzerVersion = "web-market-analysis",
                StartedAtUtc = publishedScope.PublishedAtUtc,
                CompletedAtUtc = DateTime.UtcNow,
                NetworkRequestCount = executionResult.Evidence.FetchedCount,
                CacheMode = "mixed"
            });
        await _marketIntelligenceStore.SavePublicationAsync(projection.Publication, ct);
        await _marketDataSourceStore.SaveListingFactsAsync(projection.SourceFacts, ct);
        ct.ThrowIfCancellationRequested();
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return null;
        }

        _appState.ApplyMarketAnalysisPublication(
            analysisList,
            shoppingPlans,
            _recipeLayerWorkflow.BuildActiveProcurementItems(plan),
            changedDecisions > 0,
            recipeBasis,
            publishedScope,
            projection.Publication.Summary);

        if (!string.IsNullOrEmpty(planId) &&
            _appState.IsCurrentPlanSession(plan, planSessionVersion) &&
            string.Equals(_appState.CurrentPlanId, planId, StringComparison.Ordinal))
        {
            await _planPersistence.SaveMarketAnalysisAsync(
                planId,
                shoppingPlans,
                analysisList,
                RecommendationMode.MinimizeTotalCost,
                _appState.MarketAnalysisLens,
                recipeBasis,
                publishedScope,
                _appState.MarketIntelligence,
                projection.Publication.Summary);
        }

        ct.ThrowIfCancellationRequested();
        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return null;
        }

        await _indexedDb.AutoSaveStateAsync(_appState);
        ct.ThrowIfCancellationRequested();
        return _appState.IsCurrentPlanSession(plan, planSessionVersion)
            ? new PublishMarketAnalysisResult(changedDecisions)
            : null;
    }

    private sealed record PublishMarketAnalysisResult(int ChangedDecisionCount);

    private PublishedMarketAnalysisScopeSnapshot CreateLensPublicationScope(MarketAcquisitionLens lens)
    {
        var previousScope = _appState.PublishedMarketAnalysisScope;
        if (previousScope == null)
        {
            return _appState.CreateCurrentMarketAnalysisScopeSnapshot();
        }

        return previousScope with
        {
            Lens = lens,
            PlanSessionVersion = _appState.PlanSessionVersion,
            PublishedAtUtc = DateTime.UtcNow
        };
    }

    private static MarketIntelligencePublicationContext ToMarketIntelligencePublicationContext(
        PublishedMarketAnalysisScopeSnapshot scope)
    {
        return new MarketIntelligencePublicationContext(
            MarketIntelligencePublicationContextKind.Known,
            scope.Scope,
            scope.SelectedDataCenter,
            scope.SelectedRegion,
            scope.RequestedDataCenters.ToArray(),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            null,
            false,
            RecommendationMode.MinimizeTotalCost,
            scope.Lens,
            null,
            scope.PlanSessionVersion,
            null,
            scope.PublishedAtUtc);
    }

    private static MarketEvidenceSet CreateEmptyEvidence(PublishedMarketAnalysisScopeSnapshot scope)
    {
        return new MarketEvidenceSet(
            new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
            Array.Empty<(int itemId, string dataCenter)>(),
            scope.Scope,
            scope.RequestedDataCenters.ToArray(),
            scope.SelectedDataCenter,
            scope.SelectedRegion,
            TimeSpan.Zero,
            fetchedCount: 0,
            scope.PublishedAtUtc);
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

    private sealed record MarketAnalysisRequestSnapshot(
        MarketFetchScope Scope,
        string SelectedDataCenter,
        string SelectedRegion,
        MarketAcquisitionLens Lens);
}
