using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIVCraftArchitect.Core.Models;

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

    /// <summary>
    /// Partial data references (NPCs, locations, etc.) that provide additional context.
    /// Used to resolve vendor IDs to full vendor information including alternate locations.
    /// </summary>
    [JsonPropertyName("partials")]
    public List<GarlandPartial>? Partials { get; set; }
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
    /// Vendors that sell this item. Can be an array of vendor objects or vendor IDs (integers).
    /// </summary>
    [JsonPropertyName("vendors")]
    public List<object>? VendorsRaw { get; set; }
    
    /// <summary>
    /// Parsed vendor information (only includes properly formatted vendor objects)
    /// </summary>
    [JsonIgnore]
    public List<GarlandVendor> Vendors => ParseVendors(VendorsRaw, Id);
    
    private static List<GarlandVendor> ParseVendors(List<object>? rawVendors, int itemId)
    {
        if (rawVendors == null) return new List<GarlandVendor>();
        
        var result = new List<GarlandVendor>();
        foreach (var v in rawVendors)
        {
            // Handle already-parsed GarlandVendor objects (e.g., from in-memory cache or deserialization)
            if (v is GarlandVendor garlandVendor)
            {
                result.Add(garlandVendor);
                continue;
            }
            
            // Handle JsonElement (from fresh JSON deserialization)
            if (v is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        var vendor = JsonSerializer.Deserialize<GarlandVendor>(element.GetRawText());
                        if (vendor != null) result.Add(vendor);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[GarlandItem] Failed to parse vendor entry for item {itemId}. Skipping. Error: {ex.Message}");
                    }
                }
                // Integer vendor IDs are ignored - we can't use them without additional lookups
                continue;
            }
            
            // Handle JsonDocument (alternative JSON representation)
            if (v is JsonDocument doc)
            {
                try
                {
                    var vendor = JsonSerializer.Deserialize<GarlandVendor>(doc.RootElement.GetRawText());
                    if (vendor != null) result.Add(vendor);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GarlandItem] Failed to parse JsonDocument vendor for item {itemId}. Skipping. Error: {ex.Message}");
                }
                continue;
            }
        }
        return result;
    }
    
    /// <summary>
    /// Whether this item has any vendor references (including integer IDs).
    /// Use this to check if item can be bought from a vendor, since some vendors
    /// are listed as IDs only (e.g., Ixali Vendor for Walnut Lumber).
    /// </summary>
    [JsonIgnore]
    public bool HasVendorReferences => VendorsRaw?.Any() == true;
    
    /// <summary>
    /// Vendor price in gil (root-level price field).
    /// This is set when the item can be purchased from vendors listed as IDs only.
    /// </summary>
    [JsonPropertyName("price")]
    public int Price { get; set; }
    
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

    /// <summary>
    /// Partial data references for this item (NPCs, locations, etc.).
    /// Populated from the API response when available.
    /// </summary>
    [JsonIgnore]
    public List<GarlandPartial>? Partials { get; set; }

    /// <summary>
    /// Gets all NPC partials with the given name (for finding alternate vendor locations).
    /// Only processes partials of type "npc", skips other types (item, node, mob, leve, etc.).
    /// </summary>
    public List<GarlandNpcPartial> GetNpcPartialsByName(string npcName)
    {
        if (Partials == null) return new List<GarlandNpcPartial>();

        return Partials
            .Where(p => p.Type == "npc")
            .Select(p => p.GetNpcObject())
            .Where(npc => npc != null &&
                         npc.Name.Equals(npcName, StringComparison.OrdinalIgnoreCase))
            .Cast<GarlandNpcPartial>()
            .ToList();
    }
}

public class GarlandCraft
{
    /// <summary>
    /// Recipe ID. Can be integer or string (e.g., "companyCraft_123" for workshop recipes).
    /// </summary>
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; set; }
    
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

    /// <summary>
    /// List of alternate locations where this vendor can be found.
    /// Populated from Garland API partials data.
    /// </summary>
    [JsonIgnore]
    public List<string> AlternateLocations { get; set; } = new();
}

