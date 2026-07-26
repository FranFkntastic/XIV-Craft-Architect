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
            [new MarketCoverageListing("Aether", "Alpha", 2, 1, 2, 50, true)],
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
        Assert.Empty(compact.CoverageSet.CompactSplit!.Listings);
        Assert.Equal(["Alpha", "Beta"], compact.WorldOptions.Select(world => world.WorldName));
        Assert.All(compact.WorldOptions, world => Assert.Empty(world.ExcludedListings));
        Assert.All(compact.WorldOptions, world => Assert.Empty(world.Listings));

        var json = JsonSerializer.Serialize(compact, EngineJsonSerializerOptions.CreateWire());
        Assert.Contains("\"valueScore\":\"79228162514264337593543950335\"", json);
        var roundTrip = JsonSerializer.Deserialize<DetailedShoppingPlan>(
            json,
            EngineJsonSerializerOptions.CreateWire());
        Assert.Equal(decimal.MaxValue, roundTrip!.WorldOptions[0].ProcurementPriorityScore);
        Assert.Equal(decimal.MaxValue, roundTrip!.WorldOptions[1].ValueScore);
        Assert.Equal(
            decimal.MaxValue,
            JsonSerializer.Deserialize<decimal>(
                "7.922816251426434e+28",
                EngineJsonSerializerOptions.CreateWire()));
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

    [Fact]
    public void PrepareProcurementEvidenceForScope_KeepsOnlyTheChosenRegionWithoutLosingItemMeaning()
    {
        var northAmerica = World("Alpha");
        var europe = World("Omega", "Chaos");
        var northAmericaCoverage = Coverage("na", "Aether", "Alpha");
        var crossRegionCoverage = Coverage(
            "cross-region",
            "Aether",
            "Alpha",
            new MarketCoverageWorld("Chaos", "Omega", 1, 1, 40, 40));
        var source = new DetailedShoppingPlan
        {
            ItemId = 102,
            Name = "Regional Item",
            QuantityNeeded = 2,
            HqQuantityNeeded = 2,
            WorldOptions = [northAmerica, europe],
            RecommendedWorld = europe,
            RecommendedSplit =
            [
                new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Alpha", QuantityToBuy = 1 },
                new SplitWorldPurchase { DataCenter = "Chaos", WorldName = "Omega", QuantityToBuy = 1 }
            ],
            CoverageSet = new MarketCoverageSet(
                102,
                "Regional Item",
                2,
                northAmericaCoverage,
                crossRegionCoverage,
                null,
                null,
                [northAmericaCoverage, crossRegionCoverage])
        };
        var request = new ProcurementRouteExecutionRequest
        {
            ActiveProcurementItems =
            [
                new MaterialAggregate { ItemId = 102, Name = "Regional Item", TotalQuantity = 2, RequiresHq = true }
            ],
            Scope = MarketFetchScope.EntireRegion,
            SelectedRegion = "North America",
            SelectedDataCenter = "Aether"
        };

        var filtered = Assert.Single(
            ProcurementRouteExecutionService.PrepareProcurementEvidenceForScope([source], request));

        Assert.Equal(2, filtered.HqQuantityNeeded);
        Assert.Null(filtered.RecommendedWorld);
        Assert.Equal("Aether", Assert.Single(filtered.WorldOptions).DataCenter);
        Assert.Equal("Aether", Assert.Single(filtered.RecommendedSplit!).DataCenter);
        var coverage = Assert.Single(filtered.CoverageSet!.AllCandidates);
        Assert.Equal("na", coverage.CandidateId);
        Assert.Equal("Aether", Assert.Single(coverage.Worlds).DataCenter);
    }

    private static WorldShoppingSummary World(string name, string dataCenter = "Aether") => new()
    {
        DataCenter = dataCenter,
        WorldName = name,
        TotalQuantityPurchased = 2,
        HasSufficientStock = true,
        Listings = [new ShoppingListingEntry { Quantity = 2, NeededFromStack = 2, PricePerUnit = 50 }],
        ExcludedListings = [new ShoppingListingEntry { Quantity = 99, PricePerUnit = 999_999 }]
    };

    private static MarketCoverageOption Coverage(
        string candidateId,
        string dataCenter,
        string worldName,
        params MarketCoverageWorld[] additionalWorlds) =>
        new(
            candidateId,
            MarketCoverageTier.SingleWorld,
            MarketCoverageKind.SupportedListings,
            MarketCoverageQualityPolicy.HqOnly,
            2,
            2,
            0,
            100,
            100,
            50,
            MarketCoveragePriceBand.Competitive,
            [new MarketCoverageWorld(dataCenter, worldName, 2, 2, 100, 100), .. additionalWorlds],
            [new MarketCoverageListing(dataCenter, worldName, 2, 2, 2, 50, true)],
            new MarketCoverageFriction(1, 1, 0, 0, 0),
            MarketCoverageSavings.None,
            true,
            null);
}
