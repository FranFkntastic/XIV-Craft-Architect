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
    /// Crafting recipes this item is used in
    /// </summary>
    [JsonPropertyName("craft")]
    public List<GarlandCraft>? Crafts { get; set; }
    
    /// <summary>
    /// Recipes that produce this item
    /// </summary>
    [JsonPropertyName("usedInCraft")]
    public List<GarlandUsedInCraft>? UsedInCrafts { get; set; }
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
