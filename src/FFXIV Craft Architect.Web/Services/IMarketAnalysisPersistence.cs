using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public interface IMarketAnalysisPersistence
{
    Task<bool> SaveAsync(
        string planId,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<MarketItemAnalysis> marketItemAnalyses,
        RecommendationMode mode,
        MarketAcquisitionLens lens,
        StoredRecipeOperationSnapshot? recipeBasis,
        PublishedMarketAnalysisScopeSnapshot? publishedScope,
        MarketIntelligence? marketIntelligence);
}

public sealed class IndexedDbMarketAnalysisPersistence : IMarketAnalysisPersistence
{
    private readonly IndexedDbService _indexedDb;

    public IndexedDbMarketAnalysisPersistence(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
    }

    public Task<bool> SaveAsync(
        string planId,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<MarketItemAnalysis> marketItemAnalyses,
        RecommendationMode mode,
        MarketAcquisitionLens lens,
        StoredRecipeOperationSnapshot? recipeBasis,
        PublishedMarketAnalysisScopeSnapshot? publishedScope,
        MarketIntelligence? marketIntelligence) =>
        _indexedDb.SaveMarketAnalysisAsync(
            planId,
            shoppingPlans,
            marketItemAnalyses,
            mode,
            lens,
            recipeBasis,
            publishedScope,
            marketIntelligence);
}
