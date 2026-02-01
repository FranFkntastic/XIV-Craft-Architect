using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using FFXIVCraftArchitect.Models;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for interacting with the Universalis API.
/// Ported from Python: UNIVERSALIS_API constant
/// </summary>
public class UniversalisService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UniversalisService> _logger;

    private const string UniversalisApiUrl = "https://universalis.app/api/v2/{0}/{1}";
    private const string UniversalisMarketUrl = "https://universalis.app/market/{0}";
    private const string WorldsUrl = "https://universalis.app/api/v2/worlds";
    private const string DataCentersUrl = "https://universalis.app/api/v2/data-centers";

    // Cache for world data
    private WorldData? _worldDataCache;

    public UniversalisService(HttpClient httpClient, ILogger<UniversalisService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Get market board listings for an item.
    /// </summary>
    public async Task<UniversalisResponse> GetMarketDataAsync(string worldOrDc, int itemId, CancellationToken ct = default)
    {
        var url = string.Format(UniversalisApiUrl, Uri.EscapeDataString(worldOrDc), itemId);
        _logger.LogDebug("Fetching market data for {ItemId} on {WorldOrDc}", itemId, worldOrDc);

        var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadFromJsonAsync<UniversalisResponse>(ct);
        return data ?? new UniversalisResponse { ItemId = itemId };
    }

    /// <summary>
    /// Get market data for multiple items at once.
    /// </summary>
    public async Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkAsync(
        string worldOrDc, 
        IEnumerable<int> itemIds, 
        CancellationToken ct = default)
    {
        var ids = string.Join(",", itemIds);
        var url = string.Format(UniversalisApiUrl, Uri.EscapeDataString(worldOrDc), ids);
        
        _logger.LogDebug("Fetching bulk market data for {Count} items", itemIds.Count());

        var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        // Bulk responses are keyed by item ID
        var result = await response.Content.ReadFromJsonAsync<Dictionary<string, UniversalisResponse>>(ct);
        
        if (result == null)
            return new Dictionary<int, UniversalisResponse>();

        // Convert string keys to int keys
        return result.ToDictionary(
            kvp => int.Parse(kvp.Key),
            kvp => kvp.Value
        );
    }

    /// <summary>
    /// Get world and data center information.
    /// </summary>
    public async Task<WorldData> GetWorldDataAsync(CancellationToken ct = default)
    {
        if (_worldDataCache != null)
            return _worldDataCache;

        _logger.LogDebug("Fetching world data from Universalis");

        // Fetch worlds and data centers in parallel
        var worldsTask = _httpClient.GetFromJsonAsync<List<WorldInfo>>(WorldsUrl, ct);
        var dataCentersTask = _httpClient.GetFromJsonAsync<List<DataCenterInfo>>(DataCentersUrl, ct);

        await Task.WhenAll(worldsTask, dataCentersTask);

        var worlds = await worldsTask ?? new List<WorldInfo>();
        var dataCenters = await dataCentersTask ?? new List<DataCenterInfo>();

        // Build lookup dictionaries
        var worldIdToName = worlds.ToDictionary(w => w.Id, w => w.Name);
        var worldNameToId = worlds.ToDictionary(w => w.Name, w => w.Id);

        var dcToWorlds = dataCenters.ToDictionary(
            dc => dc.Name,
            dc => dc.WorldIds
                .Where(id => worldIdToName.ContainsKey(id))
                .Select(id => worldIdToName[id])
                .OrderBy(name => name)
                .ToList()
        );

        _worldDataCache = new WorldData
        {
            WorldIdToName = worldIdToName,
            DataCenterToWorlds = dcToWorlds
        };

        return _worldDataCache;
    }

    /// <summary>
    /// Get the URL to view an item on Universalis website.
    /// </summary>
    public static string GetMarketUrl(int itemId)
    {
        return string.Format(UniversalisMarketUrl, itemId);
    }

    /// <summary>
    /// Calculate optimal shopping plan for a set of items.
    /// </summary>
    public ShoppingPlan CalculateShoppingPlan(
        string itemName,
        int itemId,
        int quantityNeeded,
        List<MarketListing> listings)
    {
        var plan = new ShoppingPlan
        {
            ItemId = itemId,
            Name = itemName,
            QuantityNeeded = quantityNeeded
        };

        var remaining = quantityNeeded;
        long totalCost = 0;

        foreach (var listing in listings.OrderBy(l => l.PricePerUnit))
        {
            if (remaining <= 0)
                break;

            var toBuy = Math.Min(listing.Quantity, remaining);
            totalCost += toBuy * listing.PricePerUnit;
            remaining -= toBuy;

            plan.Entries.Add(new ShoppingPlanEntry
            {
                Quantity = toBuy,
                PricePerUnit = listing.PricePerUnit,
                WorldName = listing.WorldName,
                RetainerName = listing.RetainerName
            });
        }

        plan.TotalCost = totalCost;
        return plan;
    }
}
