using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketAnalysisExecutionService : IMarketAnalysisExecutionService
{
    private readonly IMarketCacheService _marketCache;
    private readonly IMarketPriceLadderAnalysisService _marketPriceLadderAnalysisService;

    public MarketAnalysisExecutionService(
        IMarketCacheService marketCache,
        IMarketPriceLadderAnalysisService marketPriceLadderAnalysisService)
    {
        _marketCache = marketCache;
        _marketPriceLadderAnalysisService = marketPriceLadderAnalysisService;
    }

    public async Task<MarketAnalysisExecutionResult> ExecuteAsync(
        MarketAnalysisExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var items = request.Items.ToList();
        var evidence = await MarketEvidenceLoader.LoadAsync(
            _marketCache,
            items.Select(item => item.ItemId),
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion,
            request.MaxAge,
            progress,
            ct);
        var analyses = await _marketPriceLadderAnalysisService.AnalyzeAsync(
            new MarketAnalysisRequest
            {
                Items = items,
                Evidence = evidence,
                RecommendationMode = request.RecommendationMode,
                AnalysisConfig = request.AnalysisConfig,
                ExpectedWorldsByDataCenter = request.ExpectedWorldsByDataCenter
            },
            progress,
            ct,
            executionOptions);
        var shoppingPlans = analyses
            .Select(analysis => _marketPriceLadderAnalysisService.ProjectToShoppingPlan(
                analysis,
                request.Lens,
                request.AnalysisConfig))
            .ToList();

        return new MarketAnalysisExecutionResult(evidence, analyses, shoppingPlans);
    }
}
