namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketEvidenceCacheMerger
{
    public static CachedMarketData PreferNewestWorldEvidence(
        CachedMarketData? retained,
        CachedMarketData incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (retained == null || retained.Worlds.Count == 0 || incoming.Worlds.Count == 0)
        {
            return incoming;
        }

        for (var index = 0; index < incoming.Worlds.Count; index++)
        {
            var incomingWorld = incoming.Worlds[index];
            var retainedWorld = retained.Worlds.FirstOrDefault(world => SameWorld(world, incomingWorld));
            if (retainedWorld == null)
            {
                continue;
            }

            if (GetEvidenceTime(retainedWorld, retained.FetchedAt) > GetEvidenceTime(incomingWorld, incoming.FetchedAt))
            {
                incoming.Worlds[index] = retainedWorld;
            }
        }

        return incoming;
    }

    private static bool SameWorld(CachedWorldData left, CachedWorldData right) =>
        left.WorldId.HasValue && right.WorldId.HasValue
            ? left.WorldId.Value == right.WorldId.Value
            : string.Equals(left.WorldName, right.WorldName, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset GetEvidenceTime(CachedWorldData world, DateTime fetchedAt)
    {
        var timestamp = world.ObservedAtUnixMilliseconds ?? world.LastUploadTimeUnixMilliseconds;
        return timestamp.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value)
            : new DateTimeOffset(CacheTimeHelper.NormalizeToUtc(fetchedAt));
    }
}
