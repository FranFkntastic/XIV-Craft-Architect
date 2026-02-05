using System.Text.Json.Serialization;

namespace FFXIVCraftArchitect.Core.Models;

/// <summary>
/// World/Server information from Universalis API.
/// </summary>
public class WorldInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Data Center information from Universalis API.
/// </summary>
public class DataCenterInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;
    
    [JsonPropertyName("worlds")]
    public List<int> WorldIds { get; set; } = new();
}

/// <summary>
/// Aggregated world data for the UI.
/// </summary>
public class WorldData
{
    public Dictionary<int, string> WorldIdToName { get; set; } = new();
    public Dictionary<string, List<string>> DataCenterToWorlds { get; set; } = new();
    public List<string> DataCenters => DataCenterToWorlds.Keys.OrderBy(n => n).ToList();
}

/// <summary>
/// World classification/status from FFXIV world status.
/// </summary>
public enum WorldClassification
{
    Standard,
    Preferred,
    PreferredPlus,
    Congested
}

/// <summary>
/// World status information.
/// </summary>
public class WorldStatus
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public WorldClassification Classification { get; set; }
}
