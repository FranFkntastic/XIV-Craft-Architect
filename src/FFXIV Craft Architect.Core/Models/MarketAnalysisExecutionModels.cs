using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Core.Models;

public sealed class MarketAnalysisExecutionRequest
{
    public IReadOnlyList<MaterialAggregate> Items { get; init; } = Array.Empty<MaterialAggregate>();

    public MarketFetchScope Scope { get; init; } = MarketFetchScope.SelectedDataCenter;

    public string SelectedDataCenter { get; init; } = string.Empty;

    public string SelectedRegion { get; init; } = string.Empty;

    public TimeSpan? MaxAge { get; init; }

    public RecommendationMode RecommendationMode { get; init; } = RecommendationMode.MinimizeTotalCost;

    public MarketAcquisitionLens Lens { get; init; } = MarketAcquisitionLens.MinimumUpfrontCost;

    public MarketAnalysisConfig AnalysisConfig { get; init; } = new();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedWorldsByDataCenter { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record MarketAnalysisExecutionResult(
    MarketEvidenceSet Evidence,
    List<MarketItemAnalysis> Analyses,
    List<DetailedShoppingPlan> ShoppingPlans);
