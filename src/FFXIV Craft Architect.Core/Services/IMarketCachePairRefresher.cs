using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Refetches exactly the given item/data-center pairs for an interactive page, storing each
/// answer as it lands. Stored content is the same as <see cref="IMarketCacheService.RefreshRequestedAsync"/>.
/// </summary>
public interface IMarketCachePairRefresher
{
    Task<MarketPairRefreshResult> RefreshPairsAsync(
        IReadOnlyCollection<(int itemId, string dataCenter)> pairs,
        CancellationToken ct = default);
}

/// <param name="Stored">What the cache now holds for each refreshed pair.</param>
/// <param name="Failed">Pairs Universalis did not answer, with the failure per data center.</param>
public sealed record MarketPairRefreshResult(
    IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> Stored,
    IReadOnlyList<(int itemId, string dataCenter)> Failed,
    IReadOnlyDictionary<string, string> FailureByDataCenter);
