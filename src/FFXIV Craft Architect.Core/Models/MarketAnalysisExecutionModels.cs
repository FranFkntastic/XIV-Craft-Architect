using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Core.Models;

public sealed class MarketAnalysisExecutionRequest
{
    public IReadOnlyList<MaterialAggregate> Items { get; init; } = Array.Empty<MaterialAggregate>();

    public MarketFetchScope Scope { get; init; } = MarketFetchScope.SelectedDataCenter;

    public string SelectedDataCenter { get; init; } = string.Empty;

    public string SelectedRegion { get; init; } = string.Empty;

    /// <summary>
    /// Maximum reusable-cache age. Null uses the cache default. Must be greater than zero.
    /// </summary>
    public TimeSpan? MaxAge { get; init; }

    /// <summary>
    /// When true, refreshes the requested market-evidence pairs instead of reusing cache.
    /// This is intentionally separate from <see cref="MaxAge"/>.
    /// </summary>
    public bool ForceRefreshData { get; init; }

    public RecommendationMode RecommendationMode { get; init; } = RecommendationMode.MinimizeTotalCost;

    public MarketAcquisitionLens Lens { get; init; } = MarketAcquisitionLens.MinimumUpfrontCost;

    public MarketAnalysisConfig AnalysisConfig { get; init; } = new();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedWorldsByDataCenter { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public readonly record struct MarketAnalysisExecutionTimings(
    TimeSpan MarketFetchDuration,
    TimeSpan LadderAnalysisDuration,
    TimeSpan ShoppingPlanProjectionDuration)
{
    public MarketAnalysisExecutionTimings(
        TimeSpan marketFetchDuration,
        TimeSpan analysisDuration)
        : this(marketFetchDuration, analysisDuration, TimeSpan.Zero)
    {
    }

    public TimeSpan AnalysisDuration => LadderAnalysisDuration + ShoppingPlanProjectionDuration;

    public bool HasMeasuredDuration =>
        MarketFetchDuration > TimeSpan.Zero || AnalysisDuration > TimeSpan.Zero;
}

public sealed record MarketAnalysisExecutionResult(
    MarketEvidenceSet Evidence,
    List<MarketItemAnalysis> Analyses,
    List<DetailedShoppingPlan> ShoppingPlans,
    MarketAnalysisExecutionTimings Timings = default);
