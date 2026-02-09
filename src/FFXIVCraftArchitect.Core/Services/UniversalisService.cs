using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services.Interfaces;

namespace FFXIVCraftArchitect.Core.Services;

/// <summary>
/// Service for interacting with the Universalis API.
/// Ported from Python: UNIVERSALIS_API constant
/// </summary>
public class UniversalisService : IUniversalisService
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
    /// Includes rate limiting (100ms delay between chunks) to avoid API throttling.
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
                        var delayMs = (int)Math.Pow(2, retry) * 500; // 1s, 2s, 4s
                        _logger?.LogWarning("Retry {Retry}/{MaxRetries} for chunk {ChunkIndex} after {DelayMs}ms delay", 
                            retry, maxRetries, (i / chunkSize) + 1, delayMs);
                        await Task.Delay(delayMs, ct);
                    }

                    var response = await _httpClient.GetAsync(url, ct);
                    
                    // Handle rate limiting (429)
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        _logger?.LogWarning("Rate limited (429) on chunk {ChunkIndex}, will retry", (i / chunkSize) + 1);
                        if (retry < maxRetries - 1) continue;
                        throw new HttpRequestException("Rate limited by Universalis API");
                    }
                    
                    response.EnsureSuccessStatusCode();

                    // Single-item and multi-item responses have different structures
                    if (chunk.Count == 1)
                    {
                        // Single item: response is direct UniversalisResponse
                        var singleResult = await response.Content.ReadFromJsonAsync<UniversalisResponse>(ct);
                        if (singleResult != null)
                        {
                            allResults[chunk[0]] = singleResult;
                            _logger?.LogDebug("Single item response: ItemId={ItemId}, Listings={Listings}",
                                singleResult.ItemId, singleResult.Listings?.Count ?? 0);
                        }
                    }
                    else
                    {
                        // Multiple items: response is UniversalisBulkResponse with "items" dictionary
                        var bulkResult = await response.Content.ReadFromJsonAsync<UniversalisBulkResponse>(ct);
                        
                        _logger?.LogDebug("Chunk {ChunkIndex} response: Items={ItemCount}, Expected={ExpectedCount}",
                            (i / chunkSize) + 1, bulkResult?.Items?.Count ?? 0, chunk.Count);
                        
                        if (bulkResult?.Items != null)
                        {
                            foreach (var kvp in bulkResult.Items)
                            {
                                _logger?.LogDebug("  - Item ID {ItemId}: Listings={Listings}", 
                                    kvp.Key, kvp.Value.Listings?.Count ?? 0);
                                allResults[kvp.Key] = kvp.Value;
                            }
                            
                            // Check for missing items
                            var missing = chunk.Where(id => !bulkResult.Items.ContainsKey(id)).ToList();
                            if (missing.Any())
                            {
                                _logger?.LogWarning("Missing from response: {MissingIds}", string.Join(", ", missing));
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

            // Rate limiting delay between chunks (except for the last one)
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
    /// Get market data for multiple items at once using parallel requests for faster fetching.
    /// Uses a semaphore to limit concurrent requests and avoid rate limiting.
    /// </summary>
    /// <param name="worldOrDc">World or data center name</param>
    /// <param name="itemIds">Item IDs to fetch</param>
    /// <param name="maxConcurrency">Maximum concurrent requests (default: 3)</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkParallelAsync(
        string worldOrDc, 
        IEnumerable<int> itemIds, 
        int maxConcurrency = 3,
        CancellationToken ct = default)
    {
        const int chunkSize = 100; // Universalis API limit
        const int delayBetweenChunksMs = 50; // Small delay even in parallel mode
        const int maxRetries = 3;
        
        var itemIdList = itemIds.ToList();
        var chunks = new List<List<int>>();
        
        // Split into chunks
        for (int i = 0; i < itemIdList.Count; i += chunkSize)
        {
            chunks.Add(itemIdList.Skip(i).Take(chunkSize).ToList());
        }
        
        _logger?.LogInformation("Fetching bulk market data for {Count} items in {ChunkCount} parallel chunks (max concurrency: {Concurrency})", 
            itemIdList.Count, chunks.Count, maxConcurrency);

        var allResults = new ConcurrentDictionary<int, UniversalisResponse>();
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var completedChunks = 0;

        // Process all chunks in parallel with limited concurrency
        var tasks = chunks.Select(async (chunk, index) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var chunkNum = index + 1;
                var ids = string.Join(",", chunk);
                var url = string.Format(UniversalisApiUrl, Uri.EscapeDataString(worldOrDc), ids);
                
                _logger?.LogDebug("[Parallel] Fetching chunk {ChunkNum}/{TotalChunks} ({ItemCount} items)", 
                    chunkNum, chunks.Count, chunk.Count);

                // Retry logic with exponential backoff
                bool success = false;
                Exception? lastException = null;
                
                for (int retry = 0; retry < maxRetries && !success; retry++)
                {
                    try
                    {
                        if (retry > 0)
                        {
                            var delayMs = (int)Math.Pow(2, retry) * 500; // 1s, 2s, 4s
                            _logger?.LogWarning("[Parallel] Retry {Retry}/{MaxRetries} for chunk {ChunkNum} after {DelayMs}ms delay", 
                                retry, maxRetries, chunkNum, delayMs);
                            await Task.Delay(delayMs, ct);
                        }

                        var response = await _httpClient.GetAsync(url, ct);
                        
                        // Handle rate limiting (429)
                        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            _logger?.LogWarning("[Parallel] Rate limited (429) on chunk {ChunkNum}, will retry", chunkNum);
                            if (retry < maxRetries - 1) continue;
                            throw new HttpRequestException("Rate limited by Universalis API");
                        }
                        
                        response.EnsureSuccessStatusCode();

                        // Single-item and multi-item responses have different structures
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
                        var completed = Interlocked.Increment(ref completedChunks);
                        _logger?.LogDebug("[Parallel] Chunk {ChunkNum} completed ({Completed}/{Total})", 
                            chunkNum, completed, chunks.Count);
                    }
                    catch (Exception ex) when (retry < maxRetries - 1)
                    {
                        lastException = ex;
                        _logger?.LogWarning(ex, "[Parallel] Chunk {ChunkNum} failed, will retry", chunkNum);
                    }
                }

                if (!success && lastException != null)
                {
                    _logger?.LogError(lastException, "[Parallel] Chunk {ChunkNum} failed after {MaxRetries} retries", chunkNum, maxRetries);
                }

                // Small delay even in parallel mode to be nice to the API
                if (delayBetweenChunksMs > 0)
                {
                    await Task.Delay(delayBetweenChunksMs, ct);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        // Wait for all chunks to complete
        await Task.WhenAll(tasks);
        
        _logger?.LogInformation("[Parallel] Fetched market data for {FetchedCount}/{RequestedCount} items in {CompletedChunks}/{TotalChunks} chunks",
            allResults.Count, itemIdList.Count, completedChunks, chunks.Count);

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
