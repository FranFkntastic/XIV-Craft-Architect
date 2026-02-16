using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FFXIVCraftArchitect.Core.Helpers;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Core.Services;

/// <summary>
/// Service for interacting with the Garland Tools API.
/// Ported from Python: GARLAND_SEARCH and GARLAND_ITEM constants
/// </summary>
public class GarlandService : IGarlandService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GarlandService>? _logger;

    private const string GarlandSearchUrl = "https://www.garlandtools.org/api/search.php?text={0}&lang=en";
    private const string GarlandItemUrl = "https://www.garlandtools.org/db/doc/item/en/3/{0}.json";
    private const string GarlandZoneUrl = "https://www.garlandtools.org/db/doc/zone/en/3/{0}.json";

    // Cache for zone names to avoid repeated API calls
    private static readonly ConcurrentDictionary<int, string> _zoneNameCache = new();

    public GarlandService(HttpClient httpClient, ILogger<GarlandService>? logger = null)
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
        _logger?.LogInformation("[GarlandService] ===== Search Started =====");
        _logger?.LogInformation("[GarlandService] Query: '{Query}'", query);
        _logger?.LogDebug("[GarlandService] URL: {Url}", url);

        try
        {
            _logger?.LogDebug("[GarlandService] Sending HTTP GET...");
            var response = await _httpClient.GetAsync(url, ct);
            _logger?.LogInformation("[GarlandService] HTTP Response: {Status}", response.StatusCode);
            
            response.EnsureSuccessStatusCode();

            var rawJson = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogDebug("[GarlandService] Response size: {Length} chars", rawJson.Length);
            
            // Log first 1000 chars of JSON for debugging
            var preview = rawJson.Length > 1000 ? rawJson[..1000] + "..." : rawJson;
            _logger?.LogDebug("[GarlandService] JSON Preview:\n{Preview}", preview);

            // Try to parse with detailed error logging
            List<GarlandSearchResult>? results;
            try
            {
                _logger?.LogDebug("[GarlandService] Deserializing JSON...");
                results = JsonSerializer.Deserialize<List<GarlandSearchResult>>(rawJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                _logger?.LogInformation("[GarlandService] Deserialization successful, {Count} raw results", results?.Count ?? 0);
            }
            catch (JsonException ex)
            {
                _logger?.LogError("[GarlandService] ===== JSON PARSE ERROR =====");
                _logger?.LogError("[GarlandService] Path: {Path}", ex.Path);
                _logger?.LogError("[GarlandService] Line: {Line}, Position: {Pos}", ex.LineNumber, ex.BytePositionInLine);
                _logger?.LogError("[GarlandService] Message: {Message}", ex.Message);
                
                // Try to extract problematic section
                if (ex.Path?.StartsWith("$") == true)
                {
                    var match = Regex.Match(ex.Path, @"\[(\d+)\]");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var index))
                    {
                        _logger?.LogError("[GarlandService] Problematic array index: {Index}", index);
                        
                        // Extract surrounding context from JSON
                        var lines = rawJson.Split('\n');
                        var startLine = Math.Max(0, (ex.LineNumber ?? 1) - 5);
                        var endLine = Math.Min(lines.Length, (ex.LineNumber ?? 1) + 5);
                        _logger?.LogError("[GarlandService] JSON context (lines {Start}-{End}):", startLine, endLine);
                        for (var i = startLine; i < endLine; i++)
                        {
                            _logger?.LogError("  {LineNum}: {Content}", i + 1, lines[i]);
                        }
                    }
                }
                
                throw; // Re-throw to let caller handle
            }
            
            // Filter to only items (not recipes, quests, etc.)
            // Defensive: filter out null results and results with null Type or Object
            var filteredResults = results
                ?.Where(r => r != null && r.Type == "item" && r.Object != null)
                .ToList() ?? new List<GarlandSearchResult>();
            _logger?.LogInformation("[GarlandService] Filtered to {Count} items (type='item')", filteredResults.Count);
            
            // Log first few results
            for (var i = 0; i < Math.Min(3, filteredResults.Count); i++)
            {
                var r = filteredResults[i];
                _logger?.LogDebug("[GarlandService] Result[{Index}]: ID={Id}, Name='{Name}', Icon={Icon}", 
                    i, r.Id, r.Object?.Name ?? "null", r.Object?.IconId ?? 0);
            }
            
            _logger?.LogInformation("[GarlandService] ===== Search Complete =====");
            return filteredResults;
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError("[GarlandService] HTTP Request failed: {Message}", ex.Message);
            _logger?.LogError("[GarlandService] Stack trace: {Stack}", ex.StackTrace);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("[GarlandService] Unexpected error: {Message}", ex.Message);
            _logger?.LogError("[GarlandService] Stack trace: {Stack}", ex.StackTrace);
            throw;
        }
    }

    /// <summary>
    /// Get full item data including recipe information.
    /// </summary>
    public async Task<GarlandItem?> GetItemAsync(int itemId, CancellationToken ct = default)
    {
        var url = string.Format(GarlandItemUrl, itemId);
        _logger?.LogInformation("[GarlandService] Fetching item data: {ItemId}", itemId);
        _logger?.LogDebug("[GarlandService] URL: {Url}", url);

        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            _logger?.LogDebug("[GarlandService] HTTP Status: {Status}", response.StatusCode);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<GarlandItemResponse>(ct);
            _logger?.LogInformation("[GarlandService] Item fetched: {Name} (Icon: {Icon})", 
                data?.Item?.Name ?? "null", data?.Item?.IconId ?? 0);
            
            // Transfer partials from response wrapper to item for vendor location resolution
            if (data?.Item != null && data.Partials != null)
            {
                data.Item.Partials = data.Partials;
                _logger?.LogDebug("[GarlandService] Transferred {PartialCount} partials to item", data.Partials.Count);
            }
            
            return data?.Item;
        }
        catch (Exception ex)
        {
            _logger?.LogError("[GarlandService] Failed to fetch item {ItemId}: {Message}", itemId, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Get the crafting recipe for an item, if one exists.
    /// </summary>
    public async Task<Recipe?> GetRecipeAsync(int itemId, CancellationToken ct = default)
    {
        _logger?.LogInformation("[GarlandService] Fetching recipe for item {ItemId}", itemId);
        
        var item = await GetItemAsync(itemId, ct);
        if (item?.Crafts == null || item.Crafts.Count == 0)
        {
            _logger?.LogDebug("[GarlandService] No recipe found for item {ItemId}", itemId);
            return null;
        }

        var craft = item.Crafts[0];
        _logger?.LogInformation("[GarlandService] Recipe found: Job={Job}, Level={Level}, Yield={Yield}, Ingredients={Ingredients}",
            JobHelper.GetJobName(craft.JobId), craft.RecipeLevel, craft.Yield, craft.Ingredients.Count);

        return new Recipe
        {
            ItemId = item.Id,
            Name = item.Name,
            IconId = item.IconId,
            Job = JobHelper.GetJobName(craft.JobId),
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

    /// <summary>
    /// Gets the zone name from Garland Tools API.
    /// CURRENTLY DORMANT: Hard-coded mappings in GarlandNpcPartial are used instead for performance.
    /// Reserved for future use if dynamic zone lookup becomes necessary.
    /// Uses a static cache to avoid repeated API calls for the same zone.
    /// </summary>
    public async Task<string?> GetZoneNameAsync(int zoneId, CancellationToken ct = default)
    {
        // Check cache first
        if (_zoneNameCache.TryGetValue(zoneId, out var cachedName))
        {
            _logger?.LogDebug("[GarlandService] Zone {ZoneId} found in cache: {ZoneName}", zoneId, cachedName);
            return cachedName;
        }

        try
        {
            var url = string.Format(GarlandZoneUrl, zoneId);
            _logger?.LogDebug("[GarlandService] Fetching zone data for ID {ZoneId}: {Url}", zoneId, url);

            var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("[GarlandService] Failed to fetch zone {ZoneId}: HTTP {Status}", zoneId, response.StatusCode);
                return null;
            }

            var zoneData = await response.Content.ReadFromJsonAsync<GarlandZoneResponse>(ct);
            if (zoneData?.Zone?.Name != null)
            {
                var zoneName = zoneData.Zone.Name;
                // Use TryAdd to prevent race condition where another thread already added this zone
                if (_zoneNameCache.TryAdd(zoneId, zoneName))
                {
                    _logger?.LogInformation("[GarlandService] Cached zone name: {ZoneId} = {ZoneName}", zoneId, zoneName);
                }
                else
                {
                    // Another thread already cached it - use their value
                    zoneName = _zoneNameCache[zoneId];
                    _logger?.LogDebug("[GarlandService] Using existing cached zone name for {ZoneId}: {ZoneName}", zoneId, zoneName);
                }
                return zoneName;
            }

            _logger?.LogWarning("[GarlandService] Zone {ZoneId} has no name in response", zoneId);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[GarlandService] Error fetching zone {ZoneId}", zoneId);
            return null;
        }
    }

    /// <summary>
    /// Fetch multiple items in parallel with rate limiting.
    /// Uses conservative settings (max 2 concurrent, 200ms initial delay) to be respectful to the free API.
    /// </summary>
    /// <param name="itemIds">Item IDs to fetch</param>
    /// <param name="useParallel">Whether to use parallel fetching (default: true)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Dictionary of item ID to item data</returns>
    public async Task<Dictionary<int, GarlandItem>> GetItemsAsync(
        IEnumerable<int> itemIds, 
        bool useParallel = true,
        CancellationToken ct = default)
    {
        const int maxConcurrency = 2; // Conservative: only 2 concurrent requests
        
        var ids = itemIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<int, GarlandItem>();
        
        var results = new ConcurrentDictionary<int, GarlandItem>();
        var delayStrategy = new AdaptiveDelayStrategy(
            initialDelayMs: 200,  // Start conservative (200ms)
            minDelayMs: 100,
            maxDelayMs: 10000,    // Max 10s backoff
            backoffMultiplier: 2.0,
            rateLimitMultiplier: 4.0);
        
        _logger?.LogInformation("[GarlandService] Fetching {Count} items (parallel={UseParallel})", 
            ids.Count, useParallel);

        if (useParallel && ids.Count > 1)
        {
            var semaphore = new SemaphoreSlim(maxConcurrency);
            var completedCount = 0;
            var failedCount = 0;

            var tasks = ids.Select(async id =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    // Apply adaptive delay before each request
                    var delay = delayStrategy.GetDelay();
                    if (delay > 0)
                    {
                        await Task.Delay(delay, ct);
                    }
                    
                    var item = await GetItemAsync(id, ct);
                    if (item != null)
                    {
                        results[id] = item;
                        delayStrategy.ReportSuccess();
                    }
                    
                    var completed = Interlocked.Increment(ref completedCount);
                    _logger?.LogDebug("[GarlandService] Item {ItemId} fetched ({Completed}/{Total})", 
                        id, completed, ids.Count);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedCount);
                    delayStrategy.ReportFailure(System.Net.HttpStatusCode.BadRequest);
                    _logger?.LogWarning(ex, "[GarlandService] Failed to fetch item {ItemId}", id);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
            
            _logger?.LogInformation(
                "[GarlandService] Parallel fetch complete: {Completed}/{Total} succeeded, {Failed} failed, final delay: {Delay}ms",
                completedCount, ids.Count, failedCount, delayStrategy.GetDelay());
        }
        else
        {
            // Sequential fetching
            foreach (var id in ids)
            {
                try
                {
                    var delay = delayStrategy.GetDelay();
                    if (delay > 0)
                    {
                        await Task.Delay(delay, ct);
                    }
                    
                    var item = await GetItemAsync(id, ct);
                    if (item != null)
                    {
                        results[id] = item;
                        delayStrategy.ReportSuccess();
                    }
                }
                catch (Exception ex)
                {
                    delayStrategy.ReportFailure(System.Net.HttpStatusCode.BadRequest);
                    _logger?.LogWarning(ex, "[GarlandService] Failed to fetch item {ItemId}", id);
                }
            }
            
            _logger?.LogInformation(
                "[GarlandService] Sequential fetch complete: {FetchedCount}/{TotalCount} items, final delay: {Delay}ms",
                results.Count, ids.Count, delayStrategy.GetDelay());
        }

        return new Dictionary<int, GarlandItem>(results);
    }
}
