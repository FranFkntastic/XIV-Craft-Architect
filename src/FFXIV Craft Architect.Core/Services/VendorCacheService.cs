using System.Collections.Concurrent;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Caches vendor data for items to avoid repeated Garland API calls.
/// Persists to disk in the application directory as vendor_cache.json.
/// </summary>
public class VendorCacheService : IVendorCacheService, IDisposable
{
    private readonly IGarlandService _garlandService;
    private readonly ILogger<VendorCacheService> _logger;
    private readonly ConcurrentDictionary<int, VendorCacheEntry> _cache = new();
    private readonly string _cacheFilePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private bool _disposed;
    private bool _isDirty;

    public int Count => _cache.Count;

    public VendorCacheService(IGarlandService garlandService, ILogger<VendorCacheService> logger)
    {
        _garlandService = garlandService;
        _logger = logger;
        
        // Store cache in application directory
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _cacheFilePath = Path.Combine(appDir, "vendor_cache.json");
        
        // Load cache on startup (fire and forget - don't block construction)
        _ = LoadAsync();
    }

    public VendorCacheEntry? Get(int itemId)
    {
        return _cache.TryGetValue(itemId, out var entry) ? entry : null;
    }

    public async Task<VendorCacheEntry?> GetOrFetchAsync(int itemId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(itemId, out var cached))
        {
            _logger.LogDebug("[VendorCache] Cache hit for item {ItemId}", itemId);
            return cached;
        }

        _logger.LogDebug("[VendorCache] Cache miss for item {ItemId}, fetching from Garland", itemId);
        
        var item = await _garlandService.GetItemAsync(itemId, ct);
        if (item == null)
        {
            _logger.LogWarning("[VendorCache] Failed to fetch item {ItemId} from Garland", itemId);
            return null;
        }

        var entry = CreateEntryFromGarlandItem(item);
        if (entry != null)
        {
            _cache[itemId] = entry;
            _isDirty = true;
            _logger.LogDebug("[VendorCache] Cached {VendorCount} vendors for item {ItemId}", 
                entry.Vendors.Count, itemId);
        }
        
