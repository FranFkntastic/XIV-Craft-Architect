using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketPriceLadderAnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_TinyCheapBaitListing_DoesNotCreateStrongBulkStock()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            listings:
            [
                Listing(quantity: 1, price: 10, retainer: "Bait"),
                Listing(quantity: 99, price: 100, retainer: "Fair Stack")
            ]);

        var analyses = await service.AnalyzeAsync(request);

        var world = Assert.Single(Assert.Single(analyses).Worlds);
        Assert.NotEqual(100, world.CompetitiveQuantity);
        Assert.Equal(MarketCoverageBucket.Full, world.CoverageBucket);
        Assert.Equal(MarketScoreBucket.Unavailable, world.Scores.Single(score => score.Lens == MarketAcquisitionLens.BulkValue).ScoreBucket);
    }

    [Fact]
    public async Task AnalyzeAsync_BulkValueRanksDeepCompetitiveStockAboveTinyBait()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            worlds:
            [
                World("BaitWorld",
                [
                    Listing(quantity: 1, price: 10, retainer: "Bait"),
                    Listing(quantity: 99, price: 100, retainer: "Fair Stack")
                ]),
                World("DeepWorld",
                [
                    Listing(quantity: 80, price: 95, retainer: "Deep Seller")
                ])
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var bait = analysis.Worlds.Single(world => world.WorldName == "BaitWorld");
        var deep = analysis.Worlds.Single(world => world.WorldName == "DeepWorld");
        Assert.True(
            deep.Scores.Single(score => score.Lens == MarketAcquisitionLens.BulkValue).Score >
            bait.Scores.Single(score => score.Lens == MarketAcquisitionLens.BulkValue).Score);
    }

    [Fact]
    public async Task AnalyzeAsync_CloseUndercutsAndFairBulkStack_FormOneCompetitiveShelf()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            listings:
            [
                Listing(quantity: 5, price: 95, retainer: "Undercut One"),
                Listing(quantity: 7, price: 98, retainer: "Undercut Two"),
                Listing(quantity: 99, price: 102, retainer: "Bulk Seller")
            ]);

        var analyses = await service.AnalyzeAsync(request);

        var world = Assert.Single(Assert.Single(analyses).Worlds);
        Assert.Equal(111, world.CompetitiveQuantity);
        Assert.Equal(MarketCoverageBucket.Full, world.CoverageBucket);
        Assert.True(world.PriceBands[0].IsCompetitiveShelf);
    }

    [Fact]
    public async Task AnalyzeAsync_TinyBaitWithFairRegionalShelf_TreatsShelfAsScopeSaneAndCompetitive()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            listings:
            [
                Listing(quantity: 1, price: 10, retainer: "Bait"),
                Listing(quantity: 99, price: 100, retainer: "Fair Stack")
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var world = Assert.Single(analysis.Worlds);
        Assert.Equal(100, world.ScopeSaneQuantity);
        Assert.Equal(100, world.ScopeCompetitiveQuantity);
        Assert.Equal(1.0m, world.ScopeSaneCoverageRatio);
        Assert.True(world.AnalysisScopeBaselineUnitPrice > 90);
        Assert.True(world.SaneThresholdUnitPrice >= 180);
    }

    [Fact]
    public async Task AnalyzeAsync_ManyTinyBaitListings_UsesWeightedScopeBaseline()
    {
        var service = CreateService();
        var baitListings = Enumerable.Range(1, 25)
            .Select(index => Listing(quantity: 1, price: 10, retainer: $"Bait {index}"))
            .ToList();
        var request = CreateRequest(
            quantityNeeded: 100,
            listings: [.. baitListings, Listing(quantity: 99, price: 100, retainer: "Fair Stack")]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var world = Assert.Single(analysis.Worlds);
        Assert.Equal(124, world.ScopeSaneQuantity);
        Assert.Equal(124, world.ScopeCompetitiveQuantity);
        Assert.Equal(100, analysis.AnalysisScopeMedianUnitPrice);
        Assert.True(world.AnalysisScopeBaselineUnitPrice > 80);
    }

    [Fact]
    public async Task AnalyzeAsync_ListingsAboveTwoTimesScopeBaseline_AreScopeInsane()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            listings:
            [
                Listing(quantity: 100, price: 100, retainer: "Fair Stack"),
                Listing(quantity: 10, price: 250, retainer: "Wild Ask")
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var world = Assert.Single(analysis.Worlds);
        Assert.Equal(100, world.ScopeSaneQuantity);
        Assert.Equal(10, world.ScopeInsaneQuantity);
        Assert.True(world.SaneThresholdUnitPrice < 250);
        Assert.Equal(1.0m, world.ScopeSaneCoverageRatio);
    }

    [Fact]
    public async Task AnalyzeAsync_SharpPriceJump_ReportsPriceBreakAfterCompetitiveStock()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 60,
            listings:
            [
                Listing(quantity: 30, price: 100, retainer: "Active A"),
                Listing(quantity: 30, price: 105, retainer: "Active B"),
                Listing(quantity: 99, price: 180, retainer: "Old Seller")
            ]);

        var analyses = await service.AnalyzeAsync(request);

        var band = Assert.Single(Assert.Single(Assert.Single(analyses).Worlds).PriceBands, band => band.IsCompetitiveShelf);
        Assert.Equal(60, band.Quantity);
        Assert.True(band.NextBreakPercent >= 70);
    }

    [Fact]
    public async Task AnalyzeAsync_PartialDeepCompetitiveStock_RemainsValuableForBulkLens()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 200,
            listings:
            [
                Listing(quantity: 120, price: 100, retainer: "Deep Partial"),
                Listing(quantity: 20, price: 180, retainer: "Old Seller")
            ]);

        var analyses = await service.AnalyzeAsync(request);

        var world = Assert.Single(Assert.Single(analyses).Worlds);
        Assert.Equal(120, world.CompetitiveQuantity);
        Assert.Equal(MarketCoverageBucket.PartialDeep, world.CoverageBucket);
        Assert.True(world.Scores.Single(score => score.Lens == MarketAcquisitionLens.BulkValue).Score > 0);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingMarketData_ReturnsSevereDataQualityWithoutZeroStockAssumption()
    {
        var service = CreateService();
        var request = CreateMissingDataRequest(quantityNeeded: 50);

        var analyses = await service.AnalyzeAsync(request);

        var item = Assert.Single(analyses);
        Assert.False(item.HasCompleteScopeData);
        Assert.Equal(MarketDataQualityBucket.Missing, item.WorstDataQualityBucket);
        Assert.Equal(["Aether"], item.MissingDataCenters);
        var world = Assert.Single(item.Worlds);
        Assert.Equal("Siren", world.WorldName);
        Assert.Equal(MarketDataQualityBucket.Missing, world.DataQualityBucket);
        Assert.Equal(MarketScoreBucket.Unavailable, world.Scores.Single(score => score.Lens == MarketAcquisitionLens.MinimumUpfrontCost).ScoreBucket);
        Assert.Contains("Missing", item.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnalyzeAsync_DataQualityScore_DecaysAsFetchedAtGetsOlder()
    {
        var service = CreateService();
        var fresh = await service.AnalyzeAsync(CreateRequest(fetchedAtUtc: DateTime.UtcNow.AddMinutes(-5)));
        var old = await service.AnalyzeAsync(CreateRequest(fetchedAtUtc: DateTime.UtcNow.AddHours(-12)));

        var freshWorld = Assert.Single(Assert.Single(fresh).Worlds);
        var oldWorld = Assert.Single(Assert.Single(old).Worlds);
        Assert.True(freshWorld.DataQualityScore > oldWorld.DataQualityScore);
        Assert.NotEqual(freshWorld.DataQualityBucket, oldWorld.DataQualityBucket);
    }

    [Fact]
    public async Task AnalyzeAsync_PerWorldUniversalisUploadTime_DrivesDataQualityBeforeResponseOrFetchTime()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            fetchedAtUtc: now.AddMinutes(-2),
            responseUploadedAtUtc: now.AddMinutes(-3),
            worlds:
            [
                World("FreshWorld", [Listing(10, 100, "Fresh")], uploadedAtUtc: now.AddMinutes(-4)),
                World("StaleWorld", [Listing(10, 90, "Stale")], uploadedAtUtc: now.AddHours(-8))
            ])));

        var freshWorld = analysis.Worlds.Single(world => world.WorldName == "FreshWorld");
        var staleWorld = analysis.Worlds.Single(world => world.WorldName == "StaleWorld");
        Assert.Equal(MarketDataAgeSource.UniversalisWorldUpload, freshWorld.DataAgeSource);
        Assert.Equal(MarketDataAgeSource.UniversalisWorldUpload, staleWorld.DataAgeSource);
        Assert.Equal(MarketDataQualityBucket.Current, freshWorld.DataQualityBucket);
        Assert.Equal(MarketDataQualityBucket.VeryOld, staleWorld.DataQualityBucket);
        Assert.True(freshWorld.DataQualityScore > staleWorld.DataQualityScore);
    }

    [Fact]
    public async Task AnalyzeAsync_ResponseUploadTime_FallsBackWhenWorldUploadTimeIsMissing()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            fetchedAtUtc: now.AddMinutes(-2),
            responseUploadedAtUtc: now.AddHours(-2))));

        var world = Assert.Single(analysis.Worlds);
        Assert.Equal(MarketDataAgeSource.UniversalisResponseUpload, world.DataAgeSource);
        Assert.Equal(MarketDataQualityBucket.Old, world.DataQualityBucket);
    }

    [Fact]
    public async Task AnalyzeAsync_LocalFetchFallback_IsCappedWhenUniversalisUploadTimeIsMissing()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            fetchedAtUtc: now.AddMinutes(-2))));

        var world = Assert.Single(analysis.Worlds);
        Assert.Equal(MarketDataAgeSource.LocalFetchFallback, world.DataAgeSource);
        Assert.Equal(MarketDataQualityBucket.Aging, world.DataQualityBucket);
        Assert.True(world.DataQualityScore <= 70);
    }

    [Fact]
    public async Task AnalyzeAsync_ListingReviewTime_IsProjectedOntoAnalyzedListings()
    {
        var service = CreateService();
        var reviewedAtUtc = DateTimeOffset.FromUnixTimeSeconds(1710000000).UtcDateTime;
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            listings:
            [
                Listing(quantity: 10, price: 100, retainer: "Seller", reviewedAtUtc: reviewedAtUtc)
            ])));

        var listing = Assert.Single(Assert.Single(analysis.Worlds).Listings);
        Assert.Equal(reviewedAtUtc, listing.LastReviewTimeUtc);
    }

    [Fact]
    public async Task ProjectToShoppingPlan_SameAnalysisAndLens_ProducesDeterministicPlan()
    {
        var service = CreateService();
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest()));

        var first = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.MinimumUpfrontCost);
        var second = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.MinimumUpfrontCost);

        Assert.Equal(first.ItemId, second.ItemId);
        Assert.Equal(first.RecommendedWorld?.WorldName, second.RecommendedWorld?.WorldName);
        Assert.Equal(first.WorldOptions.Select(w => w.WorldName), second.WorldOptions.Select(w => w.WorldName));
    }

    [Fact]
    public async Task ProjectToShoppingPlan_DifferentTimestampMetadata_DoesNotChangePurchaseFeasibilityOrCosts()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var currentAnalysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            fetchedAtUtc: now.AddMinutes(-2),
            responseUploadedAtUtc: now.AddMinutes(-2))));
        var fallbackAnalysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            fetchedAtUtc: now.AddHours(-6))));

        var currentPlan = service.ProjectToShoppingPlan(currentAnalysis, MarketAcquisitionLens.MinimumUpfrontCost);
        var fallbackPlan = service.ProjectToShoppingPlan(fallbackAnalysis, MarketAcquisitionLens.MinimumUpfrontCost);

        var currentWorld = Assert.Single(currentPlan.WorldOptions);
        var fallbackWorld = Assert.Single(fallbackPlan.WorldOptions);
        Assert.Equal(currentWorld.WorldName, fallbackWorld.WorldName);
        Assert.Equal(currentWorld.TotalCost, fallbackWorld.TotalCost);
        Assert.Equal(currentWorld.HasSufficientStock, fallbackWorld.HasSufficientStock);
        Assert.Equal(currentWorld.ShortfallQuantity, fallbackWorld.ShortfallQuantity);
        Assert.Equal(currentPlan.RecommendedWorld?.WorldName, fallbackPlan.RecommendedWorld?.WorldName);
    }

    [Fact]
    public async Task ProjectToShoppingPlan_CheaperVeryOldWorld_DoesNotOutrankFreshCompetitiveStock()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            quantityNeeded: 10,
            worlds:
            [
                World("StaleWorld", [Listing(10, 900, "Old Seller")], uploadedAtUtc: now.AddHours(-12)),
                World("FreshWorld", [Listing(10, 1_000, "Fresh Seller")], uploadedAtUtc: now.AddMinutes(-5))
            ])));

        var plan = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.MinimumUpfrontCost);

        Assert.Equal("FreshWorld", plan.RecommendedWorld?.WorldName);
        Assert.Equal(9_000, plan.WorldOptions.Single(world => world.WorldName == "StaleWorld").TotalCost);
        Assert.Equal(10_000, plan.WorldOptions.Single(world => world.WorldName == "FreshWorld").TotalCost);
    }

    [Fact]
    public async Task ProjectToShoppingPlan_PartialWorldsBuildRecommendedSplitWhenTogetherTheySatisfyNeed()
    {
        var service = CreateService();
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            quantityNeeded: 200,
            worlds:
            [
                World("FirstWorld", [Listing(120, 100, "Deep Partial")]),
                World("SecondWorld", [Listing(80, 105, "Second Partial")])
            ])));

        var plan = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.BulkValue);

        Assert.Null(plan.RecommendedWorld);
        Assert.NotNull(plan.RecommendedSplit);
        Assert.Equal(200, plan.RecommendedSplit.Sum(split => split.QuantityToBuy));
        Assert.Equal(["FirstWorld", "SecondWorld"], plan.RecommendedSplit.Select(split => split.WorldName).ToArray());
    }

    [Fact]
    public async Task ProjectToShoppingPlan_MinimumUpfront_DoesNotTreatPartialDeepAsFullPurchase()
    {
        var service = CreateService();
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            quantityNeeded: 200,
            listings:
            [
                Listing(quantity: 120, price: 100, retainer: "Deep Partial"),
                Listing(quantity: 20, price: 180, retainer: "Old Seller")
            ])));

        var plan = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.MinimumUpfrontCost);

        var world = Assert.Single(plan.WorldOptions);
        Assert.False(world.HasSufficientStock);
        Assert.Equal(60, world.ShortfallQuantity);
        Assert.Null(plan.RecommendedWorld);
    }

    [Fact]
    public async Task ProjectToShoppingPlan_BulkValue_PartialDeepWorldRemainsInWorldOptions()
    {
        var service = CreateService();
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            quantityNeeded: 200,
            listings:
            [
                Listing(quantity: 120, price: 100, retainer: "Deep Partial"),
                Listing(quantity: 20, price: 180, retainer: "Old Seller")
            ])));

        var plan = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.BulkValue);

        var world = Assert.Single(plan.WorldOptions);
        Assert.Equal("Siren", world.WorldName);
        Assert.False(world.HasSufficientStock);
    }

    [Fact]
    public async Task ProjectToShoppingPlan_ScopeSaneQuantities_DoNotChangeProcurementListings()
    {
        var service = CreateService();
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            quantityNeeded: 100,
            listings:
            [
                Listing(quantity: 1, price: 10, retainer: "Bait"),
                Listing(quantity: 99, price: 100, retainer: "Fair Stack"),
                Listing(quantity: 10, price: 500, retainer: "Insane Stack")
            ])));

        var plan = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.MinimumUpfrontCost);

        var world = Assert.Single(plan.WorldOptions);
        Assert.Equal(1, world.TotalQuantityPurchased);
        Assert.Equal(10, world.TotalCost);
        Assert.Equal(["Bait"], world.Listings.Select(listing => listing.RetainerName).ToArray());
        Assert.Equal(100, analysis.Worlds.Single().ScopeSaneQuantity);
    }

    private static MarketPriceLadderAnalysisService CreateService() => new();

    private static MarketAnalysisRequest CreateRequest(
        int quantityNeeded = 10,
        DateTime? fetchedAtUtc = null,
        DateTime? responseUploadedAtUtc = null,
        IReadOnlyList<CachedListing>? listings = null,
        IReadOnlyList<CachedWorldData>? worlds = null)
    {
        var material = new MaterialAggregate
        {
            ItemId = 123,
            Name = "Test Item",
            TotalQuantity = quantityNeeded
        };
        var data = new CachedMarketData
        {
            ItemId = material.ItemId,
            DataCenter = "Aether",
            FetchedAt = fetchedAtUtc ?? DateTime.UtcNow,
            LastUploadTimeUnixMilliseconds = responseUploadedAtUtc.HasValue
                ? new DateTimeOffset(CacheTimeHelper.NormalizeToUtc(responseUploadedAtUtc.Value)).ToUnixTimeMilliseconds()
                : null,
            DCAveragePrice = 100,
            Worlds = (worlds ?? [World("Siren", listings ?? [Listing(quantityNeeded, 100, "Default")])]).ToList()
        };

        return new MarketAnalysisRequest
        {
            Items = [material],
            Evidence = new MarketEvidenceSet(
                new Dictionary<(int itemId, string dataCenter), CachedMarketData>
                {
                    [(material.ItemId, "Aether")] = data
                },
                [(material.ItemId, "Aether")],
                MarketFetchScope.SelectedDataCenter,
                ["Aether"],
                "Aether",
                "North America",
                maxAge: null,
                fetchedCount: 0,
                loadedAtUtc: DateTime.UtcNow)
        };
    }

    private static MarketAnalysisRequest CreateMissingDataRequest(int quantityNeeded)
    {
        var material = new MaterialAggregate
        {
            ItemId = 123,
            Name = "Test Item",
            TotalQuantity = quantityNeeded
        };

        return new MarketAnalysisRequest
        {
            Items = [material],
            Evidence = new MarketEvidenceSet(
                new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
                [(material.ItemId, "Aether")],
                MarketFetchScope.SelectedDataCenter,
                ["Aether"],
                "Aether",
                "North America",
                maxAge: null,
                fetchedCount: 0,
                loadedAtUtc: DateTime.UtcNow),
            ExpectedWorldsByDataCenter = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Aether"] = ["Siren"]
            }
        };
    }

    private static CachedListing Listing(int quantity, long price, string retainer, DateTime? reviewedAtUtc = null)
    {
        return new CachedListing
        {
            Quantity = quantity,
            PricePerUnit = price,
            RetainerName = retainer,
            LastReviewTimeUnix = reviewedAtUtc.HasValue
                ? new DateTimeOffset(CacheTimeHelper.NormalizeToUtc(reviewedAtUtc.Value)).ToUnixTimeSeconds()
                : null
        };
    }

    private static CachedWorldData World(string name, IReadOnlyList<CachedListing> listings, DateTime? uploadedAtUtc = null)
    {
        return new CachedWorldData
        {
            WorldName = name,
            LastUploadTimeUnixMilliseconds = uploadedAtUtc.HasValue
                ? new DateTimeOffset(CacheTimeHelper.NormalizeToUtc(uploadedAtUtc.Value)).ToUnixTimeMilliseconds()
                : null,
            Listings = listings.ToList()
        };
    }
}
