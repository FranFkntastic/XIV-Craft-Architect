using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Core.Models;

public sealed class MarketEvidenceSet
{
    public MarketEvidenceSet(
        IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> entries,
        IReadOnlyList<(int itemId, string dataCenter)> requestedPairs,
        MarketFetchScope scope,
        IReadOnlyList<string> dataCenters,
        string selectedDataCenter,
        string selectedRegion,
        TimeSpan? maxAge,
        int fetchedCount,
        DateTime loadedAtUtc,
        MarketCacheDecisionSnapshot? cacheDecision = null)
    {
        Entries = entries;
        RequestedPairs = requestedPairs;
        Scope = scope;
        DataCenters = dataCenters;
        SelectedDataCenter = selectedDataCenter;
        SelectedRegion = selectedRegion;
        MaxAge = maxAge;
        FetchedCount = fetchedCount;
        LoadedAtUtc = loadedAtUtc;
        CacheDecision = cacheDecision;
        MissingRequests = requestedPairs
            .Where(pair => !entries.ContainsKey(pair))
            .ToList();
    }

    public IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> Entries { get; }

    public IReadOnlyList<(int itemId, string dataCenter)> RequestedPairs { get; }

    public IReadOnlyList<(int itemId, string dataCenter)> MissingRequests { get; }

    public MarketFetchScope Scope { get; }

    public IReadOnlyList<string> DataCenters { get; }

    public string SelectedDataCenter { get; }

    public string SelectedRegion { get; }

    public TimeSpan? MaxAge { get; }

    public int FetchedCount { get; }

    public DateTime LoadedAtUtc { get; }

    public MarketCacheDecisionSnapshot? CacheDecision { get; }

    public bool IsPartial => MissingRequests.Count > 0;

    public IReadOnlyList<CachedMarketData> GetEntriesForItem(int itemId)
    {
        return DataCenters
            .Select(dataCenter => Entries.TryGetValue((itemId, dataCenter), out var entry) ? entry : null)
            .Where(entry => entry != null)
            .Cast<CachedMarketData>()
            .ToList();
    }
}

public sealed record MarketCacheDecisionSnapshot
{
    public int RequestedItemCount { get; init; }

    public int RequestedPairCount { get; init; }

    public int FreshHitCount { get; init; }

    public int StaleExistingEntryCount { get; init; }

    public int MissingEntryCount { get; init; }

    public int OrdinaryFetchedPairCount { get; init; }

    public int SuspectRefreshPairCount { get; init; }

    public int ForcedRefreshPairCount { get; init; }

    public int? HttpChunkRequestCount { get; init; }

    public int DataCenterFetchCallCount { get; init; }

    public int SplitCount { get; init; }

    public int RateLimit429Count { get; init; }

    public int GatewayTimeout504Count { get; init; }

    public int CleanupStaleDeletionCount { get; init; }

    public int CacheSizeEvictionCount { get; init; }

    public int VerificationFailureCount { get; init; }

    public TimeSpan? MaxAge { get; init; }

    public bool ForceRefreshData { get; init; }

    public MarketFetchScope Scope { get; init; }

    public string SelectedDataCenter { get; init; } = string.Empty;

    public string SelectedRegion { get; init; } = string.Empty;

    public string Trigger { get; init; } = string.Empty;
}

public sealed class MarketAnalysisRequest
{
    public IReadOnlyList<MaterialAggregate> Items { get; init; } = Array.Empty<MaterialAggregate>();

    public MarketEvidenceSet Evidence { get; init; } = null!;

    public RecommendationMode RecommendationMode { get; init; } = RecommendationMode.MinimizeTotalCost;

    public MarketAnalysisConfig AnalysisConfig { get; init; } = new();

    public HashSet<string> BlacklistedWorlds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<MarketWorldKey> BlacklistedMarketWorlds { get; init; } = new();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedWorldsByDataCenter { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}