/// <summary>
/// Partial data reference from Garland API.
/// Contains related data like NPCs, locations, etc. that are referenced by ID in other fields.
/// </summary>
public class GarlandPartial
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public object? IdRaw { get; set; }

    [JsonIgnore]
    public int Id => ConvertToInt(IdRaw);

    /// <summary>
    /// Raw JSON element for the partial object.
    /// Use GetNpcObject() to deserialize as GarlandNpcPartial (only valid when Type == "npc").
    /// </summary>
    [JsonPropertyName("obj")]
    public JsonElement? ObjectRaw { get; set; }

    /// <summary>
    /// Gets the NPC object if this partial is an NPC type.
    /// Returns null for other partial types (item, node, mob, leve, etc.).
    /// </summary>
    public GarlandNpcPartial? GetNpcObject()
    {
        if (Type != "npc" || !ObjectRaw.HasValue)
            return null;

        try
        {
            return JsonSerializer.Deserialize<GarlandNpcPartial>(ObjectRaw.Value.GetRawText());
        }
        catch (JsonException ex)
        {
            return null;
        }
    }

    /// <summary>
    /// Converts various numeric types to int. Handles JsonElement which is boxed when deserialized into object?.
    /// Note: Boxing is inherent to System.Text.Json deserialization into object properties.
    /// To avoid boxing entirely, properties would need to be typed as JsonElement directly.
    /// </summary>
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
}
    }

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

/// <summary>
/// NPC partial data from Garland API.
/// Contains vendor/merchant information including location.
/// </summary>
public class GarlandNpcPartial
{
    [JsonPropertyName("i")]
    public long Id { get; set; }

