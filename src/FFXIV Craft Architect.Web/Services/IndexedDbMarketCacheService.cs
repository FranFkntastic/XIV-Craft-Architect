using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// IndexedDB implementation of IMarketCacheService for Blazor WebAssembly.
/// Uses Unix timestamps to avoid DateTime serialization issues.
/// Implements automatic cleanup and cache size limits.
/// </summary>
public class IndexedDbMarketCacheService : IMarketCacheService, IMarketCacheDiagnosticsProvider
{
    private readonly IJSRuntime _jsRuntime;
    private readonly UniversalisService _universalisService;
    private readonly ILogger<IndexedDbMarketCacheService>? _logger;
    private readonly TimeSpan _defaultMaxAge = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _populateSemaphore = new(1, 1);
    private const long MaxCacheSizeBytes = 500 * 1024 * 1024; // 500MB max
    private const int MaxCacheEntries = 10000; // Max 10k items
    private const int MaxDataCenterFetchConcurrency = 2;

    public MarketCacheDecisionSnapshot? LastDecisionSnapshot { get; private set; }

    public IndexedDbMarketCacheService(
        IJSRuntime jsRuntime,
        UniversalisService universalisService,
        ILogger<IndexedDbMarketCacheService>? logger = null)
    {
        _jsRuntime = jsRuntime;
        _universalisService = universalisService;
        _logger = logger;

        _logger?.LogInformation("[IndexedDbMarketCache] Initialized with maxSize={MaxSize}MB, maxEntries={MaxEntries}",
            MaxCacheSizeBytes / 1024 / 1024, MaxCacheEntries);
    }

    private static string GetKey(int itemId, string dataCenter) => $"{itemId}@{dataCenter}";

    /// <summary>
    /// Converts Unix timestamp to DateTimeOffset safely.
    /// </summary>
    private static DateTimeOffset UnixToDateTimeOffset(long unixSeconds)
    {
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }

    /// <summary>
    /// Converts DateTime to Unix timestamp safely.
    /// </summary>
    private static long DateTimeToUnix(DateTime dateTime)
    {
        return new DateTimeOffset(dateTime).ToUnixTimeSeconds();
    }

