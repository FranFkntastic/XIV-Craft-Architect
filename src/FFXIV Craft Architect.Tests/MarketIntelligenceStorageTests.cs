using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketIntelligenceStorageTests
{
    [Fact]
    public void PublicationSummary_CanRepresentHotStateWithoutListingDetails()
    {
        var publicationId = Guid.NewGuid();
        var detailKey = new MarketIntelligenceDetailKey(
            publicationId,
            MarketFetchScope.EntireRegion,
            5338,
            new MarketWorldKey("Aether", "Siren"),
            "plan:v1:item:5338:qty:12");

        var summary = new MarketIntelligencePublicationSummary
        {
            PublicationId = publicationId,
            PublicationContext = CreatePublicationContext(MarketFetchScope.EntireRegion),
            Items =
            [
                new MarketItemSummary
                {
                    ItemId = 5338,
                    Name = "Mythril Ore",
                    QuantityNeeded = 12,
                    Scope = MarketFetchScope.EntireRegion,
                    RecommendedWorld = new MarketWorldKey("Aether", "Siren"),
                    RecommendedTotalCost = 1_200,
                    CompetitiveAverageUnitPrice = 100,
                    CoverageBucket = MarketCoverageBucket.Full,
                    DataQualityBucket = MarketDataQualityBucket.Current,
                    Confidence = MarketPriceEvaluationConfidence.High,
                    DetailKey = detailKey,
                    Worlds =
                    [
                        new WorldMarketSummary
                        {
                            World = new MarketWorldKey("Aether", "Siren"),
                            QuantityNeeded = 12,
                            CompetitiveQuantity = 12,
                            TotalListingQuantity = 30,
                            CompetitiveAverageUnitPrice = 100,
                            CoverageBucket = MarketCoverageBucket.Full,
                            DataQualityBucket = MarketDataQualityBucket.Current,
                            DetailKey = detailKey
                        }
                    ]
                }
            ],
            DetailManifest = new MarketIntelligenceDetailManifest
            {
                PublicationId = publicationId,
                Entries =
                [
                    new MarketIntelligenceDetailManifestEntry
                    {
                        Key = detailKey,
                        Availability = MarketIntelligenceDetailAvailability.Available,
                        ListingCount = 2,
                        DetailBytes = 512
                    }
                ]
            }
        };

        Assert.True(summary.HasDetailManifest);
        Assert.False(summary.ContainsLoadedListingDetails);
        Assert.Equal(detailKey, Assert.Single(summary.Items).DetailKey);
        Assert.NotNull(Assert.Single(Assert.Single(summary.Items).Worlds).DetailKey);
    }

    [Fact]
    public void DetailKey_IdentityIncludesPublicationScopeItemWorldAndDemandFingerprint()
    {
        var publicationId = Guid.NewGuid();
        var key = new MarketIntelligenceDetailKey(
            publicationId,
            MarketFetchScope.EntireRegion,
            5338,
            new MarketWorldKey("Aether", "Siren"),
            "plan:v1:item:5338:qty:12");

        Assert.NotEqual(key, key with { PublicationId = Guid.NewGuid() });
        Assert.NotEqual(key, key with { Scope = MarketFetchScope.SelectedDataCenter });
        Assert.NotEqual(key, key with { ItemId = 5339 });
        Assert.NotEqual(key, key with { World = new MarketWorldKey("Primal", "Excalibur") });
        Assert.NotEqual(key, key with { DemandFingerprint = "plan:v1:item:5338:qty:20" });
    }

    [Fact]
    public void DetailKey_CanRepresentItemLevelDetailWithoutDummyWorld()
    {
        var publicationId = Guid.NewGuid();
        var itemDetailKey = new MarketIntelligenceDetailKey(
            publicationId,
            MarketFetchScope.EntireRegion,
            5338,
            null,
            "plan:v1:item:5338:qty:12");
        var worldDetailKey = itemDetailKey with { World = new MarketWorldKey("Aether", "Siren") };

        Assert.Null(itemDetailKey.World);
        Assert.NotEqual(itemDetailKey, worldDetailKey);
    }

    [Fact]
    public async Task InMemoryStore_RoundTripsSummaryDetailsRunRecordsAndCanonicalFacts()
    {
        var store = new InMemoryMarketIntelligenceStore();
        var publicationId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var detailKey = new MarketIntelligenceDetailKey(
            publicationId,
            MarketFetchScope.SelectedDataCenter,
            5338,
            new MarketWorldKey("Aether", "Siren"),
            "plan:v1:item:5338:qty:12");

        var summary = new MarketIntelligencePublicationSummary
        {
            PublicationId = publicationId,
            PublicationContext = CreatePublicationContext(MarketFetchScope.SelectedDataCenter),
            ActiveRunId = runId,
            Items =
            [
                new MarketItemSummary
                {
                    ItemId = 5338,
                    Name = "Mythril Ore",
                    QuantityNeeded = 12,
                    Scope = MarketFetchScope.SelectedDataCenter,
                    DetailKey = detailKey
                }
            ],
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
        var detail = new MarketListingDetail
        {
            Key = detailKey,
            Listings =
            [
                new AnalyzedMarketListing
                {
                    SortIndex = 0,
                    Quantity = 12,
                    PricePerUnit = 100,
                    RetainerName = "Test Retainer",
                    Competitiveness = MarketListingCompetitiveness.Competitive,
                    PriceSanity = MarketListingPriceSanity.Sane
                }
            ]
        };
        var run = new MarketAnalysisRunRecord
        {
            RunId = runId,
            PublicationId = publicationId,
            AnalyzerVersion = "test-analyzer",
            Scope = MarketFetchScope.SelectedDataCenter,
            StartedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = DateTime.UtcNow.AddSeconds(1),
            CacheMode = "warm",
            MarketIntelligencePayloadBytes = 128,
            RetainedDetailBytes = 256
        };
        var fact = new CanonicalMarketListingFact
        {
            PublicationId = publicationId,
            RunId = runId,
            DemandFingerprint = "plan:v1:item:5338:qty:12",
            ItemId = 5338,
            Scope = MarketFetchScope.SelectedDataCenter,
            DataCenter = "Aether",
            WorldName = "Siren",
            RetrievedAtUtc = DateTime.UtcNow,
            Quantity = 12,
            UnitPrice = 100,
            IsHq = true,
            RetainerName = "Test Retainer",
            SourceProvider = "Universalis"
        };

        await store.SavePublicationAsync(
            new MarketIntelligencePublicationWrite(summary, [detail], [run]),
            CancellationToken.None);
        await store.SaveListingFactsAsync([fact], CancellationToken.None);

        var restoredSummary = await store.LoadPublicationSummaryAsync(publicationId, CancellationToken.None);
        var restoredManifest = await store.LoadDetailManifestAsync(publicationId, CancellationToken.None);
        var restoredDetails = await store.LoadDetailsAsync(
            new MarketIntelligenceDetailQuery(publicationId, 5338, new MarketWorldKey("Aether", "Siren")),
            CancellationToken.None);
        var restoredRun = await store.LoadRunRecordAsync(runId, CancellationToken.None);
        var restoredFacts = await store.LoadListingFactsAsync(
            new MarketDataSourceQuery(
                5338,
                MarketFetchScope.SelectedDataCenter,
                "Aether",
                "Siren",
                publicationId,
                runId,
                "plan:v1:item:5338:qty:12"),
            CancellationToken.None);

        Assert.Equal(publicationId, restoredSummary?.PublicationId);
        Assert.Equal(detailKey, Assert.Single(restoredManifest!.Entries).Key);
        Assert.Equal(100, Assert.Single(Assert.Single(restoredDetails).Listings).PricePerUnit);
        Assert.Equal("test-analyzer", restoredRun?.AnalyzerVersion);
        Assert.Equal("Universalis", Assert.Single(restoredFacts).SourceProvider);
    }

    [Fact]
    public async Task InMemoryStore_AtomicWriteMarksMissingAvailableManifestDetails()
    {
        var store = new InMemoryMarketIntelligenceStore();
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
            PublicationContext = CreatePublicationContext(MarketFetchScope.SelectedDataCenter),
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

        var manifest = await store.LoadDetailManifestAsync(publicationId, CancellationToken.None);
        var entry = Assert.Single(manifest!.Entries);

        Assert.Equal(MarketIntelligenceDetailAvailability.Missing, entry.Availability);
        Assert.Contains("not included", entry.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InMemoryStore_PruneMarksRemovedManifestDetailsAsPruned()
    {
        var store = new InMemoryMarketIntelligenceStore();
        var activePublicationId = Guid.NewGuid();
        var prunedPublicationId = Guid.NewGuid();
        var prunedDetailKey = new MarketIntelligenceDetailKey(
            prunedPublicationId,
            MarketFetchScope.SelectedDataCenter,
            5338,
            new MarketWorldKey("Aether", "Siren"),
            "plan:v1:item:5338:qty:12");
        var prunedSummary = new MarketIntelligencePublicationSummary
        {
            PublicationId = prunedPublicationId,
            PublicationContext = CreatePublicationContext(MarketFetchScope.SelectedDataCenter),
            DetailManifest = new MarketIntelligenceDetailManifest
            {
                PublicationId = prunedPublicationId,
                Entries =
                [
                    new MarketIntelligenceDetailManifestEntry
                    {
                        Key = prunedDetailKey,
                        Availability = MarketIntelligenceDetailAvailability.Available,
                        ListingCount = 1
                    }
                ]
            }
        };
        var prunedDetail = new MarketListingDetail { Key = prunedDetailKey };

        await store.SavePublicationAsync(
            new MarketIntelligencePublicationWrite(prunedSummary, [prunedDetail], []),
            CancellationToken.None);
        await store.PruneDetailsAsync(
            new MarketIntelligencePruneRequest(activePublicationId, null, null),
            CancellationToken.None);

        var manifest = await store.LoadDetailManifestAsync(prunedPublicationId, CancellationToken.None);
        var details = await store.LoadDetailsAsync(
            new MarketIntelligenceDetailQuery(prunedPublicationId),
            CancellationToken.None);

        Assert.Empty(details);
        Assert.Equal(MarketIntelligenceDetailAvailability.Pruned, Assert.Single(manifest!.Entries).Availability);
    }

    private static MarketIntelligencePublicationContext CreatePublicationContext(MarketFetchScope scope) =>
        new(
            MarketIntelligencePublicationContextKind.Known,
            scope,
            "Aether",
            "North America",
            ["Aether"],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Aether"] = ["Siren"]
            },
            TimeSpan.FromHours(24),
            ForceRefreshData: false,
            RecommendationMode.MinimizeTotalCost,
            MarketAcquisitionLens.MinimumUpfrontCost,
            null,
            WebPlanSessionVersion: 1,
            WebMarketAnalysisVersion: 1,
            DateTime.UtcNow);
}
