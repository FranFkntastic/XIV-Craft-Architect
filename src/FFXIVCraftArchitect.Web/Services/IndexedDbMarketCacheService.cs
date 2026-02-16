using System.Text.Json;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services;
using Microsoft.JSInterop;

namespace FFXIVCraftArchitect.Web.Services;

/// <summary>
/// IndexedDB implementation of IMarketCacheService for Blazor WebAssembly.
/// Uses Unix timestamps to avoid DateTime serialization issues.
/// Implements automatic cleanup and cache size limits.
/// </summary>
public class IndexedDbMarketCacheService : IMarketCacheService
{
    private readonly IJSRuntime _jsRuntime;
    private readonly UniversalisService _universalisService;
    private readonly ILogger<IndexedDbMarketCacheService>? _logger;
    private readonly TimeSpan _defaultMaxAge = TimeSpan.FromHours(1);
    private const long MaxCacheSizeBytes = 500 * 1024 * 1024; // 500MB max
    private const int MaxCacheEntries = 10000; // Max 10k items

    public IndexedDbMarketCacheService(
        IJSRuntime jsRuntime,
        UniversalisService universalisService,
        ILogger<IndexedDbMarketCacheService>? logger = null)
    {
        _jsRuntime = jsRuntime;
        _universalisService = universalisService;
        _logger = logger;
        
        _logger?.LogInformation("[IndexedDbMarketCache] Initialized with maxSize={MaxSize}MB, maxEntries={MaxEntries}", 
            MaxCacheSizeBytes / 1024 / 1024, MaxCacheEntries);
    }

    private static string GetKey(int itemId, string dataCenter) => $"{itemId}@{dataCenter}";

    /// <summary>
    /// Converts Unix timestamp to DateTimeOffset safely.
    /// </summary>
    private static DateTimeOffset UnixToDateTimeOffset(long unixSeconds)
    {
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    /// <summary>
    /// Converts DateTime to Unix timestamp safely.
    /// </summary>
    private static long DateTimeToUnix(DateTime dateTime)
    {
        return new DateTimeOffset(dateTime).ToUnixTimeSeconds();
    }

    public async Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var maxAgeSeconds = (long)(maxAge ?? _defaultMaxAge).TotalSeconds;
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow) - maxAgeSeconds;
        var key = GetKey(itemId, dataCenter);
        
        try
        {
            var entry = await _jsRuntime.InvokeAsync<IndexedDbMarketCacheEntry?>("IndexedDB.loadMarketData", key);
            
            if (entry == null)
            {
                _logger?.LogDebug("[IndexedDbMarketCache] MISS for {ItemId}@{DataCenter}", itemId, dataCenter);
                return null;
            }
            
            // Check if stale using Unix timestamp comparison
            if (entry.FetchedAtUnix <= cutoffUnix)
            {
                var age = DateTime.UtcNow - UnixToDateTimeOffset(entry.FetchedAtUnix).DateTime;
                _logger?.LogDebug("[IndexedDbMarketCache] STALE for {ItemId}@{DataCenter} (age: {Age:F1}h)", 
                    itemId, dataCenter, age.TotalHours);
                return null;
            }
            
            _logger?.LogDebug("[IndexedDbMarketCache] HIT for {ItemId}@{DataCenter}", itemId, dataCenter);
            
            return new CachedMarketData
            {
                ItemId = itemId,
                DataCenter = dataCenter,
                FetchedAtUnix = entry.FetchedAtUnix,
                DCAveragePrice = entry.DcAvgPrice,
                HQAveragePrice = entry.HqAvgPrice,
                Worlds = entry.Worlds ?? new List<CachedWorldData>()
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error getting data for {ItemId}@{DataCenter}", itemId, dataCenter);
            return null;
        }
    }

