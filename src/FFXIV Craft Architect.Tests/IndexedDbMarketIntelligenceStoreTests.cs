using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Tests;

public class IndexedDbMarketIntelligenceStoreTests
{
    [Fact]
    public async Task SavePublicationAsync_UsesSingleAtomicInteropCallAndNormalizesMissingDetails()
    {
        var jsRuntime = new RecordingJsRuntime();
        var store = new IndexedDbMarketIntelligenceStore(jsRuntime);
        var publicationId = Guid.NewGuid();
        var detailKey = new MarketIntelligenceDetailKey(
            publicationId,
            MarketFetchScope.SelectedDataCenter,
            5338,
            new MarketWorldKey("Aether", "Siren"),
            "plan:v1:item:5338:qty:12");
        var summary = new MarketIntelligencePublicationSummary
        {
            PublicationId = publicationId,
            DetailManifest = new MarketIntelligenceDetailManifest
            {
                PublicationId = publicationId,
                Entries =
                [
                    new MarketIntelligenceDetailManifestEntry
                    {
                        Key = detailKey,
                        Availability = MarketIntelligenceDetailAvailability.Available,
                        ListingCount = 1
                    }
                ]
            }
        };

        await store.SavePublicationAsync(
            new MarketIntelligencePublicationWrite(summary, [], []),
            CancellationToken.None);

        Assert.Equal("IndexedDB.saveMarketPublication", jsRuntime.LastIdentifier);
        var write = Assert.IsType<MarketIntelligencePublicationWrite>(Assert.Single(jsRuntime.LastArgs ?? []));
        var manifestEntry = Assert.Single(write.Summary.DetailManifest.Entries);
        Assert.Equal(MarketIntelligenceDetailAvailability.Missing, manifestEntry.Availability);
    }

    [Fact]
    public async Task SavePublicationAsync_LargePublicationWritesDetailsInChunksBeforeSummary()
    {
        var jsRuntime = new RecordingJsRuntime();
        var store = new IndexedDbMarketIntelligenceStore(jsRuntime);
        var publicationId = Guid.NewGuid();
        var details = Enumerable
            .Range(1, 129)
            .Select(itemId => new MarketListingDetail
            {
                Key = new MarketIntelligenceDetailKey(
                    publicationId,
                    MarketFetchScope.SelectedDataCenter,
                    itemId,
                    new MarketWorldKey("Aether", "Siren"),
                    $"plan:v1:item:{itemId}:qty:1")
            })
            .ToList();
        var summary = new MarketIntelligencePublicationSummary
        {
            PublicationId = publicationId,
            DetailManifest = new MarketIntelligenceDetailManifest
            {
                PublicationId = publicationId,
                Entries = details
                    .Select(detail => new MarketIntelligenceDetailManifestEntry
                    {
                        Key = detail.Key,
                        Availability = MarketIntelligenceDetailAvailability.Available
                    })
                    .ToList()
            }
        };

        await store.SavePublicationAsync(
            new MarketIntelligencePublicationWrite(summary, details, []),
            CancellationToken.None);

        Assert.Equal(
            [
                "IndexedDB.saveMarketPublicationDetails",
                "IndexedDB.saveMarketPublicationDetails",
                "IndexedDB.saveMarketPublicationSummary"
            ],
            jsRuntime.Calls.Select(call => call.Identifier));
        Assert.All(
            jsRuntime.Calls.Where(call => call.Identifier == "IndexedDB.saveMarketPublicationDetails"),
            call => Assert.InRange(Assert.IsAssignableFrom<IReadOnlyList<MarketListingDetail>>(call.Args[1]).Count, 1, 128));
        Assert.Equal("IndexedDB.saveMarketPublicationSummary", jsRuntime.LastIdentifier);
    }

