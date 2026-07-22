using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ProcurementRouteCompactionTests
{
    [Fact]
    public void CompactResultShoppingPlans_PreservesHqCoverageAndSelectedWorlds()
    {
        var first = World("Alpha");
        var second = World("Beta");
        var coverage = new MarketCoverageOption(
            "coverage",
            MarketCoverageTier.CompactSplit,
            MarketCoverageKind.SupportedListings,
            MarketCoverageQualityPolicy.HqOnly,
            2,
            2,
            0,
            100,
            100,
            50,
            MarketCoveragePriceBand.Competitive,
            [
                new MarketCoverageWorld("Aether", "Alpha", 1, 1, 50, 50),
                new MarketCoverageWorld("Aether", "Beta", 1, 1, 50, 50)
            ],
            [],
            new MarketCoverageFriction(2, 1, 1, 1, 0),
            MarketCoverageSavings.None,
            true,
            null);
        var source = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "HQ Item",
            QuantityNeeded = 2,
            HqQuantityNeeded = 2,
            WorldOptions = [first, second, World("Gamma")],
            CoverageSet = new MarketCoverageSet(100, "HQ Item", 2, null, coverage, null, null, [coverage])
        };

        var compact = Assert.Single(ProcurementRouteExecutionService.CompactResultShoppingPlans([source]));

        Assert.Equal(2, compact.HqQuantityNeeded);
        Assert.Equal("coverage", Assert.Single(compact.CoverageSet!.AllCandidates).CandidateId);
        Assert.Null(compact.CoverageSet.SingleWorld);
        Assert.Equal("coverage", compact.CoverageSet.CompactSplit?.CandidateId);
        Assert.Equal(["Alpha", "Beta"], compact.WorldOptions.Select(world => world.WorldName));
        Assert.All(compact.WorldOptions, world => Assert.Empty(world.ExcludedListings));
        Assert.All(compact.WorldOptions, world => Assert.Single(world.Listings));
    }

    private static WorldShoppingSummary World(string name) => new()
    {
        DataCenter = "Aether",
        WorldName = name,
        TotalQuantityPurchased = 2,
        HasSufficientStock = true,
        Listings = [new ShoppingListingEntry { Quantity = 2, NeededFromStack = 2, PricePerUnit = 50 }],
        ExcludedListings = [new ShoppingListingEntry { Quantity = 99, PricePerUnit = 999_999 }]
    };
}
