namespace FFXIV_Craft_Architect.Core.Models;

public enum MarketAcquisitionLens
{
    MinimumUpfrontCost,
    BulkValue
}

public enum MarketScoreBucket
{
    Optimal,
    Competitive,
    Expensive,
    PoorFit,
    Unavailable
}

public enum MarketCoverageBucket
{
    Full,
    PartialDeep,
    PartialThin,
    None
}

public enum MarketDataQualityBucket
{
    Current,
    Aging,
    Old,
    VeryOld,
    Missing
}

public enum MarketDataAgeSource
{
    UniversalisWorldUpload,
    UniversalisResponseUpload,
    LocalFetchFallback,
    Missing
}

public sealed class MarketItemAnalysis
{
    public int ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int QuantityNeeded { get; init; }
    public MarketFetchScope Scope { get; init; }
    public DateTime LoadedAtUtc { get; init; }
    public decimal AnalysisScopeBaselineUnitPrice { get; init; }
    public decimal AnalysisScopeAverageUnitPrice { get; init; }
    public decimal AnalysisScopeMedianUnitPrice { get; init; }
    public decimal SaneThresholdUnitPrice { get; init; }
    public IReadOnlyList<string> RequestedDataCenters { get; init; } = [];
    public IReadOnlyList<string> PresentDataCenters { get; init; } = [];
    public IReadOnlyList<string> MissingDataCenters { get; init; } = [];
    public bool HasCompleteScopeData => MissingDataCenters.Count == 0;
    public MarketDataQualityBucket WorstDataQualityBucket { get; init; } = MarketDataQualityBucket.Missing;
    public List<WorldMarketAnalysis> Worlds { get; init; } = new();
    public string? Warning { get; init; }
}

public sealed class WorldMarketAnalysis
{
    public string DataCenter { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public int QuantityNeeded { get; init; }
    public int CompetitiveQuantity { get; init; }
    public int LocalCompetitiveQuantity { get; init; }
    public int ScopeCompetitiveQuantity { get; init; }
    public int ScopeSaneQuantity { get; init; }
    public int ScopeInsaneQuantity { get; init; }
    public int TotalSaneQuantity { get; init; }
    public int TotalListingQuantity { get; init; }
    public decimal CompetitiveCoverageRatio { get; init; }
    public decimal ScopeCompetitiveCoverageRatio { get; init; }
    public decimal ScopeSaneCoverageRatio { get; init; }
    public decimal SaneCoverageRatio { get; init; }
    public decimal AnalysisScopeBaselineUnitPrice { get; init; }
    public decimal AnalysisScopeAverageUnitPrice { get; init; }
    public decimal AnalysisScopeMedianUnitPrice { get; init; }
    public decimal SaneThresholdUnitPrice { get; init; }
    public MarketCoverageBucket CoverageBucket { get; init; }
    public DateTime? FetchedAtUtc { get; init; }
    public DateTime? MarketUploadedAtUtc { get; init; }
    public MarketDataAgeSource DataAgeSource { get; init; } = MarketDataAgeSource.Missing;
    public TimeSpan? DataAge { get; init; }
    public decimal DataQualityScore { get; init; }
    public MarketDataQualityBucket DataQualityBucket { get; init; }
    public List<MarketPriceBand> PriceBands { get; init; } = new();
    public List<AnalyzedMarketListing> Listings { get; init; } = new();
    public List<WorldLensScore> Scores { get; init; } = new();
}

public sealed class MarketPriceBand
{
    public int FirstListingIndex { get; init; }
    public int LastListingIndex { get; init; }
    public long MinUnitPrice { get; init; }
    public long MaxUnitPrice { get; init; }
    public decimal WeightedAverageUnitPrice { get; init; }
    public int ListingCount { get; init; }
    public int Quantity { get; init; }
    public decimal? NextBreakPercent { get; init; }
    public bool IsCompetitiveShelf { get; init; }
}

public sealed class AnalyzedMarketListing
{
    public int SortIndex { get; init; }
    public int Quantity { get; init; }
    public long PricePerUnit { get; init; }
    public string RetainerName { get; init; } = string.Empty;
    public bool IsHq { get; init; }
    public bool IsOutlier { get; init; }
    public bool IsInCompetitiveShelf { get; init; }
    public DateTime? LastReviewTimeUtc { get; init; }
}

public sealed class WorldLensScore
{
    public MarketAcquisitionLens Lens { get; init; }
    public decimal Score { get; init; }
    public int Rank { get; init; }
    public MarketScoreBucket ScoreBucket { get; init; }
    public string Summary { get; init; } = string.Empty;
}
