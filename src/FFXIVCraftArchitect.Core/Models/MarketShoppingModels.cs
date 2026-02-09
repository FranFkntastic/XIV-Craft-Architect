using FFXIVCraftArchitect.Core.Services;

namespace FFXIVCraftArchitect.Core.Models;

/// <summary>
/// Configuration for market analysis behavior.
/// Controls the trade-off between cost optimization and travel efficiency.
/// </summary>
public class MarketAnalysisConfig
{
    /// <summary>
    /// Weight between fewer worlds (0) and optimal cost (100).
    /// 0 = Minimize travel, visit as few worlds as possible
    /// 50 = Balanced approach
    /// 100 = Pure cost optimization, visit any world for best price
    /// </summary>
    public int CostVsTravelWeight { get; set; } = 50;
    
    /// <summary>
    /// Minimum savings percentage required to suggest a multi-world split.
    /// Default 10% - splits that save less than this are not recommended.
    /// </summary>
    public decimal MinSplitSavingsPercent { get; set; } = 10m;
    
    /// <summary>
    /// Maximum number of worlds to visit for a single item.
    /// Uses soft limit based on tier 1 recommendations if null.
    /// </summary>
    public int? MaxWorldsPerItem { get; set; }
    
    /// <summary>
    /// Whether to prefer worlds that are already being visited for other items.
    /// Enabled by default for travel consolidation.
    /// </summary>
    public bool PreferConsolidatedWorlds { get; set; } = true;
    
    /// <summary>
    /// How much extra weight to give consolidated worlds (0-50).
    /// Higher values strongly prefer visiting worlds already on the route.
    /// </summary>
    public int ConsolidationBonus { get; set; } = 20;
    
    /// <summary>
    /// Creates config from settings service.
    /// </summary>
    public static MarketAnalysisConfig FromSettings(SettingsService settings)
    {
        return new MarketAnalysisConfig
        {
            CostVsTravelWeight = settings.Get("analysis.cost_vs_travel_weight", 50),
            MinSplitSavingsPercent = settings.Get("analysis.min_split_savings", 10m),
            MaxWorldsPerItem = settings.Get<int?>("analysis.max_worlds_per_item", null),
            PreferConsolidatedWorlds = settings.Get("analysis.prefer_consolidated", true),
            ConsolidationBonus = settings.Get("analysis.consolidation_bonus", 20)
        };
    }
    
    /// <summary>
    /// Gets the effective max worlds per item based on tier 1 count.
    /// </summary>
    public int GetEffectiveMaxWorlds(int tier1WorldCount)
    {
        if (MaxWorldsPerItem.HasValue)
            return MaxWorldsPerItem.Value;
        
        // Soft limit: tier 1 worlds + 1 for supplemental
        // Weight adjusts this: lower weight = tighter limit
        var adjustment = (100 - CostVsTravelWeight) / 25; // 0 to 4
        return Math.Max(2, tier1WorldCount + 1 - adjustment);
    }
    
    /// <summary>
    /// Checks if a split purchase meets the minimum savings threshold.
    /// </summary>
    public bool MeetsSavingsThreshold(decimal savingsPercent)
    {
        return savingsPercent >= MinSplitSavingsPercent;
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

    public bool HasOptions => WorldOptions.Count > 0;
    
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
    public long TotalCost => (long)(QuantityToBuy * PricePerUnit);
    
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
    public bool IsFullyUnderAverage { get; set; }
    public int TotalQuantityPurchased { get; set; }
    public int ExcessQuantity { get; set; }
    
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
}
