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
            ScopeAnalysis(100, "Baited Ore", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100),
            ScopeAnalysis(200, "Thin Ore", scopeSaneQuantity: 10, scopeCompetitiveQuantity: 10)
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
                    ScopeWorld("Full", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100),
                    ScopeWorld("Thin", scopeSaneQuantity: 25, scopeCompetitiveQuantity: 5),
                    ScopeWorld("Missing", scopeSaneQuantity: 0, scopeCompetitiveQuantity: 0)
                ]
            }
        };

        var label = MarketAnalysisGridViewService.FormatCoverageLabel(plan, analysisByItemId);

        Assert.Equal("1 full, 1 partial across 3 worlds", label);
    }

    [Fact]
    public void FormatCoverage_UsesCompetitiveStockWhenScopeContextExists()
    {
        var world = ScopeWorld("Uncompetitive Full", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 25);

        Assert.Equal("25/100", MarketAnalysisGridViewService.FormatCoverage(world));
        Assert.Equal(MarketCoverageBucket.PartialThin, MarketAnalysisGridViewService.GetDisplayCoverageBucket(world));
    }

    [Fact]
    public void GetOrderedWorlds_UsesScopeAwareDisplayScoreBeforeStoredLensRank()
    {
        var optimal = ScopeWorld("Later Rank", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, rank: 50);
        var partial = ScopeWorld("Early Rank", scopeSaneQuantity: 50, scopeCompetitiveQuantity: 10, rank: 1);
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
                ScopeWorld("Siren", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100),
                ScopeWorld("Faerie", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100),
                ScopeWorld("Jenova", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100)
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
    public void GetOrderedWorlds_StockDepthSort_UsesCompetitiveQuantity()
    {
        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds =
            [
                ScopeWorld("Medium", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 50),
                ScopeWorld("Low", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 10),
                ScopeWorld("High", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 90)
            ]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.StockDepth,
            sortDescending: true);

        Assert.Equal(["High", "Medium", "Low"], ordered.Select(world => world.WorldName));
    }

    [Fact]
    public void GetOrderedWorlds_PriceValueSort_UsesDisplayedProjectFitBeforeStoredLensRank()
    {
        var optimal = ScopeWorld("Displayed Optimal", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, rank: 50);
        var poorFit = ScopeWorld("Stored Rank Winner", scopeSaneQuantity: 5, scopeCompetitiveQuantity: 5, rank: 1);
        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds = [poorFit, optimal]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.PriceValue,
            sortDescending: false);

        Assert.Equal(["Displayed Optimal", "Stored Rank Winner"], ordered.Select(world => world.WorldName));
    }

    [Fact]
    public void GetOrderedWorlds_PriceValueSort_PriceBandOverlayUsesVisibleBandInterpretation()
    {
        var thinCheapWorld = ScopeWorld("Thin Cheap", scopeSaneQuantity: 2, scopeCompetitiveQuantity: 2, rank: 1);
        thinCheapWorld.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 10,
            MaxUnitPrice = 10,
            WeightedAverageUnitPrice = 10,
            Quantity = 2,
            ListingCount = 1,
            IsCompetitiveShelf = true
        });

        var representativeWorld = ScopeWorld("Representative", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, rank: 50);
        representativeWorld.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 100,
            MaxUnitPrice = 100,
            WeightedAverageUnitPrice = 100,
            Quantity = 100,
            ListingCount = 4,
            IsCompetitiveShelf = true
        });

        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds = [thinCheapWorld, representativeWorld]
        };

        var ordered = MarketAnalysisGridViewService.GetOrderedWorlds(
            analysis,
            MarketAcquisitionLens.MinimumUpfrontCost,
            MarketAnalysisWorldGridSortColumn.PriceValue,
            sortDescending: false,
            MarketAnalysisEvidenceOverlay.PriceBandOverlay);

        Assert.Equal(["Representative", "Thin Cheap"], ordered.Select(world => world.WorldName));
    }

    [Fact]
    public void GetOrderedWorlds_PriceValueSort_PriceBandOverlayTreatsCredibleCompetitiveBandAsRepresentative()
    {
        var credibleCompetitiveWorld = ScopeWorld("Credible Competitive", scopeSaneQuantity: 30, scopeCompetitiveQuantity: 30, rank: 50);
        credibleCompetitiveWorld.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 100,
            MaxUnitPrice = 105,
            WeightedAverageUnitPrice = 103,
            Quantity = 30,
            ListingCount = 2,
            IsCompetitiveShelf = true
        });

        var deeperNonCompetitiveWorld = ScopeWorld("Deeper Noncompetitive", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 0, rank: 1);
        deeperNonCompetitiveWorld.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 150,
            MaxUnitPrice = 150,
            WeightedAverageUnitPrice = 150,
            Quantity = 100,
            ListingCount = 1,
            IsCompetitiveShelf = false
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
    public void FormatWorldPriceBandSummary_ExposesVisibleBandInterpretation()
    {
        var world = ScopeWorld("Credible Competitive", scopeSaneQuantity: 30, scopeCompetitiveQuantity: 30);
        world.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 1,
            MinUnitPrice = 100,
            MaxUnitPrice = 105,
            WeightedAverageUnitPrice = 103,
            Quantity = 30,
            ListingCount = 2,
            IsCompetitiveShelf = true
        });

        Assert.Equal("included evidence", MarketAnalysisGridViewService.FormatWorldPriceBandRole(world));
        Assert.Equal("is-optimal", MarketAnalysisGridViewService.GetWorldPriceBandScoreClass(world));
        Assert.Equal("30 in band at ~103g", MarketAnalysisGridViewService.FormatWorldPriceBandSummary(world));
    }

    [Fact]
    public void GetWorldPriceBandScoreClass_UsesExistingWorldScorePalette()
    {
        var thin = ScopeWorld("Thin", scopeSaneQuantity: 2, scopeCompetitiveQuantity: 2);
        thin.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 10,
            MaxUnitPrice = 10,
            WeightedAverageUnitPrice = 10,
            Quantity = 2,
            ListingCount = 1,
            IsCompetitiveShelf = true
        });

        var unclassified = ScopeWorld("Unclassified", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 0);
        unclassified.PriceBands.Add(new MarketPriceBand
        {
            FirstListingIndex = 0,
            LastListingIndex = 0,
            MinUnitPrice = 150,
            MaxUnitPrice = 150,
            WeightedAverageUnitPrice = 150,
            Quantity = 100,
            ListingCount = 1,
            IsCompetitiveShelf = false
        });

        Assert.Equal("is-competitive", MarketAnalysisGridViewService.GetWorldPriceBandScoreClass(thin));
        Assert.Equal("is-unavailable", MarketAnalysisGridViewService.GetWorldPriceBandScoreClass(unclassified));
    }

    [Fact]
    public void GetOrderedWorlds_ValueSort_UsesCompetitiveDiscountAgainstGoodAverage()
    {
        var analysis = new MarketItemAnalysis
        {
            ItemId = 100,
            Name = "Baited Ore",
            Worlds =
            [
                ScopeWorld("Small Discount", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 95),
                ScopeWorld("Large Discount", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 80),
                ScopeWorld("Above Average", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 110)
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
    public void FormatCompetitiveValue_DescribesDistanceFromGoodAverage(decimal worldAverage, string expected)
    {
        var world = ScopeWorld("Siren", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, goodAverage: 100, worldCompetitiveAverage: worldAverage);

        Assert.Equal(expected, MarketAnalysisGridViewService.FormatCompetitiveValue(world));
    }

    [Fact]
    public void FormatCompetitiveValue_LowOutlierStockStillContributesToWorldValue()
    {
        var world = ScopeWorld("Zalera", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 1);

        Assert.Equal("-99%", MarketAnalysisGridViewService.FormatCompetitiveValue(world));
    }

    [Fact]
    public void FormatCompetitiveValueTooltip_ExplainsSignedDifferenceFromGoodAverage()
    {
        var world = ScopeWorld("Siren", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, goodAverage: 1_100, worldCompetitiveAverage: 1_055);

        var tooltip = MarketAnalysisGridViewService.FormatCompetitiveValueTooltip(world);

        Assert.Equal("Siren's competitive average is 4% less than the regional good average: 1,055g vs 1,100g.", tooltip);
    }

    [Fact]
    public void FormatCompetitiveValueTooltip_ExplainsSignedDifferenceAboveGoodAverage()
    {
        var world = ScopeWorld("Siren", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, goodAverage: 1_100, worldCompetitiveAverage: 1_210);

        var tooltip = MarketAnalysisGridViewService.FormatCompetitiveValueTooltip(world);

        Assert.Equal("Siren's competitive average is 10% greater than the regional good average: 1,210g vs 1,100g.", tooltip);
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
            ScopeCompetitiveQuantity = 100,
            AnalysisScopeCompetitiveAverageUnitPrice = 100,
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
                    IsCompetitiveShelf = true
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
        Assert.Equal(["Price shelf +100%", "Above good avg"], second.Select(divider => divider.Label));
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
    public void FormatWorldPriceSummary_WithScopeContext_UsesPerWorldLensSummary()
    {
        var world = new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = "Siren",
            AnalysisScopeBaselineUnitPrice = 100,
            SaneThresholdUnitPrice = 200,
            Scores =
            [
                new WorldLensScore
                {
                    Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                    Summary = "12,000g to cover need"
                }
            ]
        };

        var summary = MarketAnalysisGridViewService.FormatWorldPriceSummary(
            world,
            MarketAcquisitionLens.MinimumUpfrontCost);

        Assert.Equal("12,000g to cover need", summary);
    }

    [Fact]
    public void FormatWorldPriceSummary_WithScopeCompetitiveAverage_IgnoresStaleShelfSummary()
    {
        var world = new WorldMarketAnalysis
        {
            DataCenter = "Dynamis",
            WorldName = "Kraken",
            QuantityNeeded = 500,
            CompetitiveThresholdUnitPrice = 1_661,
            SaneThresholdUnitPrice = 2_215,
            ScopeCompetitiveQuantity = 603,
            ScopeCompetitiveAverageUnitPrice = 907,
            Scores =
            [
                new WorldLensScore
                {
                    Lens = MarketAcquisitionLens.BulkValue,
                    Summary = "0 competitive at ~602g"
                }
            ]
        };

        var summary = MarketAnalysisGridViewService.FormatWorldPriceSummary(
            world,
            MarketAcquisitionLens.BulkValue);

        Assert.Equal("603 competitive at ~907g", summary);
    }

    [Fact]
    public void FormatAnalysisScopePriceSummary_ShowsCompetitiveAverageAndThresholds()
    {
        var analysis = new MarketItemAnalysis
        {
            AnalysisScopeBaselineUnitPrice = 100,
            AnalysisScopeAverageUnitPrice = 125,
            AnalysisScopeCompetitiveAverageUnitPrice = 110,
            AnalysisScopeMedianUnitPrice = 90,
            CompetitiveThresholdUnitPrice = 150,
            SaneThresholdUnitPrice = 200
        };

        var summary = MarketAnalysisGridViewService.FormatAnalysisScopePriceSummary(analysis);

        Assert.Equal("good avg ~110g; base ~100g; avg ~125g; competitive <= 150g; insane >= 200g", summary);
    }

    [Fact]
    public void FormatAnalysisScopePriceSummary_PrefersStoredPriceEvaluation()
    {
        var analysis = new MarketItemAnalysis
        {
            AnalysisScopeBaselineUnitPrice = 1,
            AnalysisScopeAverageUnitPrice = 1,
            AnalysisScopeCompetitiveAverageUnitPrice = 100,
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

        Assert.Equal("good avg ~100g; avg ~125g; competitive <= 150g; insane >= 200g", summary);
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
        var shelfClass = MarketAnalysisGridViewService.GetListingRowClass(
            world,
            listing,
            MarketAnalysisEvidenceOverlay.ShelfOverlay);
        var bandClass = MarketAnalysisGridViewService.GetListingRowClass(
            world,
            listing,
            MarketAnalysisEvidenceOverlay.PriceBandOverlay);

        Assert.DoesNotContain("ma-band-tone-", shelfClass);
        Assert.DoesNotContain("ma-band-edge-", shelfClass);
        Assert.Contains(expectedToneClass, bandClass);
        Assert.Contains(expectedEdgeClass, bandClass);
    }

    [Fact]
    public void GetListingPriceBandTooltip_ExplainsBandRoleForPriceBandOverlayOnly()
    {
        var listing = Listing(sortIndex: 0, quantity: 2, pricePerUnit: 120);
        var world = WorldWithListingBand(listing, quantityNeeded: 100, bandQuantity: 2, isCompetitiveShelf: false);

        var shelfTooltip = MarketAnalysisGridViewService.GetListingPriceBandTooltip(
            world,
            listing,
            MarketAnalysisEvidenceOverlay.ShelfOverlay);
        var bandTooltip = MarketAnalysisGridViewService.GetListingPriceBandTooltip(
            world,
            listing,
            MarketAnalysisEvidenceOverlay.PriceBandOverlay);

        Assert.Equal(string.Empty, shelfTooltip);
        Assert.Contains("Thin price band", bandTooltip);
        Assert.Contains("does not contribute", bandTooltip);
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
            WorldWithListingBand(lowOutlier, quantityNeeded: 100, bandQuantity: 5, isCompetitiveShelf: false),
            lowOutlier,
            "ma-band-tone-low",
            "ma-band-edge-low-outlier"
        ];

        var thin = Listing(
            sortIndex: 0,
            quantity: 2,
            pricePerUnit: 120,
            competitiveness: MarketListingCompetitiveness.Competitive,
            isScopeCompetitive: true);
        yield return
        [
            WorldWithListingBand(thin, quantityNeeded: 100, bandQuantity: 2, isCompetitiveShelf: false),
            thin,
            "ma-band-tone-mid",
            "ma-band-edge-thin"
        ];

        var representative = Listing(
            sortIndex: 0,
            quantity: 100,
            pricePerUnit: 100,
            competitiveness: MarketListingCompetitiveness.Competitive,
            isScopeCompetitive: true);
        yield return
        [
            WorldWithListingBand(representative, quantityNeeded: 100, bandQuantity: 100, isCompetitiveShelf: true),
            representative,
            "ma-band-tone-mid",
            "ma-band-edge-representative"
        ];

        var credibleCompetitive = Listing(
            sortIndex: 0,
            quantity: 30,
            pricePerUnit: 100,
            competitiveness: MarketListingCompetitiveness.Competitive,
            isScopeCompetitive: true);
        yield return
        [
            WorldWithListingBand(credibleCompetitive, quantityNeeded: 100, bandQuantity: 30, isCompetitiveShelf: true, listingCount: 2),
            credibleCompetitive,
            "ma-band-tone-mid",
            "ma-band-edge-representative"
        ];

        var expensiveTail = Listing(sortIndex: 0, quantity: 10, pricePerUnit: 1_000, priceSanity: MarketListingPriceSanity.Insane);
        yield return
        [
            WorldWithListingBand(expensiveTail, quantityNeeded: 100, bandQuantity: 10, isCompetitiveShelf: false),
            expensiveTail,
            "ma-band-tone-high",
            "ma-band-edge-expensive-tail"
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
        bool isCompetitiveShelf,
        int listingCount = 1)
    {
        return new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = "Siren",
            QuantityNeeded = quantityNeeded,
            ScopeCompetitiveAverageUnitPrice = 100,
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
                    IsCompetitiveShelf = isCompetitiveShelf
                }
            ]
        };
    }

    private static AnalyzedMarketListing Listing(
        int sortIndex,
        int quantity,
        long pricePerUnit,
        MarketListingPriceSanity priceSanity = MarketListingPriceSanity.Sane,
        MarketListingCompetitiveness competitiveness = MarketListingCompetitiveness.Unknown,
        bool isScopeCompetitive = false)
    {
        return new AnalyzedMarketListing
        {
            SortIndex = sortIndex,
            Quantity = quantity,
            PricePerUnit = pricePerUnit,
            PriceSanity = priceSanity,
            Competitiveness = competitiveness,
            IsScopeCompetitive = isScopeCompetitive
        };
    }

    private static MarketItemAnalysis ScopeAnalysis(
        int itemId,
        string name,
        int scopeSaneQuantity,
        int scopeCompetitiveQuantity)
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
                    ScopeCompetitiveQuantity = scopeCompetitiveQuantity,
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
        int scopeCompetitiveQuantity,
        int rank = 1,
        decimal goodAverage = 0,
        decimal worldCompetitiveAverage = 0)
    {
        return new WorldMarketAnalysis
        {
            DataCenter = "Aether",
            WorldName = worldName,
            QuantityNeeded = 100,
            SaneThresholdUnitPrice = 200,
            ScopeSaneQuantity = scopeSaneQuantity,
            ScopeCompetitiveQuantity = scopeCompetitiveQuantity,
            AnalysisScopeCompetitiveAverageUnitPrice = goodAverage,
            ScopeCompetitiveAverageUnitPrice = worldCompetitiveAverage,
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
}
