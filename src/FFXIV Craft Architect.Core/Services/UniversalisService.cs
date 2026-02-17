using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

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
    /// Uses parallel fetching with adaptive backoff for optimal performance.
    /// </summary>
    /// <param name="worldOrDc">World or data center name</param>
    /// <param name="itemIds">Item IDs to fetch</param>
    /// <param name="useParallel">Whether to use parallel fetching (default: true)</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<Dictionary<int, UniversalisResponse>> GetMarketDataBulkAsync(
        string worldOrDc,
        IEnumerable<int> itemIds,
        bool useParallel = true,
        CancellationToken ct = default)
    {
        const int chunkSize = 100; // Universalis API limit
        const int maxConcurrency = 3; // Limit concurrent requests
        const int maxRetries = 3;
        
        var itemIdList = itemIds.ToList();
        if (itemIdList.Count == 0)
            return new Dictionary<int, UniversalisResponse>();
        
        var allResults = new ConcurrentDictionary<int, UniversalisResponse>();
        var delayStrategy = new AdaptiveDelayStrategy(
            initialDelayMs: 100,  // Start aggressive
            minDelayMs: 50,
            maxDelayMs: 5000,
            backoffMultiplier: 2.0,
            rateLimitMultiplier: 3.0);
        
        _logger?.LogInformation("Fetching bulk market data for {Count} items (parallel={UseParallel})", 
            itemIdList.Count, useParallel);

        // Split into chunks
        var chunks = new List<List<int>>();
        for (int i = 0; i < itemIdList.Count; i += chunkSize)
        {
            chunks.Add(itemIdList.Skip(i).Take(chunkSize).ToList());
        }

        if (useParallel)
        {
            // Parallel fetching with adaptive backoff
            var semaphore = new SemaphoreSlim(maxConcurrency);
            var completedChunks = 0;
            var failedChunks = 0;

            var tasks = chunks.Select(async (chunk, index) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var chunkNum = index + 1;
                    await FetchChunkAsync(
                        chunk, chunkNum, chunks.Count, worldOrDc, 
                        allResults, delayStrategy, maxRetries, ct);
                    
                    var completed = Interlocked.Increment(ref completedChunks);
                    _logger?.LogDebug("Chunk {ChunkNum} completed ({Completed}/{Total})", 
                        chunkNum, completed, chunks.Count);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedChunks);
                    _logger?.LogError(ex, "Chunk {ChunkIndex} failed after all retries", index + 1);
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
            
            _logger?.LogInformation(
                "Parallel fetch complete: {Completed}/{Total} chunks succeeded, {Failed} failed, final delay: {Delay}ms",
                completedChunks, chunks.Count, failedChunks, delayStrategy.GetDelay());
        }
        else
        {
            // Sequential fetching with adaptive backoff
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                var chunkNum = i + 1;
                
                try
                {
                    await FetchChunkAsync(
                        chunk, chunkNum, chunks.Count, worldOrDc,
                        allResults, delayStrategy, maxRetries, ct);
                    
                    _logger?.LogDebug("Chunk {ChunkNum}/{Total} completed", chunkNum, chunks.Count);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Chunk {ChunkNum} failed after all retries", chunkNum);
                }

                // Apply adaptive delay between chunks (except last)
                if (i < chunks.Count - 1)
                {
                    var delay = delayStrategy.GetDelay();
                    if (delay > 0)
                    {
                        _logger?.LogDebug("Waiting {DelayMs}ms before next chunk", delay);
                        await Task.Delay(delay, ct);
                    }
                }
            }
            
            _logger?.LogInformation(
                "Sequential fetch complete: {FetchedCount}/{RequestedCount} items, final delay: {Delay}ms",
                allResults.Count, itemIdList.Count, delayStrategy.GetDelay());
        }

        return new Dictionary<int, UniversalisResponse>(allResults);
    }

    /// <summary>
    /// Helper method to fetch a single chunk with retry logic and adaptive backoff.
    /// </summary>
    private async Task FetchChunkAsync(
        List<int> chunk,
        int chunkNum,
        int totalChunks,
        string worldOrDc,
        ConcurrentDictionary<int, UniversalisResponse> results,
        AdaptiveDelayStrategy delayStrategy,
        int maxRetries,
        CancellationToken ct)
    {
        var ids = string.Join(",", chunk);
        var url = string.Format(UniversalisApiUrl, Uri.EscapeDataString(worldOrDc), ids);
        
        _logger?.LogDebug("Fetching chunk {ChunkNum}/{TotalChunks} ({ItemCount} items, delay: {Delay}ms)", 
            chunkNum, totalChunks, chunk.Count, delayStrategy.GetDelay());

        Exception? lastException = null;
        
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                if (retry > 0)
                {
                    var retryDelay = delayStrategy.GetDelay() * retry;
                    _logger?.LogWarning("Retry {Retry}/{MaxRetries} for chunk {ChunkNum} after {DelayMs}ms", 
                        retry, maxRetries, chunkNum, retryDelay);
                    await Task.Delay(retryDelay, ct);
                }

                var response = await _httpClient.GetAsync(url, ct);

                // Handle specific error codes
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    delayStrategy.ReportFailure(response.StatusCode);
                    _logger?.LogWarning("Rate limited (429) on chunk {ChunkNum}, increasing delay to {Delay}ms", 
                        chunkNum, delayStrategy.GetDelay());
                    if (retry < maxRetries - 1) continue;
                    throw new HttpRequestException("Rate limited by Universalis API");
                }
                else if ((int)response.StatusCode == 504) // Gateway Timeout
                {
                    delayStrategy.ReportFailure(response.StatusCode);
                    _logger?.LogWarning("Gateway timeout (504) on chunk {ChunkNum}, increasing delay to {Delay}ms", 
                        chunkNum, delayStrategy.GetDelay());
                    if (retry < maxRetries - 1) continue;
                    throw new HttpRequestException("Gateway timeout from Universalis API");
                }

                response.EnsureSuccessStatusCode();

                // Parse response
                if (chunk.Count == 1)
                {
                    var singleResult = await response.Content.ReadFromJsonAsync<UniversalisResponse>(ct);
                    if (singleResult != null)
                    {
                        results[chunk[0]] = singleResult;
                    }
                }
                else
                {
                    var bulkResult = await response.Content.ReadFromJsonAsync<UniversalisBulkResponse>(ct);
                    if (bulkResult?.Items != null)
                    {
                        foreach (var kvp in bulkResult.Items)
                        {
                            results[kvp.Key] = kvp.Value;
                        }
                    }
                }
                
                // Success - report to delay strategy
                delayStrategy.ReportSuccess();
                return; // Success, exit retry loop
            }
            catch (Exception ex)
            {
                lastException = ex;
                
                // Report failure for non-retryable errors or final retry
                if (retry == maxRetries - 1 || 
                    (ex is HttpRequestException && 
                     ex.Message.Contains("Rate limited")))
                {
                    delayStrategy.ReportFailure(System.Net.HttpStatusCode.BadRequest);
                }
                
                if (retry < maxRetries - 1)
                {
                    _logger?.LogWarning(ex, "Chunk {ChunkNum} failed on attempt {Attempt}, will retry", 
                        chunkNum, retry + 1);
                }
            }
        }

        // All retries exhausted
        if (lastException != null)
        {
            throw lastException;
        }
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
