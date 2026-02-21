using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

/// <summary>
/// Service for interacting with the Universalis API.
/// Provides market board data retrieval.
/// </summary>
public interface IUniversalisService
{
    /// <summary>
    /// Get market board listings for an item.
    /// </summary>
    /// <remarks>
    /// DEPRECATED: This method is to be subsumed by GetMarketDataBulkAsync.
    /// Use GetMarketDataBulkAsync with a single-item list for new code.
    /// </remarks>
    /// <param name="worldOrDc">World or data center name</param>
    /// <param name="itemId">Item ID</param>
    /// <param name="hqOnly">If true, only return HQ listings</param>
    /// <param name="entries">Number of listings to return (default 10, 0 = all)</param>
    /// <param name="ct">Cancellation token</param>
    [Obsolete("Use GetMarketDataBulkAsync instead. This method will be removed in a future version.")]
    Task<UniversalisResponse> GetMarketDataAsync(
        string worldOrDc, 
        int itemId, 
        bool hqOnly = false,
        int entries = 10,
        CancellationToken ct = default);
    
    /// <summary>
    /// Get market data for multiple items at once.
    /// Universalis API has a limit of 100 items per request, so we chunk large requests.
    /// Uses parallel fetching with adaptive backoff for optimal performance.
    /// </summary>
    /// <param name="worldOrDc">World or data center name</param>
    /// <param name="itemIds">Item IDs to fetch</param>
    /// <param name="useParallel">Whether to use parallel fetching (default: true)</param>
    /// <param name="ct">Cancellation token</param>
    Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkAsync(
        string worldOrDc, 
        IEnumerable<int> itemIds, 
        bool useParallel = true,
        CancellationToken ct = default);
    
    /// <summary>
    /// Get world and data center information.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    Task<WorldData> GetWorldDataAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Get cached world data (returns null if not loaded yet).
    /// </summary>
    WorldData? GetCachedWorldData();
    
    /// <summary>
    /// Get the URL to view an item on Universalis website.
    /// </summary>
    /// <param name="itemId">Item ID</param>
    static string GetMarketUrl(int itemId) => $"https://universalis.app/market/{itemId}";
    
    /// <summary>
    /// Calculate optimal shopping plan for a set of items.
    /// </summary>
    ShoppingPlan CalculateShoppingPlan(
        string itemName,
        int itemId,
        int quantityNeeded,
        List<MarketListing> listings);
}
