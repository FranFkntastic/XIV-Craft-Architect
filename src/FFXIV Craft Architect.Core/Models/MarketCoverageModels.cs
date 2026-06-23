namespace FFXIV_Craft_Architect.Core.Models;

public sealed record MarketCoverageSet(
    int ItemId,
    string ItemName,
    int QuantityNeeded,
    MarketCoverageOption? SingleWorld,
    MarketCoverageOption? CompactSplit,
    MarketCoverageOption? WideSplit,
    MarketCoverageOption? CheapestObserved,
    IReadOnlyList<MarketCoverageOption> AllCandidates)
{
    public static MarketCoverageSet Empty(int itemId, string itemName, int quantityNeeded) =>
        new(itemId, itemName, quantityNeeded, null, null, null, null, Array.Empty<MarketCoverageOption>());
}

public sealed record MarketCoverageOption(
    string CandidateId,
    MarketCoverageTier Tier,
    MarketCoverageKind Kind,
    MarketCoverageQualityPolicy QualityPolicy,
    int QuantityCovered,
    int QuantityToPurchase,
    int ExcessQuantity,
    decimal ExactNeededCost,
    decimal CashOutCost,
    decimal AverageUnitCost,
    MarketCoveragePriceBand PriceBand,
    IReadOnlyList<MarketCoverageWorld> Worlds,
    IReadOnlyList<MarketCoverageListing> Listings,
    MarketCoverageFriction Friction,
    MarketCoverageSavings Savings,
    bool IsDefaultEligible,
    string? DegradedReason);

public sealed record MarketCoverageWorld(
    string DataCenter,
    string WorldName,
    int QuantityCovered,
    int QuantityToPurchase,
    decimal ExactNeededCost,
    decimal CashOutCost);

public sealed record MarketCoverageListing(
    string DataCenter,
    string WorldName,
    int QuantityAvailable,
    int QuantityUsed,
    int QuantityPurchased,
    decimal PricePerUnit,
    bool IsHq);

public sealed record MarketCoverageFriction(
    int WorldCount,
    int DataCenterCount,
    int SmallestContribution,
    int LargestContribution,
    int ExcessQuantity);

public sealed record MarketCoverageSavings(
    decimal VersusSingleWorld,
    decimal VersusSingleWorldPercent)
{
    public static MarketCoverageSavings None { get; } = new(0, 0);
}

public enum MarketCoverageTier
{
    SingleWorld,
    CompactSplit,
    WideSplit,
    CheapestObserved
}

public enum MarketCoverageKind
{
    SupportedListings,
    ProjectedAverage,
    Unavailable
}

public enum MarketCoverageQualityPolicy
{
    NqOrHq,
    HqOnly
}

public enum MarketCoveragePriceBand
{
    Unknown,
    Deal,
    Competitive,
    Sane,
    OutOfBand
}
