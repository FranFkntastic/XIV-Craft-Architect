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
    public void GetOrderedPlans_CoverageSort_UsesScopeSaneStock()
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
}
