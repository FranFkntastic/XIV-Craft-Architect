using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class UniversalisMarketDataMapper
{
    public static CachedMarketData ToCachedMarketData(
        int itemId,
        string dataCenter,
        UniversalisResponse response,
        WorldData? worldData,
        DateTime fetchedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(response);

        var worldNameToId = worldData?.WorldIdToName
            .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var uploadTimesByWorldName = response.WorldUploadTimes
            .Where(pair => IsValidUnixMilliseconds(pair.Value))
            .Select(pair => worldData?.WorldIdToName.TryGetValue(pair.Key, out var worldName) == true
                ? (WorldName: worldName, WorldId: pair.Key, UploadTime: pair.Value)
                : (WorldName: null, WorldId: pair.Key, UploadTime: pair.Value))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.WorldName))
            .ToDictionary(
                pair => pair.WorldName!,
                pair => (pair.WorldId, pair.UploadTime),
                StringComparer.OrdinalIgnoreCase);

        var worlds = new List<CachedWorldData>();
        var listedWorldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var worldListing in response.Listings.GroupBy(listing => listing.WorldName))
        {
            var worldName = string.IsNullOrWhiteSpace(worldListing.Key)
                ? "Unknown"
                : worldListing.Key;
            listedWorldNames.Add(worldName);
            worldNameToId.TryGetValue(worldName, out var worldId);
            uploadTimesByWorldName.TryGetValue(worldName, out var uploadTime);

            worlds.Add(new CachedWorldData
            {
                WorldId = worldId > 0 ? worldId : null,
                WorldName = worldName,
                LastUploadTimeUnixMilliseconds = uploadTime.UploadTime > 0 ? uploadTime.UploadTime : null,
                Listings = worldListing.Select(listing => new CachedListing
                {
                    Quantity = listing.Quantity,
                    PricePerUnit = listing.PricePerUnit,
                    RetainerName = string.IsNullOrWhiteSpace(listing.RetainerName) ? "Unknown" : listing.RetainerName,
                    IsHq = listing.IsHq,
                    LastReviewTimeUnix = IsValidUnixSeconds(listing.LastReviewTimeUnix)
                        ? listing.LastReviewTimeUnix
                        : null
                }).ToList()
            });
        }

        foreach (var uploadTime in uploadTimesByWorldName)
        {
            if (listedWorldNames.Contains(uploadTime.Key))
            {
                continue;
            }

            worlds.Add(new CachedWorldData
            {
                WorldId = uploadTime.Value.WorldId,
                WorldName = uploadTime.Key,
                LastUploadTimeUnixMilliseconds = uploadTime.Value.UploadTime,
                Listings = []
            });
        }

        return new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            FetchedAt = fetchedAtUtc,
            LastUploadTimeUnixMilliseconds = IsValidUnixMilliseconds(response.LastUploadTimeUnixMilliseconds)
                ? response.LastUploadTimeUnixMilliseconds
                : null,
            DCAveragePrice = (decimal)(response.AveragePriceNq > 0 ? response.AveragePriceNq : response.AveragePrice),
            HQAveragePrice = response.AveragePriceHq > 0 ? (decimal)response.AveragePriceHq : null,
            Worlds = worlds
        };
    }

    private static bool IsValidUnixMilliseconds(long? value)
    {
        return value.HasValue && value.Value > 0;
    }

    private static bool IsValidUnixSeconds(long? value)
    {
        return value.HasValue && value.Value > 0;
    }
}
