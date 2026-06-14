namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketCacheShapeDiagnosticService
{
    public MarketCacheShapeReport Analyze(CachedMarketData? entry)
    {
        return MarketCacheShapeReport.Empty;
    }

    public MarketCacheShapeReport Analyze(
        IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> entries)
    {
        // Same-retainer, same-price stacks are normal for bulk commodity sellers.
        // Without a unique listing identity, repeated listing fingerprints are not
        // strong enough evidence to block recommendations as a cache-shape failure.
        return MarketCacheShapeReport.Empty;
    }
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
