using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.Core.Services;

/// <summary>
/// Service for interacting with the Universalis API.
/// Ported from Python: UNIVERSALIS_API constant
/// </summary>
public class UniversalisService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UniversalisService>? _logger;

    private const string UniversalisApiUrl = "https://universalis.app/api/v2/{0}/{1}";
    private const string UniversalisMarketUrl = "https://universalis.app/market/{0}";
    private const string WorldsUrl = "https://universalis.app/api/v2/worlds";
    private const string DataCentersUrl = "https://universalis.app/api/v2/data-centers";

    // Cache for world data
    private WorldData? _worldDataCache;
    
    /// <summary>
    /// Get cached world data (returns null if not loaded yet).
    /// </summary>
    public WorldData? GetCachedWorldData() => _worldDataCache;

    public UniversalisService(HttpClient httpClient, ILogger<UniversalisService>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Get market board listings for an item.
    /// </summary>
    /// <param name="worldOrDc">World or data center name</param>
    /// <param name="itemId">Item ID</param>
    /// <param name="hqOnly">If true, only return HQ listings</param>
    /// <param name="entries">Number of listings to return (default 10)</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<UniversalisResponse> GetMarketDataAsync(
        string worldOrDc, 
        int itemId, 
        bool hqOnly = false,
        int entries = 10,
        CancellationToken ct = default)
    {
        var baseUrl = string.Format(UniversalisApiUrl, Uri.EscapeDataString(worldOrDc), itemId);
        var queryParams = new List<string>();
        
        if (hqOnly)
            queryParams.Add("hq=true");
        if (entries != 10)
            queryParams.Add($"entries={entries}");
        
        var url = queryParams.Count > 0 
            ? $"{baseUrl}?{string.Join("&", queryParams)}"
            : baseUrl;
        
        _logger?.LogDebug("Fetching market data for {ItemId} on {WorldOrDc} (HQ={HqOnly})", itemId, worldOrDc, hqOnly);

        var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadFromJsonAsync<UniversalisResponse>(ct);
        return data ?? new UniversalisResponse { ItemId = itemId };
    }

    /// <summary>
    /// Get market data for multiple items at once.
    /// Universalis API has a limit of 100 items per request, so we chunk large requests.
    /// </summary>
    public async Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkAsync(
        string worldOrDc, 
        IEnumerable<int> itemIds, 
        CancellationToken ct = default)
    {
        const int chunkSize = 100; // Universalis API limit
        const int delayBetweenChunksMs = 100; // Rate limiting delay
        const int maxRetries = 3;
        
        var itemIdList = itemIds.ToList();
        var allResults = new Dictionary<int, UniversalisResponse>();
        
        _logger?.LogDebug("Fetching bulk market data for {Count} items (chunked by {ChunkSize})", 
            itemIdList.Count, chunkSize);

        // Process in chunks to respect API limits
        for (int i = 0; i < itemIdList.Count; i += chunkSize)
        {
            var chunk = itemIdList.Skip(i).Take(chunkSize).ToList();
            var ids = string.Join(",", chunk);
            var url = string.Format(UniversalisApiUrl, Uri.EscapeDataString(worldOrDc), ids);
            
            _logger?.LogDebug("Fetching chunk {ChunkIndex} ({Start}-{End} of {Total})", 
                (i / chunkSize) + 1, i + 1, Math.Min(i + chunkSize, itemIdList.Count), itemIdList.Count);

            // Retry logic with exponential backoff
            bool success = false;
            for (int retry = 0; retry < maxRetries && !success; retry++)
            {
                try
                {
                    if (retry > 0)
                    {
                        var delayMs = (int)Math.Pow(2, retry) * 500;
                        _logger?.LogWarning("Retry {Retry}/{MaxRetries} for chunk {ChunkIndex} after {DelayMs}ms delay", 
                            retry, maxRetries, (i / chunkSize) + 1, delayMs);
                        await Task.Delay(delayMs, ct);
                    }

                    var response = await _httpClient.GetAsync(url, ct);
                    
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        _logger?.LogWarning("Rate limited (429) on chunk {ChunkIndex}, will retry", (i / chunkSize) + 1);
                        if (retry < maxRetries - 1) continue;
                        throw new HttpRequestException("Rate limited by Universalis API");
                    }
                    
                    response.EnsureSuccessStatusCode();

                    if (chunk.Count == 1)
                    {
                        var singleResult = await response.Content.ReadFromJsonAsync<UniversalisResponse>(ct);
                        if (singleResult != null)
                        {
                            allResults[chunk[0]] = singleResult;
                        }
                    }
                    else
                    {
                        var bulkResult = await response.Content.ReadFromJsonAsync<UniversalisBulkResponse>(ct);
                        
                        if (bulkResult?.Items != null)
                        {
                            foreach (var kvp in bulkResult.Items)
                            {
                                allResults[kvp.Key] = kvp.Value;
                            }
                        }
                    }
                    
                    success = true;
                }
                catch (Exception ex) when (retry < maxRetries - 1)
                {
                    _logger?.LogWarning(ex, "Chunk {ChunkIndex} failed, will retry", (i / chunkSize) + 1);
                }
            }

            if (i + chunkSize < itemIdList.Count)
            {
                await Task.Delay(delayBetweenChunksMs, ct);
            }
        }
        
        _logger?.LogInformation("Fetched market data for {FetchedCount}/{RequestedCount} items",
            allResults.Count, itemIdList.Count);

        return allResults;
    }

    /// <summary>
    /// Get market data for multiple items at once using parallel requests.
    /// </summary>
    public async Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkParallelAsync(
        string worldOrDc, 
        IEnumerable<int> itemIds, 
        int maxConcurrency = 3,
        CancellationToken ct = default)
    {
        const int chunkSize = 100;
        const int maxRetries = 3;
        
        var itemIdList = itemIds.ToList();
        var chunks = new List<List<int>>();
        
        for (int i = 0; i < itemIdList.Count; i += chunkSize)
        {
            chunks.Add(itemIdList.Skip(i).Take(chunkSize).ToList());
        }
        
        _logger?.LogInformation("Fetching bulk market data for {Count} items in {ChunkCount} parallel chunks", 
            itemIdList.Count, chunks.Count);

        var allResults = new ConcurrentDictionary<int, UniversalisResponse>();
        var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = chunks.Select(async (chunk, index) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var chunkNum = index + 1;
                var ids = string.Join(",", chunk);
                var url = string.Format(UniversalisApiUrl, Uri.EscapeDataString(worldOrDc), ids);
                
                for (int retry = 0; retry < maxRetries; retry++)
                {
                    try
                    {
                        if (retry > 0)
                        {
                            var delayMs = (int)Math.Pow(2, retry) * 500;
                            await Task.Delay(delayMs, ct);
                        }

                        var response = await _httpClient.GetAsync(url, ct);
                        
                        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            if (retry < maxRetries - 1) continue;
                            throw new HttpRequestException("Rate limited by Universalis API");
                        }
                        
                        response.EnsureSuccessStatusCode();

                        if (chunk.Count == 1)
                        {
                            var singleResult = await response.Content.ReadFromJsonAsync<UniversalisResponse>(ct);
                            if (singleResult != null)
                            {
                                allResults[chunk[0]] = singleResult;
                            }
                        }
                        else
                        {
                            var bulkResult = await response.Content.ReadFromJsonAsync<UniversalisBulkResponse>(ct);
                            
                            if (bulkResult?.Items != null)
                            {
                                foreach (var kvp in bulkResult.Items)
                                {
                                    allResults[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                        
                        break;
                    }
                    catch when (retry < maxRetries - 1)
                    {
                        // Will retry
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        
        return new Dictionary<int, UniversalisResponse>(allResults);
    }

    /// <summary>
    /// Get world and data center information.
    /// </summary>
    public async Task<WorldData> GetWorldDataAsync(CancellationToken ct = default)
    {
        if (_worldDataCache != null)
            return _worldDataCache;

        _logger?.LogDebug("Fetching world data from Universalis");

        try
        {
            var worldsTask = _httpClient.GetFromJsonAsync<List<WorldInfo>>(WorldsUrl, ct);
            var dataCentersTask = _httpClient.GetFromJsonAsync<List<DataCenterInfo>>(DataCentersUrl, ct);

            await Task.WhenAll(worldsTask, dataCentersTask);

            var worlds = await worldsTask ?? new List<WorldInfo>();
            var dataCenters = await dataCentersTask ?? new List<DataCenterInfo>();

            var worldIdToName = worlds.ToDictionary(w => w.Id, w => w.Name);

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
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "Failed to fetch world data from Universalis");
            throw new InvalidOperationException($"Failed to fetch world data: {ex.Message}. This may be due to CORS restrictions in the browser.", ex);
        }
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
