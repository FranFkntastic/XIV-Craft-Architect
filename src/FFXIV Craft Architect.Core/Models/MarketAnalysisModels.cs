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
    Ancient,
    Missing
}

public enum MarketTravelPriority
{
    DataCenterTransfersFirst,
    WorldVisitsFirst
}

public enum MarketDataAgeSource
{
    UniversalisWorldUpload,
    UniversalisResponseUpload,
    LocalFetchFallback,
    Missing
}

public sealed record PublishedMarketAnalysisScopeSnapshot(
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    IReadOnlyList<string> RequestedDataCenters,
    MarketAcquisitionLens Lens,
    long PlanSessionVersion,
    DateTime PublishedAtUtc);

public enum MarketListingPriceSanity
{
    Sane = 0,
    LowOutlier = 1,
    Outlier = 2,
    Insane = 3
}

public enum MarketListingCompetitiveness
{
    Unknown = 0,
    Deal = 1,
    Competitive = 2,
    Fair = 3,
    Uncompetitive = 4,
    Excluded = 5
}

public enum MarketPriceQualityPolicy
{
    Unknown = 0,
    NqOnly = 1,
    HqOnly = 2,
    Combined = 3,
    DualChannel = 4
}

public enum MarketPriceRegionCredibility
{
    Unknown = 0,
    Thin = 1,
    Credible = 2,
    Strong = 3
}

public enum PriceBandCompetitiveness
{
    Unknown = 0,
    LowOutlier = 1,
    Competitive = 2,
    Uncompetitive = 3,
    Insane = 4
}

public enum PriceBandDepth
{
    None = 0,
    Thin = 1,
    Usable = 2,
    Deep = 3
}

public enum MarketPriceEvaluationConfidence
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public enum MarketPriceEvaluationReasonCode
{
    Unknown = 0,
    AcceptedDueToQuantityDespiteLowDiversity = 1,
    ExcludedFromCentralRegionButProcurementEligible = 2,
    RejectedAsThinLowRegion = 3,
    RejectedAsHighOutlierRegion = 4,
    QualityChannelFallbackToCombined = 5
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
    public decimal AnalysisCompetitiveAverageUnitPrice { get; init; }
    public int ProcurementSignalQuantity { get; init; }
    public decimal PrimaryProcurementShelfAverageUnitPrice { get; init; }
    public long CostToCoverTotalGil { get; init; }
    public decimal CostToCoverUnitPrice { get; init; }
    public long CostToCoverMaxUnitPrice { get; init; }
    public decimal AnalysisScopeMedianUnitPrice { get; init; }
    public decimal CompetitiveThresholdUnitPrice { get; init; }
    public decimal SaneThresholdUnitPrice { get; init; }
    public IReadOnlyList<string> RequestedDataCenters { get; init; } = [];
    public IReadOnlyList<string> PresentDataCenters { get; init; } = [];
    public IReadOnlyList<string> MissingDataCenters { get; init; } = [];
    public bool HasCompleteScopeData => MissingDataCenters.Count == 0;
    public MarketDataQualityBucket WorstDataQualityBucket { get; init; } = MarketDataQualityBucket.Missing;
    public MarketPriceEvaluation? PriceEvaluation { get; init; }
    public List<MarketScopePriceBand> ScopePriceBands { get; init; } = new();
    public List<WorldMarketAnalysis> Worlds { get; init; } = new();
    public string? Warning { get; init; }
}

public sealed class MarketPriceEvaluation
{
    public int ItemId { get; init; }
    public MarketFetchScope Scope { get; init; }
    public MarketPriceQualityPolicy QualityPolicy { get; init; }
    public DateTime EvaluatedAtUtc { get; init; }
    public MarketCentralPriceRegion CentralRegion { get; init; } = new();
    public MarketPriceThresholds Thresholds { get; init; } = new();
    public MarketListingClassCounts ListingClassCounts { get; init; } = new();
    public MarketPriceEvaluationConfidence Confidence { get; init; }
    public MarketPriceEvaluationDiagnostics Diagnostics { get; init; } = new();
}

public sealed class MarketPriceEvaluationContext
{
    public decimal BaselineUnitPrice { get; init; }
    public decimal AverageUnitPrice { get; init; }
    public decimal CompetitiveAverageUnitPrice { get; init; }
    public decimal MedianUnitPrice { get; init; }
    public decimal CompetitiveThresholdUnitPrice { get; init; }
    public decimal SaneThresholdUnitPrice { get; init; }
    public long LowOutlierMaxUnitPrice { get; init; }
    public MarketPriceEvaluation PriceEvaluation { get; init; } = new();
}

public sealed class MarketCentralPriceRegion
{
    public long MinUnitPrice { get; init; }
    public long MaxUnitPrice { get; init; }
    public decimal MedianUnitPrice { get; init; }
    public decimal WeightedAverageUnitPrice { get; init; }
    public int ListingCount { get; init; }
    public int TotalQuantity { get; init; }
    public int DistinctRetainerCount { get; init; }
    public int DistinctWorldCount { get; init; }
    public MarketDataQualityBucket DataQualityBucket { get; init; } = MarketDataQualityBucket.Missing;
    public MarketPriceRegionCredibility Credibility { get; init; }
}

