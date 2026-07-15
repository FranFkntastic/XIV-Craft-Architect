using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public sealed class MarketEvidenceReconciliationServiceTests
{
    private static readonly DateTime NowUtc = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReconcileAsync_CurrentPublishedEvidence_IsReusedWithoutExecution()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>(MockBehavior.Strict);
        var service = new MarketEvidenceReconciliationService(execution.Object);

        var result = await service.ReconcileAsync(Request(
            PublishedAnalysis(NowUtc - TimeSpan.FromMinutes(30)),
            PublishedPlan()));

        var item = Assert.Single(result.Items);
        Assert.Equal(MarketEvidenceReconciliationDisposition.ReusedPublished, item.Disposition);
        Assert.Equal(MarketEvidenceReconciliationReason.PublishedEvidenceEligible, item.Reason);
        Assert.Equal(TimeSpan.FromMinutes(30), item.OldestEvidenceAge);
        Assert.Empty(result.ReconciledItems);
        Assert.Equal(0, result.FetchedCount);
    }

    [Fact]
    public async Task ReconcileAsync_StoredCurrentBucketWhoseTimestampExpired_IsRefreshed()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>();
        MarketAnalysisExecutionRequest? captured = null;
        execution.Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback<MarketAnalysisExecutionRequest, IProgress<string>?, CancellationToken, MarketAnalysisExecutionOptions?>(
                (request, _, _, _) => captured = request)
            .ReturnsAsync(ExecutionResult(fetchedCount: 1));
        var service = new MarketEvidenceReconciliationService(execution.Object);

        var result = await service.ReconcileAsync(Request(
            PublishedAnalysis(NowUtc - TimeSpan.FromHours(13), MarketDataQualityBucket.Current),
            PublishedPlan()));

        var item = Assert.Single(result.Items);
        Assert.Equal(MarketEvidenceReconciliationDisposition.Refreshed, item.Disposition);
        Assert.Equal(MarketEvidenceReconciliationReason.RecommendationExpired, item.Reason);
        Assert.Equal(TimeSpan.FromHours(13), item.OldestEvidenceAge);
        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromHours(1), captured.MaxAge);
        Assert.False(captured.ForceRefreshData);
    }

    [Fact]
    public async Task ReconcileAsync_ExpiredEvidenceRecentlyRebuiltFromCache_IsReused()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>(MockBehavior.Strict);
        var service = new MarketEvidenceReconciliationService(execution.Object);
        var analysis = PublishedAnalysis(
            NowUtc - TimeSpan.FromHours(13),
            MarketDataQualityBucket.VeryOld);
        analysis.LastReconciledAtUtc = NowUtc - TimeSpan.FromMinutes(10);

        var result = await service.ReconcileAsync(Request(analysis, PublishedPlan()));

        var item = Assert.Single(result.Items);
        Assert.Equal(MarketEvidenceReconciliationDisposition.ReusedPublished, item.Disposition);
        Assert.Equal(MarketEvidenceReconciliationReason.RecentlyReconciled, item.Reason);
        Assert.Equal(TimeSpan.FromHours(13), item.OldestEvidenceAge);
        Assert.Empty(result.ReconciledItems);
    }

    [Fact]
    public async Task ReconcileAsync_ForcedRefresh_BypassesEligiblePublishedEvidence()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>();
        MarketAnalysisExecutionRequest? captured = null;
        execution.Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback<MarketAnalysisExecutionRequest, IProgress<string>?, CancellationToken, MarketAnalysisExecutionOptions?>(
                (request, _, _, _) => captured = request)
            .ReturnsAsync(ExecutionResult(fetchedCount: 1));
        var service = new MarketEvidenceReconciliationService(execution.Object);
        var request = Request(
            PublishedAnalysis(NowUtc - TimeSpan.FromMinutes(5)),
            PublishedPlan()).WithPolicy(MarketEvidenceReconciliationPolicy.ForcedRefresh());

        var result = await service.ReconcileAsync(request);

        Assert.Equal(MarketEvidenceReconciliationReason.ForcedRefresh, Assert.Single(result.Items).Reason);
        Assert.True(captured?.ForceRefreshData);
    }

    [Fact]
    public async Task ReconcileAsync_IncompleteRegionScope_RebuildsOnlyAffectedItem()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(ExecutionResult(fetchedCount: 0));
        var service = new MarketEvidenceReconciliationService(execution.Object);
        var analysis = PublishedAnalysis(NowUtc - TimeSpan.FromMinutes(15))
            .WithScope(MarketFetchScope.EntireRegion);

        var result = await service.ReconcileAsync(new MarketEvidenceReconciliationRequest
        {
            Items = [Item()],
            PublishedAnalyses = [analysis],
            PublishedShoppingPlans = [PublishedPlan()],
            Scope = MarketFetchScope.EntireRegion,
            SelectedDataCenter = "Aether",
            SelectedRegion = "North America",
            EvaluatedAtUtc = NowUtc
        });

        var item = Assert.Single(result.Items);
        Assert.Equal(MarketEvidenceReconciliationDisposition.RebuiltFromCache, item.Disposition);
        Assert.Equal(MarketEvidenceReconciliationReason.ScopeIncomplete, item.Reason);
        Assert.Equal(101, Assert.Single(result.ReconciledItems).ItemId);
    }

    [Fact]
    public async Task ReconcileAsync_RecentNegativeEvidence_IsReusableUntilCacheWindowExpires()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>(MockBehavior.Strict);
        var service = new MarketEvidenceReconciliationService(execution.Object);
        var analysis = PublishedAnalysis(NowUtc - TimeSpan.FromMinutes(30)).WithNoWorlds();

        var result = await service.ReconcileAsync(Request(analysis, PublishedPlan()));

        var item = Assert.Single(result.Items);
        Assert.Equal(MarketEvidenceReconciliationDisposition.ReusedPublished, item.Disposition);
        Assert.Equal(TimeSpan.FromMinutes(10), item.OldestEvidenceAge);
    }

    [Fact]
    public async Task ReconcileAsync_ExpiredNegativeEvidence_IsReconciled()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(ExecutionResult(fetchedCount: 0));
        var service = new MarketEvidenceReconciliationService(execution.Object);
        var analysis = PublishedAnalysis(NowUtc - TimeSpan.FromHours(2))
            .WithNoWorlds()
            .WithLoadedAt(NowUtc - TimeSpan.FromHours(2));

        var result = await service.ReconcileAsync(Request(analysis, PublishedPlan()));

        var item = Assert.Single(result.Items);
        Assert.Equal(MarketEvidenceReconciliationDisposition.RebuiltFromCache, item.Disposition);
        Assert.Equal(MarketEvidenceReconciliationReason.RecommendationExpired, item.Reason);
        Assert.Equal(TimeSpan.FromHours(2), item.OldestEvidenceAge);
    }

    [Fact]
    public async Task ReconcileWorldAsync_RefreshesOnlyRequestedWorldAndPreservesItsNeighbors()
    {
        var existing = CachedEntry(
            CachedWorld("Siren", 100),
            CachedWorld("Cactuar", 50));
        CachedMarketData? stored = null;
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(service => service.GetWithStaleAsync(101, "Aether", It.IsAny<TimeSpan?>()))
            .ReturnsAsync((existing, false));
        cache.Setup(service => service.SetAsync(101, "Aether", It.IsAny<CachedMarketData>()))
            .Callback<int, string, CachedMarketData>((_, _, value) => stored = value)
            .Returns(Task.CompletedTask);
        var universalis = new Mock<IUniversalisService>(MockBehavior.Strict);
        universalis.Setup(service => service.GetMarketDataBulkAsync(
                "Siren",
                It.Is<IEnumerable<int>>(ids => ids.SequenceEqual(new[] { 101 })),
                false,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, UniversalisResponse>
            {
                [101] = new()
                {
                    ItemId = 101,
                    LastUploadTimeUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Listings =
                    [
                        new MarketListing
                        {
                            Quantity = 3,
                            PricePerUnit = 200,
                            RetainerName = "Fresh Retainer"
                        }
                    ]
                }
            });
        var service = WorldService(cache.Object, universalis.Object);

        var result = await service.ReconcileWorldAsync(WorldRequest());

        Assert.NotNull(stored);
        Assert.Equal(2, stored.Worlds.Count);
        Assert.Equal(200, Assert.Single(stored.Worlds.Single(world => world.WorldName == "Siren").Listings).PricePerUnit);
        Assert.Equal(50, Assert.Single(stored.Worlds.Single(world => world.WorldName == "Cactuar").Listings).PricePerUnit);
        Assert.Equal(200, Assert.Single(result.Analysis.Worlds.Single(world => world.WorldName == "Siren").Listings).PricePerUnit);
        Assert.Equal(MarketEvidenceOrigin.Universalis, result.Evidence.Origin);
        universalis.VerifyAll();
    }

    [Fact]
    public async Task ReconcileWorldAsync_AcceptsMarketMafiosoEvidenceWithoutCallingUniversalis()
    {
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(service => service.GetWithStaleAsync(101, "Aether", It.IsAny<TimeSpan?>()))
            .ReturnsAsync((CachedEntry(CachedWorld("Siren", 100)), false));
        cache.Setup(service => service.SetAsync(101, "Aether", It.IsAny<CachedMarketData>()))
            .Returns(Task.CompletedTask);
        var universalis = new Mock<IUniversalisService>(MockBehavior.Strict);
        var service = WorldService(cache.Object, universalis.Object);
        var snapshot = new MarketWorldEvidenceSnapshot(
            101,
            "Aether",
            "Siren",
            MarketEvidenceOrigin.MarketMafioso,
            DateTime.UtcNow,
            MarketUpdatedAtUtc: null,
            [new MarketWorldEvidenceListing(2, 175, "Mafioso", false)]);

        var result = await service.ReconcileWorldAsync(WorldRequest(snapshot));

        var world = Assert.Single(result.Analysis.Worlds);
        Assert.Equal(MarketDataAgeSource.MarketMafiosoObservation, world.DataAgeSource);
        Assert.Equal(175, Assert.Single(world.Listings).PricePerUnit);
        Assert.Equal(MarketEvidenceOrigin.MarketMafioso, result.Evidence.Origin);
        universalis.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReconcileWorldAsync_EmptyObservedBoardReplacesStaleListings()
    {
        CachedMarketData? stored = null;
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(service => service.GetWithStaleAsync(101, "Aether", It.IsAny<TimeSpan?>()))
            .ReturnsAsync((CachedEntry(CachedWorld("Siren", 100)), false));
        cache.Setup(service => service.SetAsync(101, "Aether", It.IsAny<CachedMarketData>()))
            .Callback<int, string, CachedMarketData>((_, _, value) => stored = value)
            .Returns(Task.CompletedTask);
        var service = WorldService(cache.Object, Mock.Of<IUniversalisService>());
        var snapshot = new MarketWorldEvidenceSnapshot(
            101,
            "Aether",
            "Siren",
            MarketEvidenceOrigin.ManualObservation,
            DateTime.UtcNow,
            MarketUpdatedAtUtc: null,
            Listings: []);

        var result = await service.ReconcileWorldAsync(WorldRequest(snapshot));

        Assert.Empty(Assert.Single(stored!.Worlds).Listings);
        Assert.Empty(Assert.Single(result.Analysis.Worlds).Listings);
        Assert.Null(result.ShoppingPlan.RecommendedWorld);
    }

    [Fact]
    public async Task ReconcileWorldAsync_PartialObservationPreservesUnseenRetainedListings()
    {
        CachedMarketData? stored = null;
        var retained = CachedWorld("Siren", 100);
        retained.EvidenceOrigin = MarketEvidenceOrigin.MarketMafioso;
        retained.ObservedAtUnixMilliseconds = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeMilliseconds();
        retained.Listings[0].ListingId = "retained";
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(service => service.GetWithStaleAsync(101, "Aether", It.IsAny<TimeSpan?>()))
            .ReturnsAsync((CachedEntry(retained), false));
        cache.Setup(service => service.SetAsync(101, "Aether", It.IsAny<CachedMarketData>()))
            .Callback<int, string, CachedMarketData>((_, _, value) => stored = value)
            .Returns(Task.CompletedTask);
        var service = WorldService(cache.Object, Mock.Of<IUniversalisService>());
        var snapshot = new MarketWorldEvidenceSnapshot(
            101,
            "Aether",
            "Siren",
            MarketEvidenceOrigin.MarketMafioso,
            DateTime.UtcNow,
            MarketUpdatedAtUtc: null,
            [new MarketWorldEvidenceListing(2, 175, "Visible", false, ListingId: "visible")],
            MarketEvidenceCompleteness.Partial,
            ReportedListingCount: 8,
            ListingCapacity: 2,
            IsTruncated: true);

        var result = await service.ReconcileWorldAsync(WorldRequest(snapshot));

        var world = Assert.Single(stored!.Worlds);
        Assert.Equal(MarketEvidenceCompleteness.Partial, world.EvidenceCompleteness);
        Assert.True(world.IsTruncated);
        Assert.Equal(8, world.ReportedListingCount);
        Assert.Equal(2, world.Listings.Count);
        Assert.Contains(world.Listings, listing => listing.ListingId == "retained");
        Assert.Contains(world.Listings, listing => listing.ListingId == "visible");
        Assert.True(result.Applied);
    }

    [Fact]
    public async Task ReconcileWorldAsync_OlderObservationDoesNotReplaceNewerLiveEvidence()
    {
        var retained = CachedWorld("Siren", 100);
        retained.EvidenceOrigin = MarketEvidenceOrigin.MarketMafioso;
        retained.ObservedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(service => service.GetWithStaleAsync(101, "Aether", It.IsAny<TimeSpan?>()))
            .ReturnsAsync((CachedEntry(retained), false));
        var service = WorldService(cache.Object, Mock.Of<IUniversalisService>());
        var snapshot = new MarketWorldEvidenceSnapshot(
            101,
            "Aether",
            "Siren",
            MarketEvidenceOrigin.MarketMafioso,
            DateTime.UtcNow.AddMinutes(-5),
            MarketUpdatedAtUtc: null,
            [new MarketWorldEvidenceListing(2, 175, "Older", false)]);

        var result = await service.ReconcileWorldAsync(WorldRequest(snapshot));

        Assert.False(result.Applied);
        Assert.Equal(100, Assert.Single(Assert.Single(result.Analysis.Worlds).Listings).PricePerUnit);
        cache.Verify(service => service.SetAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<CachedMarketData>()), Times.Never);
    }

    private static MarketEvidenceReconciliationRequest Request(
        MarketItemAnalysis analysis,
        DetailedShoppingPlan plan) =>
        new()
        {
            Items = [Item()],
            PublishedAnalyses = [analysis],
            PublishedShoppingPlans = [plan],
            Scope = MarketFetchScope.SelectedDataCenter,
            SelectedDataCenter = "Aether",
            SelectedRegion = "North America",
            EvaluatedAtUtc = NowUtc
        };

    private static MaterialAggregate Item() =>
        new() { ItemId = 101, Name = "Test Item", TotalQuantity = 5 };

    private static MarketEvidenceReconciliationService WorldService(
        IMarketCacheService cache,
        IUniversalisService universalis) =>
        new(
            Mock.Of<IMarketAnalysisExecutionService>(),
            cache,
            universalis,
            new MarketPriceLadderAnalysisService());

    private static MarketWorldEvidenceReconciliationRequest WorldRequest(
        MarketWorldEvidenceSnapshot? snapshot = null) =>
        new()
        {
            Item = Item(),
            DataCenter = "Aether",
            WorldName = "Siren",
            ObservedEvidence = snapshot,
            Scope = MarketFetchScope.SelectedDataCenter,
            SelectedDataCenter = "Aether",
            SelectedRegion = "North America"
        };

    private static CachedMarketData CachedEntry(params CachedWorldData[] worlds) =>
        new()
        {
            ItemId = 101,
            DataCenter = "Aether",
            FetchedAt = DateTime.UtcNow - TimeSpan.FromMinutes(20),
            Worlds = worlds.ToList()
        };

    private static CachedWorldData CachedWorld(string worldName, long unitPrice) =>
        new()
        {
            WorldName = worldName,
            LastUploadTimeUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Listings =
            [
                new CachedListing
                {
                    Quantity = 5,
                    PricePerUnit = unitPrice,
                    RetainerName = $"{worldName} Retainer"
                }
            ]
        };

    private static MarketItemAnalysis PublishedAnalysis(
        DateTime evidenceTimestampUtc,
        MarketDataQualityBucket bucket = MarketDataQualityBucket.Current) =>
        new()
        {
            ItemId = 101,
            Name = "Test Item",
            QuantityNeeded = 5,
            Scope = MarketFetchScope.SelectedDataCenter,
            LoadedAtUtc = NowUtc - TimeSpan.FromMinutes(10),
            RequestedDataCenters = ["Aether"],
            PresentDataCenters = ["Aether"],
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    MarketUploadedAtUtc = evidenceTimestampUtc,
                    DataQualityBucket = bucket
                }
            ]
        };

    private static DetailedShoppingPlan PublishedPlan() =>
        new()
        {
            ItemId = 101,
            Name = "Test Item",
            QuantityNeeded = 5,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalQuantityPurchased = 5
            }
        };

    private static MarketAnalysisExecutionResult ExecutionResult(int fetchedCount) =>
        new(
            new MarketEvidenceSet(
                fetchedCount > 0
                    ? new Dictionary<(int itemId, string dataCenter), CachedMarketData>
                    {
                        [(101, "Aether")] = new CachedMarketData
                        {
                            ItemId = 101,
                            DataCenter = "Aether",
                            FetchedAt = DateTime.UtcNow
                        }
                    }
                    : new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
                fetchedCount > 0 ? [(101, "Aether")] : [],
                MarketFetchScope.SelectedDataCenter,
                ["Aether"],
                "Aether",
                "North America",
                TimeSpan.FromHours(1),
                fetchedCount,
                NowUtc),
            [PublishedAnalysis(NowUtc)],
            [PublishedPlan()]);
}