    [Fact]
    public async Task SaveListingFactsAsync_LargeFactSetWritesChunksWithStableOffsets()
    {
        var jsRuntime = new RecordingJsRuntime();
        var store = new IndexedDbMarketIntelligenceStore(jsRuntime);
        var publicationId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var facts = Enumerable
            .Range(0, 257)
            .Select(index => new CanonicalMarketListingFact
            {
                PublicationId = publicationId,
                RunId = runId,
                ItemId = 5338,
                Scope = MarketFetchScope.SelectedDataCenter,
                DataCenter = "Aether",
                WorldName = "Siren",
                UnitPrice = 100 + index,
                Quantity = 1
            })
            .ToList();

        await store.SaveListingFactsAsync(facts, CancellationToken.None);

        Assert.Equal(
            ["IndexedDB.saveMarketListingFacts", "IndexedDB.saveMarketListingFacts"],
            jsRuntime.Calls.Select(call => call.Identifier));
        Assert.Equal(0, Assert.IsType<int>(jsRuntime.Calls[0].Args[1]));
        Assert.Equal(256, Assert.IsType<int>(jsRuntime.Calls[1].Args[1]));
        Assert.Equal(256, Assert.IsAssignableFrom<IReadOnlyList<CanonicalMarketListingFact>>(jsRuntime.Calls[0].Args[0]).Count);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CanonicalMarketListingFact>>(jsRuntime.Calls[1].Args[0]));
    }

    [Fact]
    public async Task LoadAndSaveMethods_UseStableIndexedDbInteropNames()
    {
        var jsRuntime = new RecordingJsRuntime();
        var store = new IndexedDbMarketIntelligenceStore(jsRuntime);
        var publicationId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await store.LoadPublicationSummaryAsync(publicationId, CancellationToken.None);
        Assert.Equal("IndexedDB.loadMarketPublicationSummary", jsRuntime.LastIdentifier);
        Assert.Equal(publicationId, Assert.Single(jsRuntime.LastArgs ?? []));

        await store.LoadDetailManifestAsync(publicationId, CancellationToken.None);
        Assert.Equal("IndexedDB.loadMarketDetailManifest", jsRuntime.LastIdentifier);

        await store.LoadDetailsAsync(
            new MarketIntelligenceDetailQuery(publicationId, 5338, new MarketWorldKey("Aether", "Siren")),
            CancellationToken.None);
        Assert.Equal("IndexedDB.loadMarketDetails", jsRuntime.LastIdentifier);
        Assert.IsType<MarketIntelligenceDetailQuery>(Assert.Single(jsRuntime.LastArgs ?? []));

        await store.LoadRunRecordAsync(runId, CancellationToken.None);
        Assert.Equal("IndexedDB.loadMarketRunRecord", jsRuntime.LastIdentifier);

        await store.SaveListingFactsAsync([], CancellationToken.None);
        Assert.Equal("IndexedDB.saveMarketListingFacts", jsRuntime.LastIdentifier);

        await store.LoadListingFactsAsync(
            new MarketDataSourceQuery(5338, MarketFetchScope.SelectedDataCenter, "Aether", "Siren"),
            CancellationToken.None);
        Assert.Equal("IndexedDB.loadMarketListingFacts", jsRuntime.LastIdentifier);

        await store.PruneDetailsAsync(
            new MarketIntelligencePruneRequest(publicationId, null, null),
            CancellationToken.None);
        Assert.Equal("IndexedDB.pruneMarketDetails", jsRuntime.LastIdentifier);
    }

    [Fact]
    public async Task SavePublicationAsync_DoesNotSwallowAtomicWriteFailure()
    {
        var jsRuntime = new RecordingJsRuntime { ThrowOnSaveMarketPublication = true };
        var store = new IndexedDbMarketIntelligenceStore(jsRuntime);
        var summary = new MarketIntelligencePublicationSummary { PublicationId = Guid.NewGuid() };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SavePublicationAsync(
                new MarketIntelligencePublicationWrite(summary, [], []),
                CancellationToken.None));
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        private readonly List<RecordedJsCall> _calls = [];

        public IReadOnlyList<RecordedJsCall> Calls => _calls;

        public string? LastIdentifier { get; private set; }
        public object?[]? LastArgs { get; private set; }
        public bool ThrowOnSaveMarketPublication { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            RecordCall(identifier, args);
            if (ThrowOnSaveMarketPublication && identifier == "IndexedDB.saveMarketPublication")
            {
                throw new InvalidOperationException("simulated quota failure");
            }

            return new ValueTask<TValue>(DefaultValue<TValue>());
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            RecordCall(identifier, args);
            if (ThrowOnSaveMarketPublication && identifier == "IndexedDB.saveMarketPublication")
            {
                throw new InvalidOperationException("simulated quota failure");
            }

            return new ValueTask<TValue>(DefaultValue<TValue>());
        }

        private void RecordCall(string identifier, object?[]? args)
        {
            LastIdentifier = identifier;
            LastArgs = args;
            _calls.Add(new RecordedJsCall(identifier, args ?? []));
        }

        private static TValue DefaultValue<TValue>()
        {
            var type = typeof(TValue);
            if (type == typeof(bool))
            {
                return (TValue)(object)true;
            }

            if (type == typeof(IReadOnlyList<MarketListingDetail>) ||
                type == typeof(List<MarketListingDetail>))
            {
                return (TValue)(object)new List<MarketListingDetail>();
            }

            if (type == typeof(IReadOnlyList<CanonicalMarketListingFact>) ||
                type == typeof(List<CanonicalMarketListingFact>))
            {
                return (TValue)(object)new List<CanonicalMarketListingFact>();
            }

            return default!;
        }
    }

    private sealed record RecordedJsCall(string Identifier, object?[] Args);
}
