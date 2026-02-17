using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIV_Craft_Architect.Core.Models.Teamcraft;

/// <summary>
/// Represents a recipe from Teamcraft's CDN data.
/// Maps directly to the JSON structure from recipes.json
/// </summary>
public class TeamcraftRecipe
{
    /// <summary>Recipe ID (matches Artisan's RecipeId). Can be int or string (for FC recipes like "fc1")</summary>
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int Id { get; set; }

    /// <summary>Job ID (9=Carpenter, 10=Blacksmith, etc.)</summary>
    public int Job { get; set; }

    /// <summary>Recipe level</summary>
    public int Lvl { get; set; }

    /// <summary>Number of items produced</summary>
    public int Yields { get; set; }

    /// <summary>Item ID of the result (what we're crafting)</summary>
    public int Result { get; set; }

    /// <summary>Star rating (0-4)</summary>
    public int Stars { get; set; }

    /// <summary>Can use Quick Synthesis</summary>
    public bool Qs { get; set; }

    /// <summary>Can produce HQ</summary>
    public bool Hq { get; set; }

    /// <summary>Durability</summary>
    public int Durability { get; set; }

    /// <summary>Quality</summary>
    public int Quality { get; set; }

    /// <summary>Progress required</summary>
    public int Progress { get; set; }

    /// <summary>Suggested craftsmanship</summary>
    public int SuggestedCraftsmanship { get; set; }

    /// <summary>Required craftsmanship</summary>
    public int CraftsmanshipReq { get; set; }

    /// <summary>Required control</summary>
    public int ControlReq { get; set; }

    /// <summary>Recipe level</summary>
    public int Rlvl { get; set; }

    /// <summary>Required quality (for expert recipes)</summary>
    public int RequiredQuality { get; set; }

    /// <summary>Is expert recipe</summary>
    public bool Expert { get; set; }

    /// <summary>Ingredients required</summary>
    public List<TeamcraftIngredient> Ingredients { get; set; } = new();
}

/// <summary>
/// JSON converter that handles both int and string values, returning 0 for non-numeric strings.
/// This handles FC recipes which have string IDs like "fc1" - those will return 0 and be filtered out.
/// </summary>
public class FlexibleIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetInt32();
        }
        else if (reader.TokenType == JsonTokenType.String)
        {
            var stringValue = reader.GetString();
            if (int.TryParse(stringValue, out int result))
            {
                return result;
            }
            // Non-numeric strings (like "fc1") return 0
            return 0;
        }
        return 0;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// Ingredient in a Teamcraft recipe
/// </summary>
public class TeamcraftIngredient
{
    /// <summary>Item ID</summary>
    public int Id { get; set; }

    /// <summary>Quantity required</summary>
    public int Amount { get; set; }

    /// <summary>Quality contribution</summary>
    public double Quality { get; set; }
}
