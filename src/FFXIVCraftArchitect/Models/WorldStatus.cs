using System.Text.Json.Serialization;

namespace FFXIVCraftArchitect.Models;

/// <summary>
/// World classification/status from Lodestone.
/// </summary>
public enum WorldClassification
{
    Standard,
    Preferred,
    PreferredPlus,
    Congested
}

/// <summary>
/// Status information for a single world.
/// </summary>
public class WorldStatus
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("classification")]
    public WorldClassification Classification { get; set; }
    
    [JsonPropertyName("canCreateCharacter")]
    public bool CanCreateCharacter { get; set; }
    
    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; }
    
    [JsonIgnore]
    public bool IsCongested => Classification == WorldClassification.Congested;
    
    [JsonIgnore]
    public string ClassificationDisplay => Classification switch
    {
        WorldClassification.Standard => "Standard",
        WorldClassification.Preferred => "Preferred",
        WorldClassification.PreferredPlus => "Preferred+",
        WorldClassification.Congested => "Congested",
        _ => "Unknown"
    };
}

/// <summary>
/// Complete world status data for all regions.
/// </summary>
public class WorldStatusData
{
    [JsonPropertyName("worlds")]
    public Dictionary<string, WorldStatus> Worlds { get; set; } = new();
    
    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; }
    
    [JsonPropertyName("source")]
    public string Source { get; set; } = "Lodestone";
}
