using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIVCraftArchitect.Models;

// ============================================================================
// Garland Tools API Models
// ============================================================================

/// <summary>
/// Search result from Garland Tools search API.
/// </summary>
public class GarlandSearchResult
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
    
    // Store raw JSON element to handle both string and int IDs
    [JsonPropertyName("id")]
    private JsonElement _idElement { get; set; }
    
    // Computed property - not serialized
    public int Id => _idElement.ValueKind == JsonValueKind.String 
        ? int.Parse(_idElement.GetString()!) 
        : _idElement.GetInt32();
    
    [JsonPropertyName("obj")]
    public GarlandSearchObject Object { get; set; } = new();
}

public class GarlandSearchObject
{
    [JsonPropertyName("n")]
    public string Name { get; set; } = string.Empty;
    
    // Icon ID can be int, string, or missing
    [JsonPropertyName("i")]
    private JsonElement _iconElement { get; set; }
    
    public int IconId => _iconElement.ValueKind switch
    {
        JsonValueKind.Number => _iconElement.GetInt32(),
        JsonValueKind.String => int.TryParse(_iconElement.GetString(), out var id) ? id : 0,
        _ => 0
    };
    
    // ClassJob can be int, string, or missing
    [JsonPropertyName("c")]
    private JsonElement _classJobElement { get; set; }
    
    public int? ClassJob => _classJobElement.ValueKind switch
    {
        JsonValueKind.Number => _classJobElement.GetInt32(),
        JsonValueKind.String => int.TryParse(_classJobElement.GetString(), out var cj) ? cj : null,
        _ => null
    };
    
    // Level can be int, string, or missing
    [JsonPropertyName("l")]
    private JsonElement _levelElement { get; set; }
    
    public int? Level => _levelElement.ValueKind switch
    {
        JsonValueKind.Number => _levelElement.GetInt32(),
        JsonValueKind.String => int.TryParse(_levelElement.GetString(), out var lvl) ? lvl : null,
        _ => null
    };
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
