using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class IndexedDbMarketIntelligenceStore : IMarketIntelligenceStore, IMarketDataSourceStore
{
    private const int DetailWriteChunkSize = 128;
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<IndexedDbMarketIntelligenceStore>? _logger;

    public IndexedDbMarketIntelligenceStore(
        IJSRuntime jsRuntime,
        ILogger<IndexedDbMarketIntelligenceStore>? logger = null)
    {
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _logger = logger;
    }

    public async Task SavePublicationAsync(
        MarketIntelligencePublicationWrite publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);

        var normalized = NormalizePublicationWrite(publication);
        if (normalized.Details.Count <= DetailWriteChunkSize)
        {
            await _jsRuntime.InvokeAsync<bool>(
                "IndexedDB.saveMarketPublication",
                cancellationToken,
                normalized);
            return;
        }

        foreach (var detailChunk in normalized.Details.Chunk(DetailWriteChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _jsRuntime.InvokeAsync<bool>(
                "IndexedDB.saveMarketPublicationDetails",
                cancellationToken,
                normalized.Summary.PublicationId,
                detailChunk);
        }

        if (normalized.RunRecords.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _jsRuntime.InvokeAsync<bool>(
                "IndexedDB.saveMarketPublicationRunRecords",
                cancellationToken,
                normalized.Summary.PublicationId,
                normalized.RunRecords);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _jsRuntime.InvokeAsync<bool>(
            "IndexedDB.saveMarketPublicationSummary",
            cancellationToken,
            normalized.Summary);
    }

    public async Task SaveRunRecordsAsync(
        Guid publicationId,
        IReadOnlyList<MarketAnalysisRunRecord> runRecords,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runRecords);
        if (runRecords.Count == 0)
        {
            return;
        }

        await _jsRuntime.InvokeAsync<bool>(
            "IndexedDB.saveMarketPublicationRunRecords",
            cancellationToken,
            publicationId,
            runRecords);
    }

    public async Task<MarketIntelligencePublicationSummary?> LoadPublicationSummaryAsync(
        Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        return await _jsRuntime.InvokeAsync<MarketIntelligencePublicationSummary?>(
            "IndexedDB.loadMarketPublicationSummary",
            cancellationToken,
            publicationId);
    }

    public async Task<MarketIntelligenceDetailManifest?> LoadDetailManifestAsync(
        Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        return await _jsRuntime.InvokeAsync<MarketIntelligenceDetailManifest?>(
            "IndexedDB.loadMarketDetailManifest",
            cancellationToken,
            publicationId);
    }

    public async Task<IReadOnlyList<MarketListingDetail>> LoadDetailsAsync(
        MarketIntelligenceDetailQuery query,
        CancellationToken cancellationToken = default)
    {
        var details = await _jsRuntime.InvokeAsync<List<MarketListingDetail>?>(
            "IndexedDB.loadMarketDetails",
            cancellationToken,
            query);

        return details ?? [];
    }

    public async Task<MarketAnalysisRunRecord?> LoadRunRecordAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        return await _jsRuntime.InvokeAsync<MarketAnalysisRunRecord?>(
            "IndexedDB.loadMarketRunRecord",
            cancellationToken,
            runId);
    }

    public async Task PruneDetailsAsync(
        MarketIntelligencePruneRequest request,
        CancellationToken cancellationToken = default)
    {
        await _jsRuntime.InvokeAsync<bool>(
            "IndexedDB.pruneMarketDetails",
            cancellationToken,
            request);
    }

    public Task SaveListingFactsAsync(
        IReadOnlyList<CanonicalMarketListingFact> facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<CanonicalMarketListingFact>> LoadListingFactsAsync(
        MarketDataSourceQuery query,
        CancellationToken cancellationToken = default)
    {
        var facts = await _jsRuntime.InvokeAsync<List<CanonicalMarketListingFact>?>(
            "IndexedDB.loadMarketListingFactsFromDetails",
            cancellationToken,
            query);

        if (facts is { Count: > 0 })
        {
            return facts;
        }

        facts = await _jsRuntime.InvokeAsync<List<CanonicalMarketListingFact>?>(
            "IndexedDB.loadMarketListingFacts",
            cancellationToken,
            query);

        return facts ?? [];
    }

    private static MarketIntelligencePublicationWrite NormalizePublicationWrite(
        MarketIntelligencePublicationWrite publication)
    {
        var detailKeys = publication.Details.Select(detail => detail.Key).ToHashSet();
        var summary = publication.Summary;
        var manifest = new MarketIntelligenceDetailManifest
        {
            PublicationId = summary.DetailManifest.PublicationId,
            Entries = summary.DetailManifest.Entries
                .Select(entry =>
                    entry.Availability == MarketIntelligenceDetailAvailability.Available &&
                    !detailKeys.Contains(entry.Key)
                        ? new MarketIntelligenceDetailManifestEntry
                        {
                            Key = entry.Key,
                            Availability = MarketIntelligenceDetailAvailability.Missing,
                            ListingCount = entry.ListingCount,
                            DetailBytes = entry.DetailBytes,
                            UnavailableReason = "Detail was not included in the atomic publication write."
                        }
                        : entry)
                .ToList()
        };
        var normalizedSummary = new MarketIntelligencePublicationSummary
        {
            SchemaVersion = summary.SchemaVersion,
            PublicationId = summary.PublicationId,
            ActiveRunId = summary.ActiveRunId,
            PublicationContext = summary.PublicationContext,
            Items = summary.Items,
            UnavailableMarketItems = summary.UnavailableMarketItems,
            DetailManifest = manifest
        };

        return new MarketIntelligencePublicationWrite(
            normalizedSummary,
            publication.Details,
            publication.RunRecords);
    }
}
