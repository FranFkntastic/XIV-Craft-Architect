using System.Diagnostics;

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
        if (request.MaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxAge),
                request.MaxAge,
                "Use ForceRefreshData when fresh market evidence is required.");
        }

        var items = request.Items.ToList();
        var fetchStopwatch = Stopwatch.StartNew();
        var evidence = await MarketEvidenceLoader.LoadAsync(
            _marketCache,
            items.Select(item => item.ItemId),
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion,
            request.MaxAge,
            request.ForceRefreshData,
            progress,
            ct);
        fetchStopwatch.Stop();

        var ladderAnalysisStopwatch = Stopwatch.StartNew();
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
        ladderAnalysisStopwatch.Stop();

        progress?.Report($"Projecting market recommendations for {analyses.Count} items...");
        var shoppingPlanProjectionStopwatch = Stopwatch.StartNew();
        var shoppingPlans = analyses
            .Select(analysis => _marketPriceLadderAnalysisService.ProjectToShoppingPlan(
                analysis,
                request.Lens,
                request.AnalysisConfig))
            .ToList();
        shoppingPlanProjectionStopwatch.Stop();

        return new MarketAnalysisExecutionResult(
            evidence,
            analyses,
            shoppingPlans,
            new MarketAnalysisExecutionTimings(
                fetchStopwatch.Elapsed,
                ladderAnalysisStopwatch.Elapsed,
                shoppingPlanProjectionStopwatch.Elapsed));
    }
}
