using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

[Trait(TestTraits.Surface, TestTraits.DeployWeb)]
public sealed class MarketAnalysisActionableTotalTests
{
    [Fact]
    public void Total_UsesCashOutForSelectedListingStacks()
    {
        var coverage = Coverage(exactNeededCost: 1_461, cashOutCost: 48_213);
        var plan = new DetailedShoppingPlan
        {
            ItemId = 1,
            Name = "Darksteel Ore",
            QuantityNeeded = 3,
            CoverageSet = new MarketCoverageSet(1, "Darksteel Ore", 3, coverage, null, null, coverage, [coverage])
        };

        Assert.Equal(48_213, MarketAnalysisGridViewService.GetTotalCost(plan));
        Assert.Equal("48,213g", MarketAnalysisGridViewService.FormatTotalCost(plan));
        Assert.Contains("selected stacks", MarketAnalysisGridViewService.GetTotalCostTooltip(plan));
    }

    [Fact]
    public void Total_DoesNotPresentUnsupportedProjectionAsPurchasable()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 1,
            Name = "Thin Ore",
            QuantityNeeded = 10,
            DCAveragePrice = 500,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    TotalQuantityPurchased = 2,
                    TotalCost = 1_000
                }
            ]
        };

        Assert.Equal(0, MarketAnalysisGridViewService.GetTotalCost(plan));
        Assert.Equal("Unavailable", MarketAnalysisGridViewService.FormatTotalCost(plan));
    }

    private static MarketCoverageOption Coverage(decimal exactNeededCost, decimal cashOutCost)
    {
        return new MarketCoverageOption(
            "selected",
            MarketCoverageTier.SingleWorld,
            MarketCoverageKind.SupportedListings,
            MarketCoverageQualityPolicy.NqOrHq,
            3,
            99,
            96,
            exactNeededCost,
            cashOutCost,
            exactNeededCost / 3,
            MarketCoveragePriceBand.Competitive,
            [new MarketCoverageWorld("Primal", "Ultros", 3, 99, exactNeededCost, cashOutCost)],
            [new MarketCoverageListing("Primal", "Ultros", 99, 3, 99, cashOutCost / 99, false)],
            new MarketCoverageFriction(1, 1, 3, 3, 96),
            MarketCoverageSavings.None,
            true,
            null);
    }
}
