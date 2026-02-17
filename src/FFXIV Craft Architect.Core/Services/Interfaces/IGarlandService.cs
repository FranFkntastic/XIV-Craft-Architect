using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

/// <summary>
/// Service for interacting with the Garland Tools API.
/// Provides item search and recipe lookup functionality.
/// </summary>
public interface IGarlandService
{
    /// <summary>
    /// Search for items by name.
    /// </summary>
    /// <param name="query">Search query (item name)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of matching items</returns>
    Task<List<GarlandSearchResult>> SearchAsync(string query, CancellationToken ct = default);
    
    /// <summary>
    /// Get full item data including recipe information.
    /// </summary>
    /// <param name="itemId">Item ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Item data or null if not found</returns>
    Task<GarlandItem?> GetItemAsync(int itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Get the crafting recipe for an item, if one exists.
    /// </summary>
    /// <param name="itemId">Item ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Recipe or null if not craftable</returns>
    Task<Recipe?> GetRecipeAsync(int itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Fetch multiple items in parallel with rate limiting.
    /// Uses conservative settings (max 2 concurrent) to be respectful to the free API.
    /// </summary>
    /// <param name="itemIds">Item IDs to fetch</param>
    /// <param name="useParallel">Whether to use parallel fetching (default: true)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Dictionary of item ID to item data</returns>
    Task<Dictionary<int, GarlandItem>> GetItemsAsync(
        IEnumerable<int> itemIds, 
        bool useParallel = true,
        CancellationToken ct = default);
}
