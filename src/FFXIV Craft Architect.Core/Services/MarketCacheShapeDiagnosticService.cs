namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketCacheShapeDiagnosticService
{
    private const int MinimumRepeatedFingerprintCount = 25;
    private const decimal MinimumRepeatedFingerprintShare = 0.5m;

    public MarketCacheShapeReport Analyze(CachedMarketData? entry)
    {
        if (entry is null)
        {
            return MarketCacheShapeReport.Empty;
        }

        return Analyze(
            new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(entry.ItemId, entry.DataCenter)] = entry
            });
    }

    public MarketCacheShapeReport Analyze(
        IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> entries)
    {
        var issues = new List<MarketCacheShapeIssue>();

        foreach (var ((itemId, dataCenter), entry) in entries)
        {
            foreach (var world in entry.Worlds)
            {
                AddRepeatedListingFingerprintIssues(itemId, dataCenter, world, issues);
            }
        }

        return issues.Count == 0
            ? MarketCacheShapeReport.Empty
            : new MarketCacheShapeReport(issues);
    }

    private static void AddRepeatedListingFingerprintIssues(
        int itemId,
        string dataCenter,
        CachedWorldData world,
        List<MarketCacheShapeIssue> issues)
    {
        if (world.Listings.Count < MinimumRepeatedFingerprintCount)
        {
            return;
        }

        var listingsWithMeaningfulIdentity = world.Listings
            .Where(HasMeaningfulListingIdentity)
            .ToList();
        if (listingsWithMeaningfulIdentity.Count < MinimumRepeatedFingerprintCount)
        {
            return;
        }

        var repeatedFingerprint = listingsWithMeaningfulIdentity
            .GroupBy(CreateFingerprint)
            .Select(group => new
            {
                Fingerprint = group.Key,
                Count = group.Count()
            })
            .Where(group => group.Count >= MinimumRepeatedFingerprintCount)
            .OrderByDescending(group => group.Count)
            .FirstOrDefault();

        if (repeatedFingerprint is null)
        {
            return;
        }

        var repeatedShare = repeatedFingerprint.Count / (decimal)listingsWithMeaningfulIdentity.Count;
        if (repeatedShare < MinimumRepeatedFingerprintShare)
        {
            return;
        }

        var message =
            $"Item {itemId} on {dataCenter}/{world.WorldName} has {repeatedFingerprint.Count} repeated cached listings " +
            $"with the same price, quantity, retainer ({repeatedFingerprint.Fingerprint.RetainerName}), HQ flag, and review time. " +
            "This may indicate an inflated cache payload.";
        issues.Add(new MarketCacheShapeIssue(
            MarketCacheShapeIssueKind.RepeatedListingFingerprint,
            MarketCacheShapeSeverity.Warning,
            message,
            itemId,
            dataCenter,
            world.WorldName,
            repeatedFingerprint.Count,
            world.Listings.Count));
    }

    private static bool HasMeaningfulListingIdentity(CachedListing listing)
    {
        return listing.LastReviewTimeUnix.HasValue &&
            !string.IsNullOrWhiteSpace(listing.RetainerName) &&
            !string.Equals(listing.RetainerName, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static CachedListingFingerprint CreateFingerprint(CachedListing listing)
    {
        return new CachedListingFingerprint(
            listing.Quantity,
            listing.PricePerUnit,
            listing.RetainerName,
            listing.IsHq,
            listing.LastReviewTimeUnix);
    }

    private sealed record CachedListingFingerprint(
        int Quantity,
        long PricePerUnit,
        string RetainerName,
        bool IsHq,
        long? LastReviewTimeUnix);
}

public sealed record MarketCacheShapeReport(IReadOnlyList<MarketCacheShapeIssue> Issues)
{
    public static MarketCacheShapeReport Empty { get; } = new(Array.Empty<MarketCacheShapeIssue>());

    public bool HasIssues => Issues.Count > 0;
}

public sealed record MarketCacheShapeIssue(
    MarketCacheShapeIssueKind Kind,
    MarketCacheShapeSeverity Severity,
    string Message,
    int ItemId,
    string DataCenter,
    string WorldName,
    int RepeatedListingCount,
    int TotalWorldListingCount);

public enum MarketCacheShapeIssueKind
{
    RepeatedListingFingerprint
}

public enum MarketCacheShapeSeverity
{
    Info,
    Warning,
    Error
}
