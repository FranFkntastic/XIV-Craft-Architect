using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for managing a temporary blacklist of worlds that are unavailable for travel.
/// Worlds can be blacklisted by the user when they find a world is travel-prohibited.
/// Blacklisted worlds expire after a configurable duration (default 30 minutes).
/// </summary>
public class WorldBlacklistService
{
    private readonly ILogger<WorldBlacklistService> _logger;
    private readonly ConcurrentDictionary<int, BlacklistEntry> _blacklistedWorlds = new();
    private readonly TimeSpan _defaultBlacklistDuration = TimeSpan.FromMinutes(30);
    
    public WorldBlacklistService(ILogger<WorldBlacklistService> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Event raised when a world is added to the blacklist.
    /// </summary>
    public event EventHandler<WorldBlacklistEventArgs>? WorldBlacklisted;
    
    /// <summary>
    /// Event raised when a world is removed from the blacklist (either expired or manually removed).
    /// </summary>
    public event EventHandler<WorldBlacklistEventArgs>? WorldUnblacklisted;
    
    /// <summary>
    /// Adds a world to the blacklist for the default duration (30 minutes).
    /// </summary>
    /// <param name="worldId">The world ID to blacklist</param>
    /// <param name="worldName">The world name for logging/display</param>
    /// <param name="reason">Optional reason for blacklisting</param>
    public void AddToBlacklist(int worldId, string worldName, string? reason = null)
    {
        AddToBlacklist(worldId, worldName, _defaultBlacklistDuration, reason);
    }
    
    /// <summary>
    /// Adds a world to the blacklist for a specified duration.
    /// </summary>
    /// <param name="worldId">The world ID to blacklist</param>
    /// <param name="worldName">The world name for logging/display</param>
    /// <param name="duration">How long to keep the world blacklisted</param>
    /// <param name="reason">Optional reason for blacklisting</param>
    public void AddToBlacklist(int worldId, string worldName, TimeSpan duration, string? reason = null)
    {
        var expiresAt = DateTime.UtcNow.Add(duration);
        var entry = new BlacklistEntry
        {
            WorldId = worldId,
            WorldName = worldName,
            BlacklistedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
            Reason = reason ?? "User blacklisted"
        };
        
        var wasAdded = _blacklistedWorlds.AddOrUpdate(worldId, entry, (key, oldValue) => entry);
        
        _logger.LogInformation("[WorldBlacklist] Added {WorldName} (ID: {WorldId}) to blacklist until {Expires:HH:mm:ss}. Reason: {Reason}",
            worldName, worldId, expiresAt, entry.Reason);
        
        WorldBlacklisted?.Invoke(this, new WorldBlacklistEventArgs(worldId, worldName, expiresAt));
    }
    
    /// <summary>
    /// Adds a world to the blacklist by name (requires world ID lookup).
    /// </summary>
    public void AddToBlacklist(string worldName, Dictionary<string, int> worldNameToId, string? reason = null)
    {
        if (!worldNameToId.TryGetValue(worldName, out var worldId))
        {
            _logger.LogWarning("[WorldBlacklist] Cannot blacklist {WorldName}: unknown world", worldName);
            return;
        }
        
        AddToBlacklist(worldId, worldName, reason);
    }
    
    /// <summary>
    /// Removes a world from the blacklist.
    /// </summary>
    public bool RemoveFromBlacklist(int worldId)
    {
        if (_blacklistedWorlds.TryRemove(worldId, out var entry))
        {
            _logger.LogInformation("[WorldBlacklist] Removed {WorldName} (ID: {WorldId}) from blacklist",
                entry.WorldName, worldId);
            WorldUnblacklisted?.Invoke(this, new WorldBlacklistEventArgs(worldId, entry.WorldName, null));
            return true;
        }
        return false;
    }
    
    /// <summary>
    /// Clears all worlds from the blacklist.
    /// </summary>
    public void ClearBlacklist()
    {
        var entries = _blacklistedWorlds.ToList();
        _blacklistedWorlds.Clear();
        
        foreach (var kvp in entries)
        {
            WorldUnblacklisted?.Invoke(this, new WorldBlacklistEventArgs(kvp.Key, kvp.Value.WorldName, null));
        }
        
        _logger.LogInformation("[WorldBlacklist] Cleared {Count} worlds from blacklist", entries.Count);
    }
    
    /// <summary>
    /// Checks if a world is currently blacklisted (and not expired).
    /// Automatically removes expired entries.
    /// </summary>
    public bool IsBlacklisted(int worldId)
    {
        CleanupExpiredEntries();
        return _blacklistedWorlds.ContainsKey(worldId);
    }
    
    /// <summary>
    /// Checks if a world is currently blacklisted by name.
    /// </summary>
    public bool IsBlacklisted(string worldName, Dictionary<string, int> worldNameToId)
    {
        if (!worldNameToId.TryGetValue(worldName, out var worldId))
        {
            return false;
        }
        
        return IsBlacklisted(worldId);
    }
    
    /// <summary>
    /// Gets the blacklist entry for a world if it exists and is not expired.
    /// </summary>
    public BlacklistEntry? GetBlacklistEntry(int worldId)
    {
        CleanupExpiredEntries();
        _blacklistedWorlds.TryGetValue(worldId, out var entry);
        return entry;
    }
    
    /// <summary>
    /// Gets all currently blacklisted worlds (excluding expired).
    /// </summary>
    public IReadOnlyList<BlacklistEntry> GetBlacklistedWorlds()
    {
        CleanupExpiredEntries();
        return _blacklistedWorlds.Values.ToList();
    }
    
    /// <summary>
    /// Gets the number of currently blacklisted worlds.
    /// </summary>
    public int BlacklistedCount => GetBlacklistedWorlds().Count;
    
    /// <summary>
    /// Gets the remaining time for a blacklisted world.
    /// Returns null if the world is not blacklisted.
    /// </summary>
    public TimeSpan? GetRemainingTime(int worldId)
    {
        if (_blacklistedWorlds.TryGetValue(worldId, out var entry))
        {
            var remaining = entry.ExpiresAt - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }
    
    /// <summary>
    /// Removes expired entries from the blacklist.
    /// </summary>
    private void CleanupExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var expired = _blacklistedWorlds
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var worldId in expired)
        {
            if (_blacklistedWorlds.TryRemove(worldId, out var entry))
            {
                _logger.LogDebug("[WorldBlacklist] Expired and removed {WorldName} (ID: {WorldId})",
                    entry.WorldName, worldId);
                WorldUnblacklisted?.Invoke(this, new WorldBlacklistEventArgs(worldId, entry.WorldName, null));
            }
        }
    }
}

