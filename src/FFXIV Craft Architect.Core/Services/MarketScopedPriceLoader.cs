using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketScopedPriceLoader
{
    public static async Task<Dictionary<int, CachedMarketData>> LoadBestEntriesAsync(
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

        var evidence = await MarketEvidenceLoader.LoadAsync(
            marketCache,
            itemIds,
            scope,
            selectedDataCenter,
            selectedRegion,
            maxAge,
            progress,
            ct);

        var entries = new Dictionary<int, CachedMarketData>();
        foreach (var itemId in evidence.RequestedPairs.Select(pair => pair.itemId).Distinct())
        {
            var best = evidence.GetEntriesForItem(itemId)
                .OrderBy(GetComparableAveragePrice)
                .ThenBy(candidate => candidate.DataCenter, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (best != null)
            {
                entries[itemId] = best;
            }
        }

        return entries;
    }

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
        var entries = await LoadBestEntriesAsync(
            marketCache,
            itemIds,
            scope,
            selectedDataCenter,
            selectedRegion,
            maxAge,
            progress,
            ct);

        return entries.ToDictionary(
            pair => pair.Key,
            pair => ConvertFromCachedData(pair.Value));
    }

    public static decimal GetComparableAveragePrice(CachedMarketData data)
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
                IsHq = listing.IsHq,
                LastReviewTimeUnix = listing.LastReviewTimeUnix
            }));
        }

        return new UniversalisResponse
        {
            ItemId = cached.ItemId,
            DataCenterName = cached.DataCenter,
            LastUploadTimeUnixMilliseconds = cached.LastUploadTimeUnixMilliseconds,
            WorldUploadTimes = cached.Worlds
                .Where(world => world.WorldId.HasValue && world.LastUploadTimeUnixMilliseconds.HasValue)
                .ToDictionary(
                    world => world.WorldId!.Value,
                    world => world.LastUploadTimeUnixMilliseconds!.Value),
            Listings = listings,
            AveragePrice = (double)GetComparableAveragePrice(cached),
            AveragePriceNq = (double)GetComparableAveragePrice(cached),
            AveragePriceHq = cached.HQAveragePrice.HasValue ? (double)cached.HQAveragePrice.Value : 0
        };
    }
}
