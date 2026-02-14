using FFXIVCraftArchitect.Core.Services;

namespace FFXIVCraftArchitect.Core.Models;

/// <summary>
/// Configuration for market analysis behavior.
/// Controls the trade-off between cost optimization and travel efficiency.
/// </summary>
public class MarketAnalysisConfig
{
    /// <summary>
    /// Maximum number of worlds to visit for a single item.
    /// Uses soft limit based on tier 1 recommendations if null.
    /// </summary>
    public int? MaxWorldsPerItem { get; set; }
    
    /// <summary>
    /// Whether to enable multi-world split purchases.
    /// </summary>
    public bool EnableSplitWorld { get; set; } = false;
    
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
            EnableSplitWorld = settings.Get("analysis.enable_split_world", false)
        };
    }
    
    /// <summary>
    /// Gets the effective max worlds per item based on tier 1 count.
    /// </summary>
    public int GetEffectiveMaxWorlds(int tier1WorldCount)
    {
        if (MaxWorldsPerItem.HasValue)
            return MaxWorldsPerItem.Value;
        
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
    
    /// <summary>
    /// Vendor information for items that can be purchased from vendors.
    /// Only includes vendors that accept gil as currency.
    /// </summary>
    public List<GarlandVendor> Vendors { get; set; } = new();

    public bool HasOptions => WorldOptions.Count > 0;
    
    /// <summary>
    /// Total quantity available across ALL worlds (for stock availability checking).
    /// Vendors always have unlimited stock.
    /// </summary>
    public int TotalAvailableQuantity => 
        RecommendedWorld?.WorldName == "Vendor" ? QuantityNeeded : WorldOptions.Sum(w => w.TotalQuantityPurchased);
    
    /// <summary>
    /// Whether the total available stock across all worlds is sufficient.
    /// Vendors always have sufficient stock.
    /// </summary>
    public bool HasSufficientStock => 
        RecommendedWorld?.WorldName == "Vendor" || TotalAvailableQuantity >= QuantityNeeded;
    
    /// <summary>
    /// The shortfall quantity if stock is insufficient across all worlds.
    /// Always 0 for vendor purchases.
    /// </summary>
    public int StockShortfall => 
        RecommendedWorld?.WorldName == "Vendor" ? 0 : Math.Max(0, QuantityNeeded - TotalAvailableQuantity);
    
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
            if (RecommendedWorld == null || SplitTotalCost == null) return null;
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
    /// <summary>
    /// The world to purchase from.
    /// </summary>
    public string WorldName { get; set; } = string.Empty;
    
    /// <summary>
    /// Quantity to buy from this world.
    /// </summary>
    public int QuantityToBuy { get; set; }
    
    /// <summary>
    /// Price per unit on this world.
    /// </summary>
    public decimal PricePerUnit { get; set; }
    
    /// <summary>
    /// Total cost for this portion (QuantityToBuy * PricePerUnit).
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
    public string TravelContext { get; set; } = "Primary";
    
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
                return null;
                
            var multipliers = ExcludedListings
                .Select(l => (decimal)l.PricePerUnit / ModePricePerUnit)
                .ToList();
                
            return (multipliers.Min(), multipliers.Max());
        }
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
    public List<GarlandVendor> Vendors { get; set; } = new();
    
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
    /// Price per unit on this world.
    /// </summary>
    public decimal PricePerUnit { get; set; }
    
    /// <summary>
    /// Total cost for this portion (QuantityOnThisWorld * PricePerUnit).
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
    public string TravelContext { get; set; } = "Primary";
    
    /// <summary>
    /// Display format: "×X of Y" where X is quantity on this world, Y is total needed.
    /// </summary>
    public string QuantityDisplay => $"×{QuantityOnThisWorld} of {TotalQuantityNeeded}";
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
    public bool IsVendor => WorldName == "Vendor";
    
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
    public List<GarlandVendor> Vendors { get; set; } = new();

    /// <summary>
    /// The specific vendor selected for this purchase (e.g., "Material Supplier - Limsa").
    /// Only populated when IsVendor is true and a specific vendor was selected.
    /// </summary>
    public string? SelectedVendorName { get; set; }
}
