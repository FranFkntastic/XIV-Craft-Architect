using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisGridViewServiceTests
{
    [Fact]
    public void GetOrderedPlans_DefaultRecommendedSort_UsesBestWorldRankThenName()
    {
        var plans = new[]
        {
            Plan(300, "Zinc Ore", 300),
            Plan(100, "Silver Ore", 100),
            Plan(200, "Copper Ore", 200)
        };
        var analyses = new[]
        {
            Analysis(100, "Silver Ore", rank: 2),
            Analysis(200, "Copper Ore", rank: 1),
            Analysis(300, "Zinc Ore", rank: 1)
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedPlans(
            plans,
            analyses,
            MarketAcquisitionLens.BulkValue,
            MarketSortOption.ByRecommended,
            sortColumn: null,
            sortDescending: false);

        Assert.Equal([200, 300, 100], ordered.Select(plan => plan.ItemId));
    }

    [Fact]
    public void GetOrderedPlans_ColumnSortOverridesDefaultSortAndTogglesDirection()
    {
        var plans = new[]
        {
            Plan(100, "Silver Ore", 500),
            Plan(200, "Copper Ore", 100),
            Plan(300, "Zinc Ore", 300)
        };

        var ascending = MarketAnalysisGridViewService.GetOrderedPlans(
            plans,
            Array.Empty<MarketItemAnalysis>(),
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketSortOption.ByRecommended,
            MarketAnalysisGridSortColumn.Total,
            sortDescending: false);
        var descending = MarketAnalysisGridViewService.GetOrderedPlans(
            plans,
            Array.Empty<MarketItemAnalysis>(),
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketSortOption.ByRecommended,
            MarketAnalysisGridSortColumn.Total,
            sortDescending: true);

        Assert.Equal([200, 300, 100], ascending.Select(plan => plan.ItemId));
        Assert.Equal([100, 300, 200], descending.Select(plan => plan.ItemId));
    }

    [Fact]
    public void GetOrderedPlans_QuantitySort_UsesNeededQuantityNotAvailableStock()
    {
        var lowNeedHighStock = Plan(100, "Low Need", 100);
        lowNeedHighStock.QuantityNeeded = 2;
        lowNeedHighStock.WorldOptions[0].TotalQuantityPurchased = 500;

        var highNeedThinStock = Plan(200, "High Need", 100);
        highNeedThinStock.QuantityNeeded = 50;
        highNeedThinStock.WorldOptions[0].TotalQuantityPurchased = 1;

        var ascending = MarketAnalysisGridViewService.GetOrderedPlans(
            [highNeedThinStock, lowNeedHighStock],
            Array.Empty<MarketItemAnalysis>(),
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketSortOption.ByRecommended,
            MarketAnalysisGridSortColumn.Quantity,
            sortDescending: false);
        var descending = MarketAnalysisGridViewService.GetOrderedPlans(
            [highNeedThinStock, lowNeedHighStock],
            Array.Empty<MarketItemAnalysis>(),
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketSortOption.ByRecommended,
            MarketAnalysisGridSortColumn.Quantity,
            sortDescending: true);

        Assert.Equal([100, 200], ascending.Select(plan => plan.ItemId));
        Assert.Equal([200, 100], descending.Select(plan => plan.ItemId));
    }

    [Fact]
    public void GetOrderedPlans_CoverageSort_UsesScopeCompetitiveStock()
    {
        var plans = new[]
        {
            Plan(100, "Baited Ore", 500),
            Plan(200, "Thin Ore", 100)
        };
        var analyses = new[]
        {
            ScopeAnalysis(100, "Baited Ore", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100),
            ScopeAnalysis(200, "Thin Ore", scopeSaneQuantity: 10, scopePrimaryUsableQuantity: 10)
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedPlans(
            plans,
            analyses,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketSortOption.ByRecommended,
            MarketAnalysisGridSortColumn.Coverage,
            sortDescending: false);

        Assert.Equal([100, 200], ordered.Select(plan => plan.ItemId));
    }

    [Fact]
    public void FormatCoverageLabel_UsesScopeAwareCoverageBuckets()
    {
        var plan = Plan(100, "Baited Ore", 500);
        var analysisByItemId = new Dictionary<int, MarketItemAnalysis>
        {
            [100] = new()
            {
                ItemId = 100,
                Name = "Baited Ore",
                Worlds =
                [
                    ScopeWorld("Full", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100),
                    ScopeWorld("Thin", scopeSaneQuantity: 25, scopePrimaryUsableQuantity: 5),
                    ScopeWorld("Missing", scopeSaneQuantity: 0, scopePrimaryUsableQuantity: 0)
                ]
            }
        };

        var label = MarketAnalysisGridViewService.FormatCoverageLabel(plan, analysisByItemId);

        Assert.Equal("1 full, 1 partial across 3 worlds", label);
    }

    [Fact]
    public void FormatCoverage_UsesCompetitiveStockWhenScopeContextExists()
    {
        var world = ScopeWorld("Uncompetitive Full", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 25);

        Assert.Equal("25/100", MarketAnalysisGridViewService.FormatCoverage(world));
        Assert.Equal(MarketCoverageBucket.PartialThin, MarketAnalysisGridViewService.GetDisplayCoverageBucket(world));
    }

    [Fact]
    public void FormatCoverage_DoesNotCapViableStockAtNeededQuantity()
    {
        var world = new WorldMarketAnalysis
        {
            DataCenter = "Crystal",
            WorldName = "Surplus",
            QuantityNeeded = 5_994,
            SaneThresholdUnitPrice = 200,
            ScopeSaneQuantity = 7_000,
            PrimaryUsableQuantity = 7_000
        };

        Assert.Equal("7,000/5,994", MarketAnalysisGridViewService.FormatCoverage(world));
        Assert.Equal(MarketCoverageBucket.Full, MarketAnalysisGridViewService.GetDisplayCoverageBucket(world));
    }

    [Fact]
    public void GetOrderedWorlds_UsesScopeAwareDisplayScoreBeforeStoredLensRank()
    {
        var optimal = ScopeWorld("Later Rank", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, rank: 50);
        var partial = ScopeWorld("Early Rank", scopeSaneQuantity: 50, scopePrimaryUsableQuantity: 10, rank: 1);
        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds = [partial, optimal]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost);

        Assert.Equal(["Later Rank", "Early Rank"], ordered.Select(world => world.WorldName));
    }

    [Fact]
    public void GetOrderedWorlds_WorldColumnSortsAlphabeticallyAndTogglesDirection()
    {
        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds =
            [
                ScopeWorld("Siren", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100),
                ScopeWorld("Faerie", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100),
                ScopeWorld("Jenova", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100)
            ]
        };

        var ascending = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.World,
            sortDescending: false);
        var descending = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.World,
            sortDescending: true);

        Assert.Equal(["Faerie", "Jenova", "Siren"], ascending.Select(world => world.WorldName));
        Assert.Equal(["Siren", "Jenova", "Faerie"], descending.Select(world => world.WorldName));
    }

    [Fact]
    public void GetOrderedWorlds_StockDepthSort_UsesPriceSignalQuantity()
    {
        var broadStockThinBand = ScopeWorld("Broad Stock Thin Band", scopeSaneQuantity: 5, scopePrimaryUsableQuantity: 0, worldCompetitiveAverage: 100);
        broadStockThinBand.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 100,
            MaxUnitPrice = 100,
            WeightedAverageUnitPrice = 100,
            Quantity = 5,
            ListingCount = 1,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = false,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Thin
        });

        var deeperBand = ScopeWorld("Deeper Band", scopeSaneQuantity: 80, scopePrimaryUsableQuantity: 80, worldCompetitiveAverage: 122);
        deeperBand.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 2,
            MinUnitPrice = 120,
            MaxUnitPrice = 124,
            WeightedAverageUnitPrice = 122,
            Quantity = 80,
            ListingCount = 3,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Deep,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds = [broadStockThinBand, deeperBand]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.StockDepth,
            sortDescending: true);

        Assert.Equal(["Deeper Band", "Broad Stock Thin Band"], ordered.Select(world => world.WorldName));
    }

    [Fact]
    public void GetOrderedWorlds_UnitPriceSort_UsesPriceSignalAverageForCompetitivenessOverlay()
    {
        var cheaperLaterRank = ScopeWorld("Cheaper Later Rank", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, rank: 50, worldCompetitiveAverage: 103);
        cheaperLaterRank.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 100,
            MaxUnitPrice = 105,
            WeightedAverageUnitPrice = 103,
            Quantity = 40,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Usable,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        var pricierEarlyRank = ScopeWorld("Pricier Early Rank", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, rank: 1, worldCompetitiveAverage: 153);
        pricierEarlyRank.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 150,
            MaxUnitPrice = 155,
            WeightedAverageUnitPrice = 153,
            Quantity = 80,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Deep,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds = [pricierEarlyRank, cheaperLaterRank]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.PriceValue,
            sortDescending: false,
            MarketAnalysisEvidenceOverlay.CompetitivenessOverlay);

        Assert.Equal(["Cheaper Later Rank", "Pricier Early Rank"], ordered.Select(world => world.WorldName));
    }

    [Fact]
    public void GetOrderedWorlds_UnitPriceSortDescending_UsesPriceSignalAverage()
    {
        var cheaperWorld = ScopeWorld("Cheaper", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, worldCompetitiveAverage: 103);
        cheaperWorld.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 100,
            MaxUnitPrice = 105,
            WeightedAverageUnitPrice = 103,
            Quantity = 40,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Usable,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        var pricierWorld = ScopeWorld("Pricier", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, worldCompetitiveAverage: 153);
        pricierWorld.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 150,
            MaxUnitPrice = 155,
            WeightedAverageUnitPrice = 153,
            Quantity = 80,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Deep,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds = [cheaperWorld, pricierWorld]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.PriceValue,
            sortDescending: true,
            MarketAnalysisEvidenceOverlay.PriceBandOverlay);

        Assert.Equal(["Pricier", "Cheaper"], ordered.Select(world => world.WorldName));
    }

    [Fact]
    public void GetOrderedWorlds_UnitPriceSort_UsesPriceSignalAverageForPriceBandOverlay()
    {
        var thinCheapWorld = ScopeWorld("Thin Cheap", scopeSaneQuantity: 2, scopePrimaryUsableQuantity: 0, rank: 1, worldCompetitiveAverage: 10);
        thinCheapWorld.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 10,
            MaxUnitPrice = 10,
            WeightedAverageUnitPrice = 10,
            Quantity = 2,
            ListingCount = 1,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = false,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Thin
        });

        var competitiveWorld = ScopeWorld("Competitive", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, rank: 50, worldCompetitiveAverage: 100);
        competitiveWorld.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 100,
            MaxUnitPrice = 100,
            WeightedAverageUnitPrice = 100,
            Quantity = 100,
            ListingCount = 4,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Deep,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds = [thinCheapWorld, competitiveWorld]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.PriceValue,
            sortDescending: false,
            MarketAnalysisEvidenceOverlay.PriceBandOverlay);

        Assert.Equal(["Thin Cheap", "Competitive"], ordered.Select(world => world.WorldName));
    }

    [Fact]
    public void FormatWorldUnitPrice_UsesPriceSignalInsteadOfLowOutlierBand()
    {
        var world = ScopeWorld("Diabolos", scopeSaneQuantity: 4_402, scopePrimaryUsableQuantity: 4_402, worldCompetitiveAverage: 890);
        world.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 1,
            MaxUnitPrice = 1,
            WeightedAverageUnitPrice = 1,
            Quantity = 4,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.LowOutlier,
            Depth = PriceBandDepth.Thin
        });
        world.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 2,
            LastListingIndex = 3,
            MinUnitPrice = 880,
            MaxUnitPrice = 900,
            WeightedAverageUnitPrice = 890,
            Quantity = 4_402,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Deep,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        Assert.Equal("~890g / unit", MarketAnalysisGridViewService.FormatWorldUnitPrice(world));
        Assert.Equal("4,402", MarketAnalysisGridViewService.FormatWorldMarketDepthQuantity(world));
        Assert.Equal("deep", MarketAnalysisGridViewService.FormatWorldMarketDepthDescriptor(world));
        Assert.Equal("is-optimal", MarketAnalysisGridViewService.GetWorldUnitPriceScoreClass(world));
    }

    [Fact]
    public void FormatWorldUnitPrice_UsesThinPriceSignalWhenProcurementCandidateIsMissing()
    {
        var world = new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = "ThinWorld",
            QuantityNeeded = 999,
            SaneThresholdUnitPrice = 14_200,
            ScopeSaneQuantity = 150,
            PrimaryUsableQuantity = 0,
            AnalysisCompetitiveAverageUnitPrice = 7_100,
            PriceSignalQuantity = 150,
            PriceSignalAverageUnitPrice = 7_000,
            PriceSignalDepth = PriceBandDepth.Thin,
            DataQualityBucket = MarketDataQualityBucket.Current,
            PriceBands =
            [
                new MarketPriceBand
                {
                    FirstListingIndex = 0,
                    LastListingIndex = 0,
                    MinUnitPrice = 7_000,
                    MaxUnitPrice = 7_000,
                    WeightedAverageUnitPrice = 7_000,
                    Quantity = 150,
                    ListingCount = 1,
                    Competitiveness = PriceBandCompetitiveness.Competitive,
                    Depth = PriceBandDepth.Thin,
                    IsPriceSignalBand = true,
                    IsPrimaryUsableBand = false
                }
            ]
        };

        Assert.Equal("~7,000g / unit", MarketAnalysisGridViewService.FormatWorldUnitPrice(world));
        Assert.Equal("150", MarketAnalysisGridViewService.FormatWorldMarketDepthQuantity(world));
        Assert.Equal("thin", MarketAnalysisGridViewService.FormatWorldMarketDepthDescriptor(world));
        Assert.Equal("-1%", MarketAnalysisGridViewService.FormatCompetitiveValue(world));
        Assert.Equal("is-competitive", MarketAnalysisGridViewService.GetWorldUnitPriceScoreClass(world));
        Assert.Equal(MarketScoreBucket.Unavailable, MarketAnalysisGridViewService.GetDisplayScoreBucket(world, MarketAcquisitionLens.BulkValue));
    }

    [Fact]
    public void GetOrderedWorlds_PriceValueSort_PriceBandOverlayTreatsCredibleCompetitiveBandAsCompetitive()
    {
        var credibleCompetitiveWorld = ScopeWorld("Credible Competitive", scopeSaneQuantity: 30, scopePrimaryUsableQuantity: 30, rank: 50, worldCompetitiveAverage: 103);
        credibleCompetitiveWorld.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 100,
            MaxUnitPrice = 105,
            WeightedAverageUnitPrice = 103,
            Quantity = 30,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Usable,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        var deeperNonCompetitiveWorld = ScopeWorld("Deeper Noncompetitive", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 0, rank: 1);
        deeperNonCompetitiveWorld.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 150,
            MaxUnitPrice = 150,
            WeightedAverageUnitPrice = 150,
            Quantity = 100,
            ListingCount = 1,
            IsPrimaryUsableBand = false,
            Competitiveness = PriceBandCompetitiveness.Uncompetitive,
            Depth = PriceBandDepth.Deep
        });

        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds = [deeperNonCompetitiveWorld, credibleCompetitiveWorld]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.PriceValue,
            sortDescending: false,
            MarketAnalysisEvidenceOverlay.PriceBandOverlay);

        Assert.Equal(["Credible Competitive", "Deeper Noncompetitive"], ordered.Select(world => world.WorldName));
    }

    [Fact]
    public void FormatWorldUnitPriceAndStockDepth_ExposePriceSignalWithoutRoleText()
    {
        var world = ScopeWorld("Credible Competitive", scopeSaneQuantity: 30, scopePrimaryUsableQuantity: 30, worldCompetitiveAverage: 103);
        world.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 100,
            MaxUnitPrice = 105,
            WeightedAverageUnitPrice = 103,
            Quantity = 30,
            ListingCount = 2,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Usable,
            IsPriceSignalBand = true,
            IsPrimaryUsableBand = true
        });

        Assert.Equal("~103g / unit", MarketAnalysisGridViewService.FormatWorldUnitPrice(world));
        Assert.Equal("30", MarketAnalysisGridViewService.FormatWorldMarketDepthQuantity(world));
        Assert.Equal("moderate", MarketAnalysisGridViewService.FormatWorldMarketDepthDescriptor(world));
        Assert.Equal("is-optimal", MarketAnalysisGridViewService.GetWorldPriceBandScoreClass(world));
        Assert.Equal("is-optimal", MarketAnalysisGridViewService.GetWorldUnitPriceScoreClass(world));
    }

    [Fact]
    public void GetWorldUnitPriceScoreClass_DoesNotDependOnEvidenceOverlay()
    {
        var world = ScopeWorld("Thin", scopeSaneQuantity: 2, scopePrimaryUsableQuantity: 2, rank: 50);
        world.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 10,
            MaxUnitPrice = 10,
            WeightedAverageUnitPrice = 10,
            Quantity = 2,
            ListingCount = 1,
            IsPrimaryUsableBand = false,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Thin
        });

        Assert.Equal("is-unavailable", MarketAnalysisGridViewService.GetWorldUnitPriceScoreClass(world));
        Assert.Equal(
            MarketAnalysisGridViewService.GetWorldPriceBandScoreClass(world),
            MarketAnalysisGridViewService.GetWorldUnitPriceScoreClass(world));
    }

    [Fact]
    public void GetWorldPriceBandScoreClass_UsesExistingWorldScorePalette()
    {
        var thin = ScopeWorld("Thin", scopeSaneQuantity: 2, scopePrimaryUsableQuantity: 2);
        thin.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 10,
            MaxUnitPrice = 10,
            WeightedAverageUnitPrice = 10,
            Quantity = 2,
            ListingCount = 1,
            IsPrimaryUsableBand = false,
            Competitiveness = PriceBandCompetitiveness.Competitive,
            Depth = PriceBandDepth.Thin
        });

        var unclassified = ScopeWorld("Unclassified", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 0);
        unclassified.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 150,
            MaxUnitPrice = 150,
            WeightedAverageUnitPrice = 150,
            Quantity = 100,
            ListingCount = 1,
            IsPrimaryUsableBand = false,
            Competitiveness = PriceBandCompetitiveness.Uncompetitive,
            Depth = PriceBandDepth.Deep
        });

        Assert.Equal("is-unavailable", MarketAnalysisGridViewService.GetWorldPriceBandScoreClass(thin));
        Assert.Equal("is-unavailable", MarketAnalysisGridViewService.GetWorldPriceBandScoreClass(unclassified));
    }

    [Fact]
    public void GetOrderedWorlds_ValueSort_UsesProcurementSignalDiscountAgainstMarketFallback()
    {
        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds =
            [
                ScopeWorld("Small Discount", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 95),
                ScopeWorld("Large Discount", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 80),
                ScopeWorld("Above Average", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 110)
            ]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.Value,
            sortDescending: false);

        Assert.Equal(["Large Discount", "Small Discount", "Above Average"], ordered.Select(world => world.WorldName));
    }

    [Theory]
    [InlineData(80, "-20%")]
    [InlineData(100, "0%")]
    [InlineData(110, "+10%")]
    public void FormatCompetitiveValue_DescribesDistanceFromMarketFallback(decimal worldAverage, string expected)
    {
        var world = ScopeWorld("Siren", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, goodAverage: 100, worldCompetitiveAverage: worldAverage);

        Assert.Equal(expected, MarketAnalysisGridViewService.FormatCompetitiveValue(world));
    }

    [Fact]
    public void FormatCompetitiveValue_PriceSignalDiscountContributesToWorldValue()
    {
        var world = ScopeWorld("Zalera", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 1);

        Assert.Equal("-99%", MarketAnalysisGridViewService.FormatCompetitiveValue(world));
    }

    [Fact]
    public void FormatCompetitiveValueTooltip_ExplainsSignedDifferenceFromMarketFallback()
    {
        var world = ScopeWorld("Siren", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, goodAverage: 1_100, worldCompetitiveAverage: 1_055);

        var tooltip = MarketAnalysisGridViewService.FormatCompetitiveValueTooltip(world);

        Assert.Equal("Siren's procurement signal is 4% less than the regional market evidence fallback: 1,055g vs 1,100g.", tooltip);
    }

    [Fact]
    public void FormatCompetitiveValueTooltip_ExplainsSignedDifferenceAboveMarketFallback()
    {
        var world = ScopeWorld("Siren", scopeSaneQuantity: 100, scopePrimaryUsableQuantity: 100, goodAverage: 1_100, worldCompetitiveAverage: 1_210);

        var tooltip = MarketAnalysisGridViewService.FormatCompetitiveValueTooltip(world);

        Assert.Equal("Siren's procurement signal is 10% greater than the regional market evidence fallback: 1,210g vs 1,100g.", tooltip);
    }

    [Fact]
    public void GetListingDividersBefore_AddsAverageThresholdDividersWithoutDuplicates()
    {
        var world = new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = "Siren",
            QuantityNeeded = 100,
            SaneThresholdUnitPrice = 200,
            ScopeSaneQuantity = 100,
            PrimaryUsableQuantity = 100,
            AnalysisCompetitiveAverageUnitPrice = 100,
            AnalysisScopeAverageUnitPrice = 150,
            DataQualityBucket = MarketDataQualityBucket.Current,
            PriceBands =
            [
                new MarketPriceBand
                {
                    FirstListingIndex = 0,
                    LastListingIndex = 0,
                    WeightedAverageUnitPrice = 80,
                    NextBreakPercent = 100,
                    IsPrimaryUsableBand = true
                }
            ],
            Listings =
            [
                new AnalyzedMarketListing { SortIndex = 0, Quantity = 10, PricePerUnit = 80 },
                new AnalyzedMarketListing { SortIndex = 1, Quantity = 10, PricePerUnit = 120 },
                new AnalyzedMarketListing { SortIndex = 2, Quantity = 10, PricePerUnit = 170 }
            ],
            Scores =
            [
                new WorldLensScore
                {
                    Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                    Rank = 1,
                    ScoreBucket = MarketScoreBucket.Unavailable
                }
            ]
        };

        var first = MarketAnalysisGridViewService.GetListingDividersBefore(world, world.Listings[0]);
        var second = MarketAnalysisGridViewService.GetListingDividersBefore(world, world.Listings[1]);
        var third = MarketAnalysisGridViewService.GetListingDividersBefore(world, world.Listings[2]);

        Assert.Empty(first);
        Assert.Equal(["Price band +100%", "Above market fallback"], second.Select(divider => divider.Label));
        Assert.Equal(["Above avg"], third.Select(divider => divider.Label));
    }

    [Theory]
    [InlineData(null, false, MarketAnalysisWorldGridSortColumn.World, false)]
    [InlineData(MarketAnalysisWorldGridSortColumn.World, false, MarketAnalysisWorldGridSortColumn.World, true)]
    [InlineData(MarketAnalysisWorldGridSortColumn.World, true, MarketAnalysisWorldGridSortColumn.World, false)]
    [InlineData(MarketAnalysisWorldGridSortColumn.Coverage, true, MarketAnalysisWorldGridSortColumn.World, false)]
    public void ToggleWorldSort_UpdatesColumnAndDirection(
        MarketAnalysisWorldGridSortColumn? currentColumn,
        bool currentDescending,
        MarketAnalysisWorldGridSortColumn clickedColumn,
        bool expectedDescending)
    {
        var next = MarketAnalysisGridViewService.ToggleWorldSort(currentColumn, currentDescending, clickedColumn);

        Assert.Equal(clickedColumn, next.Column);
        Assert.Equal(expectedDescending, next.Descending);
    }

    [Fact]
    public void FormatAnalysisScopePriceSummary_ShowsMarketFallbackAndThresholds()
    {
        var analysis = new MarketItemAnalysis
        {
            AnalysisScopeBaselineUnitPrice = 100,
            AnalysisScopeAverageUnitPrice = 125,
            AnalysisCompetitiveAverageUnitPrice = 110,
            CostToCoverUnitPrice = 115,
            AnalysisScopeMedianUnitPrice = 90,
            CompetitiveThresholdUnitPrice = 150,
            SaneThresholdUnitPrice = 200
        };

        var summary = MarketAnalysisGridViewService.FormatAnalysisScopePriceSummary(analysis);

        Assert.Equal("market fallback ~110g; base ~100g; avg ~125g; acceptable <= 150g; insane >= 200g", summary);
        Assert.DoesNotContain("cost to cover", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatAnalysisScopePriceSummary_PrefersStoredPriceEvaluation()
    {
        var analysis = new MarketItemAnalysis
        {
            AnalysisScopeBaselineUnitPrice = 1,
            AnalysisScopeAverageUnitPrice = 1,
            AnalysisCompetitiveAverageUnitPrice = 100,
            CompetitiveThresholdUnitPrice = 1,
            SaneThresholdUnitPrice = 1,
            PriceEvaluation = new MarketPriceEvaluation
            {
                CentralRegion = new MarketCentralPriceRegion
                {
                    WeightedAverageUnitPrice = 125
                },
                Thresholds = new MarketPriceThresholds
                {
                    DealCeilingUnitPrice = 100,
                    CompetitiveCeilingUnitPrice = 150,
                    InsaneFloorUnitPrice = 200
                }
            }
        };

        var summary = MarketAnalysisGridViewService.FormatAnalysisScopePriceSummary(analysis);

        Assert.Equal("market fallback ~100g; avg ~125g; acceptable <= 150g; insane >= 200g", summary);
    }

    [Fact]
    public void IsUnsupportedProjectedCost_NoFullRecommendation_ReturnsTrueWithTooltipClass()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Thin Ore",
            QuantityNeeded = 10,
            DCAveragePrice = 6_408,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    TotalCost = 300,
                    TotalQuantityPurchased = 3
                }
            ]
        };

        Assert.True(MarketAnalysisGridViewService.IsUnsupportedProjectedCost(plan));
        Assert.Equal("ma-total-value is-projected-unsupported", MarketAnalysisGridViewService.GetTotalCostClass(plan));
        Assert.Equal(
            MarketAnalysisGridViewService.UnsupportedProjectedCostTooltip,
            MarketAnalysisGridViewService.GetTotalCostTooltip(plan));
        Assert.Equal(64_080, MarketAnalysisGridViewService.GetTotalCost(plan));
    }

    [Fact]
    public void IsUnsupportedProjectedCost_FullSplitRecommendation_ReturnsFalseWithCalculatedTotalTooltip()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Split Ore",
            QuantityNeeded = 10,
            RecommendedSplit =
            [
                new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Siren", QuantityToBuy = 6, TotalCost = 600 },
                new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Faerie", QuantityToBuy = 4, TotalCost = 440 }
            ]
        };

        Assert.False(MarketAnalysisGridViewService.IsUnsupportedProjectedCost(plan));
        Assert.Equal("ma-total-value", MarketAnalysisGridViewService.GetTotalCostClass(plan));
        var tooltip = MarketAnalysisGridViewService.GetTotalCostTooltip(plan);
        Assert.Contains("Calculated Total", tooltip);
        Assert.Contains("recommended split", tooltip);
    }

    [Fact]
    public void GetTotalCostTooltip_CoverageCandidate_ExplainsExactNeededAndCashOut()
    {
        var plan = CreatePlanWithCoverage(exactNeededCost: 1_000, cashOutCost: 1_200);

        var tooltip = MarketAnalysisGridViewService.GetTotalCostTooltip(plan);

        Assert.Equal(1_000, MarketAnalysisGridViewService.GetTotalCost(plan));
        Assert.Contains("Exact needed: 1,000g", tooltip);
        Assert.Contains("Cash out: 1,200g", tooltip);
    }

    [Fact]
    public void GetTotalCostClass_CheapestObservedDiagnostic_DoesNotMarkSupportedRecommendationUnsupported()
    {
        var plan = CreatePlanWithSupportedSingleWorldAndCheapestObservedDiagnostic();

        Assert.False(MarketAnalysisGridViewService.IsUnsupportedProjectedCost(plan));
        Assert.Equal("ma-total-value", MarketAnalysisGridViewService.GetTotalCostClass(plan));
    }

    [Fact]
    public void GetTotalCost_SupportedCoverageUsesDefaultEligibleEstimate()
    {
        var plan = CreatePlanWithSupportedSingleWorldAndCheapestObservedDiagnostic();

        Assert.Equal(3_529_250, MarketAnalysisGridViewService.GetTotalCost(plan));
    }

    [Fact]
    public void GetTotalCost_CheapestObservedDiagnosticOnly_IsNotPrimaryTotalFallback()
    {
        var plan = CreatePlanWithCheapestObservedOnly(exactNeededCost: 900);

        Assert.False(MarketAnalysisGridViewService.IsUnsupportedProjectedCost(plan));
        Assert.Equal("ma-total-value", MarketAnalysisGridViewService.GetTotalCostClass(plan));
        Assert.Equal(0, MarketAnalysisGridViewService.GetTotalCost(plan));
    }

    [Fact]
    public void ResolveSelectedPlan_UsesItemIdAcrossNewPlanInstancesAndFallsBackWhenMissing()
    {
        var oldSelection = Plan(100, "Old Instance", 100);
        var plans = new[]
        {
            Plan(100, "New Instance", 100),
            Plan(200, "Other", 200)
        };

        var selected = MarketAnalysisGridViewService.ResolveSelectedPlan(plans, oldSelection.ItemId);
        var fallback = MarketAnalysisGridViewService.ResolveSelectedPlan(plans, selectedItemId: 999);

        Assert.Same(plans[0], selected);
        Assert.Same(plans[0], fallback);
    }

    [Theory]
    [MemberData(nameof(PriceBandListingClassCases))]
    public void GetListingRowClass_PriceBandOverlayAddsToneAndEdgeClasses(
        WorldMarketAnalysis world,
        AnalyzedMarketListing listing,
        string expectedToneClass,
        string expectedEdgeClass)
    {
        var competitivenessClass = MarketAnalysisGridViewService.GetListingRowClass(
            world,
            listing,
            MarketAnalysisEvidenceOverlay.CompetitivenessOverlay);
        var bandClass = MarketAnalysisGridViewService.GetListingRowClass(
            world,
            listing,
            MarketAnalysisEvidenceOverlay.PriceBandOverlay);

        Assert.DoesNotContain("ma-band-tone-", competitivenessClass);
        Assert.DoesNotContain("ma-band-edge-", competitivenessClass);
        Assert.Contains(expectedToneClass, bandClass);
        Assert.Contains(expectedEdgeClass, bandClass);
    }

    [Fact]
    public void GetListingPriceBandTooltip_ExplainsBandSignalForPriceBandOverlayOnly()
    {
        var listing = Listing(sortIndex: 0, quantity: 2, pricePerUnit: 120);
        var world = WorldWithListingBand(listing, quantityNeeded: 100, bandQuantity: 2, isPrimaryUsableBand: false);

        var competitivenessTooltip = MarketAnalysisGridViewService.GetListingPriceBandTooltip(
            world,
            listing,
            MarketAnalysisEvidenceOverlay.CompetitivenessOverlay);
        var bandTooltip = MarketAnalysisGridViewService.GetListingPriceBandTooltip(
            world,
            listing,
            MarketAnalysisEvidenceOverlay.PriceBandOverlay);

        Assert.Equal(string.Empty, competitivenessTooltip);
        Assert.Contains("Thin deal price band", bandTooltip);
        Assert.Contains("does not drive procurement pricing", bandTooltip);
    }

    [Theory]
    [InlineData(null, false, MarketAnalysisGridSortColumn.Item, false)]
    [InlineData(MarketAnalysisGridSortColumn.Item, false, MarketAnalysisGridSortColumn.Item, true)]
    [InlineData(MarketAnalysisGridSortColumn.Item, true, MarketAnalysisGridSortColumn.Item, false)]
    [InlineData(MarketAnalysisGridSortColumn.Total, true, MarketAnalysisGridSortColumn.Item, false)]
    public void ToggleSort_UpdatesColumnAndDirection(
        MarketAnalysisGridSortColumn? currentColumn,
        bool currentDescending,
        MarketAnalysisGridSortColumn clickedColumn,
        bool expectedDescending)
    {
        var next = MarketAnalysisGridViewService.ToggleSort(currentColumn, currentDescending, clickedColumn);

        Assert.Equal(clickedColumn, next.Column);
        Assert.Equal(expectedDescending, next.Descending);
    }

    public static IEnumerable<object[]> PriceBandListingClassCases()
    {
        var lowOutlier = Listing(sortIndex: 0, quantity: 5, pricePerUnit: 1, priceSanity: MarketListingPriceSanity.LowOutlier);
        yield return
        [
            WorldWithListingBand(lowOutlier, quantityNeeded: 100, bandQuantity: 5, isPrimaryUsableBand: false),
            lowOutlier,
            "ma-band-tone-low",
            "ma-band-edge-low-outlier"
        ];

        var thin = Listing(
            sortIndex: 0,
            quantity: 2,
            pricePerUnit: 120,
            competitiveness: MarketListingCompetitiveness.Competitive);
        yield return
        [
            WorldWithListingBand(thin, quantityNeeded: 100, bandQuantity: 2, isPrimaryUsableBand: false),
            thin,
            "ma-band-tone-mid",
            "ma-band-edge-thin"
        ];

        var competitive = Listing(
            sortIndex: 0,
            quantity: 100,
            pricePerUnit: 100,
            competitiveness: MarketListingCompetitiveness.Competitive);
        yield return
        [
            WorldWithListingBand(competitive, quantityNeeded: 100, bandQuantity: 100, isPrimaryUsableBand: true),
            competitive,
            "ma-band-tone-mid",
            "ma-band-edge-competitive"
        ];

        var credibleCompetitive = Listing(
            sortIndex: 0,
            quantity: 30,
            pricePerUnit: 100,
            competitiveness: MarketListingCompetitiveness.Competitive);
        yield return
        [
            WorldWithListingBand(credibleCompetitive, quantityNeeded: 100, bandQuantity: 30, isPrimaryUsableBand: true, listingCount: 2),
            credibleCompetitive,
            "ma-band-tone-mid",
            "ma-band-edge-competitive"
        ];

        var insane = Listing(sortIndex: 0, quantity: 10, pricePerUnit: 1_000, priceSanity: MarketListingPriceSanity.Insane);
        yield return
        [
            WorldWithListingBand(insane, quantityNeeded: 100, bandQuantity: 10, isPrimaryUsableBand: false),
            insane,
            "ma-band-tone-high",
            "ma-band-edge-insane"
        ];
    }

    private static DetailedShoppingPlan Plan(int itemId, string name, long totalCost)
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 1,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalCost = totalCost,
                TotalQuantityPurchased = 1
            },
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    TotalCost = totalCost,
                    TotalQuantityPurchased = 1
                }
            ]
        };
    }

    private static MarketItemAnalysis Analysis(int itemId, string name, int rank)
    {
        return new MarketItemAnalysis
        {
            ItemId = itemId,
            Name = name,
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    Scores =
                    [
                        new WorldLensScore
                        {
                            Lens = MarketAcquisitionLens.BulkValue,
                            Rank = rank,
                            ScoreBucket = MarketScoreBucket.Competitive
                        }
                    ]
                }
            ]
        };
    }

    private static WorldMarketAnalysis WorldWithListingBand(
        AnalyzedMarketListing listing,
        int quantityNeeded,
        int bandQuantity,
        bool isPrimaryUsableBand,
        int listingCount = 1)
    {
        var depth = bandQuantity >= Math.Max(quantityNeeded / 2, 1)
            ? PriceBandDepth.Deep
            : bandQuantity >= Math.Max(quantityNeeded / 4, 1)
                ? PriceBandDepth.Usable
                : PriceBandDepth.Thin;
        var competitiveness = listing.PriceSanity switch
        {
            MarketListingPriceSanity.LowOutlier => PriceBandCompetitiveness.LowOutlier,
            MarketListingPriceSanity.Insane or MarketListingPriceSanity.Outlier => PriceBandCompetitiveness.Insane,
            _ when listing.Competitiveness is MarketListingCompetitiveness.Fair or MarketListingCompetitiveness.Uncompetitive => PriceBandCompetitiveness.Uncompetitive,
            _ => PriceBandCompetitiveness.Competitive
        };

        return new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = "Siren",
            QuantityNeeded = quantityNeeded,
            PrimaryUsableAverageUnitPrice = 100,
            Listings = [listing],
            PriceBands =
            [
                new MarketPriceBand
                {
                    FirstListingIndex = listing.SortIndex,
                    LastListingIndex = listing.SortIndex,
                    MinUnitPrice = listing.PricePerUnit,
                    MaxUnitPrice = listing.PricePerUnit,
                    WeightedAverageUnitPrice = listing.PricePerUnit,
                    ListingCount = listingCount,
                    Quantity = bandQuantity,
                    Competitiveness = competitiveness,
                    Depth = depth,
                    IsPrimaryUsableBand = isPrimaryUsableBand &&
                        competitiveness == PriceBandCompetitiveness.Competitive &&
                        depth is PriceBandDepth.Usable or PriceBandDepth.Deep
                }
            ]
        };
    }

    private static AnalyzedMarketListing Listing(
        int sortIndex,
        int quantity,
        long pricePerUnit,
        MarketListingPriceSanity priceSanity = MarketListingPriceSanity.Sane,
        MarketListingCompetitiveness competitiveness = MarketListingCompetitiveness.Unknown)
    {
        return new AnalyzedMarketListing
        {
            SortIndex = sortIndex,
            Quantity = quantity,
            PricePerUnit = pricePerUnit,
            PriceSanity = priceSanity,
            Competitiveness = competitiveness
        };
    }

    private static MarketItemAnalysis ScopeAnalysis(
        int itemId,
        string name,
        int scopeSaneQuantity,
        int scopePrimaryUsableQuantity)
    {
        return new MarketItemAnalysis
        {
            ItemId = itemId,
            Name = name,
            Worlds =
            [
                new WorldMarketAnalysis
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    QuantityNeeded = 100,
                    SaneThresholdUnitPrice = 200,
                    ScopeSaneQuantity = scopeSaneQuantity,
                    PrimaryUsableQuantity = scopePrimaryUsableQuantity,
                    CoverageBucket = MarketCoverageBucket.None,
                    Scores =
                    [
                        new WorldLensScore
                        {
                            Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                            Rank = 1,
                            ScoreBucket = MarketScoreBucket.Unavailable
                        }
                    ]
                }
            ]
        };
    }

    private static WorldMarketAnalysis ScopeWorld(
        string worldName,
        int scopeSaneQuantity,
        int scopePrimaryUsableQuantity,
        int rank = 1,
        decimal goodAverage = 0,
        decimal worldCompetitiveAverage = 0)
    {
        var priceSignalQuantity = worldCompetitiveAverage > 0
            ? scopeSaneQuantity
            : 0;

        return new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = worldName,
            QuantityNeeded = 100,
            SaneThresholdUnitPrice = 200,
            ScopeSaneQuantity = scopeSaneQuantity,
            PrimaryUsableQuantity = scopePrimaryUsableQuantity,
            PriceSignalQuantity = priceSignalQuantity,
            AnalysisCompetitiveAverageUnitPrice = goodAverage,
            PrimaryUsableAverageUnitPrice = worldCompetitiveAverage,
            PriceSignalAverageUnitPrice = worldCompetitiveAverage,
            PriceSignalDepth = GetPriceSignalDepth(priceSignalQuantity, 100),
            DataQualityBucket = MarketDataQualityBucket.Current,
            Scores =
            [
                new WorldLensScore
                {
                    Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                    Rank = rank,
                    ScoreBucket = MarketScoreBucket.Unavailable
                }
            ]
        };
    }

    private static PriceBandDepth GetPriceSignalDepth(int quantity, int quantityNeeded)
    {
        if (quantity <= 0 || quantityNeeded <= 0)
        {
            return PriceBandDepth.None;
        }

        if (quantity >= Math.Max(quantityNeeded / 2, 1))
        {
            return PriceBandDepth.Deep;
        }

        return quantity >= Math.Max(quantityNeeded / 4, 1)
            ? PriceBandDepth.Usable
            : PriceBandDepth.Thin;
    }

    private static DetailedShoppingPlan CreatePlanWithCoverage(decimal exactNeededCost, decimal cashOutCost)
    {
        var coverage = CreateCoverageOption(
            MarketCoverageTier.SingleWorld,
            exactNeededCost,
            cashOutCost,
            isDefaultEligible: true);
        return new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Coverage Item",
            QuantityNeeded = 10,
            CoverageSet = new MarketCoverageSet(
                100,
                "Coverage Item",
                10,
                SingleWorld: coverage,
                CompactSplit: null,
                WideSplit: null,
                CheapestObserved: null,
                AllCandidates: [coverage])
        };
    }

    private static DetailedShoppingPlan CreatePlanWithCheapestObservedOnly(decimal exactNeededCost)
    {
        var coverage = CreateCoverageOption(
            MarketCoverageTier.CheapestObserved,
            exactNeededCost,
            exactNeededCost,
            isDefaultEligible: false);
        return new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Diagnostic Item",
            QuantityNeeded = 10,
            CoverageSet = new MarketCoverageSet(
                100,
                "Diagnostic Item",
                10,
                SingleWorld: null,
                CompactSplit: null,
                WideSplit: null,
                CheapestObserved: coverage,
                AllCandidates: [coverage])
            };
    }

    private static DetailedShoppingPlan CreatePlanWithSupportedSingleWorldAndCheapestObservedDiagnostic()
    {
        var singleWorld = CreateCoverageOption(
            MarketCoverageTier.SingleWorld,
            exactNeededCost: 3_529_250,
            cashOutCost: 3_551_750,
            isDefaultEligible: true);
        var cheapestObserved = CreateCoverageOption(
            MarketCoverageTier.CheapestObserved,
            exactNeededCost: 3_004_308,
            cashOutCost: 3_009_102,
            isDefaultEligible: false);

        return new DetailedShoppingPlan
        {
            ItemId = 5059,
            Name = "Cobalt Ingot",
            QuantityNeeded = 3_996,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Crystal",
                WorldName = "Coeurl",
                TotalCost = 3_551_750,
                TotalQuantityPurchased = 4_021,
                HasSufficientStock = true
            },
            CoverageSet = new MarketCoverageSet(
                5059,
                "Cobalt Ingot",
                3_996,
                SingleWorld: singleWorld,
                CompactSplit: null,
                WideSplit: null,
                CheapestObserved: cheapestObserved,
                AllCandidates: [singleWorld, cheapestObserved])
        };
    }

    private static MarketCoverageOption CreateCoverageOption(
        MarketCoverageTier tier,
        decimal exactNeededCost,
        decimal cashOutCost,
        bool isDefaultEligible)
    {
        return new MarketCoverageOption(
            CandidateId: $"100-10-{tier.ToString().ToLowerInvariant()}-nqorhq-siren",
            Tier: tier,
            Kind: MarketCoverageKind.SupportedListings,
            QualityPolicy: MarketCoverageQualityPolicy.NqOrHq,
            QuantityCovered: 10,
            QuantityToPurchase: 12,
            ExcessQuantity: 2,
            ExactNeededCost: exactNeededCost,
            CashOutCost: cashOutCost,
            AverageUnitCost: exactNeededCost / 10,
            PriceBand: MarketCoveragePriceBand.Competitive,
            Worlds:
            [
                new MarketCoverageWorld(
                    DataCenter: "Aether",
                    WorldName: "Siren",
                    QuantityCovered: 10,
                    QuantityToPurchase: 12,
                    ExactNeededCost: exactNeededCost,
                    CashOutCost: cashOutCost)
            ],
            Listings: [],
            Friction: new MarketCoverageFriction(
                WorldCount: 1,
                DataCenterCount: 1,
                SmallestContribution: 10,
                LargestContribution: 10,
                ExcessQuantity: 2),
            Savings: MarketCoverageSavings.None,
            IsDefaultEligible: isDefaultEligible,
            DegradedReason: null);
    }
}
