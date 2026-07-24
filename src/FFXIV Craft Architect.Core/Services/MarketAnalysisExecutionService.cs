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
        var execution = executionOptions ?? MarketAnalysisExecutionOptions.Synchronous;
        var evidenceLoadStartedAtUtc = DateTime.UtcNow - TimeSpan.FromSeconds(1);
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
            ct,
            request.SkipCachePopulation);
        fetchStopwatch.Stop();
        var fetchedItemIds = evidence.Entries
            .Where(entry => CacheTimeHelper.NormalizeToUtc(entry.Value.FetchedAt) >= evidenceLoadStartedAtUtc)
            .Select(entry => entry.Key.itemId)
            .ToHashSet();

        progress?.Report($"[stage] evidence loaded ({items.Count} items), ladder analysis starting...");
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
            execution);
        ladderAnalysisStopwatch.Stop();
        var analysesByItemId = analyses.ToDictionary(analysis => analysis.ItemId);
        var shoppingPlans = new List<DetailedShoppingPlan>(analyses.Count);
        var shoppingPlansByItemId = new Dictionary<int, DetailedShoppingPlan>(analyses.Count);
        var result = new MarketAnalysisExecutionResult(
            evidence,
            analyses,
            shoppingPlans,
            new MarketAnalysisExecutionTimings(
                fetchStopwatch.Elapsed,
                ladderAnalysisStopwatch.Elapsed,
                TimeSpan.Zero),
            fetchedItemIds,
            analysesByItemId,
            shoppingPlansByItemId);

        progress?.Report($"[stage] ladder analysis complete ({analyses.Count} analyses), projecting recommendations...");
        progress?.Report($"Projecting market recommendations for {analyses.Count} items...");
        var shoppingPlanProjectionStopwatch = Stopwatch.StartNew();
        for (var analysisIndex = 0; analysisIndex < analyses.Count; analysisIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var completedItems = analysisIndex + 1;
            if (execution.ShouldReportProgress(completedItems))
            {
                progress?.Report($"Projecting market recommendations {completedItems}/{analyses.Count}: {analyses[analysisIndex].Name}...");
            }

            var shoppingPlan = await _marketPriceLadderAnalysisService.ProjectToShoppingPlanAsync(
                analyses[analysisIndex],
                request.Lens,
                request.AnalysisConfig,
                execution,
                progress,
                ct);
            shoppingPlans.Add(shoppingPlan);
            shoppingPlansByItemId.Add(shoppingPlan.ItemId, shoppingPlan);
            if (execution.ShouldYieldAfterItem(completedItems))
            {
                await Task.Delay(1, ct);
            }
        }
        shoppingPlanProjectionStopwatch.Stop();
        progress?.Report($"[stage] recommendation projection complete ({shoppingPlans.Count} plans).");

        result.Timings = new MarketAnalysisExecutionTimings(
            fetchStopwatch.Elapsed,
            ladderAnalysisStopwatch.Elapsed,
            shoppingPlanProjectionStopwatch.Elapsed);
        return result;
    }
}
