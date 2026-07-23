using System.Collections.Concurrent;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Worker-owned transient market cache. Missing evidence is fetched in serialized
/// data-center batches so one user action cannot create a fan-out of competing
/// Universalis requests. Once accepted evidence is published into the canonical
/// session, the raw cache is released to avoid retaining two listing corpora.
/// </summary>
public sealed class WorkerMarketCacheService : IMarketCacheService
{
    private readonly ConcurrentDictionary<string, CachedMarketData> _cache = new();
    private readonly IUniversalisService _universalis;
    private readonly TimeSpan _defaultMaxAge = MarketEvidencePolicyDefaults.ReusableCacheMaxAge;

    public WorkerMarketCacheService(IUniversalisService universalis)
    {
        _universalis = universalis ?? throw new ArgumentNullException(nameof(universalis));
    }

    public void Clear() => _cache.Clear();

    public Task<CachedMarketData?> GetAsync(
        int itemId,
        string dataCenter,
        TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);
        return Task.FromResult(
            _cache.TryGetValue(Key(itemId, dataCenter), out var data) &&
            data.FetchedAt > cutoff
                ? data
                : null);
    }

    public Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(
        int itemId,
        string dataCenter,
        TimeSpan? maxAge = null)
    {
        if (!_cache.TryGetValue(Key(itemId, dataCenter), out var data))
        {
            return Task.FromResult<(CachedMarketData?, bool)>((null, false));
        }

        return Task.FromResult<(CachedMarketData?, bool)>(
            (data, data.FetchedAt <= DateTime.UtcNow - (maxAge ?? _defaultMaxAge)));
    }

    public Task<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>> GetManyAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);
        IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> result =
            requests
                .Distinct()
                .Select(request => (
                    request,
                    Found: _cache.TryGetValue(
                        Key(request.itemId, request.dataCenter),
                        out var data),
                    Data: data))
                .Where(entry => entry.Found && entry.Data!.FetchedAt > cutoff)
                .ToDictionary(entry => entry.request, entry => entry.Data!);
        return Task.FromResult(result);
    }

    public Task SetAsync(int itemId, string dataCenter, CachedMarketData data)
    {
        SetCore(itemId, dataCenter, data);
        return Task.CompletedTask;
    }

    public async Task<bool> HasValidCacheAsync(
        int itemId,
        string dataCenter,
        TimeSpan? maxAge = null) =>
        await GetAsync(itemId, dataCenter, maxAge) is not null;

    public async Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        var found = await GetManyAsync(requests, maxAge);
        return requests.Distinct().Where(request => !found.ContainsKey(request)).ToList();
    }

    public Task<int> CleanupStaleAsync(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var stale = _cache
            .Where(entry => entry.Value.FetchedAt <= cutoff)
            .Select(entry => entry.Key)
            .ToArray();
        foreach (var key in stale)
        {
            _cache.TryRemove(key, out _);
        }

        return Task.FromResult(stale.Length);
    }

    public Task<CacheStats> GetStatsAsync()
    {
        var entries = _cache.Values.ToArray();
        var valid = entries.Count(entry => !entry.IsOlderThan(_defaultMaxAge));
        return Task.FromResult(new CacheStats
        {
            TotalEntries = entries.Length,
            ValidEntries = valid,
            StaleEntries = entries.Length - valid,
            OldestEntry = entries.Length == 0 ? null : entries.Min(entry => entry.FetchedAt),
            NewestEntry = entries.Length == 0 ? null : entries.Max(entry => entry.FetchedAt),
            ApproximateSizeBytes = entries.Sum(entry =>
                entry.Worlds.Sum(world => world.Listings.Count) * 96L)
        });
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
                "Use RefreshRequestedAsync when fresh data is required.");
        }

        return await FetchAsync(await GetMissingAsync(requests, maxAge), progress, ct);
    }

    public Task<int> RefreshRequestedAsync(
        List<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        FetchAsync(requests.Distinct().ToList(), progress, ct);

    private async Task<int> FetchAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var fetched = 0;
        var worldData = await _universalis.GetWorldDataAsync(ct);
        foreach (var group in requests
                     .GroupBy(request => request.dataCenter, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var itemIds = group.Select(request => request.itemId).Distinct().ToArray();
            progress?.Report($"Loading {itemIds.Length} market items from {group.Key}...");
            var responses = await _universalis.GetMarketDataBulkAsync(
                group.Key,
                itemIds,
                useParallel: false,
                ct);
            var fetchedAt = DateTime.UtcNow;
            foreach (var (itemId, response) in responses)
            {
                SetCore(
                    itemId,
                    group.Key,
                    UniversalisMarketDataMapper.ToCachedMarketData(
                        itemId,
                        group.Key,
                        response,
                        worldData,
                        fetchedAt));
                fetched++;
            }
        }

        return fetched;
    }

    private void SetCore(int itemId, string dataCenter, CachedMarketData data)
    {
        var key = Key(itemId, dataCenter);
        _cache.TryGetValue(key, out var retained);
        _cache[key] = MarketEvidenceCacheMerger.PreferNewestWorldEvidence(retained, data);
    }

    private static string Key(int itemId, string dataCenter) =>
        $"{itemId}@{dataCenter.Trim().ToUpperInvariant()}";
}
