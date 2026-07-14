using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Core.Models;

/// <summary>
/// Configuration for market analysis behavior.
/// Controls the trade-off between cost optimization and travel efficiency.
/// </summary>
public class MarketAnalysisConfig
{
    private int _travelTolerance;

    /// <summary>
    /// Maximum number of worlds to visit for a single item.
    /// Uses soft limit based on tier 1 recommendations if null.
    /// </summary>
    public int? MaxWorldsPerItem { get; set; }

    /// <summary>
    /// Route-wide tolerance for world and data-center travel.
    /// 0 minimizes travel; 11 requires the cheapest trustworthy route.
    /// </summary>
    public int TravelTolerance
    {
        get => _travelTolerance;
        set => _travelTolerance = Math.Clamp(value, 0, 11);
    }

    /// <summary>
    /// Whether to enable multi-world split purchases.
    /// </summary>
    public bool EnableSplitWorld { get; set; } = false;

    /// <summary>
    /// Whether route travel starts from <see cref="HomeDataCenter"/>.
    /// The origin affects travel comparison but does not require a purchase there.
    /// </summary>
    public bool StartFromHomeDataCenter { get; set; }

    /// <summary>
    /// Optional route origin used when <see cref="StartFromHomeDataCenter"/> is enabled.
    /// </summary>
    public string HomeDataCenter { get; set; } = string.Empty;

    /// <summary>
    /// Determines which travel dimension is minimized first after the gil ceiling is applied.
    /// </summary>
    public MarketTravelPriority TravelPriority { get; set; } = MarketTravelPriority.DataCenterTransfersFirst;

    /// <summary>
    /// Maximum price multiplier for filtering out fraud/gouging listings.
    /// Listings priced above (ModePrice × Multiplier) are excluded.
    /// Null disables filtering. Default 2.5x.
    /// </summary>
    public decimal? MaxPriceMultiplier { get; set; } = 2.5m;

    /// <summary>
    /// Creates config from settings service.
    /// </summary>
    public static MarketAnalysisConfig FromSettings(SettingsService settings)
    {
        return new MarketAnalysisConfig
        {
            MaxWorldsPerItem = settings.Get<int?>("analysis.max_worlds_per_item", null),
            EnableSplitWorld = settings.Get("analysis.enable_split_world", false),
            TravelTolerance = settings.Get("analysis.travel_tolerance", 0),
            StartFromHomeDataCenter = settings.Get("analysis.start_from_home_data_center", false),
            HomeDataCenter = settings.Get("analysis.home_data_center", string.Empty) ?? string.Empty,
            TravelPriority = settings.Get(
                "analysis.travel_priority",
                MarketTravelPriority.DataCenterTransfersFirst)
        };
    }

    /// <summary>
    /// Gets the effective max worlds per item based on tier 1 count.
    /// </summary>
    public int GetEffectiveMaxWorlds(int tier1WorldCount)
    {
        if (MaxWorldsPerItem.HasValue)
        {
            return MaxWorldsPerItem.Value;
        }

        // Default: reasonable limit based on tier 1 count
        return Math.Max(3, tier1WorldCount + 1);
    }
}

/// <summary>
/// Sort options for market shopping plan display.
/// </summary>
public enum MarketSortOption
{
    /// <summary>Sort by recommended world (best value first).</summary>
    ByRecommended,
    /// <summary>Sort alphabetically by item name.</summary>
    Alphabetical
}

/// <summary>
/// Filter mode for world recommendations.
/// </summary>
public enum RecommendationMode
{
    /// <summary>Minimize total cost for exact quantity needed.</summary>
    MinimizeTotalCost,
    /// <summary>Maximize value - buy more at good price per unit.</summary>
    MaximizeValue,
    /// <summary>Best price per unit regardless of quantity.</summary>
    BestUnitPrice
}

public static class MarketShoppingConstants
{
    public const string VendorWorldName = "Vendor";
}

public static class TravelContextConstants
{
    public const string Primary = "Primary";
    public const string Consolidated = "Consolidated";
    public const string Supplemental = "Supplemental";
}

