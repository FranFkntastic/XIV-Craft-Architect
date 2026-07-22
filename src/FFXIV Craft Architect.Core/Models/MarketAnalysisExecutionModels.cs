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

public sealed record MarketAnalysisExecutionResult
{
    public MarketAnalysisExecutionResult(
        MarketEvidenceSet evidence,
        List<MarketItemAnalysis> analyses,
        List<DetailedShoppingPlan> shoppingPlans,
        MarketAnalysisExecutionTimings timings = default,
        IReadOnlySet<int>? fetchedItemIds = null,
        IReadOnlyDictionary<int, MarketItemAnalysis>? analysesByItemId = null,
        IReadOnlyDictionary<int, DetailedShoppingPlan>? shoppingPlansByItemId = null)
    {
        Evidence = evidence;
        Analyses = analyses;
        ShoppingPlans = shoppingPlans;
        Timings = timings;
        FetchedItemIds = fetchedItemIds;
        AnalysesByItemId = analysesByItemId;
        ShoppingPlansByItemId = shoppingPlansByItemId;
    }

    public MarketEvidenceSet Evidence { get; init; }

    public List<MarketItemAnalysis> Analyses { get; init; }

    public List<DetailedShoppingPlan> ShoppingPlans { get; init; }

    public MarketAnalysisExecutionTimings Timings { get; internal set; }

    public IReadOnlySet<int>? FetchedItemIds { get; init; }

    public IReadOnlyDictionary<int, MarketItemAnalysis>? AnalysesByItemId { get; init; }

    public IReadOnlyDictionary<int, DetailedShoppingPlan>? ShoppingPlansByItemId { get; init; }
}
