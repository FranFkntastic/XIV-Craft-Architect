using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.Core.Services.Interfaces;

/// <summary>
/// Service for checking world status (congested, preferred, etc.)
/// </summary>
public interface IWorldStatusService
{
    /// <summary>
    /// Get the status of a specific world.
    /// </summary>
    WorldStatus? GetWorldStatus(string worldName);
    
    /// <summary>
    /// Check if a world is congested.
    /// </summary>
    bool IsWorldCongested(string worldName);
    
    /// <summary>
    /// Gets all world statuses.
    /// </summary>
    Dictionary<string, WorldStatus> GetAllWorldStatuses();
    
    /// <summary>
    /// Gets the last time the status was updated.
    /// </summary>
    DateTime? LastUpdated { get; }
    
    /// <summary>
    /// Check if data needs refresh (older than cache validity period).
    /// </summary>
    bool NeedsRefresh();
    
    /// <summary>
    /// Fetch world status data and update cache.
    /// </summary>
    Task<bool> RefreshStatusAsync(CancellationToken ct = default);
}
