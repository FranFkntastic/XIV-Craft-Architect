using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Coordinates world data loading and mapping initialization.
/// Shared across app surfaces to ensure consistent world data handling.
/// </summary>
public class WorldDataCoordinator
{
    private readonly UniversalisService _universalisService;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly PackagedWorldDirectoryService _packagedWorldDirectory;

    public WorldDataCoordinator(
        UniversalisService universalisService,
        MarketShoppingService marketShoppingService,
        PackagedWorldDirectoryService packagedWorldDirectory)
    {
        _universalisService = universalisService;
        _marketShoppingService = marketShoppingService;
        _packagedWorldDirectory = packagedWorldDirectory;
    }

    /// <summary>
    /// Loads world data and initializes the world name to ID mapping for market operations.
    /// This is the shared initialization logic used at app startup.
    /// </summary>
    /// <returns>World data containing DCs, worlds, and mappings.</returns>
    /// <exception cref="InvalidOperationException">Thrown when world data cannot be loaded.</exception>
    public Task<WorldData> InitializeWorldDataAsync()
    {
        var worldData = _packagedWorldDirectory.LoadWorldData();
        _universalisService.SeedWorldData(worldData);

        var worldNameToId = worldData.WorldIdToName.ToDictionary(
            kvp => kvp.Value,
            kvp => kvp.Key,
            StringComparer.OrdinalIgnoreCase);

        _marketShoppingService.SetWorldNameToIdMapping(worldNameToId);

        return Task.FromResult(worldData);
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
