using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Core.Models;

public enum MarketIntelligenceDetailAvailability
{
    Unknown = 0,
    Available = 1,
    Missing = 2,
    Pruned = 3,
    SummaryOnly = 4,
    Embedded = 5,
    IncompatibleSchema = 6
}

public readonly record struct MarketDemandFingerprint(string Value)
{
    public override string ToString() => Value;

    public static implicit operator MarketDemandFingerprint(string value) => new(value);
}

public readonly record struct MarketIntelligenceDetailKey(
    Guid PublicationId,
    MarketFetchScope Scope,
    int ItemId,
    MarketWorldKey? World,
    MarketDemandFingerprint DemandFingerprint);

public sealed class MarketIntelligencePublicationSummary
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid PublicationId { get; init; }

    public Guid? ActiveRunId { get; init; }

    public MarketIntelligencePublicationContext PublicationContext { get; init; } =
        MarketIntelligencePublicationContext.None;

    public IReadOnlyList<MarketItemSummary> Items { get; init; } = [];

    public IReadOnlyList<CoreMarketDataUnavailableItem> UnavailableMarketItems { get; init; } = [];

    public MarketIntelligenceDetailManifest DetailManifest { get; init; } =
        MarketIntelligenceDetailManifest.Empty;

    public bool HasDetailManifest =>
        DetailManifest.PublicationId == PublicationId && DetailManifest.Entries.Count > 0;

    public bool ContainsLoadedListingDetails => false;
}

public sealed class MarketItemSummary
{
    public int ItemId { get; init; }

    public string Name { get; init; } = string.Empty;

    public int IconId { get; init; }

    public int QuantityNeeded { get; init; }

    public MarketFetchScope Scope { get; init; }

    public MarketWorldKey? RecommendedWorld { get; init; }

    public long RecommendedTotalCost { get; init; }

    public decimal RecommendedWorldAveragePricePerUnit { get; init; }

    public string? RecommendedWorldVendorName { get; init; }

    public IReadOnlyList<VendorInfo> Vendors { get; init; } = [];

    public decimal BaselineUnitPrice { get; init; }

    public decimal AverageUnitPrice { get; init; }

    public decimal CompetitiveAverageUnitPrice { get; init; }

    public decimal MedianUnitPrice { get; init; }

    public decimal CompetitiveThresholdUnitPrice { get; init; }

    public decimal SaneThresholdUnitPrice { get; init; }

    public MarketCoverageBucket CoverageBucket { get; init; } = MarketCoverageBucket.None;

    public MarketDataQualityBucket DataQualityBucket { get; init; } = MarketDataQualityBucket.Missing;

    public MarketPriceEvaluationConfidence Confidence { get; init; } = MarketPriceEvaluationConfidence.Unknown;

    public string? Warning { get; init; }

    public MarketIntelligenceDetailKey? DetailKey { get; init; }

    public IReadOnlyList<WorldMarketSummary> Worlds { get; init; } = [];

    public IReadOnlyList<MarketSplitPurchaseSummary> RecommendedSplit { get; init; } = [];
}

public sealed class MarketSplitPurchaseSummary
{
    public MarketWorldKey World { get; init; }

    public int QuantityToBuy { get; init; }

    public decimal PricePerUnit { get; init; }

    public decimal EffectivePricePerNeededUnit { get; init; }

    public long TotalCost { get; init; }

    public bool IsPartial { get; init; }

    public string TravelContext { get; init; } = string.Empty;

    public int ExcessAvailable { get; init; }

    public MarketIntelligenceDetailKey? DetailKey { get; init; }
}

public sealed class WorldMarketSummary
{
    public MarketWorldKey World { get; init; }

    public int QuantityNeeded { get; init; }

    public int CompetitiveQuantity { get; init; }

    public int LocalCompetitiveQuantity { get; init; }

    public int ScopeCompetitiveQuantity { get; init; }

    public int ScopeSaneQuantity { get; init; }

    public int ScopeUncompetitiveQuantity { get; init; }

    public int ScopeInsaneQuantity { get; init; }

    public int TotalSaneQuantity { get; init; }

    public int TotalListingQuantity { get; init; }

    public decimal CompetitiveCoverageRatio { get; init; }

    public decimal ScopeCompetitiveCoverageRatio { get; init; }

    public decimal ScopeSaneCoverageRatio { get; init; }

    public decimal SaneCoverageRatio { get; init; }

    public decimal CompetitiveAverageUnitPrice { get; init; }

    public decimal ScopeCompetitiveAverageUnitPrice { get; init; }

    public MarketCoverageBucket CoverageBucket { get; init; } = MarketCoverageBucket.None;

    public DateTime? FetchedAtUtc { get; init; }

    public DateTime? MarketUploadedAtUtc { get; init; }

    public TimeSpan? DataAge { get; init; }

    public MarketDataAgeSource DataAgeSource { get; init; } = MarketDataAgeSource.Missing;

    public MarketDataQualityBucket DataQualityBucket { get; init; } = MarketDataQualityBucket.Missing;

    public IReadOnlyList<WorldLensScore> Scores { get; init; } = [];

    public MarketIntelligenceDetailKey? DetailKey { get; init; }
}

