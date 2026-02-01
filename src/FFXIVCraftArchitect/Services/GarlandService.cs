using System.Net.Http.Json;
using FFXIVCraftArchitect.Models;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for interacting with the Garland Tools API.
/// Ported from Python: GARLAND_SEARCH and GARLAND_ITEM constants
/// </summary>
public class GarlandService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GarlandService> _logger;

    private const string GarlandSearchUrl = "https://www.garlandtools.org/api/search.php?text={0}&lang=en";
    private const string GarlandItemUrl = "https://www.garlandtools.org/db/doc/item/en/3/{0}.json";

    public GarlandService(HttpClient httpClient, ILogger<GarlandService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Search for items by name.
    /// </summary>
    public async Task<List<GarlandSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = string.Format(GarlandSearchUrl, Uri.EscapeDataString(query));
        _logger.LogDebug("Searching Garland Tools: {Query}", query);

        var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<List<GarlandSearchResult>>(ct);
        
        // Filter to only items (not recipes, quests, etc.)
        return results?.Where(r => r.Type == "item").ToList() ?? new List<GarlandSearchResult>();
    }

    /// <summary>
    /// Get full item data including recipe information.
    /// </summary>
    public async Task<GarlandItem?> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        var url = string.Format(GarlandItemUrl, itemId);
        _logger.LogDebug("Fetching item data: {ItemId}", itemId);

        var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadFromJsonAsync<GarlandItemResponse>(ct);
        return data?.Item;
    }

    /// <summary>
    /// Get the crafting recipe for an item, if one exists.
    /// </summary>
    public async Task<Recipe?> GetRecipeAsync(int itemId, CancellationToken ct = default)
    {
        var item = await GetItemAsync(itemId, ct);
        if (item?.Crafts == null || item.Crafts.Count == 0)
            return null;

        var craft = item.Crafts[0];
        return new Recipe
        {
            ItemId = item.Id,
            Name = item.Name,
            IconId = item.IconId,
            Job = GetJobName(craft.JobId),
            Level = craft.RecipeLevel,
            Yield = craft.Yield,
            Ingredients = craft.Ingredients.Select(i => new Ingredient
            {
                ItemId = i.Id,
                Name = i.Name ?? string.Empty,
                Amount = i.Amount
            }).ToList()
        };
    }

    private static string GetJobName(int jobId)
    {
        // Job IDs from FFXIV
        return jobId switch
        {
            1 => "Carpenter",
            2 => "Blacksmith",
            3 => "Armorer",
            4 => "Goldsmith",
            5 => "Leatherworker",
            6 => "Weaver",
            7 => "Alchemist",
            8 => "Culinarian",
            _ => "Unknown"
        };
    }
}
