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
    /// Refresh world status data.
    /// </summary>
    Task RefreshStatusAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Check if data needs refresh (older than 1 hour).
    /// </summary>
    bool NeedsRefresh();
}
