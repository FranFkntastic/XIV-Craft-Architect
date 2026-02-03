namespace FFXIVCraftArchitect.Core.Models;

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

/// <summary>
/// Detailed shopping plan for a single item with world-specific options.
/// </summary>
public class DetailedShoppingPlan
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int QuantityNeeded { get; set; }
    public decimal DCAveragePrice { get; set; }
    public List<WorldShoppingSummary> WorldOptions { get; set; } = new();
    public WorldShoppingSummary? RecommendedWorld { get; set; }
    public string? Error { get; set; }
    
    /// <summary>
    /// HQ price data if available (for endgame crafting considerations).
    /// </summary>
    public decimal? HQAveragePrice { get; set; }
    public bool HasHqData => HQAveragePrice.HasValue;

    public bool HasOptions => WorldOptions.Count > 0;
}

/// <summary>
/// Shopping summary for a specific world with value metrics.
/// </summary>
public class WorldShoppingSummary
{
    public string WorldName { get; set; } = string.Empty;
    public long TotalCost { get; set; }
    public decimal AveragePricePerUnit { get; set; }
    public int ListingsUsed { get; set; }
    public List<ShoppingListingEntry> Listings { get; set; } = new();
    public bool IsFullyUnderAverage { get; set; }
    public int TotalQuantityPurchased { get; set; }
    public int ExcessQuantity { get; set; }
    
    /// <summary>
    /// The best single listing on this world (for value comparison).
    /// </summary>
    public ShoppingListingEntry? BestSingleListing { get; set; }
    
    /// <summary>
    /// Value score: lower is better. Based on price per unit of cheapest listing.
    /// </summary>
    public decimal ValueScore => BestSingleListing?.PricePerUnit ?? AveragePricePerUnit;
    
    /// <summary>
    /// Whether this world has competitively priced listings.
    /// </summary>
    public bool IsCompetitive => BestSingleListing != null && 
        BestSingleListing.PricePerUnit <= AveragePricePerUnit * 0.9m;
    
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

    public string CostDisplay => $"{TotalCost:N0}g";
    public string PricePerUnitDisplay => $"{AveragePricePerUnit:N0}g";
    public string ValueScoreDisplay => $"{ValueScore:N0}g";
    public bool HasExcess => ExcessQuantity > 0;
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
    public decimal UnitPrice { get; set; }
    public decimal HqUnitPrice { get; set; }
    public bool HasHqData => HqUnitPrice > 0;
}
