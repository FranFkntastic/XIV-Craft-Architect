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
        bool forceRefreshData = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        bool skipCachePopulation = false)
    {
        ArgumentNullException.ThrowIfNull(marketCache);
        if (forceRefreshData && skipCachePopulation)
        {
            throw new ArgumentException(
                "Forced refresh cannot skip cache population.",
                nameof(skipCachePopulation));
        }
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAge),
                maxAge,
                "Use forceRefreshData when fresh data is required for the requested market-evidence pairs.");
        }

        var distinctItemIds = itemIds.Distinct().ToList();
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(scope, selectedDataCenter, selectedRegion);
        var requests = distinctItemIds
            .SelectMany(itemId => dataCenters.Select(dataCenter => (itemId, dataCenter)))
            .ToList();

        var forceRefreshStartedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fetchedCount = requests.Count == 0 || skipCachePopulation
            ? 0
            : forceRefreshData
                ? await marketCache.RefreshRequestedAsync(requests, progress, ct)
                : await marketCache.EnsurePopulatedAsync(requests, maxAge, progress, ct);
        var cacheDecision = marketCache is IMarketCacheDiagnosticsProvider diagnosticsProvider
            ? diagnosticsProvider.LastDecisionSnapshot
            : null;
        cacheDecision = cacheDecision is null
            ? null
            : cacheDecision with
            {
                Scope = scope,
                SelectedDataCenter = selectedDataCenter,
                SelectedRegion = selectedRegion
            };
        var readMaxAge = forceRefreshData ? null : maxAge;
        var entries = requests.Count == 0
            ? new Dictionary<(int itemId, string dataCenter), CachedMarketData>()
            : await marketCache.GetManyAsync(requests, readMaxAge);
        if (forceRefreshData)
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
            DateTime.UtcNow,
            cacheDecision);
    }
}
