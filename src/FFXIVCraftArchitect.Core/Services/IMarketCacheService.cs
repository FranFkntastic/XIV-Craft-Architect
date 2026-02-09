using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.Core.Services;

/// <summary>
/// Platform-agnostic interface for caching raw market data from Universalis.
/// Implementations handle the actual storage (SQLite, PostgreSQL, etc.)
/// and are responsible for fetching missing data from the API.
/// </summary>
public interface IMarketCacheService
{
    /// <summary>
    /// Get cached market data for an item if it exists and is not stale.
    /// </summary>
    Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null);
    
    /// <summary>
    /// Get cached market data even if it's stale. Returns null only if no data exists.
    /// </summary>
    Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(int itemId, string dataCenter, TimeSpan? maxAge = null);
    
    /// <summary>
    /// Store market data in the cache.
    /// </summary>
    Task SetAsync(int itemId, string dataCenter, CachedMarketData data);
    
    /// <summary>
    /// Check if we have valid cached data for an item.
    /// </summary>
    Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null);
    
    /// <summary>
    /// Get multiple cached entries. Returns the keys that were NOT found.
    /// </summary>
    Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
        List<(int itemId, string dataCenter)> requests, 
        TimeSpan? maxAge = null);
    
    /// <summary>
    /// Remove stale entries from the cache.
    /// </summary>
    Task<int> CleanupStaleAsync(TimeSpan maxAge);
    
    /// <summary>
    /// Get cache statistics.
    /// </summary>
    Task<CacheStats> GetStatsAsync();
    
    /// <summary>
    /// Ensures the cache is populated with market data for the requested items.
    /// Fetches missing or stale data from Universalis API and stores it in the cache.
    /// This is the primary method for obtaining market data - callers should
    /// call this first, then read from cache using GetAsync.
    /// </summary>
    /// <param name="requests">List of (itemId, dataCenter) pairs to ensure are cached.</param>
    /// <param name="maxAge">Maximum age for cached data. Items older than this will be refetched. Null uses default (1 hour).</param>
    /// <param name="progress">Optional progress reporter for fetch status.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of items fetched from API (0 if all were cached).</returns>
    Task<int> EnsurePopulatedAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Cached market data for a specific item and data center.
/// </summary>
public class CachedMarketData
{
    public int ItemId { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public DateTime FetchedAt { get; set; }
    public decimal DCAveragePrice { get; set; }
    public decimal? HQAveragePrice { get; set; }
    public List<CachedWorldData> Worlds { get; set; } = new();
}

/// <summary>
/// Cached data for a specific world.
/// </summary>
public class CachedWorldData
{
    public string WorldName { get; set; } = string.Empty;
    public List<CachedListing> Listings { get; set; } = new();
    public bool IsCongested { get; set; }
}

/// <summary>
/// Cached market listing.
/// </summary>
public class CachedListing
{
    public int Quantity { get; set; }
    public long PricePerUnit { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public bool IsHq { get; set; }
}

/// <summary>
/// Cache statistics.
/// </summary>
public class CacheStats
{
    public int TotalEntries { get; set; }
    public int ValidEntries { get; set; }
    public int StaleEntries { get; set; }
    public DateTime? OldestEntry { get; set; }
    public DateTime? NewestEntry { get; set; }
    public long ApproximateSizeBytes { get; set; }
    
    public override string ToString() => 
        $"{ValidEntries} valid, {StaleEntries} stale (total: {TotalEntries})";
}
