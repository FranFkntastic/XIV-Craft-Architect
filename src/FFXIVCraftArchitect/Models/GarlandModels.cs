using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIVCraftArchitect.Models;

// ============================================================================
// Garland Tools API Models
// ============================================================================

/// <summary>
/// Search result from Garland Tools search API.
/// Uses object for polymorphic fields (int/string) - converted via properties.
/// </summary>
public class GarlandSearchResult
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    
    // Store as object to handle int/string polymorphism
    [JsonPropertyName("id")]
    public object? IdRaw { get; set; }
    
    // Computed property converts to int
    [JsonIgnore]
    public int Id => ConvertToInt(IdRaw);
    
    [JsonPropertyName("obj")]
    public GarlandSearchObject Object { get; set; } = new();
    
    private static int ConvertToInt(object? value)
    {
        return value switch
        {
            null => 0,
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s => int.TryParse(s, out var parsed) ? parsed : 0,
            JsonElement e => ConvertJsonElementToInt(e),
            _ => 0
        };
    }
    
    private static int ConvertJsonElementToInt(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(element.GetString(), out var s) ? s : 0,
            _ => 0
        };
    }
}

public class GarlandSearchObject
{
    [JsonPropertyName("n")]
    public string Name { get; set; } = string.Empty;
    
    // Icon ID can be int, string, or missing
    [JsonPropertyName("i")]
    public object? IconIdRaw { get; set; }
    
    [JsonIgnore]
    public int IconId => ConvertToInt(IconIdRaw);
    
    // ClassJob can be int, string, or missing
    [JsonPropertyName("c")]
    public object? ClassJobRaw { get; set; }
    
    [JsonIgnore]
    public int? ClassJob => ConvertToNullableInt(ClassJobRaw);
    
    // Level can be int, string, or missing
    [JsonPropertyName("l")]
    public object? LevelRaw { get; set; }
    
    [JsonIgnore]
    public int? Level => ConvertToNullableInt(LevelRaw);
    
    private static int ConvertToInt(object? value)
    {
        return value switch
        {
            null => 0,
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s => int.TryParse(s, out var parsed) ? parsed : 0,
            JsonElement e => ConvertJsonElementToInt(e),
            _ => 0
        };
    }
    
    private static int? ConvertToNullableInt(object? value)
    {
        return value switch
        {
            null => null,
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s => int.TryParse(s, out var parsed) ? parsed : null,
            JsonElement e => ConvertJsonElementToNullableInt(e),
            _ => null
        };
    }
    
    private static int ConvertJsonElementToInt(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(element.GetString(), out var s) ? s : 0,
            _ => 0
        };
    }
    
    private static int? ConvertJsonElementToNullableInt(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i : null,
            JsonValueKind.String => int.TryParse(element.GetString(), out var s) ? s : null,
            _ => null
        };
    }
}

/// <summary>
/// Full item data from Garland Tools item API.
/// </summary>
public class GarlandItemResponse
{
    [JsonPropertyName("item")]
    public GarlandItem Item { get; set; } = new();
}

public class GarlandItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("icon")]
    public int IconId { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    /// <summary>
    /// Crafting recipes this item is used in (traditional crafting)
    /// </summary>
    [JsonPropertyName("craft")]
    public List<GarlandCraft>? Crafts { get; set; }
    
    /// <summary>
    /// Company workshop recipes (airships, submarines, etc.)
    /// </summary>
    [JsonPropertyName("companyCraft")]
    public List<GarlandCompanyCraft>? CompanyCrafts { get; set; }
    
    /// <summary>
    /// Recipes that produce this item
    /// </summary>
    [JsonPropertyName("usedInCraft")]
    public List<GarlandUsedInCraft>? UsedInCrafts { get; set; }
    
    /// <summary>
    /// Vendors that sell this item
    /// </summary>
    [JsonPropertyName("vendors")]
    public List<GarlandVendor>? Vendors { get; set; }
    
    /// <summary>
    /// Whether this item can be traded on the market board (1 = true, 0 = false in JSON)
    /// </summary>
    [JsonPropertyName("tradeable")]
    public object? TradeableRaw { get; set; }
    
    [JsonIgnore]
    public bool Tradeable => TradeableRaw switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        string s => int.TryParse(s, out var val) && val != 0,
        JsonElement e => e.ValueKind == JsonValueKind.True || (e.TryGetInt32(out var i) && i != 0),
        _ => true // Default to tradeable if unknown
    };
}

public class GarlandCraft
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("job")]
    public int JobId { get; set; }
    
    [JsonPropertyName("rlvl")]
    public int RecipeLevel { get; set; }
    
    [JsonPropertyName("yield")]
    public int Yield { get; set; } = 1;
    
    [JsonPropertyName("ingredients")]
    public List<GarlandIngredient> Ingredients { get; set; } = new();
}

/// <summary>
/// Company workshop craft recipe (airship/submarine parts, etc.)
/// These have phases instead of simple ingredients
/// </summary>
public class GarlandCompanyCraft
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    /// <summary>
    /// Company crafting has multiple phases (up to 4)
    /// </summary>
    [JsonPropertyName("phases")]
    public List<GarlandCompanyPhase> Phases { get; set; } = new();
    
    /// <summary>
    /// Total number of phases
    /// </summary>
    [JsonPropertyName("phaseCount")]
    public int PhaseCount { get; set; }
}

public class GarlandCompanyPhase
{
    /// <summary>
    /// Phase number (0-indexed)
    /// </summary>
    [JsonPropertyName("phase")]
    public int PhaseNumber { get; set; }
    
    /// <summary>
    /// List of items required for this phase
    /// </summary>
    [JsonPropertyName("items")]
    public List<GarlandCompanyIngredient> Items { get; set; } = new();
}

public class GarlandCompanyIngredient
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("amount")]
    public int Amount { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    /// <summary>
    /// Phase number this ingredient belongs to
    /// </summary>
    [JsonPropertyName("phase")]
    public int Phase { get; set; }
}

public class GarlandIngredient
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("amount")]
    public int Amount { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class GarlandUsedInCraft
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}

/// <summary>
/// Vendor that sells an item
/// </summary>
public class GarlandVendor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;
    
    /// <summary>
    /// Price in gil
    /// </summary>
    [JsonPropertyName("price")]
    public int Price { get; set; }
    
    /// <summary>
    /// Currency type (usually "gil" but can be tomestones, etc.)
    /// </summary>
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "gil";
}
