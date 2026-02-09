using System.Text.Json;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services;
using Microsoft.JSInterop;

namespace FFXIVCraftArchitect.Web.Services;

/// <summary>
/// IndexedDB implementation of IMarketCacheService for Blazor WebAssembly.
/// Mirrors the behavior of SqliteMarketCacheService but uses browser IndexedDB for persistence.
/// </summary>
public class IndexedDbMarketCacheService : IMarketCacheService
{
    private readonly IJSRuntime _jsRuntime;
    private readonly UniversalisService _universalisService;
    private readonly ILogger<IndexedDbMarketCacheService>? _logger;
    private readonly TimeSpan _defaultMaxAge = TimeSpan.FromHours(1);
    private readonly JsonSerializerOptions _jsonOptions;

    public IndexedDbMarketCacheService(
        IJSRuntime jsRuntime,
        UniversalisService universalisService,
        ILogger<IndexedDbMarketCacheService>? logger = null)
    {
        _jsRuntime = jsRuntime;
        _universalisService = universalisService;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
        };
        
        _logger?.LogInformation("[IndexedDbMarketCache] Initialized");
    }

    private static string GetKey(int itemId, string dataCenter) => $"{itemId}@{dataCenter}";

    public async Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);
        var key = GetKey(itemId, dataCenter);
        
        try
        {
            var entry = await _jsRuntime.InvokeAsync<IndexedDbMarketCacheEntry?>("IndexedDB.loadMarketData", key);
            
            if (entry == null)
            {
                _logger?.LogDebug("[IndexedDbMarketCache] MISS for {ItemId}@{DataCenter}", itemId, dataCenter);
                return null;
            }
            
            if (entry.FetchedAt <= cutoff)
            {
                _logger?.LogDebug("[IndexedDbMarketCache] STALE for {ItemId}@{DataCenter}", itemId, dataCenter);
                return null;
            }
            
            _logger?.LogDebug("[IndexedDbMarketCache] HIT for {ItemId}@{DataCenter}", itemId, dataCenter);
            
            return new CachedMarketData
            {
                ItemId = itemId,
                DataCenter = dataCenter,
                FetchedAt = entry.FetchedAt,
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
        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);
        var key = GetKey(itemId, dataCenter);
        
        try
        {
            var entry = await _jsRuntime.InvokeAsync<IndexedDbMarketCacheEntry?>("IndexedDB.loadMarketData", key);
            
            if (entry == null)
            {
                _logger?.LogDebug("[IndexedDbMarketCache] NO DATA for {ItemId}@{DataCenter}", itemId, dataCenter);
                return (null, false);
            }
            
            var isStale = entry.FetchedAt <= cutoff;
            
            _logger?.LogDebug("[IndexedDbMarketCache] {Status} for {ItemId}@{DataCenter} (fetched {Hours:F1}h ago)", 
                isStale ? "STALE" : "FRESH", itemId, dataCenter, (DateTime.UtcNow - entry.FetchedAt).TotalHours);
            
            var data = new CachedMarketData
            {
                ItemId = itemId,
                DataCenter = dataCenter,
                FetchedAt = entry.FetchedAt,
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
        var age = DateTime.UtcNow - data.FetchedAt;
        
        _logger?.LogDebug("[IndexedDbMarketCache] Storing {ItemId}@{DataCenter} with FetchedAt={FetchedAt} (age={Age:F1}min)", 
            itemId, dataCenter, data.FetchedAt, age.TotalMinutes);
        
        try
        {
            var entry = new IndexedDbMarketCacheEntry
            {
                Key = key,
                ItemId = itemId,
                DataCenter = dataCenter,
                FetchedAt = data.FetchedAt,
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
                var age = DateTime.UtcNow - data.FetchedAt;
                _logger?.LogDebug("[IndexedDbMarketCache] STALE ({Age:F0}min old) for {ItemId}@{DC}", 
                    age.TotalMinutes, itemId, dataCenter);
            }
            else
            {
                hitCount++;
                var age = DateTime.UtcNow - data.FetchedAt;
                _logger?.LogDebug("[IndexedDbMarketCache] HIT ({Age:F0}min old) for {ItemId}@{DC}", 
                    age.TotalMinutes, itemId, dataCenter);
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
        var cutoff = DateTime.UtcNow - effectiveMaxAge;
        
        _logger?.LogInformation("[IndexedDbMarketCache] EnsurePopulatedAsync START - {Count} requests, maxAge={MaxAge}, cutoff={Cutoff}", 
            requests.Count, effectiveMaxAge, cutoff);
        
        if (requests.Count == 0) return 0;
        
        // Check what's missing from cache
        var missing = await GetMissingAsync(requests, maxAge);
        if (missing.Count == 0)
        {
            _logger?.LogInformation("[IndexedDbMarketCache] All {Count} items already in cache (maxAge={MaxAge})", 
                requests.Count, effectiveMaxAge);
            return 0;
        }
        
        _logger?.LogInformation("[IndexedDbMarketCache] Fetching {MissingCount}/{TotalCount} items from Universalis", 
            missing.Count, requests.Count);
        progress?.Report($"Fetching market data for {missing.Count} items...");
        
        // Group by data center for efficient bulk fetching
        var byDataCenter = missing.GroupBy(x => x.dataCenter).ToList();
        int fetchedCount = 0;
        
        foreach (var dcGroup in byDataCenter)
        {
            var dc = dcGroup.Key;
            var itemIds = dcGroup.Select(x => x.itemId).ToList();
            
            try
            {
                progress?.Report($"Fetching {itemIds.Count} items from {dc}...");
                
                var fetchedData = await _universalisService.GetMarketDataBulkAsync(dc, itemIds, ct);
                
                // Store each result in cache
                foreach (var kvp in fetchedData)
                {
                    var cachedData = ConvertUniversalisResponseToCachedData(kvp.Key, dc, kvp.Value);
                    await SetAsync(kvp.Key, dc, cachedData);
                    fetchedCount++;
                }
                
                _logger?.LogInformation("[IndexedDbMarketCache] Fetched and cached {FetchedCount}/{RequestedCount} items from {DC}",
                    fetchedData.Count, itemIds.Count, dc);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[IndexedDbMarketCache] Failed to fetch items from {DC}", dc);
                // Continue with other DCs - partial failure is acceptable
            }
        }
        
        return fetchedCount;
    }

    public async Task<int> CleanupStaleAsync(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        
        try
        {
            var deleted = await _jsRuntime.InvokeAsync<int>("IndexedDB.deleteStaleMarketData", cutoff.ToString("O"));
            _logger?.LogInformation("[IndexedDbMarketCache] Cleaned up {Count} stale entries", deleted);
            return deleted;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error cleaning up stale entries");
            return 0;
        }
    }

    public async Task<CacheStats> GetStatsAsync()
    {
        var cutoff = DateTime.UtcNow.AddHours(-1).ToString("O");
        
        try
        {
            var stats = await _jsRuntime.InvokeAsync<IndexedDbCacheStats>("IndexedDB.getMarketCacheStats", cutoff);
            
            return new CacheStats
            {
                TotalEntries = stats.Total,
                ValidEntries = stats.Valid,
                StaleEntries = stats.Stale,
                OldestEntry = stats.Oldest != null ? DateTime.Parse(stats.Oldest) : null,
                NewestEntry = stats.Newest != null ? DateTime.Parse(stats.Newest) : null,
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
        
        var now = DateTime.UtcNow;
        _logger?.LogDebug("[IndexedDbMarketCache] Setting FetchedAt={Now} for {ItemId}@{DC}", now, itemId, dataCenter);
        
        return new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            FetchedAt = now,
            DCAveragePrice = (decimal)(response.AveragePriceNq > 0 ? response.AveragePriceNq : response.AveragePrice),
            HQAveragePrice = response.AveragePriceHq > 0 ? (decimal)response.AveragePriceHq : null,
            Worlds = worlds
        };
    }
}

/// <summary>
/// Data structure for IndexedDB market cache entries.
/// </summary>
public class IndexedDbMarketCacheEntry
{
    public string Key { get; set; } = string.Empty;
    public int ItemId { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public DateTime FetchedAt { get; set; }
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
    public string? Oldest { get; set; }
    public string? Newest { get; set; }
    public long SizeBytes { get; set; }
}
