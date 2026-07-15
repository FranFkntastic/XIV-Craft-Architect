using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketPriceLadderAnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_TinyCheapLeadListing_DoesNotBecomePrimaryUsableButKeepsRealBand()
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
        Assert.Equal(99, world.PrimaryUsableQuantity);
        Assert.Equal(MarketCoverageBucket.PartialDeep, world.CoverageBucket);
        Assert.DoesNotContain(world.Listings, listing => listing.RetainerName == "Bait" && listing.IsInPrimaryUsableBand);
        Assert.Equal(MarketScoreBucket.Competitive, world.Scores.Single(score => score.Lens == MarketAcquisitionLens.BulkValue).ScoreBucket);
        Assert.Equal("99 procurement-qualified at ~100g", world.Scores.Single(score => score.Lens == MarketAcquisitionLens.BulkValue).Summary);
    }

    [Fact]
    public async Task AnalyzeAsync_BulkValueRanksFullScopeCompetitiveStockAbovePartialDiscountStock()
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
            bait.Scores.Single(score => score.Lens == MarketAcquisitionLens.BulkValue).Score >
            deep.Scores.Single(score => score.Lens == MarketAcquisitionLens.BulkValue).Score);
    }
    [Fact]
    public async Task AnalyzeAsync_PrimaryProcurementShelf_DoesNotAverageLaterAcceptableShelves()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 1_998,
            listings:
            [
                Listing(quantity: 7, price: 500, retainer: "Tiny Deal"),
                Listing(quantity: 97, price: 578, retainer: "Thin Deal"),
                Listing(quantity: 999, price: 740, retainer: "Primary A"),
                Listing(quantity: 999, price: 768, retainer: "Primary B"),
                Listing(quantity: 3_546, price: 800, retainer: "Primary Excess"),
                Listing(quantity: 42_227, price: 945, retainer: "Next Shelf"),
                Listing(quantity: 47_880, price: 1_130, retainer: "Later Shelf"),
                Listing(quantity: 7_596, price: 1_311, retainer: "Pricy Shelf")
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));
        var world = Assert.Single(analysis.Worlds);

        Assert.Equal(5_544, world.PriceSignalQuantity);
        Assert.Equal(754m, world.CostToCoverUnitPrice);
        Assert.Equal(754m, analysis.CostToCoverUnitPrice);
        Assert.Equal(world.PrimaryUsableAverageUnitPrice, world.PriceSignalAverageUnitPrice);
        Assert.True(world.PriceSignalAverageUnitPrice < analysis.AnalysisCompetitiveAverageUnitPrice);
        Assert.DoesNotContain(world.PriceBands, band => band.MinUnitPrice >= 900 && band.IsPriceSignalBand);
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
        Assert.Equal(99, world.PrimaryUsableQuantity);
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
    public async Task AnalyzeAsync_LowOutliers_DoNotPoisonRegionalAveragesButRemainWorldValue()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            worlds:
            [
                World("Zalera",
                [
                    Listing(quantity: 25, price: 1, retainer: "Zalero'sfisher"),
                    Listing(quantity: 25, price: 1, retainer: "Zalero'sfisher"),
                    Listing(quantity: 100, price: 100, retainer: "Normal Seller")
                ]),
                World("Faerie",
                [
                    Listing(quantity: 100, price: 110, retainer: "Regional Seller")
                ])
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));
        var zalera = analysis.Worlds.Single(world => world.WorldName == "Zalera");

        Assert.True(analysis.AnalysisCompetitiveAverageUnitPrice >= 100);
        Assert.True(analysis.AnalysisScopeBaselineUnitPrice >= 100);
        Assert.Equal(50, zalera.Listings.Where(listing => listing.PriceSanity == MarketListingPriceSanity.LowOutlier).Sum(listing => listing.Quantity));
        Assert.True(zalera.PrimaryUsableAverageUnitPrice < analysis.AnalysisCompetitiveAverageUnitPrice);
    }

    [Fact]
    public async Task AnalyzeAsync_DuplicatedThinLowBand_DoesNotAnchorRegionalBaseline()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 2_997,
            itemId: 12_224,
            worlds:
            [
                World("Coeurl",
                [
                    Listing(quantity: 88, price: 17, retainer: "Lawuwu"),
                    Listing(quantity: 88, price: 17, retainer: "Lawuwu")
                ]),
                World("Goblin",
                [
                    Listing(quantity: 184, price: 117, retainer: "Normal Seller")
                ]),
                World("Adamantoise",
                [
                    Listing(quantity: 1_024, price: 268, retainer: "Bulk Seller")
                ]),
                World("Jenova",
                [
                    Listing(quantity: 2_997, price: 300, retainer: "Deep Seller")
                ])
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));
        var plan = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.BulkValue);

        Assert.True(analysis.AnalysisScopeBaselineUnitPrice > 100);
        Assert.Equal(2, analysis.PriceEvaluation?.ListingClassCounts.LowOutlierCount);
        Assert.Equal(176, analysis.Worlds.Single(world => world.WorldName == "Coeurl").Listings
            .Where(listing => listing.PriceSanity == MarketListingPriceSanity.LowOutlier)
            .Sum(listing => listing.Quantity));
        Assert.True(analysis.Worlds.Single(world => world.WorldName == "Jenova").ScopeSaneQuantity >= 2_997);
        Assert.True(plan.WorldOptions.Sum(world => world.TotalQuantityPurchased) > 176);
        Assert.NotNull(plan.RecommendedWorld);
    }


    [Fact]
    public async Task AnalyzeAsync_ExtremeHighOutlierStack_DoesNotPoisonRegionalAverages()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 999,
            worlds:
            [
                World("NormalWorld",
                [
                    Listing(quantity: 999, price: 2_500, retainer: "Normal Seller")
                ]),
                World("ScamWorld",
                [
                    Listing(quantity: 10_000, price: 400_000_000, retainer: "Scam Seller")
                ])
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));
        var scamWorld = analysis.Worlds.Single(world => world.WorldName == "ScamWorld");

        Assert.True(analysis.AnalysisScopeAverageUnitPrice < 10_000);
        Assert.True(analysis.AnalysisScopeBaselineUnitPrice < 10_000);
        Assert.True(analysis.SaneThresholdUnitPrice < 20_000);
        Assert.Equal(MarketListingPriceSanity.Insane, Assert.Single(scamWorld.Listings).PriceSanity);
        Assert.Equal(0, scamWorld.PrimaryUsableQuantity);
    }
    [Fact]
    public async Task AnalyzeAsync_RegionalEvaluation_DoesNotChangeWithRequestedQuantity()
    {
        var service = CreateService();
        var worlds =
            new[]
            {
                World("Siren",
                [
                    Listing(quantity: 50, price: 1, retainer: "Suspicious Single Seller"),
                    Listing(quantity: 100, price: 100, retainer: "Normal Seller")
                ])
            };

        var smallNeed = Assert.Single(await service.AnalyzeAsync(CreateRequest(quantityNeeded: 10, worlds: worlds)));
        var largeNeed = Assert.Single(await service.AnalyzeAsync(CreateRequest(quantityNeeded: 100, worlds: worlds)));

        Assert.Equal(smallNeed.AnalysisScopeMedianUnitPrice, largeNeed.AnalysisScopeMedianUnitPrice);
        Assert.Equal(smallNeed.AnalysisScopeAverageUnitPrice, largeNeed.AnalysisScopeAverageUnitPrice);
        Assert.Equal(smallNeed.AnalysisScopeBaselineUnitPrice, largeNeed.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(smallNeed.CompetitiveThresholdUnitPrice, largeNeed.CompetitiveThresholdUnitPrice);
        Assert.Equal(100, smallNeed.AnalysisScopeBaselineUnitPrice);
    }
    [Fact]
    public async Task AnalyzeAsync_ElementalCommoditySingleSmallStackLowRegion_DoesNotDefineRegionalAverage()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 999,
            itemId: 8,
            listings:
            [
                Listing(quantity: 99, price: 10, retainer: "Small Crystal Stack"),
                Listing(quantity: 999, price: 100, retainer: "Higher Crystal Seller")
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        Assert.Equal(100, analysis.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(100, analysis.PriceEvaluation?.CentralRegion.MinUnitPrice);
    }

    [Fact]
    public async Task AnalyzeAsync_ElementalCommodityDeepLowRegion_CanDefineRegionalAverage()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 999,
            itemId: 8,
            listings:
            [
                Listing(quantity: 1_000, price: 10, retainer: "Deep Crystal Seller"),
                Listing(quantity: 999, price: 100, retainer: "Higher Crystal Seller")
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        Assert.True(analysis.AnalysisScopeAverageUnitPrice < 100);
        Assert.Equal(10, analysis.PriceEvaluation?.CentralRegion.MinUnitPrice);
    }

    [Fact]
    public async Task AnalyzeAsync_PopulatesScopePriceBandsAcrossSelectedScopeWorlds()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            worlds:
            [
                World("Siren",
                [
                    Listing(quantity: 10, price: 100, retainer: "Anchor A")
                ]),
                World("Faerie",
                [
                    Listing(quantity: 20, price: 105, retainer: "Anchor B"),
                    Listing(quantity: 30, price: 107, retainer: "Anchor C")
                ]),
                World("Cactuar",
                [
                    Listing(quantity: 5, price: 180, retainer: "High Ask")
                ])
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var competitiveBand = Assert.Single(analysis.ScopePriceBands, band => band.MinUnitPrice == 100);
        Assert.Equal(107, competitiveBand.MaxUnitPrice);
        Assert.Equal((10 * 100 + 20 * 105 + 30 * 107) / 60m, competitiveBand.WeightedAverageUnitPrice);
        Assert.Equal(60, competitiveBand.TotalQuantity);
        Assert.Equal(3, competitiveBand.ListingCount);
        Assert.Equal(2, competitiveBand.DistinctWorldCount);
        Assert.Equal(3, competitiveBand.DistinctRetainerCount);
        Assert.Equal(PriceBandCompetitiveness.Competitive, competitiveBand.Competitiveness);
        Assert.Equal(PriceBandDepth.Deep, competitiveBand.Depth);
        Assert.True(competitiveBand.BreakPercentToNextBand > 60);

    }
    [Fact]
    public async Task AnalyzeAsync_MixedQualityListings_RecordCombinedQualityFallback()
    {
        var service = CreateService();
        var request = CreateRequest(
            listings:
            [
                Listing(quantity: 10, price: 100, retainer: "NQ Seller"),
                Listing(quantity: 10, price: 110, retainer: "HQ Seller", isHq: true)
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var evaluation = Assert.IsType<MarketPriceEvaluation>(analysis.PriceEvaluation);
        Assert.Equal(MarketPriceQualityPolicy.Combined, evaluation.QualityPolicy);
        Assert.Contains(
            MarketPriceEvaluationReasonCode.QualityChannelFallbackToCombined,
            evaluation.Diagnostics.CompactReasonCodes);
    }


    [Fact]
    public async Task AnalyzeAsync_SharpPriceJump_ReportsPriceBreakAfterPrimaryUsableStock()
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

        var band = Assert.Single(Assert.Single(Assert.Single(analyses).Worlds).PriceBands, band => band.IsPrimaryUsableBand);
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
        Assert.Equal(120, world.PrimaryUsableQuantity);
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
        Assert.Equal(MarketDataQualityBucket.Old, staleWorld.DataQualityBucket);
        Assert.True(freshWorld.DataQualityScore > staleWorld.DataQualityScore);
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
    public async Task ProjectToShoppingPlan_ThinSaneShelf_RemainsAvailableForProcurementCoverage()
    {
        var service = CreateService();
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            quantityNeeded: 1_000,
            worlds:
            [
                World("Excalibur", [Listing(200, 100, "Thin Seller")]),
                World("Jenova", [Listing(800, 110, "Deep Seller")])
            ])));

        var excaliburAnalysis = analysis.Worlds.Single(world => world.WorldName == "Excalibur");
        Assert.Equal(PriceBandDepth.Thin, Assert.Single(excaliburAnalysis.PriceBands).Depth);
        Assert.Equal(0, excaliburAnalysis.PrimaryUsableQuantity);

        var plan = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.BulkValue);

        var excaliburPlan = plan.WorldOptions.Single(world => world.WorldName == "Excalibur");
        Assert.Equal(200, excaliburPlan.TotalQuantityPurchased);
        Assert.Equal(200, Assert.Single(excaliburPlan.Listings).Quantity);
        Assert.NotNull(plan.RecommendedSplit);
        Assert.Contains(plan.RecommendedSplit!, purchase => purchase.WorldName == "Excalibur");
        Assert.NotNull(plan.CoverageSet);
        Assert.Contains(
            plan.CoverageSet!.AllCandidates,
            candidate => candidate.Worlds.Any(world => world.WorldName == "Excalibur"));
    }

    [Fact]
    public async Task ProjectToShoppingPlan_ScopePolicy_ExcludesBaitAndInsaneButKeepsCredibleLocalOutlier()
    {
        var service = CreateService();
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            quantityNeeded: 100,
            worlds:
            [
                World("Excalibur",
                [
                    Listing(25, 1, "Bait Seller A"),
                    Listing(25, 1, "Bait Seller B"),
                    Listing(100, 100, "Sane Seller"),
                    Listing(2, 10_000, "Wild Seller")
                ]),
                World("Jenova", [Listing(100, 110, "Deep Seller")])
            ])));
        var excaliburAnalysis = analysis.Worlds.Single(world => world.WorldName == "Excalibur");
        Assert.Contains(excaliburAnalysis.Listings, listing => listing.PriceSanity == MarketListingPriceSanity.LowOutlier);
        Assert.Contains(excaliburAnalysis.Listings, listing =>
            listing.RetainerName == "Sane Seller" &&
            listing.PriceSanity == MarketListingPriceSanity.Outlier &&
            listing.Competitiveness == MarketListingCompetitiveness.Deal);
        Assert.Contains(excaliburAnalysis.Listings, listing => listing.PriceSanity == MarketListingPriceSanity.Insane);

        var plan = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.BulkValue);

        var listing = Assert.Single(plan.WorldOptions.Single(world => world.WorldName == "Excalibur").Listings);
        Assert.Equal("Sane Seller", listing.RetainerName);
        Assert.Equal(100, listing.Quantity);
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


    private static MarketPriceLadderAnalysisService CreateService() => new();

    private static MarketAnalysisRequest CreateRequest(
        int quantityNeeded = 10,
        DateTime? fetchedAtUtc = null,
        DateTime? responseUploadedAtUtc = null,
        IReadOnlyList<CachedListing>? listings = null,
        IReadOnlyList<CachedWorldData>? worlds = null,
        int itemId = 123)
    {
        var material = new MaterialAggregate
        {
            ItemId = itemId,
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

    private static CachedListing Listing(
        int quantity,
        long price,
        string retainer,
        DateTime? reviewedAtUtc = null,
        bool isHq = false)
    {
        return new CachedListing
        {
            Quantity = quantity,
            PricePerUnit = price,
            RetainerName = retainer,
            IsHq = isHq,
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

    private static WorldMarketAnalysis WorldAnalysis(string worldName, int rank, int quantity, long price)
    {
        return new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = worldName,
            QuantityNeeded = 100,
            TotalSaneQuantity = quantity,
            DataQualityBucket = MarketDataQualityBucket.Current,
            DataQualityScore = 100,
            Listings =
            [
                new AnalyzedMarketListing
                {
                    Quantity = quantity,
                    PricePerUnit = price,
                    RetainerName = worldName,
                    PriceSanity = MarketListingPriceSanity.Sane
                }
            ],
            Scores =
            [
                new WorldLensScore
                {
                    Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                    Rank = rank,
                    ScoreBucket = MarketScoreBucket.PoorFit
                }
            ]
        };
    }
}
