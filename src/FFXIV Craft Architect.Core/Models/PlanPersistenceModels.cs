namespace FFXIV_Craft_Architect.Core.Models;

/// <summary>
/// Simple DTO for plan file serialization.
/// Flat structure without circular references.
/// Version 2 = minimal format (no market plans in JSON).
/// </summary>
public class PlanFileData
{
    public int Version { get; set; } = 2;
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public List<PlanFileNode> RootItems { get; set; } = new();

    /// <summary>
    /// DEPRECATED: Market plans are now stored in separate .recommendations.csv files.
    /// Kept for backward compatibility with Version 1 files.
    /// </summary>
    [Obsolete("Market plans are now stored in .recommendations.csv companion files")]
    public List<MarketShoppingPlanData>? MarketPlans { get; set; }
}

/// <summary>
/// Simple DTO for plan node serialization.
/// </summary>
public class PlanFileNode
{
    public int ItemId { get; set; }
    public string? Name { get; set; }
    public int IconId { get; set; }
    public int Quantity { get; set; }

    /// <summary>
    /// Legacy property for backward compatibility. Use Source instead.
    /// </summary>
    public bool IsBuy { get; set; }

    /// <summary>
    /// Acquisition source - new preferred property.
    /// </summary>
    public AcquisitionSource? Source { get; set; }

    /// <summary>Legacy: Use MustBeHq instead.</summary>
    public bool RequiresHq { get; set; }

    /// <summary>If true, this item must be HQ quality (for plan sharing).</summary>
    public bool MustBeHq { get; set; }

    /// <summary>If true, this item can be HQ (crafted/gathered items can, crystals/aethersands cannot).</summary>
    public bool CanBeHq { get; set; }

    /// <summary>If true, this item can be bought from a vendor.</summary>
    public bool CanBuyFromVendor { get; set; }

    /// <summary>If true, this item has a craft recipe and can be crafted.</summary>
    public bool CanCraft { get; set; }

    public bool IsUncraftable { get; set; }
    public int RecipeLevel { get; set; }
    public string? Job { get; set; }
    public int Yield { get; set; } = 1;
    public decimal MarketPrice { get; set; }

    /// <summary>
    /// HQ market price per unit.
    /// </summary>
    public decimal HqMarketPrice { get; set; }

    /// <summary>
    /// Vendor price per unit (if available).
    /// </summary>
    public decimal VendorPrice { get; set; }

    public PriceSource PriceSource { get; set; }
    public string? PriceSourceDetails { get; set; }
    public string? Notes { get; set; }

    /// <summary>Full vendor options for this item.</summary>
    public List<VendorInfoData> Vendors { get; set; } = new();

    /// <summary>Selected vendor index for procurement (-1 = use cheapest).</summary>
    public int SelectedVendorIndex { get; set; } = -1;

    public List<PlanFileNode> Children { get; set; } = new();

    /// <summary>
    /// If true, this node is a circular reference and should not be expanded.
    /// </summary>
    public bool IsCircularReference { get; set; }
}

/// <summary>
/// Vendor data for persistence (equivalent to Core VendorInfo).
/// </summary>
public class VendorInfoData
{
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "gil";
}

/// <summary>
/// Saved market shopping plan data with recommended worlds and listings.
/// </summary>
public class MarketShoppingPlanData
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int QuantityNeeded { get; set; }
    public decimal DCAveragePrice { get; set; }
    public decimal? HQAveragePrice { get; set; }
    public string? Error { get; set; }
    public List<WorldShoppingSummaryData> WorldOptions { get; set; } = new();
    public WorldShoppingSummaryData? RecommendedWorld { get; set; }
}

/// <summary>
/// Saved world shopping summary data.
/// </summary>
public class WorldShoppingSummaryData
{
    public string WorldName { get; set; } = string.Empty;
    public long TotalCost { get; set; }
    public decimal AveragePricePerUnit { get; set; }
    public int ListingsUsed { get; set; }
    public bool IsFullyUnderAverage { get; set; }
    public int TotalQuantityPurchased { get; set; }
    public int ExcessQuantity { get; set; }
    public List<ShoppingListingEntryData> Listings { get; set; } = new();
    public ShoppingListingEntryData? BestSingleListing { get; set; }
}

/// <summary>
/// Saved shopping listing entry data.
/// </summary>
public class ShoppingListingEntryData
{
    public int Quantity { get; set; }
    public long PricePerUnit { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public bool IsUnderAverage { get; set; }
    public bool IsHq { get; set; }
    public int NeededFromStack { get; set; }
    public int ExcessQuantity { get; set; }
    public bool IsAdditionalOption { get; set; }
}
