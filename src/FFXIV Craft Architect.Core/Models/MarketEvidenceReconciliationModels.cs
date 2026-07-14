namespace FFXIV_Craft_Architect.Core.Models;

public static class MarketEvidencePolicyDefaults
{
    public static TimeSpan ReusableCacheMaxAge { get; } = TimeSpan.FromHours(1);

    public static TimeSpan AgingThreshold { get; } = TimeSpan.FromHours(6);

    public static TimeSpan MaximumRecommendationAge { get; } = TimeSpan.FromHours(12);

    public static TimeSpan VeryOldThreshold { get; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Controls how published market evidence is reconciled with the reusable cache and Universalis.
/// Cache reuse and recommendation eligibility are intentionally separate decisions.
/// </summary>
public sealed record MarketEvidenceReconciliationPolicy
{
    public TimeSpan ReusableCacheMaxAge { get; init; } = MarketEvidencePolicyDefaults.ReusableCacheMaxAge;

    public TimeSpan MaximumRecommendationAge { get; init; } = MarketEvidencePolicyDefaults.MaximumRecommendationAge;

    public MarketEvidenceRefreshMode RefreshMode { get; init; } = MarketEvidenceRefreshMode.RefreshIneligible;

    public bool RequireCompleteScope { get; init; } = true;

    public static MarketEvidenceReconciliationPolicy ForcedRefresh() =>
        new() { RefreshMode = MarketEvidenceRefreshMode.ForceRefresh };
}

public enum MarketEvidenceRefreshMode
{
    RefreshIneligible,
    ForceRefresh
}

public enum MarketEvidenceReconciliationDisposition
{
    ReusedPublished,
    RebuiltFromCache,
    Refreshed,
    Unavailable
}

public enum MarketEvidenceReconciliationReason
{
    PublishedEvidenceEligible,
    ForcedRefresh,
    PublishedEvidenceMissing,
    QuantityChanged,
    ScopeChanged,
    ScopeIncomplete,
    FreshnessUnverifiable,
    RecommendationExpired
}

public enum MarketEvidenceOrigin
{
    Universalis,
    MarketMafioso,
    ManualObservation
}

public sealed record MarketWorldEvidenceListing(
    int Quantity,
    long PricePerUnit,
    string RetainerName,
    bool IsHq,
    long? LastReviewTimeUnix = null);

/// <summary>
/// A complete observation of one item's listings on one world. External acquisition
/// tools can supply this directly instead of routing their evidence through Universalis.
/// </summary>
public sealed record MarketWorldEvidenceSnapshot(
    int ItemId,
    string DataCenter,
    string WorldName,
    MarketEvidenceOrigin Origin,
    DateTime ObservedAtUtc,
    DateTime? MarketUpdatedAtUtc,
    IReadOnlyList<MarketWorldEvidenceListing> Listings);

public sealed class MarketWorldEvidenceReconciliationRequest
{
    public MaterialAggregate Item { get; init; } = null!;

    public string DataCenter { get; init; } = string.Empty;

    public string WorldName { get; init; } = string.Empty;

    public MarketWorldEvidenceSnapshot? ObservedEvidence { get; init; }

    public MarketFetchScope Scope { get; init; } = MarketFetchScope.SelectedDataCenter;

    public string SelectedDataCenter { get; init; } = string.Empty;

    public string SelectedRegion { get; init; } = string.Empty;

    public RecommendationMode RecommendationMode { get; init; } = RecommendationMode.MinimizeTotalCost;

    public MarketAcquisitionLens Lens { get; init; } = MarketAcquisitionLens.MinimumUpfrontCost;

    public MarketAnalysisConfig AnalysisConfig { get; init; } = new();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedWorldsByDataCenter { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record MarketWorldEvidenceReconciliationResult(
    MarketItemAnalysis Analysis,
    DetailedShoppingPlan ShoppingPlan,
    MarketWorldEvidenceSnapshot Evidence);

public sealed record MarketEvidenceReconciliationItemResult(
    int ItemId,
    string ItemName,
    MarketEvidenceReconciliationDisposition Disposition,
    MarketEvidenceReconciliationReason Reason,
    TimeSpan? OldestEvidenceAge = null);

public sealed class MarketEvidenceReconciliationRequest
{
    public IReadOnlyList<MaterialAggregate> Items { get; init; } = Array.Empty<MaterialAggregate>();

    public IReadOnlyList<MarketItemAnalysis> PublishedAnalyses { get; init; } = Array.Empty<MarketItemAnalysis>();

    public IReadOnlyList<DetailedShoppingPlan> PublishedShoppingPlans { get; init; } = Array.Empty<DetailedShoppingPlan>();

    public MarketFetchScope Scope { get; init; } = MarketFetchScope.SelectedDataCenter;

    public string SelectedDataCenter { get; init; } = string.Empty;

    public string SelectedRegion { get; init; } = string.Empty;

    public RecommendationMode RecommendationMode { get; init; } = RecommendationMode.MinimizeTotalCost;

    public MarketAcquisitionLens Lens { get; init; } = MarketAcquisitionLens.MinimumUpfrontCost;

    public MarketAnalysisConfig AnalysisConfig { get; init; } = new();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedWorldsByDataCenter { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public MarketEvidenceReconciliationPolicy Policy { get; init; } = new();

    /// <summary>
    /// Optional deterministic clock used by tests and diagnostics. Production callers leave this null.
    /// </summary>
    public DateTime? EvaluatedAtUtc { get; init; }
}

public sealed record MarketEvidenceReconciliationResult(
    IReadOnlyList<MarketItemAnalysis> Analyses,
    IReadOnlyList<DetailedShoppingPlan> ShoppingPlans,
    IReadOnlyList<MarketEvidenceReconciliationItemResult> Items,
    IReadOnlyList<MaterialAggregate> ReconciledItems,
    int FetchedCount)
{
    public IReadOnlySet<int> ReusedItemIds => Items
        .Where(item => item.Disposition == MarketEvidenceReconciliationDisposition.ReusedPublished)
        .Select(item => item.ItemId)
        .ToHashSet();

    public IReadOnlySet<int> RefreshedItemIds => Items
        .Where(item => item.Disposition is
            MarketEvidenceReconciliationDisposition.RebuiltFromCache or
            MarketEvidenceReconciliationDisposition.Refreshed)
        .Select(item => item.ItemId)
        .ToHashSet();

    public IReadOnlySet<int> UnavailableItemIds => Items
        .Where(item => item.Disposition == MarketEvidenceReconciliationDisposition.Unavailable)
        .Select(item => item.ItemId)
        .ToHashSet();
}
