using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketScopedPriceLoader
{
    public static async Task<Dictionary<int, UniversalisResponse>> LoadResponsesAsync(
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
        if (distinctItemIds.Count == 0)
        {
            return new Dictionary<int, UniversalisResponse>();
        }

        var dataCenters = MarketFetchScopeResolver.GetDataCenters(scope, selectedDataCenter, selectedRegion);
        var requests = distinctItemIds
            .SelectMany(itemId => dataCenters.Select(dataCenter => (itemId, dataCenter)))
            .ToList();

        await marketCache.EnsurePopulatedAsync(requests, maxAge, progress, ct);

        var responses = new Dictionary<int, UniversalisResponse>();
        foreach (var itemId in distinctItemIds)
        {
            var candidates = new List<CachedMarketData>();
            foreach (var dataCenter in dataCenters)
            {
                var cached = await marketCache.GetAsync(itemId, dataCenter, maxAge);
                if (cached != null)
                {
                    candidates.Add(cached);
                }
            }

            var best = candidates
                .OrderBy(GetComparableAveragePrice)
                .ThenBy(candidate => candidate.DataCenter, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (best != null)
            {
                responses[itemId] = ConvertFromCachedData(best);
            }
        }

        return responses;
    }

    private static decimal GetComparableAveragePrice(CachedMarketData data)
    {
        if (data.DCAveragePrice > 0)
        {
            return data.DCAveragePrice;
        }

        var listingPrice = data.Worlds
            .SelectMany(world => world.Listings)
            .Select(listing => listing.PricePerUnit)
            .Where(price => price > 0)
            .DefaultIfEmpty(long.MaxValue)
            .Min();

        return listingPrice == long.MaxValue ? decimal.MaxValue : listingPrice;
    }

    private static UniversalisResponse ConvertFromCachedData(CachedMarketData cached)
    {
        var listings = new List<MarketListing>();
        foreach (var world in cached.Worlds)
        {
            listings.AddRange(world.Listings.Select(listing => new MarketListing
            {
                PricePerUnit = listing.PricePerUnit,
                Quantity = listing.Quantity,
                WorldName = world.WorldName,
                DataCenterName = cached.DataCenter,
                RetainerName = listing.RetainerName,
                IsHq = listing.IsHq
            }));
        }

        return new UniversalisResponse
        {
            ItemId = cached.ItemId,
            DataCenterName = cached.DataCenter,
            Listings = listings,
            AveragePrice = (double)GetComparableAveragePrice(cached),
            AveragePriceNq = (double)GetComparableAveragePrice(cached),
            AveragePriceHq = cached.HQAveragePrice.HasValue ? (double)cached.HQAveragePrice.Value : 0
        };
    }
}
