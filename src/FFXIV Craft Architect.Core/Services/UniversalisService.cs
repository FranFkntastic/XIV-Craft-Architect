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
    /// <remarks>
    /// DEPRECATED: This method is to be subsumed by GetMarketDataBulkAsync.
    /// Use GetMarketDataBulkAsync with a single-item list for new code.
    /// </remarks>
    /// <param name="worldOrDc">World or data center name</param>
    /// <param name="itemId">Item ID</param>
    /// <param name="hqOnly">If true, only return HQ listings</param>
    /// <param name="entries">Number of listings to return (default 10)</param>
    /// <param name="ct">Cancellation token</param>
    [Obsolete("Use GetMarketDataBulkAsync instead. This method will be removed in a future version.")]
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

#pragma warning disable CS0618 // Type or member is obsolete
        var response = await _httpClient.GetAsync(url, ct);
#pragma warning restore CS0618 // Type or member is obsolete
        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadFromJsonAsync<UniversalisResponse>(ct);
        return data ?? new UniversalisResponse { ItemId = itemId };
    }

    /// <summary>
    /// Get market data for multiple items at once.
    /// Uses adaptive chunk sizing with split-on-failure for large requests.
    /// Implements missing-ID retry to ensure complete data retrieval.
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
        const int initialChunkSize = 25; // Start smaller to avoid timeouts
        const int minChunkSize = 5;      // Don't split below this
        const int maxConcurrency = 3;
        const int maxGlobalRetries = 2;  // How many times to retry missing IDs

        var itemIdList = itemIds.ToList();
        if (itemIdList.Count == 0)
            return new Dictionary<int, UniversalisResponse>();

        var allResults = new ConcurrentDictionary<int, UniversalisResponse>();
        var delayStrategy = new AdaptiveDelayStrategy(
            initialDelayMs: 200,  // Start more conservative
            minDelayMs: 100,
            maxDelayMs: 10000,    // Allow longer delays
            backoffMultiplier: 2.0,
            rateLimitMultiplier: 3.0);

        _logger?.LogInformation("Fetching bulk market data for {Count} items from {WorldOrDc} (parallel={UseParallel})",
            itemIdList.Count, worldOrDc, useParallel);

        // Build initial work queue with smaller chunks
        var workQueue = new Queue<ChunkWorkItem>();
        var chunks = itemIdList.Chunk(initialChunkSize).ToList();
        for (int i = 0; i < chunks.Count; i++)
        {
            workQueue.Enqueue(new ChunkWorkItem(
                chunks[i].ToList(),
                chunkIndex: i,
                splitDepth: 0,
                retryCount: 0));
        }

        // Process work queue
        int totalChunksAttempted = 0;
        int chunksSucceeded = 0;
        int chunksFailed = 0;

        if (useParallel)
        {
            var semaphore = new SemaphoreSlim(maxConcurrency);

            while (workQueue.Count > 0)
            {
                var currentBatch = new List<ChunkWorkItem>();
                while (workQueue.Count > 0 && currentBatch.Count < maxConcurrency)
                {
                    currentBatch.Add(workQueue.Dequeue());
                }

                var tasks = currentBatch.Select(async workItem =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        Interlocked.Increment(ref totalChunksAttempted);
                        var result = await FetchChunkWithAdaptiveSplitAsync(
                            workItem, worldOrDc, allResults, delayStrategy, minChunkSize, ct);

                        if (result.Success)
                        {
                            Interlocked.Increment(ref chunksSucceeded);
                        }
                        else if (result.ShouldSplit && workItem.CanSplit(minChunkSize))
                        {
                            // Split failed chunk and re-queue
                            var splitChunks = workItem.Split();
                            foreach (var splitChunk in splitChunks)
                            {
                                lock (workQueue)
                                {
                                    workQueue.Enqueue(splitChunk);
                                }
                            }
                            _logger?.LogInformation(
                                "Split chunk {ChunkIndex} (size {Size}, depth {Depth}) into {SplitCount} smaller chunks due to timeout",
                                workItem.ChunkIndex, workItem.ItemIds.Count, workItem.SplitDepth, splitChunks.Count);
                        }
                        else
                        {
                            Interlocked.Increment(ref chunksFailed);
                            _logger?.LogError(
                                "Chunk {ChunkIndex} (size {Size}) failed permanently after {Retries} retries",
                                workItem.ChunkIndex, workItem.ItemIds.Count, workItem.RetryCount + 1);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks);

                // Small delay between batches to be polite to the API
                if (workQueue.Count > 0)
                {
                    var batchDelay = delayStrategy.GetDelay();
                    await Task.Delay(batchDelay, ct);
                }
            }
        }
        else
        {
            // Sequential processing
            while (workQueue.Count > 0)
            {
                var workItem = workQueue.Dequeue();
                totalChunksAttempted++;

                var result = await FetchChunkWithAdaptiveSplitAsync(
                    workItem, worldOrDc, allResults, delayStrategy, minChunkSize, ct);

                if (result.Success)
                {
                    chunksSucceeded++;
                }
                else if (result.ShouldSplit && workItem.CanSplit(minChunkSize))
                {
                    var splitChunks = workItem.Split();
                    foreach (var splitChunk in splitChunks)
                    {
                        workQueue.Enqueue(splitChunk);
                    }
                    _logger?.LogInformation(
                        "Split chunk {ChunkIndex} (size {Size}, depth {Depth}) into {SplitCount} smaller chunks due to timeout",
                        workItem.ChunkIndex, workItem.ItemIds.Count, workItem.SplitDepth, splitChunks.Count);
                }
                else
                {
                    chunksFailed++;
                    _logger?.LogError(
                        "Chunk {ChunkIndex} (size {Size}) failed permanently after {Retries} retries",
                        workItem.ChunkIndex, workItem.ItemIds.Count, workItem.RetryCount + 1);
                }

                // Adaptive delay between requests
                if (workQueue.Count > 0)
                {
                    var delay = delayStrategy.GetDelay();
                    await Task.Delay(delay, ct);
                }
            }
        }

        // Check for missing IDs and retry them
        var missingIds = itemIdList.Where(id => !allResults.ContainsKey(id)).ToList();
        int globalRetryAttempt = 0;

        while (missingIds.Count > 0 && globalRetryAttempt < maxGlobalRetries)
        {
            globalRetryAttempt++;
            _logger?.LogWarning(
                "Missing {Count} items after initial fetch, global retry attempt {Attempt}/{Max}",
                missingIds.Count, globalRetryAttempt, maxGlobalRetries);

            // Reset delay strategy for retry phase
            delayStrategy = new AdaptiveDelayStrategy(
                initialDelayMs: 500,
                minDelayMs: 250,
                maxDelayMs: 15000,
                backoffMultiplier: 2.0,
                rateLimitMultiplier: 3.0);

            // Create single-item chunks for missing IDs
            var retryWorkItems = missingIds
                .Select((id, idx) => new ChunkWorkItem(
                    new List<int> { id },
                    chunkIndex: idx,
                    splitDepth: 0,
                    retryCount: globalRetryAttempt - 1))
                .ToList();

            foreach (var workItem in retryWorkItems)
            {
                var result = await FetchChunkWithAdaptiveSplitAsync(
                    workItem, worldOrDc, allResults, delayStrategy, minChunkSize, ct);

                if (!result.Success)
                {
                    _logger?.LogError(
                        "Failed to fetch item {ItemId} on global retry attempt {Attempt}",
                        workItem.ItemIds[0], globalRetryAttempt);
                }

                // Delay between single-item retries
                await Task.Delay(delayStrategy.GetDelay(), ct);
            }

            missingIds = itemIdList.Where(id => !allResults.ContainsKey(id)).ToList();
        }

        var finalMissingCount = itemIdList.Count(id => !allResults.ContainsKey(id));
        _logger?.LogInformation(
            "Bulk fetch complete: {Succeeded}/{Attempted} chunks succeeded, {Failed} failed, " +
            "{Fetched}/{Requested} items fetched, {Missing} missing, final delay: {Delay}ms",
            chunksSucceeded, totalChunksAttempted, chunksFailed,
            allResults.Count, itemIdList.Count, finalMissingCount, delayStrategy.GetDelay());

        if (finalMissingCount > 0)
        {
            _logger?.LogWarning(
                "Failed to fetch {MissingCount} items: {ItemIds}",
                finalMissingCount,
                string.Join(", ", itemIdList.Where(id => !allResults.ContainsKey(id))));
        }

        return new Dictionary<int, UniversalisResponse>(allResults);
    }

    /// <summary>
    /// Fetches a chunk with adaptive splitting on timeout/gateway errors.
    /// </summary>
    private async Task<ChunkFetchResult> FetchChunkWithAdaptiveSplitAsync(
        ChunkWorkItem workItem,
        string worldOrDc,
        ConcurrentDictionary<int, UniversalisResponse> results,
        AdaptiveDelayStrategy delayStrategy,
        int minChunkSize,
        CancellationToken ct)
    {
        var ids = string.Join(",", workItem.ItemIds);
        var url = string.Format(UniversalisApiUrl, Uri.EscapeDataString(worldOrDc), ids);

        const int maxRetries = 3;
        Exception? lastException = null;
        bool shouldSplit = false;

        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                if (retry > 0)
                {
                    var retryDelay = delayStrategy.GetDelay() * retry;
                    _logger?.LogWarning(
                        "Retry {Retry}/{MaxRetries} for chunk {ChunkIndex} (size {Size}, depth {Depth}) after {DelayMs}ms",
                        retry, maxRetries, workItem.ChunkIndex, workItem.ItemIds.Count, workItem.SplitDepth, retryDelay);
                    await Task.Delay(retryDelay, ct);
                }

                _logger?.LogDebug(
                    "Fetching chunk {ChunkIndex} (size {Size}, depth {Depth}): {ItemIds}",
                    workItem.ChunkIndex, workItem.ItemIds.Count, workItem.SplitDepth,
                    string.Join(",", workItem.ItemIds.Take(5)) + (workItem.ItemIds.Count > 5 ? "..." : ""));

                var response = await _httpClient.GetAsync(url, ct);

                // Handle specific error codes
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    delayStrategy.ReportFailure(response.StatusCode);
                    _logger?.LogWarning(
                        "Rate limited (429) on chunk {ChunkIndex}, increasing delay to {Delay}ms",
                        workItem.ChunkIndex, delayStrategy.GetDelay());
                    if (retry < maxRetries - 1) continue;
                    return new ChunkFetchResult(false, false, new HttpRequestException("Rate limited by Universalis API"));
                }
                else if ((int)response.StatusCode == 504) // Gateway Timeout
                {
                    delayStrategy.ReportFailure(response.StatusCode);
                    _logger?.LogWarning(
                        "Gateway timeout (504) on chunk {ChunkIndex} (size {Size}), increasing delay to {Delay}ms",
                        workItem.ChunkIndex, workItem.ItemIds.Count, delayStrategy.GetDelay());

                    // Mark for splitting if chunk is splittable
                    if (workItem.CanSplit(minChunkSize))
                    {
                        shouldSplit = true;
                        break; // Exit retry loop and signal split needed
                    }

                    if (retry < maxRetries - 1) continue;
                    return new ChunkFetchResult(false, false, new HttpRequestException("Gateway timeout from Universalis API"));
                }

                response.EnsureSuccessStatusCode();

                // Parse response
                if (workItem.ItemIds.Count == 1)
                {
                    var singleResult = await response.Content.ReadFromJsonAsync<UniversalisResponse>(ct);
                    if (singleResult != null)
                    {
                        results[workItem.ItemIds[0]] = singleResult;
                    }
                }
                else
                {
                    var bulkResult = await response.Content.ReadFromJsonAsync<UniversalisBulkResponse>(ct);
                    if (bulkResult?.Items != null)
                    {
                        // Track which IDs were actually returned
                        var returnedIds = new HashSet<int>();
                        foreach (var kvp in bulkResult.Items)
                        {
                            results[kvp.Key] = kvp.Value;
                            returnedIds.Add(kvp.Key);
                        }

                        // Check for missing IDs in response
                        var requestedIds = new HashSet<int>(workItem.ItemIds);
                        var missingInResponse = requestedIds.Except(returnedIds).ToList();
                        if (missingInResponse.Count > 0)
                        {
                            _logger?.LogWarning(
                                "API response missing {Count} items for chunk {ChunkIndex}: {ItemIds}",
                                missingInResponse.Count, workItem.ChunkIndex,
                                string.Join(", ", missingInResponse));

                            // If we have missing items and can split, mark for splitting
                            if (workItem.CanSplit(minChunkSize))
                            {
                                shouldSplit = true;
                                delayStrategy.ReportFailure(System.Net.HttpStatusCode.BadRequest);
                                break;
                            }
                        }
                    }
                }

                delayStrategy.ReportSuccess();
                return new ChunkFetchResult(true, false, null);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || !ct.IsCancellationRequested)
            {
                // Timeout - treat like 504
                delayStrategy.ReportFailure(System.Net.HttpStatusCode.RequestTimeout);
                lastException = ex;
                _logger?.LogWarning(
                    "Request timeout on chunk {ChunkIndex} (size {Size})",
                    workItem.ChunkIndex, workItem.ItemIds.Count);

                if (workItem.CanSplit(minChunkSize))
                {
                    shouldSplit = true;
                    break;
                }

                if (retry >= maxRetries - 1)
                {
                    return new ChunkFetchResult(false, false, ex);
                }
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                // HTTP timeout
                delayStrategy.ReportFailure(System.Net.HttpStatusCode.RequestTimeout);
                lastException = ex;
                _logger?.LogWarning(
                    "HTTP timeout on chunk {ChunkIndex} (size {Size})",
                    workItem.ChunkIndex, workItem.ItemIds.Count);

                if (workItem.CanSplit(minChunkSize))
                {
                    shouldSplit = true;
                    break;
                }

                if (retry >= maxRetries - 1)
                {
                    return new ChunkFetchResult(false, false, ex);
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                delayStrategy.ReportFailure(System.Net.HttpStatusCode.BadRequest);

                if (retry < maxRetries - 1)
                {
                    _logger?.LogWarning(ex,
                        "Chunk {ChunkIndex} failed on attempt {Attempt}, will retry",
                        workItem.ChunkIndex, retry + 1);
                }
            }
        }

        // If we should split, signal that
        if (shouldSplit)
        {
            return new ChunkFetchResult(false, true, lastException);
        }

        // All retries exhausted
        return new ChunkFetchResult(false, false, lastException);
    }

    /// <summary>
    /// Work item representing a chunk to fetch, with metadata for adaptive splitting.
    /// </summary>
    private class ChunkWorkItem
    {
        public List<int> ItemIds { get; }
        public int ChunkIndex { get; }
        public int SplitDepth { get; }
        public int RetryCount { get; }

        public ChunkWorkItem(List<int> itemIds, int chunkIndex, int splitDepth, int retryCount)
        {
            ItemIds = itemIds;
            ChunkIndex = chunkIndex;
            SplitDepth = splitDepth;
            RetryCount = retryCount;
        }

        public bool CanSplit(int minChunkSize) => ItemIds.Count > minChunkSize;

        public List<ChunkWorkItem> Split()
        {
            var mid = ItemIds.Count / 2;
            var left = ItemIds.Take(mid).ToList();
            var right = ItemIds.Skip(mid).ToList();

            var result = new List<ChunkWorkItem>();

            if (left.Count > 0)
            {
                result.Add(new ChunkWorkItem(left, ChunkIndex, SplitDepth + 1, RetryCount));
            }

            if (right.Count > 0)
            {
                result.Add(new ChunkWorkItem(right, ChunkIndex * 2 + 1, SplitDepth + 1, RetryCount));
            }

            return result;
        }
    }

    /// <summary>
    /// Result of attempting to fetch a chunk.
    /// </summary>
    private record ChunkFetchResult(bool Success, bool ShouldSplit, Exception? Error);

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
