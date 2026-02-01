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
    
    [JsonPropertyName("id")]
    public JsonElement IdElement { get; set; }
    
    // Helper to get ID as int (handles both string and int JSON values)
    public int Id => IdElement.ValueKind == JsonValueKind.String 
        ? int.Parse(IdElement.GetString()!) 
        : IdElement.GetInt32();
    
    [JsonPropertyName("obj")]
    public GarlandSearchObject Object { get; set; } = new();
}

public class GarlandSearchObject
{
    [JsonPropertyName("n")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("i")]
    public int IconId { get; set; }
    
    [JsonPropertyName("c")]
    public int? ClassJob { get; set; }
    
    [JsonPropertyName("l")]
    public int? Level { get; set; }
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