public readonly record struct MarketWorldKey
{
    public MarketWorldKey(string dataCenter, string worldName)
    {
        DataCenter = NormalizeKeyPart(dataCenter);
        WorldName = NormalizeKeyPart(worldName);
    }

    public string DataCenter { get; init; }
    public string WorldName { get; init; }

    public bool Equals(MarketWorldKey other)
    {
        return string.Equals(DataCenter, other.DataCenter, StringComparison.OrdinalIgnoreCase)
            && string.Equals(WorldName, other.WorldName, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(DataCenter ?? string.Empty),
            StringComparer.OrdinalIgnoreCase.GetHashCode(WorldName ?? string.Empty));
    }

    private static string NormalizeKeyPart(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}

public readonly record struct MarketItemWorldKey(int ItemId, MarketWorldKey World);

/// <summary>
/// A candidate purchase route for one market choice.
/// </summary>
public class MarketPurchaseCandidate
{
    public MarketPurchaseCandidate(long gilCost, IEnumerable<MarketWorldKey> worlds)
    {
        GilCost = gilCost;
        Worlds = worlds.Distinct().ToList();
    }

    public long GilCost { get; }
    public IReadOnlyList<MarketWorldKey> Worlds { get; }
    public int ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int QuantityNeeded { get; init; }
    public int QuantityFulfilled { get; init; }
    public long MarketEvidencePenalty { get; init; }
    public bool HasTrustworthyEvidence { get; init; } = true;
    public WorldShoppingSummary? SingleWorld { get; init; }
    public List<SplitWorldPurchase>? Split { get; init; }
    public MarketCoverageOption? Coverage { get; init; }
    public bool IsSingleWorldPurchase => SingleWorld != null || Coverage?.Worlds.Count == 1;
    public bool IsSplitPurchase => Split?.Count > 0 || Coverage?.Worlds.Count > 1;
    public bool IsFullyFulfilled => QuantityNeeded <= 0 || QuantityFulfilled >= QuantityNeeded;
    public bool HasInsufficientStock => QuantityNeeded > 0 && QuantityFulfilled < QuantityNeeded;
}

/// <summary>
/// Worlds and data centers already selected for the route being built.
/// </summary>
public class MarketRouteState
{
    private readonly HashSet<MarketWorldKey> _worlds;
    private readonly HashSet<string> _dataCenters;

    public MarketRouteState()
        : this([])
    {
    }

    public MarketRouteState(IEnumerable<MarketWorldKey> worlds)
    {
        _worlds = worlds.ToHashSet();
        _dataCenters = _worlds
            .Select(w => w.DataCenter)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<MarketWorldKey> Worlds => _worlds;
    public IReadOnlyCollection<string> DataCenters => _dataCenters;

    public bool ContainsWorld(MarketWorldKey world) => _worlds.Contains(world);
    public bool ContainsDataCenter(string dataCenter) => _dataCenters.Contains(dataCenter);
}

public sealed class RoutePenaltyBreakdown
{
    private readonly long _costPlusRoutePenalty;

    public RoutePenaltyBreakdown(
        long gilCost,
        int addedWorldCount,
        int addedDataCenterCount,
        long routePenalty,
        long costPlusRoutePenalty,
        int travelTolerance,
        MarketTravelPriority travelPriority = MarketTravelPriority.DataCenterTransfersFirst)
        : this(
            gilCost,
            addedWorldCount,
            addedDataCenterCount,
            routePenalty,
            0,
            costPlusRoutePenalty,
            travelTolerance,
            travelPriority)
    {
    }

    public RoutePenaltyBreakdown(
        long gilCost,
        int addedWorldCount,
        int addedDataCenterCount,
        long routePenalty,
        long marketEvidencePenalty,
        long costPlusRoutePenalty,
        int travelTolerance,
        MarketTravelPriority travelPriority = MarketTravelPriority.DataCenterTransfersFirst)
    {
        GilCost = gilCost;
        AddedWorldCount = addedWorldCount;
        AddedDataCenterCount = addedDataCenterCount;
        RoutePenalty = routePenalty;
        MarketEvidencePenalty = marketEvidencePenalty;
        _costPlusRoutePenalty = costPlusRoutePenalty;
        TravelTolerance = travelTolerance;
        TravelPriority = travelPriority;
    }

    public long GilCost { get; }
    public int AddedWorldCount { get; }
    public int AddedDataCenterCount { get; }
    public long RoutePenalty { get; }
    public long MarketEvidencePenalty { get; }
    public int TravelTolerance { get; }
    public MarketTravelPriority TravelPriority { get; }

    /// <summary>
    /// Returns the numeric score only when numeric ordering is valid.
    /// TravelTolerance 0 uses lexicographic route ordering; use MarketRouteScoring.CompareCandidates instead.
    /// </summary>
    public long GetSortableNumericScore()
    {
        if (TravelTolerance == 0)
        {
            throw new InvalidOperationException(
                "TravelTolerance 0 uses route-first ordering. Use MarketRouteScoring.CompareCandidates or CompareScores.");
        }

        return _costPlusRoutePenalty;
    }

    internal long GetComparisonNumericScore() => _costPlusRoutePenalty;
}

public static class MarketRouteScoring
{
    private static readonly decimal?[] MaximumPremiumRates =
    [
        null,
        1.00m,
        0.75m,
        0.50m,
        0.35m,
        0.25m,
        0.18m,
        0.12m,
        0.08m,
        0.05m,
        0.02m,
        0m
    ];

    public static decimal? GetMaximumPremiumRate(int travelTolerance)
    {
        return MaximumPremiumRates[Math.Clamp(travelTolerance, 0, 11)];
    }

    public static string GetToleranceLabel(int travelTolerance)
    {
        return Math.Clamp(travelTolerance, 0, 11) switch
        {
            0 => "Fewest stops",
            11 => "Lowest cost",
            var value => $"Up to {GetMaximumPremiumRate(value)!.Value:P0} more gil for fewer stops"
        };
    }

    public static RoutePenaltyBreakdown ScoreCandidate(
        MarketPurchaseCandidate candidate,
        MarketRouteState currentRoute,
        MarketAnalysisConfig config)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(currentRoute);
        ArgumentNullException.ThrowIfNull(config);

        var addedWorldCount = candidate.Worlds.Count(w => !currentRoute.ContainsWorld(w));
        var addedDataCenterCount = candidate.Worlds
            .Select(w => w.DataCenter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(dc => !currentRoute.ContainsDataCenter(dc));

        return new RoutePenaltyBreakdown(
            candidate.GilCost,
            addedWorldCount,
            addedDataCenterCount,
            0,
            candidate.MarketEvidencePenalty,
            candidate.GilCost,
            config.TravelTolerance,
            config.TravelPriority);
    }

    public static int CompareCandidates(
        MarketPurchaseCandidate left,
        MarketPurchaseCandidate right,
        MarketRouteState currentRoute,
        MarketAnalysisConfig config)
    {
        return CompareScores(
            ScoreCandidate(left, currentRoute, config),
            ScoreCandidate(right, currentRoute, config));
    }

    public static int CompareScores(RoutePenaltyBreakdown left, RoutePenaltyBreakdown right)
    {
        if (left.TravelTolerance != right.TravelTolerance)
        {
            throw new ArgumentException(
                "Route score comparisons require matching TravelTolerance values.",
                nameof(right));
        }

        if (left.TravelPriority != right.TravelPriority)
        {
            throw new ArgumentException(
                "Route score comparisons require matching travel priorities.",
                nameof(right));
        }

        var premiumRate = GetMaximumPremiumRate(left.TravelTolerance);
        var cheapestCost = Math.Min(left.GilCost, right.GilCost);
        var leftEligible = IsWithinPremium(left.GilCost, cheapestCost, premiumRate);
        var rightEligible = IsWithinPremium(right.GilCost, cheapestCost, premiumRate);
        if (leftEligible != rightEligible)
        {
            return leftEligible ? -1 : 1;
        }

        var firstTravelComparison = left.TravelPriority == MarketTravelPriority.WorldVisitsFirst
            ? left.AddedWorldCount.CompareTo(right.AddedWorldCount)
            : left.AddedDataCenterCount.CompareTo(right.AddedDataCenterCount);
        if (firstTravelComparison != 0)
        {
            return firstTravelComparison;
        }

        var secondTravelComparison = left.TravelPriority == MarketTravelPriority.WorldVisitsFirst
            ? left.AddedDataCenterCount.CompareTo(right.AddedDataCenterCount)
            : left.AddedWorldCount.CompareTo(right.AddedWorldCount);
        if (secondTravelComparison != 0)
        {
            return secondTravelComparison;
        }

        var evidenceComparison = left.MarketEvidencePenalty.CompareTo(right.MarketEvidencePenalty);
        if (evidenceComparison != 0)
        {
            return evidenceComparison;
        }

        return left.GilCost.CompareTo(right.GilCost);
    }

    internal static bool IsWithinPremium(long cost, long cheapestCost, decimal? premiumRate)
    {
        if (premiumRate == null)
        {
            return true;
        }

        if (cheapestCost <= 0)
        {
            return cost <= cheapestCost;
        }

        var maximumCost = cheapestCost * (1m + premiumRate.Value);
        return cost <= maximumCost;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (left > 0 && right > long.MaxValue - left)
        {
            return long.MaxValue;
        }

        return left + right;
    }

}

/// <summary>
/// Complete shopping analysis for a single item across all worlds.
/// 
/// DATA FLOW:
/// This is the primary output of MarketShoppingService.CalculateDetailedShoppingPlansAsync.
/// One instance is created per market item in the crafting plan.
/// 
/// STRUCTURE:
/// - Item identification (ItemId, Name, IconId, QuantityNeeded)
/// - DC-wide statistics (DCAveragePrice, HQAveragePrice)
/// - Per-world options (WorldOptions list with full analysis)
/// - Recommendation (RecommendedWorld - the best single world)
/// - Multi-world split (RecommendedSplit - if single world can't fulfill)
/// - Vendor alternative (Vendors - if available from gil vendor)
/// 
/// USAGE IN UI:
/// WPF: Bound to MarketCardViewModel for display in MarketAnalysisView
/// Web: Passed to MarketCard.razor component for display
/// Both: Grouped by world in procurement planner views
/// 
/// KEY DECISIONS:
/// - RecommendedWorld is null if no world has sufficient stock
/// - RecommendedSplit is null if RecommendedWorld can fulfill full quantity
/// - Error is set if market data is missing or invalid
/// </summary>
public class DetailedShoppingPlan
{
    public int ItemId { get; set; }
    public string UniversalisUrl => UniversalisService.GetMarketUrl(ItemId);

    public string Name { get; set; } = string.Empty;
    public int IconId { get; set; }
    public int QuantityNeeded { get; set; }
    public decimal DCAveragePrice { get; set; }
    public List<WorldShoppingSummary> WorldOptions { get; set; } = new();
    public WorldShoppingSummary? RecommendedWorld { get; set; }
    public MarketCoverageSet? CoverageSet { get; set; }
    public string? Error { get; set; }
    public string? MarketDataWarning { get; set; }

    /// <summary>
    /// HQ price data if available (for endgame crafting considerations).
    /// </summary>
    public decimal? HQAveragePrice { get; set; }
    public bool HasHqData => HQAveragePrice.HasValue;

    /// <summary>
    /// Vendor information for items that can be purchased from NPC vendors.
    /// 
    /// POPULATED WHEN:
    /// - Item has PriceSource.Vendor (determined by PriceCheckService)
    /// - Garland API returned vendor data during plan building
    /// - Item can be bought with gil (special currency vendors excluded)
    /// 
    /// CONTENTS:
    /// - Gil vendors only (special currency vendors filtered out)
    /// - All locations for multi-location vendors (Material Supplier, etc.)
    /// - Vendor name, location, price
    /// 
    /// UI DISPLAY:
    /// - Shown in procurement plan as "Vendor" world card
    /// - Gold background to distinguish from market worlds
    /// - Vendor location shown for user's convenience
    /// - Shop icon indicates vendor source
    /// 
    /// COST CALCULATION:
    /// - Vendor items use fixed price (no market fluctuation)
    /// - Price taken from cheapest gil vendor
    /// - Always sufficient stock (unlimited vendor supply)
    /// 
    /// COMPARISON WITH VendorOptions (PlanNode):
    /// - VendorOptions (on PlanNode): ALL vendors including special currency
    /// - Vendors (on DetailedShoppingPlan): Gil vendors only, for procurement display
    /// </summary>
    public List<VendorInfo> Vendors { get; set; } = new();

    public bool HasOptions => WorldOptions.Count > 0;

    /// <summary>
    /// Total quantity available across ALL worlds (for stock availability checking).
    /// Vendors always have unlimited stock.
    /// </summary>
    public int TotalAvailableQuantity =>
        RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName ? QuantityNeeded : WorldOptions.Sum(w => w.TotalQuantityPurchased);

    /// <summary>
    /// Whether the total available stock across all worlds is sufficient.
    /// Vendors always have sufficient stock.
    /// </summary>
    public bool HasSufficientStock =>
        RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName || TotalAvailableQuantity >= QuantityNeeded;

    /// <summary>
    /// The shortfall quantity if stock is insufficient across all worlds.
    /// Always 0 for vendor purchases.
    /// </summary>
    public int StockShortfall =>
        RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName ? 0 : Math.Max(0, QuantityNeeded - TotalAvailableQuantity);

    /// <summary>
    /// Multi-world split recommendation for items that can't be fulfilled on a single world.
    /// Null if the item can be fully purchased on the recommended world.
    /// </summary>
    public List<SplitWorldPurchase>? RecommendedSplit { get; set; }

    /// <summary>
    /// Whether this item requires a multi-world split purchase.
    /// </summary>
    public bool RequiresSplitPurchase => RecommendedSplit != null && RecommendedSplit.Count > 1;

    /// <summary>
    /// Total cost if purchasing via the recommended split (null if no split needed).
    /// </summary>
    public long? SplitTotalCost => RecommendedSplit?.Sum(s => s.TotalCost);

    /// <summary>
    /// Savings percentage compared to single-world purchase (null if no viable single-world option).
    /// </summary>
    public decimal? SplitSavingsPercent
    {
        get
        {
            if (RecommendedWorld == null || SplitTotalCost == null)
            {
                return null;
            }

            var singleCost = RecommendedWorld.TotalCost;
            var splitCost = SplitTotalCost.Value;
            return singleCost > 0 ? (singleCost - splitCost) / (decimal)singleCost * 100 : 0;
        }
    }
}

/// <summary>
/// Represents a partial purchase from a specific world as part of a multi-world split.
/// </summary>
public class SplitWorldPurchase
{
    private decimal? _effectivePricePerNeededUnit;

    /// <summary>
    /// The data center this world belongs to.
    /// </summary>
    public string DataCenter { get; set; } = string.Empty;

    /// <summary>
    /// The world to purchase from.
    /// </summary>
    public string WorldName { get; set; } = string.Empty;

    /// <summary>
    /// Quantity to buy from this world.
    /// </summary>
    public int QuantityToBuy { get; set; }

    /// <summary>
    /// Actual average listing unit price for the selected stacks on this world.
    /// </summary>
    public decimal PricePerUnit { get; set; }

    /// <summary>
    /// Effective cost per needed unit after full-stack purchases are included.
    /// </summary>
    public decimal EffectivePricePerNeededUnit
    {
        get => _effectivePricePerNeededUnit
            ?? (QuantityToBuy > 0 ? TotalCost / (decimal)QuantityToBuy : PricePerUnit);
        set => _effectivePricePerNeededUnit = value;
    }

    /// <summary>
    /// Total cost for the selected listing stacks in this portion.
    /// </summary>
    public long TotalCost { get; set; }

    /// <summary>
    /// Whether this is a partial world (not the primary recommendation).
    /// </summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Context for why this world was selected:
    /// - "Primary": Best price, main destination
    /// - "Consolidated": Selected because visiting for other items
    /// - "Supplemental": Needed to complete quantity after primary
    /// </summary>
    public string TravelContext { get; set; } = TravelContextConstants.Primary;

    /// <summary>
    /// How much stock is available beyond what we need.
    /// </summary>
    public int ExcessAvailable { get; set; }

    /// <summary>
    /// Listings used for this purchase portion.
    /// </summary>
    public List<ShoppingListingEntry> Listings { get; set; } = new();
}

/// <summary>
/// Shopping summary for a specific world with value metrics.
/// </summary>
public class WorldShoppingSummary
{
    public string DataCenter { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public int WorldId { get; set; }
    public long TotalCost { get; set; }
    public decimal AveragePricePerUnit { get; set; }
    public int ListingsUsed { get; set; }
    public List<ShoppingListingEntry> Listings { get; set; } = new();

    /// <summary>
    /// Listings excluded due to excessive pricing (fraud/gouging detection).
    /// These are priced above the configured multiplier of mode price.
    /// </summary>
    public List<ShoppingListingEntry> ExcludedListings { get; set; } = new();

    public bool IsFullyUnderAverage { get; set; }
    public int TotalQuantityPurchased { get; set; }
    public int ExcessQuantity { get; set; }

    /// <summary>
    /// The mode price per unit - the price with the highest available quantity.
    /// Used for ValueScore calculation in split mode.
    /// </summary>
    public long ModePricePerUnit { get; set; }

    /// <summary>
    /// Value score: lower is better. Calculated by CalculateValueScore method.
    /// Single-world mode: ValueScore = TotalCost (Infinity if can't fulfill)
    /// Split mode: ValueScore = ModePrice / StockRatio
    /// </summary>
    public decimal ValueScore { get; set; }

    /// <summary>
    /// Projected freshness and reliability from market analysis.
    /// Actual gil costs remain in TotalCost; these fields only affect recommendation ordering.
    /// </summary>
    public decimal MarketDataQualityScore { get; set; } = 100;
    public MarketDataQualityBucket MarketDataQualityBucket { get; set; } = MarketDataQualityBucket.Current;
    public MarketDataAgeSource MarketDataAgeSource { get; set; } = MarketDataAgeSource.UniversalisWorldUpload;
    public TimeSpan? MarketDataAge { get; set; }
    public DateTime? MarketUploadedAtUtc { get; set; }
    public int LensRank { get; set; } = int.MaxValue;
    public MarketScoreBucket LensScoreBucket { get; set; } = MarketScoreBucket.Unavailable;
    public decimal ProcurementPriorityScore { get; set; }

    /// <summary>
    /// For vendor purchases: the specific vendor name and location.
    /// Only populated when WorldName is "Vendor".
    /// </summary>
    public string? VendorName { get; set; }

    /// <summary>
    /// Whether this world has competitively priced listings.
    /// </summary>
    public bool IsCompetitive => BestSingleListing != null &&
        BestSingleListing.PricePerUnit <= AveragePricePerUnit * 0.9m;

    /// <summary>
    /// Whether this world has sufficient stock to fulfill the full quantity needed.
    /// </summary>
    public bool HasSufficientStock { get; set; } = true;

    /// <summary>
    /// How many more items are needed if this world has insufficient stock.
    /// </summary>
    public int ShortfallQuantity { get; set; }

    /// <summary>
    /// The best single listing on this world (for value comparison).
    /// </summary>
    public ShoppingListingEntry? BestSingleListing { get; set; }

    /// <summary>
    /// World classification/status (Congested, Standard, Preferred, etc.)
    /// </summary>
    public WorldClassification Classification { get; set; } = WorldClassification.Standard;

    /// <summary>
    /// Whether this world is congested (cannot travel to for purchases).
    /// </summary>
    public bool IsCongested => Classification == WorldClassification.Congested;

    /// <summary>
    /// Whether this world is the user's home world.
    /// Home worlds bypass congested restrictions since you can always purchase there.
    /// </summary>
    public bool IsHomeWorld { get; set; }

    /// <summary>
    /// Whether this world has been blacklisted by the user.
    /// Blacklisted worlds are excluded from recommendations (unless it's the home world).
    /// </summary>
    public bool IsBlacklisted { get; set; }

    /// <summary>
    /// Whether travel to this world is currently prohibited (real-time from Waitingway API).
    /// This indicates the world is at capacity for visitors.
    /// </summary>
    public bool IsTravelProhibited { get; set; }

    /// <summary>
    /// Warning message for congested worlds during peak hours.
    /// </summary>
    public string? CongestedWarning { get; set; }

    public string CostDisplay => $"{TotalCost:N0}g";
    public string PricePerUnitDisplay => $"{AveragePricePerUnit:N0}g";
    public string ValueScoreDisplay => $"{ValueScore:N0}g";
    public bool HasExcess => ExcessQuantity > 0;

    /// <summary>
    /// Whether this world has any accessibility issues (congested, blacklisted, or travel prohibited).
    /// </summary>
    public bool HasAccessibilityIssues => IsCongested || IsBlacklisted || IsTravelProhibited;

    /// <summary>
    /// Whether any listings were excluded due to pricing.
    /// </summary>
    public bool HasExcludedListings => ExcludedListings?.Any() == true;

    /// <summary>
    /// Min and max price multiplier of excluded listings compared to mode price.
    /// </summary>
    public (decimal Min, decimal Max)? ExcludedPriceMultipliers
    {
        get
        {
            if (!HasExcludedListings || ModePricePerUnit <= 0)
            {
                return null;
            }

            var multipliers = ExcludedListings
                .Select(l => (decimal)l.PricePerUnit / ModePricePerUnit)
                .ToList();

            return (multipliers.Min(), multipliers.Max());
        }
    }
}

public static class MarketWorldRecommendationScoring
{
    public static decimal CalculatePriorityScore(long gilCost, WorldShoppingSummary world)
    {
        if (gilCost <= 0)
        {
            return decimal.MaxValue;
        }

        return gilCost + CalculateEvidencePenalty(gilCost, world);
    }

    public static long CalculateEvidencePenalty(long gilCost, WorldShoppingSummary world)
    {
        if (gilCost <= 0)
        {
            return 0;
        }

        var qualityScore = Math.Clamp(world.MarketDataQualityScore, 0, 100);
        if (world.MarketDataQualityBucket == MarketDataQualityBucket.Missing)
        {
            qualityScore = 0;
        }

        var effectiveQuality = Math.Max(qualityScore, 5);
        var qualityPenalty = gilCost * (100m / effectiveQuality - 1m);
        var rankPenalty = world.LensRank is > 1 and < int.MaxValue
            ? gilCost * Math.Min(world.LensRank - 1, 25) * 0.02m
            : 0m;

        return ToLongSaturating(qualityPenalty + rankPenalty);
    }

    private static long ToLongSaturating(decimal value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(value);
    }
}

/// <summary>
/// Individual listing entry in a shopping plan.
/// </summary>
public class ShoppingListingEntry
{
    public int Quantity { get; set; }  // Full stack quantity available
    public long PricePerUnit { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public bool IsUnderAverage { get; set; }
    public bool IsHq { get; set; }
    public int NeededFromStack { get; set; }  // How many we actually need from this stack
    public int ExcessQuantity { get; set; }  // How many extra we'll have

    /// <summary>
    /// If true, this listing is shown for value comparison but not needed for quantity.
    /// </summary>
    public bool IsAdditionalOption { get; set; }

    public string SubtotalDisplay => $"{(Quantity * PricePerUnit):N0}g";
    public string QuantityDisplay => ExcessQuantity > 0
        ? $"x{Quantity} (need {NeededFromStack}, +{ExcessQuantity} extra)"
        : $"x{Quantity}";

    public string HqIndicator => IsHq ? " [HQ]" : "";
    public string ListingTypeIndicator => IsAdditionalOption ? " (additional option)" : "";
}

/// <summary>
/// Analysis comparing cost to buy vs craft an item.
/// Includes HQ/NQ considerations for endgame crafting.
/// </summary>
public class CraftVsBuyAnalysis
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }

    // NQ Prices (default for most items)
    public decimal BuyCostNq { get; set; }
    public decimal CraftCost { get; set; }
    public decimal PotentialSavingsNq { get; set; }
    public decimal SavingsPercentNq { get; set; }

    // HQ Prices (important for endgame)
    public decimal BuyCostHq { get; set; }
    public decimal PotentialSavingsHq { get; set; }
    public decimal SavingsPercentHq { get; set; }
    public bool HasHqData { get; set; }

    // Legacy properties for backward compatibility
    public decimal BuyCost => BuyCostNq;
    public decimal PotentialSavings => PotentialSavingsNq;
    public decimal SavingsPercent => SavingsPercentNq;

    public bool IsCurrentlySetToCraft { get; set; }
    public CraftRecommendation RecommendationNq { get; set; }
    public CraftRecommendation RecommendationHq { get; set; }

    /// <summary>
    /// Whether HQ is required for this item (based on project settings or user preference).
    /// </summary>
    public bool IsHqRequired { get; set; }

    /// <summary>
    /// The effective recommendation based on HQ requirement.
    /// If HQ is required, uses HQ recommendation; otherwise uses NQ.
    /// </summary>
    public CraftRecommendation EffectiveRecommendation => IsHqRequired ? RecommendationHq : RecommendationNq;

    /// <summary>
    /// Effective potential savings based on HQ requirement.
    /// </summary>
    public decimal EffectivePotentialSavings => IsHqRequired ? PotentialSavingsHq : PotentialSavingsNq;

    /// <summary>
    /// Effective savings percent based on HQ requirement.
    /// </summary>
    public decimal EffectiveSavingsPercent => IsHqRequired ? SavingsPercentHq : SavingsPercentNq;

    /// <summary>
    /// Quality warning: NQ components may compromise HQ crafts for endgame items.
    /// </summary>
    public bool HasQualityWarning => HasHqData && BuyCostHq > BuyCostNq * 1.5m;

    /// <summary>
    /// For endgame crafts, HQ components are often required. Show warning if NQ seems cheaper
    /// but HQ is significantly more expensive (indicating NQ might not be viable).
    /// </summary>
    public bool IsEndgameRelevant => HasHqData && (SavingsPercentHq < 0 || BuyCostHq > CraftCost * 2);

    public string Summary => $"{ItemName} x{Quantity}: Buy {BuyCostNq:N0}g vs Craft {CraftCost:N0}g ({Math.Abs(PotentialSavingsNq):N0}g difference)";
    public bool IsSignificantSavings => Math.Abs(EffectivePotentialSavings) > 1000 || Math.Abs(EffectiveSavingsPercent) > 10;

    /// <summary>
    /// Get the appropriate recommendation based on whether HQ is required.
    /// </summary>
    public CraftRecommendation GetRecommendation(bool hqRequired) => hqRequired ? RecommendationHq : RecommendationNq;

    /// <summary>
    /// Get cost based on quality requirement.
    /// </summary>
    public decimal GetBuyCost(bool hqRequired) => hqRequired ? BuyCostHq : BuyCostNq;
}

public enum CraftRecommendation
{
    Buy,    // Cheaper to buy finished product
    Craft   // Cheaper to craft from components
}

/// <summary>
/// Price information for an item from market data.
/// </summary>
public class PriceInfo
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int QuantityAvailable { get; set; }
    public PriceSource Source { get; set; }
    public string SourceDetails { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
    public decimal HqUnitPrice { get; set; }
    public int HqQuantityAvailable { get; set; }
    public bool HasHqData => HqUnitPrice > 0;

    /// <summary>
    /// Vendor information when Source is Vendor. Contains all vendors selling this item.
    /// </summary>
    public List<VendorInfo> Vendors { get; set; } = new();

    /// <summary>
    /// Whether this item has vendor information.
    /// </summary>
    public bool HasVendorInfo => Vendors?.Any() == true;
}

/// <summary>
/// Represents a single item purchase within a world's procurement card.
/// Used in the world-centric procurement view.
/// </summary>
public class WorldItemPurchase
{
    /// <summary>
    /// The item ID.
    /// </summary>
    public int ItemId { get; set; }

    /// <summary>
    /// The item name.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// The icon ID for the item.
    /// </summary>
    public int IconId { get; set; }

    /// <summary>
    /// Quantity to purchase on this specific world.
    /// </summary>
    public int QuantityOnThisWorld { get; set; }

    /// <summary>
    /// Total quantity needed across all worlds (for split purchases).
    /// </summary>
    public int TotalQuantityNeeded { get; set; }

    /// <summary>
    /// Listing unit price on this world, or effective cost per needed unit when PriceIsEffectiveCost is true.
    /// </summary>
    public decimal PricePerUnit { get; set; }

    /// <summary>
    /// Whether PricePerUnit is an effective cost per needed unit because full listing stacks include excess.
    /// </summary>
    public bool PriceIsEffectiveCost { get; set; }

    /// <summary>
    /// Display string that distinguishes actual listing unit prices from effective full-stack costs.
    /// </summary>
    public string PriceDisplay => PriceIsEffectiveCost
        ? $"{PricePerUnit:N0}g eff."
        : $"{PricePerUnit:N0}g";

    /// <summary>
    /// Total cost for this portion.
    /// </summary>
    public long TotalCost { get; set; }

    /// <summary>
    /// Whether this item requires a multi-world split purchase.
    /// </summary>
    public bool IsSplitPurchase { get; set; }

    /// <summary>
    /// The original DetailedShoppingPlan this item came from.
    /// </summary>
    public DetailedShoppingPlan? SourcePlan { get; set; }

    /// <summary>
    /// For split purchases, indicates if this is the primary world or supplemental.
    /// </summary>
    public string TravelContext { get; set; } = TravelContextConstants.Primary;

    /// <summary>
    /// Display format: "×X of Y" where X is quantity on this world, Y is total needed.
    /// For vendor items, use SimpleQuantityDisplay instead (vendors have unlimited stock).
    /// </summary>
    public string QuantityDisplay => $"×{QuantityOnThisWorld} of {TotalQuantityNeeded}";

    /// <summary>
    /// Simple quantity display without total (e.g., "×50").
    /// Use this for vendor items where stock is unlimited.
    /// </summary>
    public string SimpleQuantityDisplay => $"×{QuantityOnThisWorld}";

    /// <summary>
    /// The vendor selling this item (only populated for vendor purchases).
    /// Contains vendor name and location for display in procurement cards.
    /// </summary>
    public VendorInfo? Vendor { get; set; }
}

public sealed class WorldItemProcurementAction
{
    public WorldProcurementCardModel World { get; init; } = new();

    public WorldItemPurchase Item { get; init; } = new();
}

/// <summary>
/// Represents a procurement card for a specific world.
/// Contains all items to be purchased on that world, aggregating both single-world and split purchases.
/// </summary>
public class WorldProcurementCardModel
{
    /// <summary>
    /// The world name (e.g., "Gilgamesh", "Vendor").
    /// </summary>
    public string WorldName { get; set; } = string.Empty;

    /// <summary>
    /// The data center this world belongs to.
    /// </summary>
    public string DataCenter { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a vendor card (not a market world).
    /// </summary>
    public bool IsVendor => WorldName == MarketShoppingConstants.VendorWorldName;

    /// <summary>
    /// Whether this world is congested (cannot travel to).
    /// </summary>
    public bool IsCongested { get; set; }

    /// <summary>
    /// Warning message for congested worlds.
    /// </summary>
    public string? CongestedWarning { get; set; }

    /// <summary>
    /// The world classification (Standard, Congested, Preferred, etc.).
    /// </summary>
    public WorldClassification Classification { get; set; } = WorldClassification.Standard;

    /// <summary>
    /// All items to be purchased on this world.
    /// </summary>
    public List<WorldItemPurchase> Items { get; set; } = new();

    /// <summary>
    /// Total cost for all items on this world.
    /// </summary>
    public long TotalCost => Items.Sum(i => i.TotalCost);

    /// <summary>
    /// Total number of items (not quantities) on this world.
    /// </summary>
    public int ItemCount => Items.Count;

    /// <summary>
    /// Total quantity of all items to purchase on this world.
    /// </summary>
    public int TotalQuantity => Items.Sum(i => i.QuantityOnThisWorld);

    /// <summary>
    /// Whether any items on this world are split purchases.
    /// </summary>
    public bool HasSplitPurchases => Items.Any(i => i.IsSplitPurchase);

    /// <summary>
    /// Items that require multi-world split purchases.
    /// </summary>
    public List<WorldItemPurchase> SplitItems => Items.Where(i => i.IsSplitPurchase).ToList();

    /// <summary>
    /// Items that are fully purchased on this world (non-split).
    /// </summary>
    public List<WorldItemPurchase> FullItems => Items.Where(i => !i.IsSplitPurchase).ToList();

    /// <summary>
    /// Vendor information for vendor cards. Contains vendor names and locations.
    /// Only populated when IsVendor is true.
    /// </summary>
    public List<VendorInfo> Vendors { get; set; } = new();

    /// <summary>
    /// The specific vendor selected for this purchase (e.g., "Material Supplier - Limsa").
    /// Only populated when IsVendor is true and a specific vendor was selected.
    /// </summary>
    public string? SelectedVendorName { get; set; }
}

/// <summary>
/// Centralized summary for displaying purchase information consistently across the UI.
/// Shows actual purchase quantity (from listings) rather than idealized quantity needed.
/// </summary>
public class PurchaseSummary
{
    public int ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int IconId { get; init; }

    /// <summary>
    /// Quantity required for the crafting plan (idealized).
    /// </summary>
    public int QuantityNeeded { get; init; }

    /// <summary>
    /// Actual quantity to purchase from listings (may include excess due to full stacks).
    /// </summary>
    public int QuantityToPurchase { get; init; }

    /// <summary>
    /// Extra items beyond what's needed (QuantityToPurchase - QuantityNeeded).
    /// </summary>
    public int ExcessQuantity { get; init; }

    /// <summary>
    /// Whether there are excess items due to full stack purchases.
    /// </summary>
    public bool HasExcess => ExcessQuantity > 0;

    /// <summary>
    /// Total cost for this purchase.
    /// </summary>
    public long TotalCost { get; init; }

    /// <summary>
    /// Average price per unit.
    /// </summary>
    public decimal AveragePricePerUnit { get; init; }

    /// <summary>
    /// The recommended world for this purchase (null if split-world or vendor).
    /// </summary>
    public WorldShoppingSummary? RecommendedWorld { get; init; }

    /// <summary>
    /// Whether this is a vendor purchase.
    /// </summary>
    public bool IsVendor => RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName;

    /// <summary>
    /// Whether this requires a split-world purchase.
    /// </summary>
    public bool RequiresSplitPurchase { get; init; }

    /// <summary>
    /// Split-world purchase details (if applicable).
    /// </summary>
    public List<SplitWorldPurchase>? RecommendedSplit { get; init; }

    /// <summary>
    /// Display text: "ItemName ×11 (x3 excess)" or "ItemName ×8" (no excess).
    /// </summary>
    public string DisplayText => HasExcess
        ? $"{Name} ×{QuantityToPurchase} (x{ExcessQuantity} excess)"
        : $"{Name} ×{QuantityToPurchase}";

    /// <summary>
    /// Short display text without excess: "ItemName ×11".
    /// </summary>
    public string ShortDisplayText => $"{Name} ×{QuantityToPurchase}";

    /// <summary>
    /// Quantity display with excess: "×11 (x3 excess)" or "×8".
    /// </summary>
    public string QuantityDisplay => HasExcess
        ? $"×{QuantityToPurchase} (x{ExcessQuantity} excess)"
        : $"×{QuantityToPurchase}";

    /// <summary>
    /// Cost display: "15,000g".
    /// </summary>
    public string CostDisplay => $"{TotalCost:N0}g";

    /// <summary>
    /// Price per unit display: "~1,364g/ea".
    /// </summary>
    public string PricePerUnitDisplay => $"~{AveragePricePerUnit:N0}g/ea";
}
