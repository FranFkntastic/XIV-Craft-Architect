using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
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
        _logger.LogInformation("[GarlandService] ===== Search Started =====");
        _logger.LogInformation("[GarlandService] Query: '{Query}'", query);
        _logger.LogDebug("[GarlandService] URL: {Url}", url);

        try
        {
            _logger.LogDebug("[GarlandService] Sending HTTP GET...");
            var response = await _httpClient.GetAsync(url, ct);
            _logger.LogInformation("[GarlandService] HTTP Response: {Status}", response.StatusCode);
            
            response.EnsureSuccessStatusCode();

            var rawJson = await response.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("[GarlandService] Response size: {Length} chars", rawJson.Length);
            
            // Log first 1000 chars of JSON for debugging
            var preview = rawJson.Length > 1000 ? rawJson[..1000] + "..." : rawJson;
            _logger.LogDebug("[GarlandService] JSON Preview:\n{Preview}", preview);

            // Try to parse with detailed error logging
            List<GarlandSearchResult>? results;
            try
            {
                _logger.LogDebug("[GarlandService] Deserializing JSON...");
                results = JsonSerializer.Deserialize<List<GarlandSearchResult>>(rawJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                _logger.LogInformation("[GarlandService] Deserialization successful, {Count} raw results", results?.Count ?? 0);
            }
            catch (JsonException ex)
            {
                _logger.LogError("[GarlandService] ===== JSON PARSE ERROR =====");
                _logger.LogError("[GarlandService] Path: {Path}", ex.Path);
                _logger.LogError("[GarlandService] Line: {Line}, Position: {Pos}", ex.LineNumber, ex.BytePositionInLine);
                _logger.LogError("[GarlandService] Message: {Message}", ex.Message);
                
                // Try to extract problematic section
                if (ex.Path?.StartsWith("$[") == true)
                {
                    var match = Regex.Match(ex.Path, @"\$(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var index))
                    {
                        _logger.LogError("[GarlandService] Problematic array index: {Index}", index);
                        
                        // Extract surrounding context from JSON
                        var lines = rawJson.Split('\n');
                        var startLine = Math.Max(0, (ex.LineNumber ?? 1) - 5);
                        var endLine = Math.Min(lines.Length, (ex.LineNumber ?? 1) + 5);
                        _logger.LogError("[GarlandService] JSON context (lines {Start}-{End}):", startLine, endLine);
                        for (var i = startLine; i < endLine; i++)
                        {
                            _logger.LogError("  {LineNum}: {Content}", i + 1, lines[i]);
                        }
                    }
                }
                
                throw; // Re-throw to let caller handle
            }
            
            // Filter to only items (not recipes, quests, etc.)
            var filteredResults = results?.Where(r => r.Type == "item").ToList() ?? new List<GarlandSearchResult>();
            _logger.LogInformation("[GarlandService] Filtered to {Count} items (type='item')", filteredResults.Count);
            
            // Log first few results
            for (var i = 0; i < Math.Min(3, filteredResults.Count); i++)
            {
                var r = filteredResults[i];
                _logger.LogDebug("[GarlandService] Result[{Index}]: ID={Id}, Name='{Name}', Icon={Icon}", 
                    i, r.Id, r.Object.Name, r.Object.IconId);
            }
            
            _logger.LogInformation("[GarlandService] ===== Search Complete =====");
            return filteredResults;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("[GarlandService] HTTP Request failed: {Message}", ex.Message);
            _logger.LogError("[GarlandService] Stack trace: {Stack}", ex.StackTrace);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("[GarlandService] Unexpected error: {Message}", ex.Message);
            _logger.LogError("[GarlandService] Stack trace: {Stack}", ex.StackTrace);
            throw;
        }
    }

    /// <summary>
    /// Get full item data including recipe information.
    /// </summary>
    public async Task<GarlandItem?> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        var url = string.Format(GarlandItemUrl, itemId);
        _logger.LogInformation("[GarlandService] Fetching item data: {ItemId}", itemId);
        _logger.LogDebug("[GarlandService] URL: {Url}", url);

        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            _logger.LogDebug("[GarlandService] HTTP Status: {Status}", response.StatusCode);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<GarlandItemResponse>(ct);
            _logger.LogInformation("[GarlandService] Item fetched: {Name} (Icon: {Icon})", 
                data?.Item?.Name ?? "null", data?.Item?.IconId ?? 0);
            return data?.Item;
        }
        catch (Exception ex)
        {
            _logger.LogError("[GarlandService] Failed to fetch item {ItemId}: {Message}", itemId, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Get the crafting recipe for an item, if one exists.
    /// </summary>
    public async Task<Recipe?> GetRecipeAsync(int itemId, CancellationToken ct = default)
    {
        _logger.LogInformation("[GarlandService] Fetching recipe for item {ItemId}", itemId);
        
        var item = await GetItemAsync(itemId, ct);
        if (item?.Crafts == null || item.Crafts.Count == 0)
        {
            _logger.LogDebug("[GarlandService] No recipe found for item {ItemId}", itemId);
            return null;
        }

        var craft = item.Crafts[0];
        _logger.LogInformation("[GarlandService] Recipe found: Job={Job}, Level={Level}, Yield={Yield}, Ingredients={Ingredients}",
            GetJobName(craft.JobId), craft.RecipeLevel, craft.Yield, craft.Ingredients.Count);

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