        return entry;
    }

    public async Task<Dictionary<int, VendorCacheEntry>> GetOrFetchBatchAsync(
        IEnumerable<int> itemIds, 
        CancellationToken ct = default)
    {
        var results = new Dictionary<int, VendorCacheEntry>();
        var idsList = itemIds.ToList();
        var toFetch = new List<int>();

        // Check cache first
        foreach (var itemId in idsList)
        {
            if (_cache.TryGetValue(itemId, out var cached))
            {
                results[itemId] = cached;
            }
            else
            {
                toFetch.Add(itemId);
            }
        }

        if (toFetch.Count == 0)
        {
            _logger.LogDebug("[VendorCache] Batch request: all {Count} items in cache", idsList.Count);
            return results;
        }

        _logger.LogInformation("[VendorCache] Fetching {Count} items from Garland ({Cached} already cached)", 
            toFetch.Count, results.Count);

        // Fetch missing items in parallel
        var fetchedItems = await _garlandService.GetItemsAsync(toFetch, useParallel: true, ct);
        
        foreach (var kvp in fetchedItems)
        {
            var entry = CreateEntryFromGarlandItem(kvp.Value);
            if (entry != null)
            {
                _cache[kvp.Key] = entry;
                results[kvp.Key] = entry;
                _isDirty = true;
            }
        }

        // Create empty entries for items with no vendors (so we don't keep re-fetching)
        foreach (var itemId in toFetch)
        {
            if (!results.ContainsKey(itemId))
            {
                var emptyEntry = new VendorCacheEntry(itemId, new List<VendorInfo>(), 0, DateTime.UtcNow);
                _cache[itemId] = emptyEntry;
                results[itemId] = emptyEntry;
                _isDirty = true;
            }
        }

        return results;
    }

    public void Set(int itemId, VendorCacheEntry entry)
    {
        _cache[itemId] = entry;
        _isDirty = true;
    }

    public void Clear()
    {
        _cache.Clear();
        _isDirty = true;
        _logger.LogInformation("[VendorCache] Cache cleared");
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (!_isDirty)
        {
            _logger.LogDebug("[VendorCache] No changes to save");
            return;
        }

        await _fileLock.WaitAsync(ct);
        try
        {
            var data = new VendorCacheData
            {
                Version = 1,
                SavedAt = DateTime.UtcNow,
                Entries = _cache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            await File.WriteAllTextAsync(_cacheFilePath, json, ct);
            _isDirty = false;
            
            _logger.LogInformation("[VendorCache] Saved {Count} entries to {Path}", 
                data.Entries.Count, _cacheFilePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                _logger.LogInformation("[VendorCache] No cache file found at {Path}", _cacheFilePath);
                return;
            }

            var json = await File.ReadAllTextAsync(_cacheFilePath, ct);
            var data = JsonSerializer.Deserialize<VendorCacheData>(json);
            
            if (data == null || data.Entries == null)
            {
                _logger.LogWarning("[VendorCache] Failed to deserialize cache file");
                return;
            }

            _cache.Clear();
            foreach (var kvp in data.Entries)
            {
                _cache[kvp.Key] = kvp.Value;
            }
            
            _isDirty = false;
            
            _logger.LogInformation("[VendorCache] Loaded {Count} entries from {Path} (saved at {SavedAt})", 
                data.Entries.Count, _cacheFilePath, data.SavedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VendorCache] Error loading cache file");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static VendorCacheEntry? CreateEntryFromGarlandItem(GarlandItem item)
    {
        var vendors = ExtractVendors(item);
        if (vendors.Count == 0)
        {
            return null;
        }

        var gilVendors = vendors.Where(v => v.IsGilVendor).ToList();
        var cheapestPrice = gilVendors.Count > 0 ? gilVendors.Min(v => v.Price) : 0;

        return new VendorCacheEntry(
            item.Id,
            vendors,
            cheapestPrice,
            DateTime.UtcNow);
    }

    private static List<VendorInfo> ExtractVendors(GarlandItem item)
    {
        var vendors = new List<VendorInfo>();

        // Handle full vendor objects
        if (item.Vendors.Count > 0)
        {
            foreach (var garlandVendor in item.Vendors)
            {
                var vendorInfo = VendorInfo.FromGarlandVendor(garlandVendor);

                if (!string.IsNullOrEmpty(garlandVendor.Name) && item.Partials != null)
                {
                    var npcPartials = item.GetNpcPartialsByName(garlandVendor.Name);
                    var primaryNpc = npcPartials.FirstOrDefault(npc =>
                        string.Equals(npc.LocationName, vendorInfo.Location, StringComparison.OrdinalIgnoreCase))
                        ?? npcPartials.FirstOrDefault();

                    if (primaryNpc?.Coordinates?.Count >= 2)
                    {
                        vendorInfo.Coordinates = new List<double> { primaryNpc.Coordinates[0], primaryNpc.Coordinates[1] };
                    }

                    var allLocations = npcPartials
                        .Select(npc => npc.LocationName)
                        .Where(loc => !string.IsNullOrWhiteSpace(loc))
                        .Distinct()
                        .ToList();

                    vendorInfo.AlternateLocations = allLocations
                        .Where(loc => !string.Equals(loc, vendorInfo.Location, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                vendors.Add(vendorInfo);
            }
        }
        // Handle ID-only references with root-level price
        else if (item.HasVendorReferences && item.Price > 0)
        {
            // Try to resolve via partials
            if (item.Partials != null)
            {
                var vendorIds = item.VendorIds;
                var vendorNpcs = item.Partials
                    .Where(p => p.Type == "npc" && vendorIds.Contains(p.Id))
                    .Select(p => p.GetNpcObject())
                    .Where(npc => npc != null)
                    .Cast<GarlandNpcPartial>()
                    .ToList();

                if (vendorNpcs.Any())
                {
                    var vendorGroups = vendorNpcs.GroupBy(npc => npc.Name);
                    foreach (var group in vendorGroups)
                    {
                        var npcList = group.ToList();
                        var primaryNpc = npcList.First();

                        vendors.Add(new VendorInfo
                        {
                            Name = primaryNpc.Name,
                            Location = primaryNpc.LocationName,
                            Price = item.Price,
                            Currency = "gil",
                            Coordinates = primaryNpc.Coordinates?.Count >= 2
                                ? new List<double> { primaryNpc.Coordinates[0], primaryNpc.Coordinates[1] }
                                : null,
                            AlternateLocations = npcList
                                .Select(npc => npc.LocationName)
                                .Where(loc => !string.IsNullOrEmpty(loc))
                                .Where(loc => !string.Equals(loc, primaryNpc.LocationName, StringComparison.OrdinalIgnoreCase))
                                .Distinct()
                                .ToList()
                        });
                    }
                }
            }

            // Fallback if no partials resolved
            if (vendors.Count == 0)
            {
                vendors.Add(new VendorInfo
                {
                    Name = "Material Supplier",
                    Location = "Any",
                    Price = item.Price,
                    Currency = "gil"
                });
            }
        }

        return vendors;
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        // Save cache on shutdown if dirty
        if (_isDirty)
        {
            try
            {
                SaveAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[VendorCache] Error saving cache on dispose");
            }
        }
        
        _fileLock.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Data structure for JSON serialization of the vendor cache.
/// </summary>
internal class VendorCacheData
{
    public int Version { get; set; }
    public DateTime SavedAt { get; set; }
    public Dictionary<int, VendorCacheEntry> Entries { get; set; } = new();
}
