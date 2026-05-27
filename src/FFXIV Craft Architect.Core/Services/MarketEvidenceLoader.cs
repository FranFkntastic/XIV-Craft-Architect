using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketEvidenceLoader
{
    public static async Task<MarketEvidenceSet> LoadAsync(
        IMarketCacheService marketCache,
        IEnumerable<int> itemIds,
        MarketFetchScope scope,
        string selectedDataCenter,
        string selectedRegion,
        TimeSpan? maxAge = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(marketCache);

        var distinctItemIds = itemIds.Distinct().ToList();
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(scope, selectedDataCenter, selectedRegion);
        var requests = distinctItemIds
            .SelectMany(itemId => dataCenters.Select(dataCenter => (itemId, dataCenter)))
            .ToList();

        var forceRefreshStartedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fetchedCount = requests.Count == 0
            ? 0
            : await marketCache.EnsurePopulatedAsync(requests, maxAge, progress, ct);
        var readMaxAge = maxAge == TimeSpan.Zero ? null : maxAge;
        var entries = requests.Count == 0
            ? new Dictionary<(int itemId, string dataCenter), CachedMarketData>()
            : await marketCache.GetManyAsync(requests, readMaxAge);
        if (maxAge == TimeSpan.Zero)
        {
            entries = entries
                .Where(entry => entry.Value.FetchedAtUnix >= forceRefreshStartedAtUnix)
                .ToDictionary(entry => entry.Key, entry => entry.Value);
        }

        return new MarketEvidenceSet(
            entries,
            requests,
            scope,
            dataCenters,
            selectedDataCenter,
            selectedRegion,
            maxAge,
            fetchedCount,
            DateTime.UtcNow);
    }
}
