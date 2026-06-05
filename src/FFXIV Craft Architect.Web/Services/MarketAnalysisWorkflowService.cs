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
        var analyses = (await LoadAnalysesForLensProjectionAsync(ct)).ToList();
        var currentPlansByItemId = _appState.ShoppingPlans
            .GroupBy(plan => plan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        var shoppingPlans = analyses
            .Select(analysis =>
            {
                var shoppingPlan = _marketPriceLadderAnalysis.ProjectToShoppingPlan(analysis, lens);
                if (currentPlansByItemId.TryGetValue(shoppingPlan.ItemId, out var currentPlan))
                {
                    shoppingPlan.IconId = currentPlan.IconId;
                }

                return shoppingPlan;
            })
            .ToList();
        var publishedScope = CreateLensPublicationScope(lens);
        var executionResult = new MarketAnalysisExecutionResult(
            CreateEmptyEvidence(publishedScope),
            analyses,
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

    private async Task<IReadOnlyList<MarketItemAnalysis>> LoadAnalysesForLensProjectionAsync(CancellationToken ct)
    {
        var publicationId = _appState.MarketIntelligenceSummary?.PublicationId;
        var analyses = _appState.MarketItemAnalyses.ToList();
        if (publicationId is null || publicationId.Value == Guid.Empty)
        {
            return analyses;
        }

        var hydrated = new List<MarketItemAnalysis>(analyses.Count);
        foreach (var analysis in analyses)
        {
            ct.ThrowIfCancellationRequested();
            var details = await _marketIntelligenceStore.LoadDetailsAsync(
                new MarketIntelligenceDetailQuery(publicationId.Value, analysis.ItemId),
                ct);
            hydrated.Add(CloneAnalysisWithDetails(analysis, details));
        }

        return hydrated;
    }

    private static MarketItemAnalysis CloneAnalysisWithDetails(
        MarketItemAnalysis analysis,
        IReadOnlyList<MarketListingDetail> details)
    {
        var itemDetail = details.FirstOrDefault(detail => detail.Key.World is null);
        var detailByWorld = details
            .Where(detail => detail.Key.World is not null)
            .GroupBy(detail => detail.Key.World!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        return new MarketItemAnalysis
        {
            ItemId = analysis.ItemId,
            Name = analysis.Name,
            QuantityNeeded = analysis.QuantityNeeded,
            Scope = analysis.Scope,
            LoadedAtUtc = analysis.LoadedAtUtc,
            AnalysisScopeBaselineUnitPrice = analysis.AnalysisScopeBaselineUnitPrice,
            AnalysisScopeAverageUnitPrice = analysis.AnalysisScopeAverageUnitPrice,
            AnalysisScopeCompetitiveAverageUnitPrice = analysis.AnalysisScopeCompetitiveAverageUnitPrice,
            AnalysisScopeMedianUnitPrice = analysis.AnalysisScopeMedianUnitPrice,
            CompetitiveThresholdUnitPrice = analysis.CompetitiveThresholdUnitPrice,
            SaneThresholdUnitPrice = analysis.SaneThresholdUnitPrice,
            RequestedDataCenters = analysis.RequestedDataCenters,
            PresentDataCenters = analysis.PresentDataCenters,
            MissingDataCenters = analysis.MissingDataCenters,
            WorstDataQualityBucket = analysis.WorstDataQualityBucket,
            PriceEvaluation = itemDetail?.PriceEvaluation ?? analysis.PriceEvaluation,
            Worlds = analysis.Worlds
                .Select(world => CloneWorldWithDetails(
                    world,
                    detailByWorld.TryGetValue(new MarketWorldKey(world.DataCenter, world.WorldName), out var detail)
                        ? detail
                        : null))
                .ToList(),
            Warning = analysis.Warning
        };
    }

    private static WorldMarketAnalysis CloneWorldWithDetails(
        WorldMarketAnalysis world,
        MarketListingDetail? detail)
    {
        return new WorldMarketAnalysis
        {
            DataCenter = world.DataCenter,
            WorldName = world.WorldName,
            QuantityNeeded = world.QuantityNeeded,
            CompetitiveQuantity = world.CompetitiveQuantity,
            LocalCompetitiveQuantity = world.LocalCompetitiveQuantity,
            ScopeCompetitiveQuantity = world.ScopeCompetitiveQuantity,
            ScopeSaneQuantity = world.ScopeSaneQuantity,
            ScopeUncompetitiveQuantity = world.ScopeUncompetitiveQuantity,
            ScopeInsaneQuantity = world.ScopeInsaneQuantity,
            TotalSaneQuantity = world.TotalSaneQuantity,
            TotalListingQuantity = world.TotalListingQuantity,
            CompetitiveCoverageRatio = world.CompetitiveCoverageRatio,
            ScopeCompetitiveCoverageRatio = world.ScopeCompetitiveCoverageRatio,
            ScopeSaneCoverageRatio = world.ScopeSaneCoverageRatio,
            SaneCoverageRatio = world.SaneCoverageRatio,
            AnalysisScopeBaselineUnitPrice = world.AnalysisScopeBaselineUnitPrice,
            AnalysisScopeAverageUnitPrice = world.AnalysisScopeAverageUnitPrice,
            AnalysisScopeCompetitiveAverageUnitPrice = world.AnalysisScopeCompetitiveAverageUnitPrice,
            ScopeCompetitiveAverageUnitPrice = world.ScopeCompetitiveAverageUnitPrice,
            AnalysisScopeMedianUnitPrice = world.AnalysisScopeMedianUnitPrice,
            CompetitiveThresholdUnitPrice = world.CompetitiveThresholdUnitPrice,
            SaneThresholdUnitPrice = world.SaneThresholdUnitPrice,
            CoverageBucket = world.CoverageBucket,
            FetchedAtUtc = world.FetchedAtUtc,
            MarketUploadedAtUtc = world.MarketUploadedAtUtc,
            DataAgeSource = world.DataAgeSource,
            DataAge = world.DataAge,
            DataQualityScore = world.DataQualityScore,
            DataQualityBucket = world.DataQualityBucket,
            PriceBands = detail?.PriceBands.ToList() ?? world.PriceBands.ToList(),
            Listings = detail?.Listings.ToList() ?? world.Listings.ToList(),
            Scores = world.Scores.ToList()
        };
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

        var hotAnalyses = MarketIntelligenceSummaryHydrator
            .HydrateMarketItemAnalyses(projection.Publication.Summary)
            .ToList();
        var hotShoppingPlans = MarketIntelligenceSummaryHydrator
            .HydrateShoppingPlans(projection.Publication.Summary)
            .ToList();

        _appState.ApplyMarketAnalysisPublication(
            hotAnalyses,
            hotShoppingPlans,
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
                hotShoppingPlans,
                hotAnalyses,
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
