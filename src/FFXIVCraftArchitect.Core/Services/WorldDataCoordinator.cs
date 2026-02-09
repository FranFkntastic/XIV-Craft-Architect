using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.Core.Services;

/// <summary>
/// Coordinates world data loading and mapping initialization.
/// Shared between WPF and Web applications to ensure consistent world data handling.
/// </summary>
public class WorldDataCoordinator
{
    private readonly UniversalisService _universalisService;
    private readonly MarketShoppingService _marketShoppingService;

    public WorldDataCoordinator(
        UniversalisService universalisService,
        MarketShoppingService marketShoppingService)
    {
        _universalisService = universalisService;
        _marketShoppingService = marketShoppingService;
    }

    /// <summary>
    /// Loads world data and initializes the world name to ID mapping for market operations.
    /// This is the shared initialization logic used by both WPF and Web applications.
    /// </summary>
    /// <returns>World data containing DCs, worlds, and mappings.</returns>
    /// <exception cref="InvalidOperationException">Thrown when world data cannot be loaded.</exception>
    public async Task<WorldData> InitializeWorldDataAsync()
    {
        var worldData = await _universalisService.GetWorldDataAsync();
        
        var worldNameToId = worldData.WorldIdToName.ToDictionary(
            kvp => kvp.Value,
            kvp => kvp.Key,
            StringComparer.OrdinalIgnoreCase);
        
        _marketShoppingService.SetWorldNameToIdMapping(worldNameToId);
        
        return worldData;
    }

    /// <summary>
    /// Gets the list of worlds for a specific data center.
    /// </summary>
    /// <param name="dataCenter">The data center name.</param>
    /// <returns>List of world names, or empty list if DC not found.</returns>
    public List<string> GetWorldsForDataCenter(string dataCenter)
    {
        var worldData = _universalisService.GetCachedWorldData();
        if (worldData?.DataCenterToWorlds.TryGetValue(dataCenter, out var worlds) == true)
        {
            return worlds.ToList();
        }
        return new List<string>();
    }
}