    [JsonPropertyName("n")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Location ID. Maps to specific zones/cities.
    /// </summary>
    [JsonPropertyName("l")]
    public int LocationId { get; set; }

    /// <summary>
    /// Coordinates [x, y] within the location.
    /// Garland API returns this as either numbers [11.04, 11.4] or strings ["12.5", "8.3"].
    /// </summary>
    [JsonPropertyName("c")]
    [JsonConverter(typeof(FlexibleCoordinatesConverter))]
    public List<double>? Coordinates { get; set; }

    /// <summary>
    /// Gets the readable location name from the location ID.
    /// </summary>
    [JsonIgnore]
    public string LocationName => LocationIdToName(LocationId);

    /// <summary>
    /// Maps Garland location IDs to readable location names.
    /// Hard-coded for common zones where vendors exist.
    /// </summary>
    private static readonly Dictionary<int, string> ZoneNameMappings = new()
    {
        // Housing Districts - Material Suppliers
        [425] = "Mist",
        [427] = "The Goblet",
        [426] = "The Lavender Beds",
        [2412] = "Shirogane",
        [4139] = "Empyreum",

        // Major Cities - A Realm Reborn
        [128] = "Limsa Lominsa",
        [130] = "Gridania",
        [131] = "Ul'dah",
        
        // City Areas
        [28] = "Limsa Lominsa Upper Decks",
        [29] = "Limsa Lominsa Lower Decks",
        [52] = "Limsa Lominsa Lower Decks",
        [53] = "Old Gridania",
        [54] = "New Gridania",
        [40] = "Ul'dah - Steps of Nald",
        [41] = "Ul'dah - Steps of Thal",
        
        // City Inns/Aetheryte Plazas
        [129] = "The Drowning Wench (Limsa)",
        [137] = "The Quicksand (Ul'dah)",
        [138] = "The Roost (Gridania)",

        // Heavensward (3.0)
        [132] = "Ishgard",
        [218] = "Foundation",
        [2301] = "The Pillars",
        [139] = "The Jeweled Crozier (Ishgard)",
        [2082] = "Idyllshire",

        // Stormblood (4.0)
        [133] = "Kugane",
        [2403] = "Rhalgr's Reach",
        [140] = "The Shiokaze Hostelry (Kugane)",
        [2411] = "The Azim Steppe",

        // Shadowbringers (5.0)
        [134] = "The Crystarium",
        [141] = "The Pendants (Crystarium)",
        [51] = "Eulmore",

        // Endwalker (6.0)
        [135] = "Old Sharlayan",
        [142] = "The Baldesion Annex (Sharlayan)",
        [3706] = "Old Sharlayan",
        [3707] = "Radz-at-Han",
        [3710] = "Garlemald",
        [3711] = "Mare Lamentorum",
        [3712] = "Ultima Thule",

        // Dawntrail (7.0)
        [136] = "Tuliyollal",
        [2500] = "Solution Nine",
        [5301] = "Urqopacha",
        [5406] = "Shaaloani",
        [4505] = "Urqopacha",
        [4506] = "Kozama'uka",
        [4507] = "Yak T'el",
        [4508] = "Shaaloani",
        [4509] = "Heritage Found",
        [4510] = "Living Memory",

        // A Realm Reborn Zones
        [24] = "Mor Dhona",
        [57] = "North Shroud",
        [42] = "Western Thanalan",
        [43] = "Central Thanalan",
        [44] = "Eastern Thanalan",
        [45] = "Southern Thanalan",
        [46] = "Northern Thanalan",
        [30] = "Middle La Noscea",
        [31] = "Lower La Noscea",
        [32] = "Eastern La Noscea",
        [33] = "Western La Noscea",
        [34] = "Upper La Noscea",
        [35] = "Outer La Noscea",
        [148] = "Central Shroud",
        [55] = "East Shroud",
        [56] = "South Shroud",
    };

    /// <summary>
    /// Maps a Garland location ID to a readable zone name.
    /// Uses hard-coded mappings for common zones, falls back to "Zone {id}" for unknown zones.
    /// </summary>
    public static string LocationIdToName(int locationId)
    {
        return ZoneNameMappings.TryGetValue(locationId, out var name) 
            ? name 
            : $"Zone {locationId}";
    }
}

/// <summary>
/// JSON converter that handles both int and string values, returning them as strings.
/// Handles Garland API where recipe IDs can be integers (regular crafts) or strings (company crafts).
/// </summary>
public class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            // Handle integers
            if (reader.TryGetInt32(out int intValue))
            {
                return intValue.ToString();
            }
            // Handle longs
            if (reader.TryGetInt64(out long longValue))
            {
                return longValue.ToString();
            }
            // Handle doubles
            if (reader.TryGetDouble(out double doubleValue))
            {
                return doubleValue.ToString();
            }
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

/// <summary>
/// JSON converter for coordinate arrays that handles both numeric and string formats.
/// Garland API returns coordinates as either [11.04, 11.4] or ["12.5", "8.3"].
/// </summary>
public class FlexibleCoordinatesConverter : JsonConverter<List<double>?>
{
    public override List<double>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return null;
        }

        var coordinates = new List<double>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetDouble(out double value))
                {
                    coordinates.Add(value);
                }
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                var stringValue = reader.GetString();
                if (double.TryParse(stringValue, out double value))
                {
                    coordinates.Add(value);
                }
            }
        }

        return coordinates.Count > 0 ? coordinates : null;
    }

    public override void Write(Utf8JsonWriter writer, List<double>? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var coord in value)
        {
            writer.WriteNumberValue(coord);
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Response from Garland Tools zone API.
/// </summary>
public class GarlandZoneResponse
{
    [JsonPropertyName("zone")]
    public GarlandZoneInfo? Zone { get; set; }
}

/// <summary>
/// Zone information from Garland Tools.
/// </summary>
public class GarlandZoneInfo
{
    [JsonPropertyName("i")]
    public int Id { get; set; }

    [JsonPropertyName("n")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("l")]
    public int? Level { get; set; }
}
