using FFXIVCraftArchitect.Models;

namespace FFXIVCraftArchitect.Services.Interfaces;

/// <summary>
/// Service for fetching and caching FFXIV world status from the Lodestone.
/// Scrapes the official world status page to get congestion data.
/// </summary>
public interface IWorldStatusService
{
    /// <summary>
    /// Gets the status for a specific world.
    /// </summary>
    /// <param name="worldName">World name</param>
    /// <returns>World status or null if not found</returns>
    WorldStatus? GetWorldStatus(string worldName);
    
    /// <summary>
    /// Checks if a world is congested.
    /// </summary>
    /// <param name="worldName">World name</param>
    /// <returns>True if world is congested</returns>
    bool IsWorldCongested(string worldName);
    
    /// <summary>
    /// Gets all world statuses.
    /// </summary>
    /// <returns>Dictionary of world name to status</returns>
    Dictionary<string, WorldStatus> GetAllWorldStatuses();
    
    /// <summary>
    /// Gets the last time the status was updated.
    /// </summary>
    DateTime? LastUpdated { get; }
    
    /// <summary>
    /// Checks if the cache needs refreshing.
    /// </summary>
    /// <returns>True if cache is stale or empty</returns>
    bool NeedsRefresh();
    
    /// <summary>
    /// Fetches world status from the Lodestone and updates the cache.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if refresh succeeded</returns>
    Task<bool> RefreshStatusAsync(CancellationToken ct = default);
}
