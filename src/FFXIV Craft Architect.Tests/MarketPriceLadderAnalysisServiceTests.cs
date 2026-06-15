using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketPriceLadderAnalysisServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_TinyCheapLeadListing_DoesNotCreateLocalShelfButUsesScopeCompetitiveStock()
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
        Assert.Equal(100, world.ScopeCompetitiveQuantity);
        Assert.Equal(MarketScoreBucket.Optimal, world.Scores.Single(score => score.Lens == MarketAcquisitionLens.BulkValue).ScoreBucket);
        Assert.Equal("100 competitive at ~99g", world.Scores.Single(score => score.Lens == MarketAcquisitionLens.BulkValue).Summary);
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
    public async Task AnalyzeAsync_CompetitiveAverageExcludesUncompetitiveAndInsaneListings()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            listings:
            [
                Listing(quantity: 100, price: 100, retainer: "Competitive Anchor"),
                Listing(quantity: 10, price: 140, retainer: "Competitive Stretch"),
                Listing(quantity: 10, price: 175, retainer: "Uncompetitive Ask"),
                Listing(quantity: 5, price: 250, retainer: "Insane Ask")
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var world = Assert.Single(analysis.Worlds);
        Assert.Equal(110, world.ScopeCompetitiveQuantity);
        Assert.Equal(15, world.ScopeInsaneQuantity + world.ScopeUncompetitiveQuantity);
        Assert.True(analysis.AnalysisScopeCompetitiveAverageUnitPrice < analysis.AnalysisScopeAverageUnitPrice);
        Assert.Contains(world.Listings, listing => listing.RetainerName == "Uncompetitive Ask" && listing.IsScopeUncompetitive && listing.PriceSanity == MarketListingPriceSanity.Sane);
        Assert.Contains(world.Listings, listing => listing.RetainerName == "Insane Ask" && listing.PriceSanity == MarketListingPriceSanity.Insane);
    }

    [Fact]
    public async Task AnalyzeAsync_ClassifiesListingCompetitivenessAndRetainsBooleanShims()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            listings:
            [
                Listing(quantity: 100, price: 100, retainer: "Deal Anchor"),
                Listing(quantity: 10, price: 140, retainer: "Competitive Stretch"),
                Listing(quantity: 10, price: 175, retainer: "Uncompetitive Ask"),
                Listing(quantity: 5, price: 250, retainer: "Insane Ask")
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var world = Assert.Single(analysis.Worlds);
        var deal = world.Listings.Single(listing => listing.RetainerName == "Deal Anchor");
        var competitive = world.Listings.Single(listing => listing.RetainerName == "Competitive Stretch");
        var uncompetitive = world.Listings.Single(listing => listing.RetainerName == "Uncompetitive Ask");
        var excluded = world.Listings.Single(listing => listing.RetainerName == "Insane Ask");
        Assert.Equal(MarketListingCompetitiveness.Deal, deal.Competitiveness);
        Assert.True(deal.IsScopeCompetitive);
        Assert.Equal(MarketListingCompetitiveness.Competitive, competitive.Competitiveness);
        Assert.True(competitive.IsScopeCompetitive);
        Assert.Equal(MarketListingCompetitiveness.Uncompetitive, uncompetitive.Competitiveness);
        Assert.True(uncompetitive.IsScopeUncompetitive);
        Assert.Equal(MarketListingCompetitiveness.Excluded, excluded.Competitiveness);
        Assert.False(excluded.IsScopeCompetitive);
        Assert.False(excluded.IsScopeUncompetitive);
    }

    [Fact]
    public async Task AnalyzeAsync_PriceSanity_UsesSingleEnumWithInsanePrecedence()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 10,
            worlds:
            [
                World("Siren",
                [
                    Listing(quantity: 10, price: 100, retainer: "Local Normal A"),
                    Listing(quantity: 10, price: 100, retainer: "Local Normal B"),
                    Listing(quantity: 1, price: 260, retainer: "Local Outlier"),
                    Listing(quantity: 1, price: 500, retainer: "Scam Ask")
                ]),
                World("Faerie",
                [
                    Listing(quantity: 100, price: 200, retainer: "Regional Anchor")
                ])
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var siren = analysis.Worlds.Single(world => world.WorldName == "Siren");
        Assert.Equal(MarketListingPriceSanity.Sane, siren.Listings.Single(listing => listing.RetainerName == "Local Normal A").PriceSanity);
        Assert.Equal(MarketListingPriceSanity.Outlier, siren.Listings.Single(listing => listing.RetainerName == "Local Outlier").PriceSanity);
        Assert.Equal(MarketListingPriceSanity.Insane, siren.Listings.Single(listing => listing.RetainerName == "Scam Ask").PriceSanity);
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

        Assert.True(analysis.AnalysisScopeCompetitiveAverageUnitPrice >= 100);
        Assert.True(analysis.AnalysisScopeBaselineUnitPrice >= 100);
        Assert.Equal(50, zalera.Listings.Where(listing => listing.PriceSanity == MarketListingPriceSanity.LowOutlier).Sum(listing => listing.Quantity));
        Assert.True(zalera.ScopeCompetitiveAverageUnitPrice < analysis.AnalysisScopeCompetitiveAverageUnitPrice);
    }

    [Fact]
    public async Task AnalyzeAsync_DuplicatedThinLowShelf_DoesNotAnchorRegionalBaseline()
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
    public async Task AnalyzeAsync_ThinSingletonBridgeShelf_DoesNotAnchorRegionalBaseline()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 2_997,
            itemId: 5_094,
            worlds:
            [
                World("Coeurl",
                [
                    Listing(quantity: 5, price: 1, retainer: "Low Outlier A"),
                    Listing(quantity: 9, price: 2, retainer: "Low Outlier B"),
                    Listing(quantity: 1, price: 800, retainer: "First Real Seller")
                ]),
                World("Malboro",
                [
                    Listing(quantity: 2, price: 12, retainer: "Thin Bridge Seller"),
                    Listing(quantity: 99, price: 982, retainer: "Representative Seller A"),
                    Listing(quantity: 99, price: 988, retainer: "Representative Seller B"),
                    Listing(quantity: 50, price: 988, retainer: "Representative Seller B")
                ]),
                World("Seraph",
                [
                    Listing(quantity: 70, price: 1_002, retainer: "Deep Seller A"),
                    Listing(quantity: 99, price: 1_102, retainer: "Deep Seller B"),
                    Listing(quantity: 99, price: 1_102, retainer: "Deep Seller B")
                ])
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        Assert.True(analysis.AnalysisScopeBaselineUnitPrice >= 800);
        Assert.True(analysis.AnalysisScopeAverageUnitPrice >= 800);
        Assert.Equal(2, analysis.PriceEvaluation?.ListingClassCounts.LowOutlierCount);
        Assert.True(analysis.Worlds.Sum(world => world.ScopeSaneQuantity) > 2);
        Assert.True(analysis.Worlds.Sum(world => world.ScopeCompetitiveQuantity) > 2);
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
        Assert.Equal(0, scamWorld.ScopeCompetitiveQuantity);
    }

    [Fact]
    public async Task AnalyzeAsync_CredibleAffordableShelfBeforeHighScamTail_IsNotLowOutlier()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 999,
            worlds:
            [
                World("NormalWorld",
                [
                    Listing(quantity: 999, price: 2_500, retainer: "Normal Seller"),
                    Listing(quantity: 2, price: 1_100_005, retainer: "Tiny High Shelf"),
                    Listing(quantity: 1, price: 100_000_000, retainer: "Scam One"),
                    Listing(quantity: 7, price: 142_857_142, retainer: "Scam Two"),
                    Listing(quantity: 4, price: 499_999_999, retainer: "Scam Three")
                ])
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));
        var world = Assert.Single(analysis.Worlds);

        Assert.Equal(2_500, analysis.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(999, world.ScopeCompetitiveQuantity);
        Assert.Equal(0, world.Listings.Where(listing => listing.PriceSanity == MarketListingPriceSanity.LowOutlier).Sum(listing => listing.Quantity));
        Assert.Equal(MarketListingPriceSanity.Insane, world.Listings.Single(listing => listing.RetainerName == "Tiny High Shelf").PriceSanity);
        Assert.Equal(14, world.Listings.Where(listing => listing.PriceSanity == MarketListingPriceSanity.Insane).Sum(listing => listing.Quantity));
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
    public async Task AnalyzeAsync_FullStackLowRegion_CanContributeToRegionalAverage()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 999,
            listings:
            [
                Listing(quantity: 99, price: 10, retainer: "Full Stack Seller"),
                Listing(quantity: 99, price: 100, retainer: "Higher Seller")
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        Assert.True(analysis.AnalysisScopeAverageUnitPrice < 100);
        Assert.Equal(10, analysis.PriceEvaluation?.CentralRegion.MinUnitPrice);
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
    public async Task AnalyzeAsync_SplitLowRegionWithSubstantialStacks_CanContributeToRegionalAverage()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 999,
            listings:
            [
                Listing(quantity: 40, price: 10, retainer: "Low Stack One"),
                Listing(quantity: 40, price: 12, retainer: "Low Stack Two"),
                Listing(quantity: 99, price: 100, retainer: "Higher Seller")
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        Assert.True(analysis.AnalysisScopeAverageUnitPrice < 100);
        Assert.Equal(10, analysis.PriceEvaluation?.CentralRegion.MinUnitPrice);
    }

    [Fact]
    public async Task AnalyzeAsync_ManyTinyLowListings_DoNotDefineRegionalAverage()
    {
        var service = CreateService();
        var tinyListings = Enumerable.Range(1, 50)
            .Select(index => Listing(quantity: 1, price: 10, retainer: $"Tiny Seller {index}"))
            .ToList();
        var request = CreateRequest(
            quantityNeeded: 999,
            listings: [.. tinyListings, Listing(quantity: 99, price: 100, retainer: "Higher Seller")]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        Assert.Equal(100, analysis.AnalysisScopeBaselineUnitPrice);
        Assert.Equal(100, analysis.PriceEvaluation?.CentralRegion.MinUnitPrice);
    }

    [Fact]
    public async Task AnalyzeAsync_PopulatesPriceEvaluationFromCurrentScopeContext()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            worlds:
            [
                World("Siren",
                [
                    Listing(quantity: 100, price: 100, retainer: "Anchor"),
                    Listing(quantity: 25, price: 130, retainer: "Stretch")
                ]),
                World("Faerie",
                [
                    Listing(quantity: 40, price: 120, retainer: "Neighbor")
                ])
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var evaluation = Assert.IsType<MarketPriceEvaluation>(analysis.PriceEvaluation);
        Assert.Equal(analysis.ItemId, evaluation.ItemId);
        Assert.Equal(analysis.Scope, evaluation.Scope);
        Assert.Equal(MarketPriceQualityPolicy.NqOnly, evaluation.QualityPolicy);
        Assert.Equal(analysis.LoadedAtUtc, evaluation.EvaluatedAtUtc);
        Assert.Equal(analysis.AnalysisScopeMedianUnitPrice, evaluation.CentralRegion.MedianUnitPrice);
        Assert.Equal(analysis.AnalysisScopeAverageUnitPrice, evaluation.CentralRegion.WeightedAverageUnitPrice);
        Assert.Equal(analysis.CompetitiveThresholdUnitPrice, evaluation.Thresholds.CompetitiveCeilingUnitPrice);
        Assert.Equal(analysis.SaneThresholdUnitPrice, evaluation.Thresholds.SaneCeilingUnitPrice);
        Assert.Equal(analysis.SaneThresholdUnitPrice, evaluation.Thresholds.InsaneFloorUnitPrice);
        Assert.Equal(3, evaluation.CentralRegion.ListingCount);
        Assert.Equal(165, evaluation.CentralRegion.TotalQuantity);
        Assert.Equal(3, evaluation.CentralRegion.DistinctRetainerCount);
        Assert.Equal(2, evaluation.CentralRegion.DistinctWorldCount);
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
    public async Task AnalyzeAsync_SingleQualityListings_RecordSpecificQualityPolicy()
    {
        var service = CreateService();
        var request = CreateRequest(
            listings:
            [
                Listing(quantity: 10, price: 100, retainer: "HQ Seller", isHq: true)
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var evaluation = Assert.IsType<MarketPriceEvaluation>(analysis.PriceEvaluation);
        Assert.Equal(MarketPriceQualityPolicy.HqOnly, evaluation.QualityPolicy);
        Assert.DoesNotContain(
            MarketPriceEvaluationReasonCode.QualityChannelFallbackToCombined,
            evaluation.Diagnostics.CompactReasonCodes);
    }

    [Fact]
    public async Task AnalyzeAsync_ExcludedQualityTail_DoesNotDriveCentralQualityPolicy()
    {
        var service = CreateService();
        var request = CreateRequest(
            listings:
            [
                Listing(quantity: 99, price: 100, retainer: "NQ Seller"),
                Listing(quantity: 1, price: 1_000, retainer: "HQ Scam", isHq: true)
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var evaluation = Assert.IsType<MarketPriceEvaluation>(analysis.PriceEvaluation);
        Assert.Equal(MarketPriceQualityPolicy.NqOnly, evaluation.QualityPolicy);
        Assert.DoesNotContain(
            MarketPriceEvaluationReasonCode.QualityChannelFallbackToCombined,
            evaluation.Diagnostics.CompactReasonCodes);
    }

    [Fact]
    public async Task AnalyzeAsync_ScopeCompetitiveAverageUnitPrice_IsCalculatedPerWorld()
    {
        var service = CreateService();
        var request = CreateRequest(
            quantityNeeded: 100,
            listings:
            [
                Listing(quantity: 100, price: 100, retainer: "Good Anchor"),
                Listing(quantity: 50, price: 120, retainer: "Good Stretch"),
                Listing(quantity: 10, price: 180, retainer: "High Ask")
            ]);

        var analysis = Assert.Single(await service.AnalyzeAsync(request));

        var world = Assert.Single(analysis.Worlds);
        Assert.Equal((100 * 100 + 50 * 120) / 150m, world.ScopeCompetitiveAverageUnitPrice);
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
        Assert.Equal(MarketDataQualityBucket.Old, staleWorld.DataQualityBucket);
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
        Assert.Equal(MarketDataQualityBucket.Aging, world.DataQualityBucket);
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
    public async Task AnalyzeAsync_PriceEvaluationCentralRegion_UsesWorstCentralListingDataQuality()
    {
        var service = CreateService();
        var now = DateTime.UtcNow;
        var analysis = Assert.Single(await service.AnalyzeAsync(CreateRequest(
            fetchedAtUtc: now.AddMinutes(-2),
            worlds:
            [
                World("StaleWorld", [Listing(10, 100, "Stale")], uploadedAtUtc: now.AddHours(-8))
            ])));

        Assert.Equal(MarketDataQualityBucket.Old, analysis.PriceEvaluation?.CentralRegion.DataQualityBucket);
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
        Assert.NotNull(currentWorld.MarketDataAge);
        Assert.NotNull(currentWorld.MarketUploadedAtUtc);
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
    public void ProjectToShoppingPlan_PartialWorldsChoosesLowestCostFullSplit()
    {
        var service = CreateService();
        var analysis = new MarketItemAnalysis
        {
            ItemId = 123,
            Name = "Test Item",
            QuantityNeeded = 100,
            Worlds =
            [
                WorldAnalysis("ExpensiveDeep", rank: 1, quantity: 80, price: 1_000),
                WorldAnalysis("CheapOne", rank: 2, quantity: 60, price: 10),
                WorldAnalysis("CheapTwo", rank: 3, quantity: 40, price: 20)
            ]
        };

        var plan = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.MinimumUpfrontCost);

        Assert.Null(plan.RecommendedWorld);
        Assert.NotNull(plan.RecommendedSplit);
        Assert.Equal(100, plan.RecommendedSplit.Sum(split => split.QuantityToBuy));
        Assert.Equal(1_400, plan.SplitTotalCost);
        Assert.Equal(["CheapOne", "CheapTwo"], plan.RecommendedSplit.Select(split => split.WorldName).ToArray());
    }

    [Fact]
    public void ProjectToShoppingPlan_CarriesGoodAverageForUnsupportedCostProjection()
    {
        var service = CreateService();
        var analysis = new MarketItemAnalysis
        {
            ItemId = 123,
            Name = "Test Item",
            QuantityNeeded = 999,
            AnalysisScopeCompetitiveAverageUnitPrice = 6_408,
            AnalysisScopeAverageUnitPrice = 8_165,
            Worlds =
            [
                WorldAnalysis("ThinWorld", rank: 1, quantity: 1, price: 3_000)
            ]
        };

        var plan = service.ProjectToShoppingPlan(analysis, MarketAcquisitionLens.MinimumUpfrontCost);

        Assert.Null(plan.RecommendedWorld);
        Assert.Null(plan.RecommendedSplit);
        Assert.Equal(6_408, plan.DCAveragePrice);
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
        Assert.Equal(80, world.ShortfallQuantity);
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
    public async Task ProjectToShoppingPlan_UsesCompetitiveListingsBeforeUncompetitiveFallback()
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
        Assert.Equal(100, world.TotalQuantityPurchased);
        Assert.Equal(9_910, world.TotalCost);
        Assert.Equal(["Bait", "Fair Stack"], world.Listings.Select(listing => listing.RetainerName).ToArray());
        Assert.Equal(100, analysis.Worlds.Single().ScopeSaneQuantity);
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
