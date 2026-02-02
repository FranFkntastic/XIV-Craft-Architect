using System.Text.Json.Serialization;

namespace FFXIVCraftArchitect.Models;

/// <summary>
/// Artisan crafting list format for export/import.
/// Based on Artisan's NewCraftingList class.
/// </summary>
public class ArtisanCraftingList
{
    [JsonPropertyName("ID")]
    public int ID { get; set; }

    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Recipes")]
    public List<ArtisanListItem> Recipes { get; set; } = new();

    [JsonPropertyName("ExpandedList")]
    public List<uint> ExpandedList { get; set; } = new();

    [JsonPropertyName("SkipIfEnough")]
    public bool SkipIfEnough { get; set; }

    [JsonPropertyName("SkipLiteral")]
    public bool SkipLiteral { get; set; }

    [JsonPropertyName("Materia")]
    public bool Materia { get; set; }

    [JsonPropertyName("Repair")]
    public bool Repair { get; set; }

    [JsonPropertyName("RepairPercent")]
    public int RepairPercent { get; set; } = 50;

    [JsonPropertyName("AddAsQuickSynth")]
    public bool AddAsQuickSynth { get; set; }

    [JsonPropertyName("TidyAfter")]
    public bool TidyAfter { get; set; } = true;

    [JsonPropertyName("OnlyRestockNonCrafted")]
    public bool OnlyRestockNonCrafted { get; set; }
}

/// <summary>
/// An item in an Artisan crafting list.
/// ID is the Recipe ID (not item ID).
/// </summary>
public class ArtisanListItem
{
    [JsonPropertyName("ID")]
    public uint ID { get; set; }

    [JsonPropertyName("Quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("ListItemOptions")]
    public ArtisanListItemOptions ListItemOptions { get; set; } = new();
}

/// <summary>
/// Options for an Artisan list item.
/// </summary>
public class ArtisanListItemOptions
{
    [JsonPropertyName("NQOnly")]
    public bool NQOnly { get; set; }

    [JsonPropertyName("Skipping")]
    public bool Skipping { get; set; }
}

/// <summary>
/// Result of an Artisan export operation.
/// </summary>
public class ArtisanExportResult
{
    /// <summary>
    /// The JSON string that can be pasted into Artisan.
    /// </summary>
    public string Json { get; set; } = string.Empty;

    /// <summary>
    /// Number of recipes in the exported list.
    /// </summary>
    public int RecipeCount { get; set; }

    /// <summary>
    /// Items that could not be exported (no recipe found).
    /// </summary>
    public List<string> MissingRecipes { get; set; } = new();

    /// <summary>
    /// Whether the export was successful.
    /// </summary>
    public bool Success => MissingRecipes.Count == 0;
}
