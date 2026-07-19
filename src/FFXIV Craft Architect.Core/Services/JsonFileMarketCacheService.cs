using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// JSON file-backed <see cref="IMarketCacheService"/> for local processes
/// (e.g. Workshop Host appraisal) that need a persistent market cache on disk.
/// </summary>
public sealed class JsonFileMarketCacheService : IMarketCacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly UniversalisService _universalis;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _defaultMaxAge = MarketEvidencePolicyDefaults.ReusableCacheMaxAge;
    private Dictionary<string, CachedMarketData>? _cache;

    public JsonFileMarketCacheService(UniversalisService universalis)
        : this(
            universalis,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FFXIV_Craft_Architect",
                "Cache"))
    {
    }

    public JsonFileMarketCacheService(UniversalisService universalis, string rootDirectory)
    {
        _universalis = universalis ?? throw new ArgumentNullException(nameof(universalis));
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? throw new ArgumentException("Market cache root is required.", nameof(rootDirectory))
            : rootDirectory;
        CachePath = Path.Combine(RootDirectory, "market-cache.json");
    }

    public string RootDirectory { get; }

    public string CachePath { get; }

    public async Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var cache = await LoadCacheAsync();
        return cache.TryGetValue(GetKey(itemId, dataCenter), out var data) && !data.IsOlderThan(maxAge ?? _defaultMaxAge)
            ? data
            : null;
    }

    public async Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var cache = await LoadCacheAsync();
        if (!cache.TryGetValue(GetKey(itemId, dataCenter), out var data))
        {
            return (null, false);
        }

        return (data, data.IsOlderThan(maxAge ?? _defaultMaxAge));
    }

    public async Task<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>> GetManyAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        var cache = await LoadCacheAsync();
        var effectiveMaxAge = maxAge ?? _defaultMaxAge;
        var entries = new Dictionary<(int itemId, string dataCenter), CachedMarketData>();
        foreach (var request in requests)
        {
            if (cache.TryGetValue(GetKey(request.itemId, request.dataCenter), out var data) &&
                !data.IsOlderThan(effectiveMaxAge))
            {
                entries[request] = data;
            }
        }

        return entries;
    }

    public async Task SetAsync(int itemId, string dataCenter, CachedMarketData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var cache = await LoadCacheAsync();
        cache.TryGetValue(GetKey(itemId, dataCenter), out var retained);
        data = MarketEvidenceCacheMerger.PreferNewestWorldEvidence(retained, data);
        data.ItemId = itemId;
        data.DataCenter = dataCenter;
        cache[GetKey(itemId, dataCenter)] = data;
        await SaveCacheAsync(cache);
    }

    public async Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null) =>
        await GetAsync(itemId, dataCenter, maxAge) != null;

    public async Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        var cache = await LoadCacheAsync();
        var effectiveMaxAge = maxAge ?? _defaultMaxAge;
        return requests
            .Where(request => !cache.TryGetValue(GetKey(request.itemId, request.dataCenter), out var data) ||
                data.IsOlderThan(effectiveMaxAge))
            .ToList();
    }

    public async Task<int> CleanupStaleAsync(TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), maxAge, "Maximum cache age must be positive.");
        }

        var cache = await LoadCacheAsync();
        var staleKeys = cache
            .Where(pair => pair.Value.IsOlderThan(maxAge))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in staleKeys)
        {
            cache.Remove(key);
        }

        if (staleKeys.Count > 0)
        {
            await SaveCacheAsync(cache);
        }

        return staleKeys.Count;
    }

    public async Task<int> ClearAsync()
    {
        var cache = await LoadCacheAsync();
        var count = cache.Count;
        cache.Clear();
        await SaveCacheAsync(cache);
        return count;
    }

    public async Task<CacheStats> GetStatsAsync()
    {
        var cache = await LoadCacheAsync();
        var entries = cache.Values.ToList();
        return new CacheStats
        {
            TotalEntries = entries.Count,
            ValidEntries = entries.Count(entry => !entry.IsOlderThan(_defaultMaxAge)),
            StaleEntries = entries.Count(entry => entry.IsOlderThan(_defaultMaxAge)),
            OldestEntry = entries.Count == 0 ? null : entries.Min(entry => entry.FetchedAt),
            NewestEntry = entries.Count == 0 ? null : entries.Max(entry => entry.FetchedAt),
            ApproximateSizeBytes = File.Exists(CachePath) ? new FileInfo(CachePath).Length : 0
        };
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

        var missing = await GetMissingAsync(requests, maxAge);
        return await FetchAndStoreAsync(missing, progress, ct);
    }

    public Task<int> RefreshRequestedAsync(
        List<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        FetchAndStoreAsync(requests, progress, ct);

    private async Task<int> FetchAndStoreAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (requests.Count == 0)
        {
            return 0;
        }

        var fetchedAtUtc = DateTime.UtcNow;
        var worldData = await TryGetWorldDataAsync(ct);
        var fetchedCount = 0;
        foreach (var group in requests.GroupBy(request => request.dataCenter, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var itemIds = group.Select(request => request.itemId).Distinct().ToArray();
            progress?.Report($"Fetching {itemIds.Length:N0} market item{(itemIds.Length == 1 ? string.Empty : "s")} for {group.Key}...");
            var responses = await _universalis.GetMarketDataBulkAsync(group.Key, itemIds, useParallel: true, ct);
            foreach (var itemId in itemIds)
            {
                if (!responses.TryGetValue(itemId, out var response))
                {
                    continue;
                }

                var cached = UniversalisMarketDataMapper.ToCachedMarketData(itemId, group.Key, response, worldData, fetchedAtUtc);
                await SetAsync(itemId, group.Key, cached);
                fetchedCount++;
            }
        }

        return fetchedCount;
    }

    private async Task<WorldData?> TryGetWorldDataAsync(CancellationToken ct)
    {
        try
        {
            return await _universalis.GetWorldDataAsync(ct);
        }
        catch
        {
            return _universalis.GetCachedWorldData();
        }
    }

    private async Task<Dictionary<string, CachedMarketData>> LoadCacheAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_cache != null)
            {
                return _cache;
            }

            if (!File.Exists(CachePath))
            {
                _cache = new Dictionary<string, CachedMarketData>(StringComparer.OrdinalIgnoreCase);
                return _cache;
            }

            await using var stream = File.OpenRead(CachePath);
            _cache = await JsonSerializer.DeserializeAsync<Dictionary<string, CachedMarketData>>(stream, JsonOptions)
                ?? new Dictionary<string, CachedMarketData>(StringComparer.OrdinalIgnoreCase);
            _cache = new Dictionary<string, CachedMarketData>(_cache, StringComparer.OrdinalIgnoreCase);
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveCacheAsync(Dictionary<string, CachedMarketData> cache)
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var temporaryPath = $"{CachePath}.{Guid.NewGuid():N}.tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, cache, JsonOptions);
            }

            File.Move(temporaryPath, CachePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string GetKey(int itemId, string dataCenter) =>
        $"{itemId}@{dataCenter.Trim()}";
}
