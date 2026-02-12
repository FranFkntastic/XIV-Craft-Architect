namespace FFXIVCraftArchitect.Core.Services.Interfaces;

/// <summary>
/// Service for fetching and caching FFXIV world travel status from Waitingway API.
/// Tracks which worlds currently have travel prohibited (congested worlds at capacity).
/// </summary>
[Obsolete("This service is disabled pending re-implementation. The current implementation is non-functional.")]
public interface IWaitingwayTravelService
{
    /// <summary>
    /// Gets the current travel prohibition status for all worlds.
    /// </summary>
    Task<Dictionary<int, bool>> GetTravelProhibitionsAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Checks if travel to a specific world is currently prohibited.
    /// </summary>
    Task<bool> IsTravelProhibitedAsync(int worldId, CancellationToken ct = default);
    
    /// <summary>
    /// Checks if travel to a specific world is currently prohibited by name.
    /// </summary>
    Task<bool> IsTravelProhibitedAsync(string worldName, Dictionary<string, int> worldNameToId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the current average DC travel time in seconds.
    /// </summary>
    Task<int?> GetAverageTravelTimeAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets the full travel status data including metadata.
    /// </summary>
    Task<TravelStatusData?> GetTravelStatusDataAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Forces a refresh of the travel status from the API.
    /// </summary>
    Task<bool> RefreshAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets world status information from Waitingway.
    /// </summary>
    Task<List<WorldStatusInfo>?> GetWorldStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// Cached travel status data.
/// </summary>
public class TravelStatusData
{
    public Dictionary<int, bool> Prohibited { get; set; } = new();
    public int TravelTime { get; set; }
    public DateTime FetchedAt { get; set; }
}

/// <summary>
/// World status information from Waitingway.
/// </summary>
public class WorldStatusInfo
{
    public int WorldId { get; set; }
    public int Status { get; set; }
    public int Category { get; set; }
    public bool CanCreateCharacter { get; set; }
    
    /// <summary>
    /// World category classification.
    /// </summary>
    public WorldCategory Classification => Category switch
    {
        0 => WorldCategory.Standard,
        1 => WorldCategory.Preferred,
        2 => WorldCategory.PreferredPlus,
        3 => WorldCategory.Congested,
        _ => WorldCategory.Standard
    };
}

/// <summary>
/// World category classification.
/// </summary>
public enum WorldCategory
{
    Standard,
    Preferred,
    PreferredPlus,
    Congested
}
