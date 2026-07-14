using System.Collections.Concurrent;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Web-compatible in-memory implementation of IMarketCacheService.
/// Stores market data in memory only (no persistence across page reloads).
/// For the Web app, this is sufficient since we fetch fresh data each session.
/// </summary>
public class WebMarketCacheService : IMarketCacheService
{
    private readonly ConcurrentDictionary<string, CachedMarketData> _cache = new();
    private readonly TimeSpan _defaultMaxAge = MarketEvidencePolicyDefaults.ReusableCacheMaxAge;

    private static string GetKey(int itemId, string dataCenter) => $"{itemId}@{dataCenter}";

    public Task<CachedMarketData?> GetAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var key = GetKey(itemId, dataCenter);
        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);

        if (_cache.TryGetValue(key, out var data) && data.FetchedAt > cutoff)
        {
            return Task.FromResult<CachedMarketData?>(data);
        }

        return Task.FromResult<CachedMarketData?>(null);
    }

    public Task<(CachedMarketData? Data, bool IsStale)> GetWithStaleAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var key = GetKey(itemId, dataCenter);
        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);

        if (_cache.TryGetValue(key, out var data))
        {
            var isStale = data.FetchedAt <= cutoff;
            return Task.FromResult<(CachedMarketData?, bool)>((data, isStale));
        }

        return Task.FromResult<(CachedMarketData?, bool)>((null, false));
    }

    public Task<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>> GetManyAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);
        var entries = new Dictionary<(int itemId, string dataCenter), CachedMarketData>();

        foreach (var (itemId, dataCenter) in requests)
        {
            var key = GetKey(itemId, dataCenter);
            if (_cache.TryGetValue(key, out var data) && data.FetchedAt > cutoff)
            {
                entries[(itemId, dataCenter)] = data;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData>>(entries);
    }

    public Task SetAsync(int itemId, string dataCenter, CachedMarketData data)
    {
        var key = GetKey(itemId, dataCenter);
        _cache.TryGetValue(key, out var retained);
        _cache[key] = MarketEvidenceCacheMerger.PreferNewestWorldEvidence(retained, data);
        return Task.CompletedTask;
    }

    public Task<bool> HasValidCacheAsync(int itemId, string dataCenter, TimeSpan? maxAge = null)
    {
        var key = GetKey(itemId, dataCenter);
        var cutoff = DateTime.UtcNow - (maxAge ?? _defaultMaxAge);

        return Task.FromResult(_cache.TryGetValue(key, out var data) && data.FetchedAt > cutoff);
    }

    public Task<List<(int itemId, string dataCenter)>> GetMissingAsync(
        List<(int itemId, string dataCenter)> requests,
        TimeSpan? maxAge = null)
    {
        var missing = new List<(int, string)>();

        foreach (var (itemId, dataCenter) in requests)
        {
            if (!HasValidCacheAsync(itemId, dataCenter, maxAge).Result)
            {
                missing.Add((itemId, dataCenter));
            }
        }

        return Task.FromResult(missing);
    }

    public Task<int> CleanupStaleAsync(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var staleKeys = _cache.Where(kvp => kvp.Value.FetchedAt < cutoff).Select(kvp => kvp.Key).ToList();

        foreach (var key in staleKeys)
        {
            _cache.TryRemove(key, out _);
        }

        return Task.FromResult(staleKeys.Count);
    }

    public Task<CacheStats> GetStatsAsync()
    {
        var now = DateTime.UtcNow;
        var entries = _cache.Values.ToList();

        return Task.FromResult(new CacheStats
        {
            TotalEntries = entries.Count,
            ValidEntries = entries.Count(e => now - e.FetchedAt < _defaultMaxAge),
            StaleEntries = entries.Count(e => now - e.FetchedAt >= _defaultMaxAge),
            OldestEntry = entries.Any() ? entries.Min(e => e.FetchedAt) : null,
            NewestEntry = entries.Any() ? entries.Max(e => e.FetchedAt) : null,
            ApproximateSizeBytes = entries.Count * 1024 // Rough estimate
        });
    }

    public Task<int> EnsurePopulatedAsync(
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

        // This cache does not fetch by itself; orchestration services decide when
        // to populate it from Universalis.
        var missing = GetMissingAsync(requests, maxAge).Result;
        return Task.FromResult(missing.Count);
    }

    public Task<int> RefreshRequestedAsync(
        List<(int itemId, string dataCenter)> requests,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // This in-memory cache does not fetch by itself; every requested pair
        // remains the caller's responsibility to refresh.
        return Task.FromResult(requests.Count);
    }
}