    public async Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var maxAgeSeconds = (long)(maxAge ?? _defaultMaxAge).TotalSeconds;
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow) - maxAgeSeconds;
        var key = GetKey(itemId, dataCenter);

        try
        {
            var entry = await _jsRuntime.InvokeAsync<IndexedDbMarketCacheEntry?>("IndexedDB.loadMarketData", key);

            if (entry == null)
            {
                _logger?.LogDebug("[IndexedDbMarketCache] MISS for {ItemId}@{DataCenter}", itemId, dataCenter);
                return null;
            }

            // Check if stale using Unix timestamp comparison
            if (entry.FetchedAtUnix <= cutoffUnix)
            {
                var age = DateTime.UtcNow - UnixToDateTimeOffset(entry.FetchedAtUnix).DateTime;
                _logger?.LogDebug("[IndexedDbMarketCache] STALE for {ItemId}@{DataCenter} (age: {Age:F1}h)",
                    itemId, dataCenter, age.TotalHours);
                return null;
            }

            _logger?.LogDebug("[IndexedDbMarketCache] HIT for {ItemId}@{DataCenter}", itemId, dataCenter);

            return new CachedMarketData
            {
                ItemId = itemId,
                DataCenter = dataCenter,
                FetchedAtUnix = entry.FetchedAtUnix,
                LastUploadTimeUnixMilliseconds = entry.LastUploadTimeUnixMilliseconds,
                DCAveragePrice = entry.DcAvgPrice,
                HQAveragePrice = entry.HqAvgPrice,
                Worlds = entry.Worlds ?? new List<CachedWorldData>()
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error getting data for {ItemId}@{DataCenter}", itemId, dataCenter);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>> GetManyAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        if (requests.Count == 0)
        {
            return new Dictionary<(int itemId, string dataCenter), CachedMarketData>();
        }

        var maxAgeSeconds = (long)(maxAge ?? _defaultMaxAge).TotalSeconds;
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow) - maxAgeSeconds;
        var requestsByKey = new Dictionary<string, (int itemId, string dataCenter)>();

        foreach (var request in requests)
        {
            requestsByKey.TryAdd(GetKey(request.itemId, request.dataCenter), request);
        }

        try
        {
            var entries = await _jsRuntime.InvokeAsync<List<IndexedDbMarketCacheEntry>>(
                "IndexedDB.loadMarketDataBulk",
                requestsByKey.Keys.ToArray(),
                cutoffUnix);

            var results = new Dictionary<(int itemId, string dataCenter), CachedMarketData>();

            foreach (var entry in entries ?? new List<IndexedDbMarketCacheEntry>())
            {
                if (entry.FetchedAtUnix <= cutoffUnix)
                {
                    continue;
                }

                var key = string.IsNullOrWhiteSpace(entry.Key)
                    ? GetKey(entry.ItemId, entry.DataCenter)
                    : entry.Key;

                if (!requestsByKey.TryGetValue(key, out var request))
                {
                    continue;
                }

                results[request] = new CachedMarketData
                {
                    ItemId = request.itemId,
                    DataCenter = request.dataCenter,
                    FetchedAtUnix = entry.FetchedAtUnix,
                    LastUploadTimeUnixMilliseconds = entry.LastUploadTimeUnixMilliseconds,
                    DCAveragePrice = entry.DcAvgPrice,
                    HQAveragePrice = entry.HqAvgPrice,
                    Worlds = entry.Worlds ?? new List<CachedWorldData>()
                };
            }

            _logger?.LogDebug(
                "[IndexedDbMarketCache] Bulk checked {Checked}, Hits {Hits}, Missing {Missing}",
                requestsByKey.Count,
                results.Count,
                requestsByKey.Count - results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error getting bulk cached data");
            return new Dictionary<(int itemId, string dataCenter), CachedMarketData>();
        }
    }

    public async Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var maxAgeSeconds = (long)(maxAge ?? _defaultMaxAge).TotalSeconds;
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow) - maxAgeSeconds;
        var key = GetKey(itemId, dataCenter);

        try
        {
            var entry = await _jsRuntime.InvokeAsync<IndexedDbMarketCacheEntry?>("IndexedDB.loadMarketData", key);

            if (entry == null)
            {
                _logger?.LogDebug("[IndexedDbMarketCache] NO DATA for {ItemId}@{DataCenter}", itemId, dataCenter);
                return (null, false);
            }

            var isStale = entry.FetchedAtUnix <= cutoffUnix;
            var age = DateTime.UtcNow - UnixToDateTimeOffset(entry.FetchedAtUnix).DateTime;

            _logger?.LogDebug("[IndexedDbMarketCache] {Status} for {ItemId}@{DataCenter} (age: {Age:F1}h)",
                isStale ? "STALE" : "FRESH", itemId, dataCenter, age.TotalHours);

            var data = new CachedMarketData
            {
                ItemId = itemId,
                DataCenter = dataCenter,
                FetchedAtUnix = entry.FetchedAtUnix,
                LastUploadTimeUnixMilliseconds = entry.LastUploadTimeUnixMilliseconds,
                DCAveragePrice = entry.DcAvgPrice,
                HQAveragePrice = entry.HqAvgPrice,
                Worlds = entry.Worlds ?? new List<CachedWorldData>()
            };

            return (data, isStale);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error getting data with stale for {ItemId}@{DataCenter}", itemId, dataCenter);
            return (null, false);
        }
    }

    public async Task SetAsync(int itemId, string dataCenter, CachedMarketData data)
    {
        var key = GetKey(itemId, dataCenter);
        var age = data.Age;

        _logger?.LogDebug("[IndexedDbMarketCache] Storing {ItemId}@{DataCenter} with FetchedAtUnix={FetchedAt} (age={Age:F1}min)",
            itemId, dataCenter, data.FetchedAtUnix, age.TotalMinutes);

        try
        {
            var entry = new IndexedDbMarketCacheEntry
            {
                Key = key,
                ItemId = itemId,
                DataCenter = dataCenter,
                FetchedAtUnix = data.FetchedAtUnix,
                LastUploadTimeUnixMilliseconds = data.LastUploadTimeUnixMilliseconds,
                DcAvgPrice = data.DCAveragePrice,
                HqAvgPrice = data.HQAveragePrice,
                Worlds = data.Worlds
            };

            await _jsRuntime.InvokeVoidAsync("IndexedDB.saveMarketData", key, entry);
            _logger?.LogDebug("[IndexedDbMarketCache] Stored {ItemId}@{DataCenter}", itemId, dataCenter);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error storing data for {ItemId}@{DataCenter}", itemId, dataCenter);
        }
    }

    public async Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        return await GetAsync(itemId, dataCenter, maxAge) != null;
    }

    public async Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        var missing = new List<(int, string)>();

        if (requests.Count == 0)
        {
            return missing;
        }

        var maxAgeSeconds = (long)(maxAge ?? _defaultMaxAge).TotalSeconds;
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow) - maxAgeSeconds;
        var requestsByKey = new Dictionary<string, (int itemId, string dataCenter)>();

        foreach (var request in requests)
        {
            requestsByKey.TryAdd(GetKey(request.itemId, request.dataCenter), request);
        }

        try
        {
            var entries = await _jsRuntime.InvokeAsync<List<IndexedDbMarketCacheEntry>>(
                "IndexedDB.loadMarketDataBulk",
                requestsByKey.Keys.ToArray(),
                cutoffUnix);

            var freshKeys = new HashSet<string>();

            foreach (var entry in entries ?? new List<IndexedDbMarketCacheEntry>())
            {
                if (entry.FetchedAtUnix <= cutoffUnix)
                {
                    continue;
                }

                var key = string.IsNullOrWhiteSpace(entry.Key)
                    ? GetKey(entry.ItemId, entry.DataCenter)
                    : entry.Key;

                if (requestsByKey.ContainsKey(key))
                {
                    freshKeys.Add(key);
                }
            }

            foreach (var request in requests)
            {
                if (!freshKeys.Contains(GetKey(request.itemId, request.dataCenter)))
                {
                    missing.Add(request);
                }
            }

            _logger?.LogInformation(
                "[IndexedDbMarketCache] Bulk checked {Checked}, Hits {Hits}, MissingOrStale {Missing}",
                requests.Count,
                requests.Count - missing.Count,
                missing.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error checking bulk cache state");
            missing.AddRange(requests);
        }

        return missing;
    }

    public async Task<int> EnsurePopulatedAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAge),
                maxAge,
                "Use RefreshRequestedAsync when fresh data is required for specific pairs.");
        }

        await _populateSemaphore.WaitAsync(ct);
        try
        {
            return await PopulateAsync(
                requests,
                maxAge,
                refreshRequestedPairs: false,
                progress,
                ct);
        }
        finally
        {
            _populateSemaphore.Release();
        }
    }

    public async Task<int> RefreshRequestedAsync(
        List<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return await PopulateAsync(
            requests,
            maxAge: null,
            refreshRequestedPairs: true,
            progress,
            ct);
    }

    private async Task<int> PopulateAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge,
        bool refreshRequestedPairs,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var effectiveMaxAge = maxAge ?? _defaultMaxAge;
        var requestedItemCount = requests.Select(request => request.itemId).Distinct().Count();

        _logger?.LogInformation(
            "[IndexedDbMarketCache] {Mode} START - {Count} requests, maxAge={MaxAge}",
            refreshRequestedPairs ? "RefreshRequestedAsync" : "EnsurePopulatedAsync",
            requests.Count,
            effectiveMaxAge);

        if (requests.Count == 0)
        {
            LastDecisionSnapshot = new MarketCacheDecisionSnapshot
            {
                MaxAge = maxAge,
                RefreshRequestedPairs = refreshRequestedPairs,
                Trigger = refreshRequestedPairs ? "refresh-requested-pairs" : "ensure-populated"
            };
            return 0;
        }

        var preCleanupState = await AnalyzeRequestedCacheAsync(requests, effectiveMaxAge);

        // STEP 1: Clean up stale entries before fetching new data
        var cleanedCount = 0;
        if (!refreshRequestedPairs)
        {
            progress?.Report("Cleaning up stale cache entries...");
            cleanedCount = await CleanupStaleAsync(effectiveMaxAge);
            if (cleanedCount > 0)
            {
                _logger?.LogInformation("[IndexedDbMarketCache] Cleaned {Count} stale entries before fetch", cleanedCount);
            }
        }

        // STEP 2: Check cache size and enforce limits
        var stats = await GetStatsAsync();
        var cacheSizeEvictionCount = 0;
        if (stats.ApproximateSizeBytes > MaxCacheSizeBytes || stats.TotalEntries > MaxCacheEntries)
        {
            _logger?.LogWarning("[IndexedDbMarketCache] Cache size exceeded (size={Size}MB, entries={Entries}). Running emergency cleanup...",
                stats.ApproximateSizeBytes / 1024 / 1024, stats.TotalEntries);
            progress?.Report("Cache size limit reached, cleaning up old entries...");

            // Aggressive cleanup - remove anything older than 30 minutes
            var cacheSizeCleanupCount = await CleanupStaleAsync(TimeSpan.FromMinutes(30));

            // If still too big, clear half the cache
            var newStats = await GetStatsAsync();
            var oldestEntryCleanupCount = 0;
            if (newStats.ApproximateSizeBytes > MaxCacheSizeBytes * 0.8)
            {
                oldestEntryCleanupCount = await ClearOldestEntriesAsync(stats.TotalEntries / 2);
            }

            cleanedCount += cacheSizeCleanupCount;
            cacheSizeEvictionCount = cacheSizeCleanupCount + oldestEntryCleanupCount;
        }

        // STEP 3: Check what's missing from cache
        var missing = refreshRequestedPairs
            ? requests
            : await GetMissingAsync(requests, maxAge);
        var dataCenterFetchCallCount = missing
            .Select(request => request.dataCenter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (missing.Count == 0)
        {
            _logger?.LogInformation("[IndexedDbMarketCache] All {Count} items already in cache", requests.Count);
            LastDecisionSnapshot = CreateDecisionSnapshot(
                requests,
                requestedItemCount,
                preCleanupState,
                fetchedCount: 0,
                verifiedCount: 0,
                cleanedCount,
                cacheSizeEvictionCount,
                dataCenterFetchCallCount: 0,
                maxAge,
                refreshRequestedPairs);
            return 0;
        }

        _logger?.LogInformation("[IndexedDbMarketCache] Fetching {MissingCount}/{TotalCount} items from Universalis",
            missing.Count, requests.Count);
        progress?.Report($"Fetching market data for {missing.Count} items...");

        // STEP 4: Group by data center for efficient bulk fetching
        var byDataCenter = missing.GroupBy(x => x.dataCenter).ToList();
        int fetchedCount = 0;
        int verifiedCount = 0;

        var fetchResults = await FetchDataCentersAsync(byDataCenter, progress, ct);
        var shouldLoadWorldData = fetchResults.Any(result => result.FetchedData.Count > 0);
        var worldData = shouldLoadWorldData
            ? await GetWorldDataForCachingAsync(ct)
            : null;

        foreach (var result in fetchResults)
        {
            // STEP 5: Store each result in cache with verification
            var entriesToCache = new List<(int itemId, string dataCenter, CachedMarketData data)>();
            foreach (var kvp in result.FetchedData)
            {
                var cachedData = ConvertUniversalisResponseToCachedData(kvp.Key, result.DataCenter, kvp.Value, worldData);
                entriesToCache.Add((kvp.Key, result.DataCenter, cachedData));
            }

            if (entriesToCache.Count > 0)
            {
                await SetManyAsync(entriesToCache);
                fetchedCount += entriesToCache.Count;

                // STEP 6: Verify the data was stored correctly
                verifiedCount += await VerifyStoredDataAsync(entriesToCache);
            }

            _logger?.LogInformation("[IndexedDbMarketCache] Fetched and cached {FetchedCount}/{RequestedCount} items from {DC} (verified total: {Verified})",
                result.FetchedData.Count, result.RequestedItemIds.Count, result.DataCenter, verifiedCount);
        }

        _logger?.LogInformation("[IndexedDbMarketCache] EnsurePopulatedAsync COMPLETE - Fetched: {Fetched}, Verified: {Verified}",
            fetchedCount, verifiedCount);

        LastDecisionSnapshot = CreateDecisionSnapshot(
            requests,
            requestedItemCount,
            preCleanupState,
            fetchedCount,
            verifiedCount,
            cleanedCount,
            cacheSizeEvictionCount,
            dataCenterFetchCallCount,
            maxAge,
            refreshRequestedPairs);

        return fetchedCount;
    }

    private async Task SetManyAsync(IReadOnlyCollection<(int itemId, string dataCenter, CachedMarketData data)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var batchEntries = entries
            .Select(entry => new IndexedDbMarketCacheBatchEntry
            {
                Key = GetKey(entry.itemId, entry.dataCenter),
                Data = new IndexedDbMarketCacheEntry
                {
                    Key = GetKey(entry.itemId, entry.dataCenter),
                    ItemId = entry.itemId,
                    DataCenter = entry.dataCenter,
                    FetchedAtUnix = entry.data.FetchedAtUnix,
                    LastUploadTimeUnixMilliseconds = entry.data.LastUploadTimeUnixMilliseconds,
                    DcAvgPrice = entry.data.DCAveragePrice,
                    HqAvgPrice = entry.data.HQAveragePrice,
                    Worlds = entry.data.Worlds
                }
            })
            .ToList();

        try
        {
            await _jsRuntime.InvokeVoidAsync("IndexedDB.saveMarketDataBatch", batchEntries);
            _logger?.LogDebug("[IndexedDbMarketCache] Stored market data batch with {Count} entries", entries.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error storing market data batch with {Count} entries", entries.Count);
        }
    }

    private async Task<RequestedCacheState> AnalyzeRequestedCacheAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        TimeSpan maxAge)
    {
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow) - (long)maxAge.TotalSeconds;
        var requestsByKey = new Dictionary<string, (int itemId, string dataCenter)>();

        foreach (var request in requests)
        {
            requestsByKey.TryAdd(GetKey(request.itemId, request.dataCenter), request);
        }

        try
        {
            var entries = await _jsRuntime.InvokeAsync<List<IndexedDbMarketCacheEntry>>(
                "IndexedDB.loadMarketDataBulk",
                requestsByKey.Keys.ToArray(),
                long.MinValue);

            var presentKeys = new HashSet<string>(StringComparer.Ordinal);
            var freshKeys = new HashSet<string>(StringComparer.Ordinal);
            var staleKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in entries ?? new List<IndexedDbMarketCacheEntry>())
            {
                var key = string.IsNullOrWhiteSpace(entry.Key)
                    ? GetKey(entry.ItemId, entry.DataCenter)
                    : entry.Key;

                if (!requestsByKey.ContainsKey(key))
                {
                    continue;
                }

                presentKeys.Add(key);
                if (entry.FetchedAtUnix <= cutoffUnix)
                {
                    staleKeys.Add(key);
                }
                else
                {
                    freshKeys.Add(key);
                }
            }

            return new RequestedCacheState(
                freshKeys.Count,
                staleKeys.Count,
                requestsByKey.Count - presentKeys.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error analyzing requested cache state");
            return new RequestedCacheState(0, 0, requestsByKey.Count);
        }
    }

    private static MarketCacheDecisionSnapshot CreateDecisionSnapshot(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        int requestedItemCount,
        RequestedCacheState preCleanupState,
        int fetchedCount,
        int verifiedCount,
        int cleanedCount,
        int cacheSizeEvictionCount,
        int dataCenterFetchCallCount,
        TimeSpan? maxAge,
        bool refreshRequestedPairs)
    {
        return new MarketCacheDecisionSnapshot
        {
            RequestedItemCount = requestedItemCount,
            RequestedPairCount = requests.Count,
            FreshHitCount = preCleanupState.FreshHitCount,
            StaleExistingEntryCount = preCleanupState.StaleEntryCount,
            MissingEntryCount = preCleanupState.MissingEntryCount,
            OrdinaryFetchedPairCount = refreshRequestedPairs ? 0 : fetchedCount,
            ForcedRefreshPairCount = refreshRequestedPairs ? fetchedCount : 0,
            DataCenterFetchCallCount = dataCenterFetchCallCount,
            CleanupStaleDeletionCount = cleanedCount,
            CacheSizeEvictionCount = cacheSizeEvictionCount,
            VerificationFailureCount = Math.Max(0, fetchedCount - verifiedCount),
            MaxAge = maxAge,
            RefreshRequestedPairs = refreshRequestedPairs,
            Trigger = refreshRequestedPairs ? "refresh-requested-pairs" : "ensure-populated"
        };
    }

    private async Task<List<DataCenterFetchResult>> FetchDataCentersAsync(
        IReadOnlyCollection<IGrouping<string, (int itemId, string dataCenter)>> dataCenterGroups,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(MaxDataCenterFetchConcurrency);
        var tasks = dataCenterGroups.Select(async dcGroup =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var dc = dcGroup.Key;
                var itemIds = dcGroup.Select(x => x.itemId).ToList();
                progress?.Report($"Fetching {itemIds.Count} items from {dc}...");

                var fetchedData = await _universalisService.GetMarketDataBulkAsync(dc, itemIds, useParallel: true, ct: ct);
                return new DataCenterFetchResult(dc, itemIds, fetchedData);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[IndexedDbMarketCache] Failed to fetch items from {DC}", dcGroup.Key);
                // Continue with other DCs - partial failure is acceptable
                return new DataCenterFetchResult(dcGroup.Key, dcGroup.Select(x => x.itemId).ToList(), new Dictionary<int, UniversalisResponse>());
            }
            finally
            {
                semaphore.Release();
            }
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    private async Task<WorldData?> GetWorldDataForCachingAsync(CancellationToken ct)
    {
        try
        {
            return await _universalisService.GetWorldDataAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[IndexedDbMarketCache] World metadata unavailable; caching data without per-world upload mapping");
            return null;
        }
    }

    private sealed record DataCenterFetchResult(
        string DataCenter,
        IReadOnlyCollection<int> RequestedItemIds,
        Dictionary<int, UniversalisResponse> FetchedData);

    private sealed record RequestedCacheState(
        int FreshHitCount,
        int StaleEntryCount,
        int MissingEntryCount);

    /// <summary>
    /// Verifies that data was stored correctly by reading it back.
    /// </summary>
    private async Task<bool> VerifyStoredDataAsync(int itemId, string dataCenter, long expectedUnixTimestamp)
    {
        try
        {
            var key = GetKey(itemId, dataCenter);
            var entry = await _jsRuntime.InvokeAsync<IndexedDbMarketCacheEntry?>("IndexedDB.loadMarketData", key);

            if (entry == null)
            {
                _logger?.LogWarning("[IndexedDbMarketCache] Verification failed - entry missing for {ItemId}@{DC}", itemId, dataCenter);
                return false;
            }

            // Allow 1 second tolerance for timestamp comparison
            var timestampMatch = Math.Abs(entry.FetchedAtUnix - expectedUnixTimestamp) <= 1;

            if (!timestampMatch)
            {
                _logger?.LogWarning("[IndexedDbMarketCache] Verification failed - timestamp mismatch for {ItemId}@{DC} (expected: {Expected}, got: {Actual})",
                    itemId, dataCenter, expectedUnixTimestamp, entry.FetchedAtUnix);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Verification error for {ItemId}@{DC}", itemId, dataCenter);
            return false;
        }
    }

    private async Task<int> VerifyStoredDataAsync(
        IReadOnlyCollection<(int itemId, string dataCenter, CachedMarketData data)> expectedEntries)
    {
        if (expectedEntries.Count == 0)
        {
            return 0;
        }

        try
        {
            var expectedByKey = new Dictionary<string, (int itemId, string dataCenter, long fetchedAtUnix)>();
            foreach (var expected in expectedEntries)
            {
                expectedByKey.TryAdd(
                    GetKey(expected.itemId, expected.dataCenter),
                    (expected.itemId, expected.dataCenter, expected.data.FetchedAtUnix));
            }

            var storedEntries = await _jsRuntime.InvokeAsync<List<IndexedDbMarketCacheEntry>>(
                "IndexedDB.loadMarketDataBulk",
                expectedByKey.Keys.ToArray(),
                long.MinValue);

            var verifiedCount = 0;
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in storedEntries ?? new List<IndexedDbMarketCacheEntry>())
            {
                var key = string.IsNullOrWhiteSpace(entry.Key)
                    ? GetKey(entry.ItemId, entry.DataCenter)
                    : entry.Key;
                if (!expectedByKey.TryGetValue(key, out var expected))
                {
                    continue;
                }

                seenKeys.Add(key);
                var timestampMatch = Math.Abs(entry.FetchedAtUnix - expected.fetchedAtUnix) <= 1;
                if (timestampMatch)
                {
                    verifiedCount++;
                }
                else
                {
                    _logger?.LogWarning(
                        "[IndexedDbMarketCache] Verification failed - timestamp mismatch for {ItemId}@{DC} (expected: {Expected}, got: {Actual})",
                        expected.itemId,
                        expected.dataCenter,
                        expected.fetchedAtUnix,
                        entry.FetchedAtUnix);
                }
            }

            foreach (var missingKey in expectedByKey.Keys.Where(key => !seenKeys.Contains(key)))
            {
                var expected = expectedByKey[missingKey];
                _logger?.LogWarning(
                    "[IndexedDbMarketCache] Verification failed - entry missing for {ItemId}@{DC}",
                    expected.itemId,
                    expected.dataCenter);
            }

            return verifiedCount;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Bulk verification error for {Count} entries", expectedEntries.Count);
            return 0;
        }
    }

    public async Task<int> CleanupStaleAsync(TimeSpan maxAge)
    {
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow) - (long)maxAge.TotalSeconds;

        try
        {
            var deleted = await _jsRuntime.InvokeAsync<int>("IndexedDB.deleteStaleMarketData", cutoffUnix);
            if (deleted > 0)
            {
                _logger?.LogInformation("[IndexedDbMarketCache] Cleaned up {Count} stale entries (older than {MaxAge})", deleted, maxAge);
            }
            return deleted;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error cleaning up stale entries");
            return 0;
        }
    }

    /// <summary>
    /// Clears the oldest N entries from the cache (LRU eviction).
    /// </summary>
    public async Task<int> ClearOldestEntriesAsync(int count)
    {
        try
        {
            var deleted = await _jsRuntime.InvokeAsync<int>("IndexedDB.deleteOldestEntries", count);
            _logger?.LogWarning("[IndexedDbMarketCache] Emergency cleanup: removed {Deleted} oldest entries", deleted);
            return deleted;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error clearing oldest entries");
            return 0;
        }
    }

    public async Task<CacheStats> GetStatsAsync()
    {
        var cutoffUnix = DateTimeToUnix(DateTime.UtcNow.AddHours(-1));

        try
        {
            var stats = await _jsRuntime.InvokeAsync<IndexedDbCacheStats>("IndexedDB.getMarketCacheStats", cutoffUnix);

            return new CacheStats
            {
                TotalEntries = stats.Total,
                ValidEntries = stats.Valid,
                StaleEntries = stats.Stale,
                OldestEntry = stats.OldestUnix > 0 ? UnixToDateTimeOffset(stats.OldestUnix).DateTime : null,
                NewestEntry = stats.NewestUnix > 0 ? UnixToDateTimeOffset(stats.NewestUnix).DateTime : null,
                ApproximateSizeBytes = stats.SizeBytes
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[IndexedDbMarketCache] Error getting stats");
            return new CacheStats();
        }
    }

    private CachedMarketData ConvertUniversalisResponseToCachedData(
        int itemId,
        string dataCenter,
        UniversalisResponse response,
        WorldData? worldData)
    {
        _logger?.LogDebug("[IndexedDbMarketCache] Converting response for {ItemId}@{DC} with {ListingCount} listings",
            itemId, dataCenter, response.Listings.Count);

        var now = DateTime.UtcNow;
        var nowUnix = DateTimeToUnix(now);
        _logger?.LogDebug("[IndexedDbMarketCache] Setting FetchedAtUnix={Now} for {ItemId}@{DC}", nowUnix, itemId, dataCenter);

        return UniversalisMarketDataMapper.ToCachedMarketData(itemId, dataCenter, response, worldData, now);
    }
}

/// <summary>
/// Data structure for IndexedDB market cache entries.
/// Uses Unix timestamp (long) instead of DateTime to avoid serialization issues.
/// </summary>
public class IndexedDbMarketCacheEntry
{
    public string Key { get; set; } = string.Empty;
    public int ItemId { get; set; }
    public string DataCenter { get; set; } = string.Empty;
    public long FetchedAtUnix { get; set; }  // Unix timestamp in seconds
    public long? LastUploadTimeUnixMilliseconds { get; set; }
    public decimal DcAvgPrice { get; set; }
    public decimal? HqAvgPrice { get; set; }
    public List<CachedWorldData> Worlds { get; set; } = new();
}

public class IndexedDbMarketCacheBatchEntry
{
    public string Key { get; set; } = string.Empty;
    public IndexedDbMarketCacheEntry Data { get; set; } = new();
}

/// <summary>
/// Statistics returned from IndexedDB.
/// </summary>
public class IndexedDbCacheStats
{
    public int Total { get; set; }
    public int Valid { get; set; }
    public int Stale { get; set; }
    public long OldestUnix { get; set; }  // Unix timestamp
    public long NewestUnix { get; set; }  // Unix timestamp
    public long SizeBytes { get; set; }
}
