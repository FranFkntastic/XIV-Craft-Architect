using System.IO;
using Microsoft.Extensions.Logging;

// Required for async method signatures
using Task = System.Threading.Tasks.Task;
using CancellationToken = System.Threading.CancellationToken;

namespace FFXIV_Craft_Architect.Services;

/// <summary>
/// Local cache for item names and icons to avoid repeated API calls.
/// Persists to JSON file in app directory.
/// </summary>
public class ItemCacheService
{
    private readonly Dictionary<int, CachedItem> _cache = new();
    private readonly string _cacheFilePath;
    private readonly ILogger<ItemCacheService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isDirty = false;

    public ItemCacheService(ILogger<ItemCacheService> logger)
    {
        _logger = logger;
        _cacheFilePath = Path.Combine(
            AppContext.BaseDirectory,
            "item_cache.json"
        );
        
        LoadCache();
    }

    /// <summary>
    /// Get item name from cache. Returns null if not cached.
    /// </summary>
    public string? GetItemName(int itemId)
    {
        _lock.Wait();
        try
        {
            return _cache.TryGetValue(itemId, out var item) ? item.Name : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get item name from cache asynchronously. Returns null if not cached.
    /// </summary>
    public async Task<string?> GetItemNameAsync(int itemId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _cache.TryGetValue(itemId, out var item) ? item.Name : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get icon ID from cache. Returns null if not cached.
    /// </summary>
    public int? GetIconId(int itemId)
    {
        _lock.Wait();
        try
        {
            return _cache.TryGetValue(itemId, out var item) ? item.IconId : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get icon ID from cache asynchronously. Returns null if not cached.
    /// </summary>
    public async Task<int?> GetIconIdAsync(int itemId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _cache.TryGetValue(itemId, out var item) ? item.IconId : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get both name and icon ID from cache.
    /// </summary>
    public (string? Name, int? IconId) GetItem(int itemId)
    {
        _lock.Wait();
        try
        {
            if (_cache.TryGetValue(itemId, out var item))
            {
                item.LastAccessed = DateTime.UtcNow;
                return (item.Name, item.IconId);
            }
            return (null, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get both name and icon ID from cache asynchronously.
    /// </summary>
    public async Task<(string? Name, int? IconId)> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(itemId, out var item))
            {
                item.LastAccessed = DateTime.UtcNow;
                return (item.Name, item.IconId);
            }
            return (null, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Store item in cache.
    /// </summary>
    public void StoreItem(int itemId, string name, int iconId = 0)
    {
        _lock.Wait();
        try
        {
            _cache[itemId] = new CachedItem
            {
                Id = itemId,
                Name = name,
                IconId = iconId,
                LastAccessed = DateTime.UtcNow
            };
            _isDirty = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Store item in cache asynchronously.
    /// </summary>
    public async Task StoreItemAsync(int itemId, string name, int iconId = 0, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _cache[itemId] = new CachedItem
            {
                Id = itemId,
                Name = name,
                IconId = iconId,
                LastAccessed = DateTime.UtcNow
            };
            _isDirty = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Store multiple items in cache.
    /// </summary>
    public void StoreItems(IEnumerable<(int Id, string Name, int IconId)> items)
    {
        _lock.Wait();
        try
        {
            foreach (var (id, name, iconId) in items)
            {
                _cache[id] = new CachedItem
                {
                    Id = id,
                    Name = name,
                    IconId = iconId,
                    LastAccessed = DateTime.UtcNow
                };
            }
            _isDirty = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Store multiple items in cache asynchronously.
    /// </summary>
    public async Task StoreItemsAsync(IEnumerable<(int Id, string Name, int IconId)> items, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var (id, name, iconId) in items)
            {
                _cache[id] = new CachedItem
                {
                    Id = id,
                    Name = name,
                    IconId = iconId,
                    LastAccessed = DateTime.UtcNow
                };
            }
            _isDirty = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Check if item exists in cache.
    /// </summary>
    public bool Contains(int itemId)
    {
        _lock.Wait();
        try
        {
            return _cache.ContainsKey(itemId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Check if item exists in cache asynchronously.
    /// </summary>
    public async Task<bool> ContainsAsync(int itemId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _cache.ContainsKey(itemId);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get cache statistics.
    /// </summary>
    public (int Count, DateTime? Oldest) GetStats()
    {
        _lock.Wait();
        try
        {
            var count = _cache.Count;
            var oldest = _cache.Values.MinBy(i => i.LastAccessed)?.LastAccessed;
            return (count, oldest);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Get cache statistics asynchronously.
    /// </summary>
    public async Task<(int Count, DateTime? Oldest)> GetStatsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var count = _cache.Count;
            var oldest = _cache.Values.MinBy(i => i.LastAccessed)?.LastAccessed;
            return (count, oldest);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Save cache to disk if dirty.
    /// </summary>
    public void SaveCache()
    {
        if (!_isDirty) return;

        _lock.Wait();
        try
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            };
            
            var data = new CacheData
            {
                SavedAt = DateTime.UtcNow,
                Items = _cache.Values.ToList()
            };
            
            var json = System.Text.Json.JsonSerializer.Serialize(data, options);
            File.WriteAllText(_cacheFilePath, json);
            _isDirty = false;
            
            _logger.LogInformation("Item cache saved: {Count} items", _cache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save item cache");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                _logger.LogInformation("No item cache found");
                return;
            }

            var json = File.ReadAllText(_cacheFilePath);
            var data = System.Text.Json.JsonSerializer.Deserialize<CacheData>(json);
            
            if (data?.Items != null)
            {
                foreach (var item in data.Items)
                {
                    _cache[item.Id] = item;
                }
                _logger.LogInformation("Item cache loaded: {Count} items", _cache.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load item cache");
        }
    }

    private class CachedItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int IconId { get; set; }
        public DateTime LastAccessed { get; set; }
    }

    private class CacheData
    {
        public DateTime SavedAt { get; set; }
        public List<CachedItem> Items { get; set; } = new();
    }
}