    public async Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var maxAgeSeconds = (long)(maxAge ?? _defaultMaxAge).TotalSeconds;
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow) - maxAgeSeconds;
        var key = GetKey(itemId, dataCenter);
        
        try
        {
            var entry = await _jsRuntime.InvokeAsync<IndexedDbMarketCacheEntry?>("IndexedDB.loadMarketData", key);
            
            if (entry == null)
            {
                _logger?.LogDebug("[IndexedDbMarketCache] NO DATA for {ItemId}@{DataCenter}", itemId, dataCenter);
                return (null, false);
            }
            
            var isStale = entry.FetchedAtUnix <= cutoffUnix;
            var age = DateTime.UtcNow - UnixToDateTimeOffset(entry.FetchedAtUnix).DateTime;
            
            _logger?.LogDebug("[IndexedDbMarketCache] {Status} for {ItemId}@{DataCenter} (age: {Age:F1}h)", 
                isStale ? "STALE" : "FRESH", itemId, dataCenter, age.TotalHours);
            
            var data = new CachedMarketData
            {
                ItemId = itemId,
                DataCenter = dataCenter,
                FetchedAtUnix = entry.FetchedAtUnix,
                DCAveragePrice = entry.DcAvgPrice,
                HQAveragePrice = entry.HqAvgPrice,
                Worlds = entry.Worlds ?? new List<CachedWorldData>()
            };
            
            return (data, isStale);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error getting data with stale for {ItemId}@{DataCenter}", itemId, dataCenter);
            return (null, false);
        }
    }

    public async Task SetAsync(int itemId, string dataCenter, CachedMarketData data)
    {
        var key = GetKey(itemId, dataCenter);
        var age = data.Age;
        
        _logger?.LogDebug("[IndexedDbMarketCache] Storing {ItemId}@{DataCenter} with FetchedAtUnix={FetchedAt} (age={Age:F1}min)", 
            itemId, dataCenter, data.FetchedAtUnix, age.TotalMinutes);
        
        try
        {
            var entry = new IndexedDbMarketCacheEntry
            {
                Key = key,
                ItemId = itemId,
                DataCenter = dataCenter,
                FetchedAtUnix = data.FetchedAtUnix,
                DcAvgPrice = data.DCAveragePrice,
                HqAvgPrice = data.HQAveragePrice,
                Worlds = data.Worlds
            };
            
            await _jsRuntime.InvokeVoidAsync("IndexedDB.saveMarketData", key, entry);
            _logger?.LogDebug("[IndexedDbMarketCache] Stored {ItemId}@{DataCenter}", itemId, dataCenter);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error storing data for {ItemId}@{DataCenter}", itemId, dataCenter);
        }
    }

    public async Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        return await GetAsync(itemId, dataCenter, maxAge) != null;
    }

    public async Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
        List<(int itemId, string dataCenter)> requests, 
        TimeSpan? maxAge = null)
    {
        var missing = new List<(int, string)>();
        int checkedCount = 0;
        int hitCount = 0;
        
        foreach (var (itemId, dataCenter) in requests)
        {
            checkedCount++;
            var (data, isStale) = await GetWithStaleAsync(itemId, dataCenter, maxAge);
            
            if (data == null)
            {
                missing.Add((itemId, dataCenter));
                _logger?.LogDebug("[IndexedDbMarketCache] MISS (no data) for {ItemId}@{DC}", itemId, dataCenter);
            }
            else if (isStale)
            {
                missing.Add((itemId, dataCenter));
                _logger?.LogDebug("[IndexedDbMarketCache] STALE (age: {Age:F0}min) for {ItemId}@{DC}", 
                    data.Age.TotalMinutes, itemId, dataCenter);
            }
            else
            {
                hitCount++;
                _logger?.LogDebug("[IndexedDbMarketCache] HIT (age: {Age:F0}min) for {ItemId}@{DC}", 
                    data.Age.TotalMinutes, itemId, dataCenter);
            }
        }
        
        _logger?.LogInformation("[IndexedDbMarketCache] Checked {Checked}, Hits {Hits}, Missing {Missing}", 
            checkedCount, hitCount, missing.Count);
        
        return missing;
    }

    public async Task<int> EnsurePopulatedAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var effectiveMaxAge = maxAge ?? _defaultMaxAge;
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow) - (long)effectiveMaxAge.TotalSeconds;
        
        _logger?.LogInformation("[IndexedDbMarketCache] EnsurePopulatedAsync START - {Count} requests, maxAge={MaxAge}", 
            requests.Count, effectiveMaxAge);
        
        if (requests.Count == 0) return 0;
        
        // STEP 1: Clean up stale entries before fetching new data
        progress?.Report("Cleaning up stale cache entries...");
        var cleanedCount = await CleanupStaleAsync(effectiveMaxAge);
        if (cleanedCount > 0)
        {
            _logger?.LogInformation("[IndexedDbMarketCache] Cleaned {Count} stale entries before fetch", cleanedCount);
        }
        
        // STEP 2: Check cache size and enforce limits
        var stats = await GetStatsAsync();
        if (stats.ApproximateSizeBytes > MaxCacheSizeBytes || stats.TotalEntries > MaxCacheEntries)
        {
            _logger?.LogWarning("[IndexedDbMarketCache] Cache size exceeded (size={Size}MB, entries={Entries}). Running emergency cleanup...",
                stats.ApproximateSizeBytes / 1024 / 1024, stats.TotalEntries);
            progress?.Report("Cache size limit reached, cleaning up old entries...");
            
            // Aggressive cleanup - remove anything older than 30 minutes
            await CleanupStaleAsync(TimeSpan.FromMinutes(30));
            
            // If still too big, clear half the cache
            var newStats = await GetStatsAsync();
            if (newStats.ApproximateSizeBytes > MaxCacheSizeBytes * 0.8)
            {
                await ClearOldestEntriesAsync(stats.TotalEntries / 2);
            }
        }
        
        // STEP 3: Check what's missing from cache
        var missing = await GetMissingAsync(requests, maxAge);
        if (missing.Count == 0)
        {
            _logger?.LogInformation("[IndexedDbMarketCache] All {Count} items already in cache", requests.Count);
            return 0;
        }
        
        _logger?.LogInformation("[IndexedDbMarketCache] Fetching {MissingCount}/{TotalCount} items from Universalis", 
            missing.Count, requests.Count);
        progress?.Report($"Fetching market data for {missing.Count} items...");
        
        // STEP 4: Group by data center for efficient bulk fetching
        var byDataCenter = missing.GroupBy(x => x.dataCenter).ToList();
        int fetchedCount = 0;
        int verifiedCount = 0;
        
        foreach (var dcGroup in byDataCenter)
        {
            var dc = dcGroup.Key;
            var itemIds = dcGroup.Select(x => x.itemId).ToList();
            
            try
            {
                progress?.Report($"Fetching {itemIds.Count} items from {dc}...");
                
                var fetchedData = await _universalisService.GetMarketDataBulkAsync(dc, itemIds, useParallel: true, ct: ct);
                
                // STEP 5: Store each result in cache with verification
                foreach (var kvp in fetchedData)
                {
                    var cachedData = ConvertUniversalisResponseToCachedData(kvp.Key, dc, kvp.Value);
                    await SetAsync(kvp.Key, dc, cachedData);
                    fetchedCount++;
                    
                    // STEP 6: Verify the data was stored correctly
                    var verified = await VerifyStoredDataAsync(kvp.Key, dc, cachedData.FetchedAtUnix);
                    if (verified)
                    {
                        verifiedCount++;
                    }
                    else
                    {
                        _logger?.LogWarning("[IndexedDbMarketCache] Data verification failed for {ItemId}@{DC}", kvp.Key, dc);
                    }
                }
                
                _logger?.LogInformation("[IndexedDbMarketCache] Fetched and cached {FetchedCount}/{RequestedCount} items from {DC} (verified: {Verified})",
                    fetchedData.Count, itemIds.Count, dc, verifiedCount);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[IndexedDbMarketCache] Failed to fetch items from {DC}", dc);
                // Continue with other DCs - partial failure is acceptable
            }
        }
        
        _logger?.LogInformation("[IndexedDbMarketCache] EnsurePopulatedAsync COMPLETE - Fetched: {Fetched}, Verified: {Verified}",
            fetchedCount, verifiedCount);
        
        return fetchedCount;
    }

    /// <summary>
    /// Verifies that data was stored correctly by reading it back.
    /// </summary>
    private async Task<bool> VerifyStoredDataAsync(int itemId, string dataCenter, long expectedUnixTimestamp)
    {
        try
        {
            var key = GetKey(itemId, dataCenter);
            var entry = await _jsRuntime.InvokeAsync<IndexedDbMarketCacheEntry?>("IndexedDB.loadMarketData", key);
            
            if (entry == null)
            {
                _logger?.LogWarning("[IndexedDbMarketCache] Verification failed - entry missing for {ItemId}@{DC}", itemId, dataCenter);
                return false;
            }
            
            // Allow 1 second tolerance for timestamp comparison
            var timestampMatch = Math.Abs(entry.FetchedAtUnix - expectedUnixTimestamp) <= 1;
            
            if (!timestampMatch)
            {
                _logger?.LogWarning("[IndexedDbMarketCache] Verification failed - timestamp mismatch for {ItemId}@{DC} (expected: {Expected}, got: {Actual})",
                    itemId, dataCenter, expectedUnixTimestamp, entry.FetchedAtUnix);
                return false;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Verification error for {ItemId}@{DC}", itemId, dataCenter);
            return false;
        }
    }

    public async Task<int> CleanupStaleAsync(TimeSpan maxAge)
    {
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow) - (long)maxAge.TotalSeconds;
        
        try
        {
            var deleted = await _jsRuntime.InvokeAsync<int>("IndexedDB.deleteStaleMarketData", cutoffUnix);
            if (deleted > 0)
            {
                _logger?.LogInformation("[IndexedDbMarketCache] Cleaned up {Count} stale entries (older than {MaxAge})", deleted, maxAge);
            }
            return deleted;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error cleaning up stale entries");
            return 0;
        }
    }

    /// <summary>
    /// Clears the oldest N entries from the cache (LRU eviction).
    /// </summary>
    public async Task<int> ClearOldestEntriesAsync(int count)
    {
        try
        {
            var deleted = await _jsRuntime.InvokeAsync<int>("IndexedDB.deleteOldestEntries", count);
            _logger?.LogWarning("[IndexedDbMarketCache] Emergency cleanup: removed {Deleted} oldest entries", deleted);
            return deleted;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error clearing oldest entries");
            return 0;
        }
    }

    public async Task<CacheStats> GetStatsAsync()
    {
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow.AddHours(-1));
        
        try
        {
            var stats = await _jsRuntime.InvokeAsync<IndexedDbCacheStats>("IndexedDB.getMarketCacheStats", cutoffUnix);
            
            return new CacheStats
            {
                TotalEntries = stats.Total,
                ValidEntries = stats.Valid,
                StaleEntries = stats.Stale,
                OldestEntry = stats.OldestUnix > 0 ? UnixToDateTimeOffset(stats.OldestUnix).DateTime : null,
                NewestEntry = stats.NewestUnix > 0 ? UnixToDateTimeOffset(stats.NewestUnix).DateTime : null,
                ApproximateSizeBytes = stats.SizeBytes
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error getting stats");
            return new CacheStats();
        }
    }

    private CachedMarketData ConvertUniversalisResponseToCachedData(int itemId, string dataCenter, UniversalisResponse response)
    {
        _logger?.LogDebug("[IndexedDbMarketCache] Converting response for {ItemId}@{DC} with {ListingCount} listings", 
            itemId, dataCenter, response.Listings.Count);
        
        var worlds = new List<CachedWorldData>();
        
        foreach (var worldListing in response.Listings.GroupBy(l => l.WorldName))
        {
            worlds.Add(new CachedWorldData
            {
                WorldName = worldListing.Key ?? "Unknown",
                Listings = worldListing.Select(l => new CachedListing
                {
                    Quantity = l.Quantity,
                    PricePerUnit = l.PricePerUnit,
                    RetainerName = l.RetainerName ?? "Unknown",
                    IsHq = l.IsHq
                }).ToList()
            });
        }
        
        var nowUnix = DateTimeToUnix(DateTime.UtcNow);
        _logger?.LogDebug("[IndexedDbMarketCache] Setting FetchedAtUnix={Now} for {ItemId}@{DC}", nowUnix, itemId, dataCenter);
        
        return new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            FetchedAtUnix = nowUnix,
            DCAveragePrice = (decimal)(response.AveragePriceNq > 0 ? response.AveragePriceNq : response.AveragePrice),
            HQAveragePrice = response.AveragePriceHq > 0 ? (decimal)response.AveragePriceHq : null,
            Worlds = worlds
        };
    }
}

/// <summary>
/// Data structure for IndexedDB market cache entries.
/// Uses Unix timestamp (long) instead of DateTime to avoid serialization issues.
/// </summary>
public class IndexedDbMarketCacheEntry
{
    public string Key { get; set; } = string.Empty;
    public int ItemId { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public long FetchedAtUnix { get; set; }  // Unix timestamp in seconds
    public decimal DcAvgPrice { get; set; }
    public decimal? HqAvgPrice { get; set; }
    public List<CachedWorldData> Worlds { get; set; } = new();
}

/// <summary>
/// Statistics returned from IndexedDB.
/// </summary>
public class IndexedDbCacheStats
{
    public int Total { get; set; }
    public int Valid { get; set; }
    public int Stale { get; set; }
    public long OldestUnix { get; set; }  // Unix timestamp
    public long NewestUnix { get; set; }  // Unix timestamp
    public long SizeBytes { get; set; }
}
