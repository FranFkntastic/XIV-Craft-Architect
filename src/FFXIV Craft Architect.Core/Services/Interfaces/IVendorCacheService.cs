using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

/// <summary>
/// Caches vendor data for items to avoid repeated Garland API calls.
/// Vendor data rarely changes (only when game patches add/modify vendors),
/// so entries have no TTL and persist across sessions.
/// </summary>
public interface IVendorCacheService
{
    /// <summary>
    /// Gets cached vendor data for an item. Returns null if not cached.
    /// </summary>
    /// <param name="itemId">The Garland item ID.</param>
    /// <returns>Cached vendor entry or null if not found.</returns>
    VendorCacheEntry? Get(int itemId);
    
    /// <summary>
    /// Gets or fetches vendor data for an item.
    /// Returns cached data if available; otherwise fetches from Garland and caches.
    /// </summary>
    /// <param name="itemId">The Garland item ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Vendor cache entry, or null if the item has no vendors.</returns>
    Task<VendorCacheEntry?> GetOrFetchAsync(int itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Batch gets or fetches vendor data for multiple items.
    /// Uses parallel fetching for cache misses.
    /// </summary>
    /// <param name="itemIds">Collection of Garland item IDs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary mapping item IDs to their vendor cache entries.</returns>
    Task<Dictionary<int, VendorCacheEntry>> GetOrFetchBatchAsync(
        IEnumerable<int> itemIds, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Manually caches vendor data for an item.
    /// Used when vendor data is fetched as part of other operations.
    /// </summary>
    /// <param name="itemId">The Garland item ID.</param>
    /// <param name="entry">The vendor cache entry to store.</param>
    void Set(int itemId, VendorCacheEntry entry);
    
    /// <summary>
    /// Clears all cached vendor data.
    /// Useful for testing or forcing a full refresh.
    /// </summary>
    void Clear();
    
    /// <summary>
    /// Gets the number of items in the cache.
    /// </summary>
    int Count { get; }
    
    /// <summary>
    /// Persists the cache to disk.
    /// Called automatically on app shutdown, but can be called manually.
    /// </summary>
    Task SaveAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Loads the cache from disk.
    /// Called automatically on startup, but can be called manually.
    /// </summary>
    Task LoadAsync(CancellationToken ct = default);
}

/// <summary>
/// Represents cached vendor data for a single item.
/// </summary>
/// <param name="ItemId">The Garland item ID.</param>
/// <param name="Vendors">List of vendors selling this item (gil vendors only for procurement).</param>
/// <param name="CheapestGilPrice">The cheapest gil vendor price (0 if no gil vendors).</param>
/// <param name="CachedAt">When this entry was cached.</param>
public record VendorCacheEntry(
    int ItemId,
    List<VendorInfo> Vendors,
    decimal CheapestGilPrice,
    DateTime CachedAt)
{
    /// <summary>
    /// Whether this item has any gil vendors.
    /// </summary>
    public bool HasGilVendors => Vendors.Any(v => v.IsGilVendor);
    
    /// <summary>
    /// Gets only the gil vendors for this item.
    /// </summary>
    public List<VendorInfo> GilVendors => Vendors.Where(v => v.IsGilVendor).ToList();
}
