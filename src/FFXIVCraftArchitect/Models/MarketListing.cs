using System.Text.Json.Serialization;

namespace FFXIVCraftArchitect.Models;

/// <summary>
/// Market board listing from Universalis API.
/// Maps to Python: listings[i] structure
/// </summary>
public class MarketListing
{
    [JsonPropertyName("pricePerUnit")]
    public long PricePerUnit { get; set; }
    
    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
    
    [JsonPropertyName("worldName")]
    public string WorldName { get; set; } = string.Empty;
    
    [JsonPropertyName("retainerName")]
    public string RetainerName { get; set; } = string.Empty;
}

/// <summary>
/// Universalis API response for market data.
/// </summary>
public class UniversalisResponse
{
    [JsonPropertyName("itemID")]
    public int ItemId { get; set; }
    
    [JsonPropertyName("worldID")]
    public int? WorldId { get; set; }
    
    [JsonPropertyName("dcName")]
    public string? DataCenterName { get; set; }
    
    [JsonPropertyName("listings")]
    public List<MarketListing> Listings { get; set; } = new();
    
    [JsonPropertyName("averagePrice")]
    public double AveragePrice { get; set; }
}

/// <summary>
/// Universalis bulk API response format.
/// </summary>
public class UniversalisBulkResponse
{
    [JsonPropertyName("itemIDs")]
    public List<int> ItemIds { get; set; } = new();
    
    /// <summary>
    /// Items keyed by item ID string
    /// </summary>
    [JsonPropertyName("items")]
    public Dictionary<int, UniversalisResponse> Items { get; set; } = new();
}

/// <summary>
/// Shopping plan for a single material.
/// </summary>
public class ShoppingPlan
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int QuantityNeeded { get; set; }
    public long TotalCost { get; set; }
    public List<ShoppingPlanEntry> Entries { get; set; } = new();
}

/// <summary>
/// Single purchase entry in a shopping plan.
/// </summary>
public class ShoppingPlanEntry
{
    public int Quantity { get; set; }
    public long PricePerUnit { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public string RetainerName { get; set; } = string.Empty;
}
