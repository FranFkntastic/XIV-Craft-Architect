namespace FFXIVCraftArchitect.Services.Interfaces;

/// <summary>
/// Service for fetching and caching FFXIV world travel status from Waitingway API.
/// Tracks which worlds currently have travel prohibited (congested worlds at capacity).
/// </summary>
public interface IWaitingwayTravelService
{
    /// <summary>
    /// Gets the current travel prohibition status for all worlds.
    /// Returns cached data if fresh, otherwise fetches from API.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Dictionary mapping world IDs to prohibition status</returns>
    Task<Dictionary<int, bool>> GetTravelProhibitionsAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Checks if travel to a specific world is currently prohibited.
    /// </summary>
    /// <param name="worldId">World ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if travel is prohibited</returns>
    Task<bool> IsTravelProhibitedAsync(int worldId, CancellationToken ct = default);
    
    /// <summary>
    /// Checks if travel to a specific world is currently prohibited.
    /// World name lookup requires world data to be loaded.
    /// </summary>
    /// <param name="worldName">World name</param>
    /// <param name="worldNameToId">Dictionary mapping world names to IDs</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if travel is prohibited</returns>
    Task<bool> IsTravelProhibitedAsync(string worldName, Dictionary<string, int> worldNameToId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the current average DC travel time in seconds.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Average travel time in seconds, or null if not available</returns>
    Task<int?> GetAverageTravelTimeAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets the full travel status data including metadata.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Travel status data or null if not available</returns>
    Task<TravelStatusData?> GetTravelStatusDataAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Forces a refresh of the travel status from the API.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if refresh succeeded</returns>
    Task<bool> RefreshAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets world status information (category, classification) from Waitingway.
    /// This is separate from travel prohibition status.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of world status information or null if not available</returns>
    Task<List<WorldStatusInfo>?> GetWorldStatusAsync(CancellationToken ct = default);
}
