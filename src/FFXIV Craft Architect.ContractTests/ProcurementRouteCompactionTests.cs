using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ProcurementRouteCompactionTests
{
    [Fact]
    public void CompactResultShoppingPlans_PreservesHqCoverageAndSelectedWorlds()
    {
        var first = World("Alpha");
        first.ProcurementPriorityScore = decimal.MaxValue;
        var second = World("Beta");
        second.ValueScore = decimal.MaxValue;
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

        var json = JsonSerializer.Serialize(compact, EngineJsonSerializerOptions.CreateWire());
        Assert.Contains("\"valueScore\":\"79228162514264337593543950335\"", json);
        var roundTrip = JsonSerializer.Deserialize<DetailedShoppingPlan>(
            json,
            EngineJsonSerializerOptions.CreateWire());
        Assert.Equal(decimal.MaxValue, roundTrip!.WorldOptions[0].ProcurementPriorityScore);
        Assert.Equal(decimal.MaxValue, roundTrip!.WorldOptions[1].ValueScore);
    }

    [Fact]
    public void CompactResultShoppingPlans_KeepsVendorRecommendationOutOfMarketWorldOptions()
    {
        var vendor = World(MarketShoppingConstants.VendorWorldName);
        vendor.DataCenter = MarketShoppingConstants.VendorWorldName;
        var source = new DetailedShoppingPlan
        {
            ItemId = 101,
            Name = "Vendor Item",
            QuantityNeeded = 2,
            RecommendedWorld = vendor,
            WorldOptions = [World("Alpha"), vendor],
            Vendors =
            [
                new VendorInfo { Name = "Supplier", Location = "Limsa", Price = 50, Currency = "gil" }
            ]
        };

        var compact = Assert.Single(ProcurementRouteExecutionService.CompactResultShoppingPlans([source]));

        Assert.Equal(MarketShoppingConstants.VendorWorldName, compact.RecommendedWorld?.WorldName);
        Assert.Empty(compact.WorldOptions);
        Assert.Single(compact.Vendors);
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