/// <summary>
/// Represents a blacklisted world entry.
/// </summary>
public class BlacklistEntry
{
    public int WorldId { get; set; }
    public string WorldName { get; set; } = string.Empty;
    public DateTime BlacklistedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    
    /// <summary>
    /// Gets the remaining time until this entry expires.
    /// </summary>
    public TimeSpan RemainingTime => ExpiresAt - DateTime.UtcNow;
    
    /// <summary>
    /// Whether this entry has expired.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    
    /// <summary>
    /// Formatted string showing when the blacklist expires.
    /// </summary>
    public string ExpiresInDisplay
    {
        get
        {
            var remaining = RemainingTime;
            if (remaining.TotalHours >= 1)
                return $"{remaining.TotalHours:F0}h remaining";
            if (remaining.TotalMinutes >= 1)
                return $"{remaining.TotalMinutes:F0}m remaining";
            return "expiring soon";
        }
    }
}

/// <summary>
/// Event args for blacklist events.
/// </summary>
public class WorldBlacklistEventArgs : EventArgs
{
    public int WorldId { get; }
    public string WorldName { get; }
    public DateTime? ExpiresAt { get; }
    
    /// <summary>
    /// Formatted string showing when the blacklist expires.
    /// </summary>
    public string ExpiresInDisplay
    {
        get
        {
            if (!ExpiresAt.HasValue) return "permanent";
            
            var remaining = ExpiresAt.Value - DateTime.UtcNow;
            if (remaining.TotalHours >= 1)
                return $"{remaining.TotalHours:F0}h remaining";
            if (remaining.TotalMinutes >= 1)
                return $"{remaining.TotalMinutes:F0}m remaining";
            return "expiring soon";
        }
    }
    
    public WorldBlacklistEventArgs(int worldId, string worldName, DateTime? expiresAt)
    {
        WorldId = worldId;
        WorldName = worldName;
        ExpiresAt = expiresAt;
    }
}
