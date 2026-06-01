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
    [InlineData(80, "20% below good")]
    [InlineData(100, "at good")]
    [InlineData(110, "10% above good")]
    public void FormatCompetitiveValue_DescribesDistanceFromGoodAverage(decimal worldAverage, string expected)
    {
        var world = ScopeWorld("Siren", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, goodAverage: 100, worldCompetitiveAverage: worldAverage);

        Assert.Equal(expected, MarketAnalysisGridViewService.FormatCompetitiveValue(world));
    }

    [Fact]
    public void FormatCompetitiveValue_LowOutlierStockStillContributesToWorldValue()
    {
        var world = ScopeWorld("Zalera", scopeSaneQuantity: 100, scopeCompetitiveQuantity: 100, goodAverage: 100, worldCompetitiveAverage: 1);

        Assert.Equal("99% below good", MarketAnalysisGridViewService.FormatCompetitiveValue(world));
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
