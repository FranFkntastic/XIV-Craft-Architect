using System.Collections.Concurrent;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class InMemoryMarketIntelligenceStore : IMarketIntelligenceStore, IMarketDataSourceStore
{
    private readonly ConcurrentDictionary<Guid, MarketIntelligencePublicationSummary> _summaries = new();
    private readonly ConcurrentDictionary<MarketIntelligenceDetailKey, MarketListingDetail> _details = new();
    private readonly ConcurrentDictionary<Guid, MarketAnalysisRunRecord> _runRecords = new();
    private readonly List<CanonicalMarketListingFact> _listingFacts = [];
    private readonly object _listingFactLock = new();

    public Task SavePublicationAsync(
        MarketIntelligencePublicationWrite publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(publication.Summary);

        cancellationToken.ThrowIfCancellationRequested();

        var detailKeys = publication.Details.Select(detail => detail.Key).ToHashSet();

        foreach (var detail in publication.Details)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _details[detail.Key] = detail;
        }

        foreach (var runRecord in publication.RunRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _runRecords[runRecord.RunId] = runRecord;
        }

        _summaries[publication.Summary.PublicationId] = NormalizePublicationSummary(
            publication.Summary,
            detailKeys);
        return Task.CompletedTask;
    }

    public Task SaveRunRecordsAsync(
        Guid publicationId,
        IReadOnlyList<MarketAnalysisRunRecord> runRecords,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runRecords);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var runRecord in runRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _runRecords[runRecord.RunId] = runRecord;
        }

        return Task.CompletedTask;
    }

    public Task<MarketIntelligencePublicationSummary?> LoadPublicationSummaryAsync(
        Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _summaries.TryGetValue(publicationId, out var summary);
        return Task.FromResult(summary);
    }

    public Task<MarketIntelligenceDetailManifest?> LoadDetailManifestAsync(
        Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _summaries.TryGetValue(publicationId, out var summary)
                ? summary.DetailManifest
                : null);
    }

    public Task<IReadOnlyList<MarketListingDetail>> LoadDetailsAsync(
        MarketIntelligenceDetailQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var details = _details.Values
            .Where(detail => detail.Key.PublicationId == query.PublicationId)
            .Where(detail => query.ItemId is null || detail.Key.ItemId == query.ItemId.Value)
            .Where(detail => query.World is null ||
                             (detail.Key.World is { } world && world.Equals(query.World.Value)))
            .Where(detail => query.DemandFingerprint is null ||
                             detail.Key.DemandFingerprint.Equals(query.DemandFingerprint.Value))
            .ToList();

        return Task.FromResult<IReadOnlyList<MarketListingDetail>>(details);
    }

    public Task<MarketAnalysisRunRecord?> LoadRunRecordAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _runRecords.TryGetValue(runId, out var runRecord);
        return Task.FromResult(runRecord);
    }

    public Task PruneDetailsAsync(
        MarketIntelligencePruneRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.KeepActivePublicationId is { } activePublicationId)
        {
            foreach (var key in _details.Keys.Where(key => key.PublicationId != activePublicationId).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                _details.TryRemove(key, out _);
            }

            foreach (var summary in _summaries.Values.Where(summary => summary.PublicationId != activePublicationId).ToList())
            {
                _summaries[summary.PublicationId] = MarkManifestDetailsPruned(summary);
            }
        }

        return Task.CompletedTask;
    }

    public Task SaveListingFactsAsync(
        IReadOnlyList<CanonicalMarketListingFact> facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_listingFactLock)
        {
            _listingFacts.AddRange(facts);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CanonicalMarketListingFact>> LoadListingFactsAsync(
        MarketDataSourceQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_listingFactLock)
        {
            var facts = _listingFacts
                .Where(fact => query.ItemId is null || fact.ItemId == query.ItemId.Value)
                .Where(fact => query.Scope is null || fact.Scope == query.Scope.Value)
                .Where(fact => string.IsNullOrWhiteSpace(query.DataCenter) ||
                               string.Equals(fact.DataCenter, query.DataCenter, StringComparison.OrdinalIgnoreCase))
                .Where(fact => string.IsNullOrWhiteSpace(query.WorldName) ||
                               string.Equals(fact.WorldName, query.WorldName, StringComparison.OrdinalIgnoreCase))
                .Where(fact => query.PublicationId is null || fact.PublicationId == query.PublicationId.Value)
                .Where(fact => query.RunId is null || fact.RunId == query.RunId.Value)
                .Where(fact => query.DemandFingerprint is null ||
                               fact.DemandFingerprint.Equals(query.DemandFingerprint.Value))
                .ToList();

            return Task.FromResult<IReadOnlyList<CanonicalMarketListingFact>>(facts);
        }
    }

    private static MarketIntelligencePublicationSummary NormalizePublicationSummary(
        MarketIntelligencePublicationSummary summary,
        IReadOnlySet<MarketIntelligenceDetailKey> storedDetailKeys)
    {
        return CopySummary(
            summary,
            new MarketIntelligenceDetailManifest
            {
                PublicationId = summary.DetailManifest.PublicationId,
                Entries = summary.DetailManifest.Entries
                    .Select(entry =>
                        entry.Availability == MarketIntelligenceDetailAvailability.Available &&
                        !storedDetailKeys.Contains(entry.Key)
                            ? CopyEntry(
                                entry,
                                MarketIntelligenceDetailAvailability.Missing,
                                "Detail was not included in the atomic publication write.")
                            : entry)
                    .ToList()
            });
    }

    private static MarketIntelligencePublicationSummary MarkManifestDetailsPruned(
        MarketIntelligencePublicationSummary summary)
    {
        return CopySummary(
            summary,
            new MarketIntelligenceDetailManifest
            {
                PublicationId = summary.DetailManifest.PublicationId,
                Entries = summary.DetailManifest.Entries
                    .Select(entry =>
                        entry.Availability == MarketIntelligenceDetailAvailability.Available
                            ? CopyEntry(
                                entry,
                                MarketIntelligenceDetailAvailability.Pruned,
                                "Detail was pruned from local cold storage.")
                            : entry)
                    .ToList()
            });
    }

    private static MarketIntelligencePublicationSummary CopySummary(
        MarketIntelligencePublicationSummary summary,
        MarketIntelligenceDetailManifest detailManifest)
    {
        return new MarketIntelligencePublicationSummary
        {
            SchemaVersion = summary.SchemaVersion,
            PublicationId = summary.PublicationId,
            ActiveRunId = summary.ActiveRunId,
            PublicationContext = summary.PublicationContext,
            Items = summary.Items,
            UnavailableMarketItems = summary.UnavailableMarketItems,
            DetailManifest = detailManifest
        };
    }

    private static MarketIntelligenceDetailManifestEntry CopyEntry(
        MarketIntelligenceDetailManifestEntry entry,
        MarketIntelligenceDetailAvailability availability,
        string unavailableReason)
    {
        return new MarketIntelligenceDetailManifestEntry
        {
            Key = entry.Key,
            Availability = availability,
            ListingCount = entry.ListingCount,
            DetailBytes = entry.DetailBytes,
            UnavailableReason = unavailableReason
        };
    }
}
