using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public interface IMarketAnalysisDetailHydrationService
{
    Task<MarketAnalysisWorldDetailHydrationResult> LoadWorldDetailAsync(
        Guid? publicationId,
        int itemId,
        WorldMarketAnalysis world,
        CancellationToken cancellationToken = default);
}

public sealed class MarketAnalysisDetailHydrationService : IMarketAnalysisDetailHydrationService
{
    private const int MaxCachedWorldDetails = 64;

    private readonly IMarketIntelligenceStore _store;
    private readonly Dictionary<MarketAnalysisWorldDetailHydrationCacheKey, MarketAnalysisWorldDetailHydrationResult> _cache = new();
    private readonly Queue<MarketAnalysisWorldDetailHydrationCacheKey> _cacheOrder = new();

    public MarketAnalysisDetailHydrationService(IMarketIntelligenceStore store)
    {
        _store = store;
    }

    public async Task<MarketAnalysisWorldDetailHydrationResult> LoadWorldDetailAsync(
        Guid? publicationId,
        int itemId,
        WorldMarketAnalysis world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (world.Listings.Count > 0)
        {
            return MarketAnalysisWorldDetailHydrationResult.Loaded(
                world.Listings,
                world.PriceBands,
                fromEmbeddedHotState: true);
        }

        if (!publicationId.HasValue || publicationId.Value == Guid.Empty)
        {
            return MarketAnalysisWorldDetailHydrationResult.Unavailable(
                MarketIntelligenceDetailAvailability.SummaryOnly,
                "Listing detail is not available for this summary.");
        }

        var worldKey = new MarketWorldKey(world.DataCenter, world.WorldName);
        var cacheKey = new MarketAnalysisWorldDetailHydrationCacheKey(publicationId.Value, itemId, worldKey);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var details = await _store.LoadDetailsAsync(
            new MarketIntelligenceDetailQuery(publicationId.Value, itemId, worldKey),
            cancellationToken);
        var worldDetail = details.FirstOrDefault(detail => detail.Key.World == worldKey);
        MarketAnalysisWorldDetailHydrationResult result;
        if (worldDetail?.Listings.Count > 0)
        {
            result = MarketAnalysisWorldDetailHydrationResult.Loaded(
                worldDetail.Listings,
                worldDetail.PriceBands,
                fromEmbeddedHotState: false);
        }
        else
        {
            var manifest = await _store.LoadDetailManifestAsync(publicationId.Value, cancellationToken);
            var manifestEntry = manifest?.Entries.FirstOrDefault(entry =>
                entry.Key.ItemId == itemId &&
                entry.Key.World is { } manifestWorld &&
                manifestWorld == worldKey);
            result = MarketAnalysisWorldDetailHydrationResult.Unavailable(
                manifestEntry?.Availability ?? MarketIntelligenceDetailAvailability.Missing,
                FormatUnavailableMessage(manifestEntry, expectedAvailableDetailMissing: manifestEntry?.Availability == MarketIntelligenceDetailAvailability.Available));
        }

        Cache(cacheKey, result);
        return result;
    }

    private void Cache(
        MarketAnalysisWorldDetailHydrationCacheKey key,
        MarketAnalysisWorldDetailHydrationResult result)
    {
        if (!_cache.ContainsKey(key))
        {
            _cacheOrder.Enqueue(key);
        }

        _cache[key] = result;
        while (_cacheOrder.Count > MaxCachedWorldDetails)
        {
            _cache.Remove(_cacheOrder.Dequeue());
        }
    }

    private static string FormatUnavailableMessage(
        MarketIntelligenceDetailManifestEntry? manifestEntry,
        bool expectedAvailableDetailMissing)
    {
        if (expectedAvailableDetailMissing)
        {
            return "Listing detail was expected in local storage, but the detail record is missing.";
        }

        if (!string.IsNullOrWhiteSpace(manifestEntry?.UnavailableReason))
        {
            return manifestEntry.UnavailableReason;
        }

        return manifestEntry?.Availability switch
        {
            MarketIntelligenceDetailAvailability.Pruned => "Listing detail was pruned from local storage.",
            MarketIntelligenceDetailAvailability.SummaryOnly => "This publication only contains summary data.",
            MarketIntelligenceDetailAvailability.IncompatibleSchema => "Listing detail uses an unsupported schema.",
            MarketIntelligenceDetailAvailability.Missing => "Listing detail is missing from local storage.",
            _ => "Listing detail is not available."
        };
    }
}

public sealed record MarketAnalysisWorldDetailHydrationResult(
    MarketAnalysisWorldDetailHydrationStatus Status,
    IReadOnlyList<AnalyzedMarketListing> Listings,
    IReadOnlyList<MarketPriceBand> PriceBands,
    string? Message,
    bool FromEmbeddedHotState)
{
    public bool HasListings => Listings.Count > 0;

    public static MarketAnalysisWorldDetailHydrationResult Loaded(
        IReadOnlyList<AnalyzedMarketListing> listings,
        IReadOnlyList<MarketPriceBand> priceBands,
        bool fromEmbeddedHotState) =>
        new(
            MarketAnalysisWorldDetailHydrationStatus.Loaded,
            listings,
            priceBands,
            null,
            fromEmbeddedHotState);

    public static MarketAnalysisWorldDetailHydrationResult Unavailable(
        MarketIntelligenceDetailAvailability availability,
        string message) =>
        new(
            availability switch
            {
                MarketIntelligenceDetailAvailability.Pruned => MarketAnalysisWorldDetailHydrationStatus.Pruned,
                MarketIntelligenceDetailAvailability.SummaryOnly => MarketAnalysisWorldDetailHydrationStatus.SummaryOnly,
                MarketIntelligenceDetailAvailability.IncompatibleSchema => MarketAnalysisWorldDetailHydrationStatus.Incompatible,
                _ => MarketAnalysisWorldDetailHydrationStatus.Missing
            },
            Array.Empty<AnalyzedMarketListing>(),
            Array.Empty<MarketPriceBand>(),
            message,
            false);
}

public enum MarketAnalysisWorldDetailHydrationStatus
{
    Loaded,
    Missing,
    Pruned,
    SummaryOnly,
    Incompatible
}

public readonly record struct MarketAnalysisWorldDetailHydrationCacheKey(
    Guid PublicationId,
    int ItemId,
    MarketWorldKey World);
