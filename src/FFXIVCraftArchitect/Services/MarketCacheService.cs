using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using FFXIVCraftArchitect.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for caching raw market data from Universalis.
/// This cache is global (shared across all plans) and persists to disk.
/// </summary>
public class MarketCacheService
{
    private readonly ILogger<MarketCacheService> _logger;
    private readonly string _cacheDirectory;
    private readonly string _cacheFilePath;
    private readonly JsonSerializerOptions _jsonOptions;
    
    // In-memory cache: key = "{itemId}_{dataCenter}"
    private readonly ConcurrentDictionary<string, CachedMarketData> _cache = new();
    
    // Cache validity duration (1 hour)
    private readonly TimeSpan _cacheValidity = TimeSpan.FromHours(1);
    
    // Lock for file operations
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public MarketCacheService(ILogger<MarketCacheService> logger)
    {
        _logger = logger;
        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "Cache");
        _cacheFilePath = Path.Combine(_cacheDirectory, "market_cache.json");
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        EnsureDirectoryExists();
        LoadCacheFromDisk();
    }

    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
                _logger.LogInformation("[MarketCache] Created cache directory: {Path}", _cacheDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MarketCache] Failed to create cache directory: {Path}", _cacheDirectory);
        }
    }

    /// <summary>
    /// Load the cache from disk on startup.
    /// </summary>
    private void LoadCacheFromDisk()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                _logger.LogInformation("[MarketCache] No existing cache file found");
                return;
            }

            var json = File.ReadAllText(_cacheFilePath);
            var data = JsonSerializer.Deserialize<MarketCacheFile>(json, _jsonOptions);
            
            if (data?.Entries != null)
            {
                foreach (var entry in data.Entries)
                {
                    _cache[entry.Key] = entry.Value;
                }
                
                _logger.LogInformation("[MarketCache] Loaded {Count} entries from disk", _cache.Count);
                
                // Clean up stale entries
                CleanupStaleEntries();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MarketCache] Failed to load cache from disk");
        }
    }

    /// <summary>
    /// Save the cache to disk.
    /// </summary>
    public async Task SaveCacheToDiskAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var data = new MarketCacheFile
            {
                SavedAt = DateTime.UtcNow,
                Entries = _cache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };

            var json = JsonSerializer.Serialize(data, _jsonOptions);
            await File.WriteAllTextAsync(_cacheFilePath, json);
            
            _logger.LogDebug("[MarketCache] Saved {Count} entries to disk", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MarketCache] Failed to save cache to disk");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Get cached market data for an item.
    /// Returns null if not cached or if cache is stale.
    /// </summary>
    public CachedMarketData? Get(int itemId, string dataCenter)
    {
        var key = GetCacheKey(itemId, dataCenter);
        
        if (_cache.TryGetValue(key, out var data))
        {
            // Check if cache is still valid
            if (DateTime.UtcNow - data.FetchedAt < _cacheValidity)
            {
                _logger.LogDebug("[MarketCache] HIT for {ItemId}@{DataCenter} (age: {Age}s)", 
                    itemId, dataCenter, (DateTime.UtcNow - data.FetchedAt).TotalSeconds);
                return data;
            }
            else
            {
                _logger.LogDebug("[MarketCache] STALE for {ItemId}@{DataCenter} (age: {Age}s)", 
                    itemId, dataCenter, (DateTime.UtcNow - data.FetchedAt).TotalSeconds);
            }
        }
        else
        {
            _logger.LogDebug("[MarketCache] MISS for {ItemId}@{DataCenter}", itemId, dataCenter);
        }
        
        return null;
    }

    /// <summary>
    /// Store market data in the cache.
    /// </summary>
    public void Set(int itemId, string dataCenter, CachedMarketData data)
    {
        var key = GetCacheKey(itemId, dataCenter);
        _cache[key] = data;
        _logger.LogDebug("[MarketCache] Stored {ItemId}@{DataCenter}", itemId, dataCenter);
    }

    /// <summary>
    /// Check if we have valid cached data for an item.
    /// </summary>
    public bool HasValidCache(int itemId, string dataCenter)
    {
        return Get(itemId, dataCenter) != null;
    }

    /// <summary>
    /// Get multiple cached entries at once.
    /// Returns the keys that were NOT found in cache.
    /// </summary>
    public List<(int itemId, string dataCenter)> GetMissingEntries(List<(int itemId, string dataCenter)> requests)
    {
        var missing = new List<(int, string)>();
        
        foreach (var (itemId, dataCenter) in requests)
        {
            if (!HasValidCache(itemId, dataCenter))
            {
                missing.Add((itemId, dataCenter));
            }
        }
        
        return missing;
    }

    /// <summary>
    /// Remove stale entries from the cache.
    /// </summary>
    private void CleanupStaleEntries()
    {
        var staleKeys = _cache
            .Where(kvp => DateTime.UtcNow - kvp.Value.FetchedAt > _cacheValidity)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleKeys)
        {
            _cache.TryRemove(key, out _);
        }

        if (staleKeys.Count > 0)
        {
            _logger.LogInformation("[MarketCache] Cleaned up {Count} stale entries", staleKeys.Count);
        }
    }

    /// <summary>
    /// Get cache statistics.
    /// </summary>
    public CacheStats GetStats()
    {
        var now = DateTime.UtcNow;
        var entries = _cache.Values.ToList();
        
        return new CacheStats
        {
            TotalEntries = entries.Count,
            ValidEntries = entries.Count(e => now - e.FetchedAt < _cacheValidity),
            StaleEntries = entries.Count(e => now - e.FetchedAt >= _cacheValidity),
            OldestEntry = entries.Count > 0 ? entries.Min(e => e.FetchedAt) : null,
            NewestEntry = entries.Count > 0 ? entries.Max(e => e.FetchedAt) : null
        };
    }

    /// <summary>
    /// Clear all cached data.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        _logger.LogInformation("[MarketCache] Cache cleared");
    }

    private static string GetCacheKey(int itemId, string dataCenter)
    {
        return $"{itemId}_{dataCenter}";
    }
}

/// <summary>
/// Raw market data cached from Universalis.
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
/// File format for persisting the cache.
/// </summary>
public class MarketCacheFile
{
    public DateTime SavedAt { get; set; }
    public Dictionary<string, CachedMarketData> Entries { get; set; } = new();
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
    
    public override string ToString()
    {
        return $"{ValidEntries} valid, {StaleEntries} stale (total: {TotalEntries})";
    }
}