public sealed class MarketPriceThresholds
{
    public decimal DealCeilingUnitPrice { get; init; }
    public decimal CompetitiveCeilingUnitPrice { get; init; }
    public decimal SaneCeilingUnitPrice { get; init; }
    public decimal InsaneFloorUnitPrice { get; init; }
}

public sealed class MarketListingClassCounts
{
    public int DealCount { get; init; }
    public int CompetitiveCount { get; init; }
    public int FairCount { get; init; }
    public int UncompetitiveCount { get; init; }
    public int ExcludedCount { get; init; }
    public int LowOutlierCount { get; init; }
    public int SaneCount { get; init; }
    public int OutlierCount { get; init; }
    public int InsaneCount { get; init; }
}

public sealed class MarketPriceEvaluationDiagnostics
{
    public List<MarketPriceEvaluationReasonCode> CompactReasonCodes { get; init; } = new();
    public List<MarketPriceRegionSummary> CompactRegionSummaries { get; init; } = new();
    public List<MarketPriceGapSummary> DetectedPriceGapSummaries { get; init; } = new();
    public bool DebugDetailAvailable { get; init; }
}

public sealed class MarketPriceRegionSummary
{
    public long MinUnitPrice { get; init; }
    public long MaxUnitPrice { get; init; }
    public int ListingCount { get; init; }
    public int TotalQuantity { get; init; }
    public MarketPriceRegionCredibility Credibility { get; init; }
    public MarketPriceEvaluationReasonCode ReasonCode { get; init; }
}

public sealed class MarketPriceGapSummary
{
    public long BeforeUnitPrice { get; init; }
    public long AfterUnitPrice { get; init; }
    public decimal BreakPercent { get; init; }
}

public sealed class WorldMarketAnalysis
{
    public string DataCenter { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public int QuantityNeeded { get; init; }
    public int PrimaryUsableQuantity { get; init; }
    public int PriceSignalQuantity { get; init; }
    public int ScopeSaneQuantity { get; init; }
    public int ScopeUncompetitiveQuantity { get; init; }
    public int ScopeInsaneQuantity { get; init; }
    public int TotalSaneQuantity { get; init; }
    public int TotalListingQuantity { get; init; }
    public long CostToCoverTotalGil { get; init; }
    public decimal CostToCoverUnitPrice { get; init; }
    public long CostToCoverMaxUnitPrice { get; init; }
    public decimal PrimaryUsableCoverageRatio { get; init; }
    public decimal PriceSignalCoverageRatio { get; init; }
    public decimal ScopeSaneCoverageRatio { get; init; }
    public decimal SaneCoverageRatio { get; init; }
    public decimal AnalysisScopeBaselineUnitPrice { get; init; }
    public decimal AnalysisScopeAverageUnitPrice { get; init; }
    public decimal AnalysisCompetitiveAverageUnitPrice { get; init; }
    public decimal PrimaryUsableAverageUnitPrice { get; init; }
    public decimal PriceSignalAverageUnitPrice { get; init; }
    public decimal AnalysisScopeMedianUnitPrice { get; init; }
    public decimal CompetitiveThresholdUnitPrice { get; init; }
    public decimal SaneThresholdUnitPrice { get; init; }
    public MarketCoverageBucket CoverageBucket { get; init; }
    public PriceBandDepth PriceSignalDepth { get; init; }
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
    public PriceBandCompetitiveness Competitiveness { get; init; }
    public PriceBandDepth Depth { get; init; }
    public bool IsPriceSignalBand { get; init; }
    public bool IsPrimaryUsableBand { get; init; }
}

public sealed class MarketScopePriceBand
{
    public long MinUnitPrice { get; init; }
    public long MaxUnitPrice { get; init; }
    public decimal WeightedAverageUnitPrice { get; init; }
    public int TotalQuantity { get; init; }
    public int ListingCount { get; init; }
    public int DistinctWorldCount { get; init; }
    public int DistinctRetainerCount { get; init; }
    public PriceBandCompetitiveness Competitiveness { get; init; }
    public PriceBandDepth Depth { get; init; }
    public decimal? BreakPercentToNextBand { get; init; }
}

public sealed class AnalyzedMarketListing
{
    public int SortIndex { get; init; }
    public int Quantity { get; init; }
    public long PricePerUnit { get; init; }
    public string RetainerName { get; init; } = string.Empty;
    public bool IsHq { get; init; }
    public MarketListingPriceSanity PriceSanity { get; init; }
    public MarketListingCompetitiveness Competitiveness { get; init; }
    public bool IsInPriceSignalBand { get; init; }
    public bool IsInPrimaryUsableBand { get; init; }
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