file static class MarketEvidenceReconciliationTestExtensions
{
    public static MarketEvidenceReconciliationRequest WithPolicy(
        this MarketEvidenceReconciliationRequest request,
        MarketEvidenceReconciliationPolicy policy) =>
        new()
        {
            Items = request.Items,
            PublishedAnalyses = request.PublishedAnalyses,
            PublishedShoppingPlans = request.PublishedShoppingPlans,
            Scope = request.Scope,
            SelectedDataCenter = request.SelectedDataCenter,
            SelectedRegion = request.SelectedRegion,
            RecommendationMode = request.RecommendationMode,
            Lens = request.Lens,
            AnalysisConfig = request.AnalysisConfig,
            ExpectedWorldsByDataCenter = request.ExpectedWorldsByDataCenter,
            Policy = policy,
            EvaluatedAtUtc = request.EvaluatedAtUtc
        };

    public static MarketItemAnalysis WithNoWorlds(this MarketItemAnalysis analysis) =>
        new()
        {
            ItemId = analysis.ItemId,
            Name = analysis.Name,
            QuantityNeeded = analysis.QuantityNeeded,
            Scope = analysis.Scope,
            LoadedAtUtc = analysis.LoadedAtUtc,
            RequestedDataCenters = analysis.RequestedDataCenters,
            PresentDataCenters = analysis.PresentDataCenters,
            MissingDataCenters = analysis.MissingDataCenters,
            Worlds = []
        };

    public static MarketItemAnalysis WithScope(
        this MarketItemAnalysis analysis,
        MarketFetchScope scope) =>
        new()
        {
            ItemId = analysis.ItemId,
            Name = analysis.Name,
            QuantityNeeded = analysis.QuantityNeeded,
            Scope = scope,
            LoadedAtUtc = analysis.LoadedAtUtc,
            RequestedDataCenters = analysis.RequestedDataCenters,
            PresentDataCenters = analysis.PresentDataCenters,
            MissingDataCenters = analysis.MissingDataCenters,
            Worlds = analysis.Worlds
        };

    public static MarketItemAnalysis WithLoadedAt(
        this MarketItemAnalysis analysis,
        DateTime loadedAtUtc) =>
        new()
        {
            ItemId = analysis.ItemId,
            Name = analysis.Name,
            QuantityNeeded = analysis.QuantityNeeded,
            Scope = analysis.Scope,
            LoadedAtUtc = loadedAtUtc,
            RequestedDataCenters = analysis.RequestedDataCenters,
            PresentDataCenters = analysis.PresentDataCenters,
            MissingDataCenters = analysis.MissingDataCenters,
            Worlds = analysis.Worlds
        };
}