public sealed class MarketIntelligenceDetailManifest
{
    public static MarketIntelligenceDetailManifest Empty { get; } = new();

    public Guid PublicationId { get; init; }

    public IReadOnlyList<MarketIntelligenceDetailManifestEntry> Entries { get; init; } = [];

    public bool HasAvailableDetails =>
        Entries.Any(entry => entry.Availability == MarketIntelligenceDetailAvailability.Available);
}

public sealed class MarketIntelligenceDetailManifestEntry
{
    public MarketIntelligenceDetailKey Key { get; init; }

    public MarketIntelligenceDetailAvailability Availability { get; init; } =
        MarketIntelligenceDetailAvailability.Unknown;

    public int ListingCount { get; init; }

    public long DetailBytes { get; init; }

    public string? UnavailableReason { get; init; }
}

public sealed class MarketListingDetail
{
    public MarketIntelligenceDetailKey Key { get; init; }

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public IReadOnlyList<MarketPriceBand> PriceBands { get; init; } = [];

    public IReadOnlyList<AnalyzedMarketListing> Listings { get; init; } = [];

    public MarketPriceEvaluation? PriceEvaluation { get; init; }

    public IReadOnlyList<MarketPriceEvaluationReasonCode> ClassificationReasons { get; init; } = [];
}

public sealed record MarketIntelligencePublicationWrite(
    MarketIntelligencePublicationSummary Summary,
    IReadOnlyList<MarketListingDetail> Details,
    IReadOnlyList<MarketAnalysisRunRecord> RunRecords);

public sealed class MarketAnalysisRunRecord
{
    public Guid RunId { get; init; }

    public Guid PublicationId { get; init; }

    public MarketDemandFingerprint DemandFingerprint { get; init; }

    public string AnalyzerVersion { get; init; } = string.Empty;

    public MarketFetchScope Scope { get; init; }

    public string SelectedDataCenter { get; init; } = string.Empty;

    public string SelectedRegion { get; init; } = string.Empty;

    public DateTime StartedAtUtc { get; init; }

    public DateTime CompletedAtUtc { get; init; }

    public TimeSpan PlanBuildDuration { get; init; }

    public TimeSpan MarketFetchDuration { get; init; }

    public TimeSpan LadderAnalysisDuration { get; init; }

    public TimeSpan ShoppingPlanProjectionDuration { get; init; }

    public TimeSpan AnalysisDuration { get; init; }

    public TimeSpan ProjectionDuration { get; init; }

    public TimeSpan PublicationDuration { get; init; }

    public TimeSpan DetailPersistenceDuration { get; init; }

    public TimeSpan SourceFactPersistenceDuration { get; init; }

    public TimeSpan HotStatePublicationDuration { get; init; }

    public TimeSpan PlanPersistenceDuration { get; init; }

    public TimeSpan AutosaveDuration { get; init; }

    public string CacheMode { get; init; } = string.Empty;

    public long MarketIntelligencePayloadBytes { get; init; }

    public long LegacyPayloadBytes { get; init; }

    public long RetainedDetailBytes { get; init; }

    public int NetworkRequestCount { get; init; }

    public int FreshCacheHitCount { get; init; }

    public int StaleCacheRefreshCount { get; init; }
}

public sealed class CanonicalMarketListingFact
{
    public Guid? PublicationId { get; init; }

    public Guid? RunId { get; init; }

    public MarketDemandFingerprint? DemandFingerprint { get; init; }

    public int ItemId { get; init; }

    public MarketFetchScope Scope { get; init; }

    public int? WorldId { get; init; }

    public string DataCenter { get; init; } = string.Empty;

    public string WorldName { get; init; } = string.Empty;

    public DateTime RetrievedAtUtc { get; init; }

    public DateTime? MarketUploadedAtUtc { get; init; }

    public DateTime? LastReviewTimeUtc { get; init; }

    public int Quantity { get; init; }

    public long UnitPrice { get; init; }

    public bool IsHq { get; init; }

    public string RetainerName { get; init; } = string.Empty;

    public string? ListingId { get; init; }

    public MarketListingPriceSanity PriceSanity { get; init; } = MarketListingPriceSanity.Sane;

    public MarketListingCompetitiveness Competitiveness { get; init; } =
        MarketListingCompetitiveness.Unknown;

    public IReadOnlyList<MarketPriceEvaluationReasonCode> ClassificationReasons { get; init; } = [];

    public string SourceProvider { get; init; } = string.Empty;

    public string? SourceScopeKey { get; init; }
}

public sealed record MarketIntelligenceDetailQuery(
    Guid PublicationId,
    int? ItemId = null,
    MarketWorldKey? World = null,
    MarketDemandFingerprint? DemandFingerprint = null);

public sealed record MarketDataSourceQuery(
    int? ItemId = null,
    MarketFetchScope? Scope = null,
    string? DataCenter = null,
    string? WorldName = null,
    Guid? PublicationId = null,
    Guid? RunId = null,
    MarketDemandFingerprint? DemandFingerprint = null);

public sealed record MarketIntelligencePruneRequest(
    Guid? KeepActivePublicationId,
    DateTime? PruneDetailsOlderThanUtc,
    int? KeepRecentPublicationCount);
