using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketCoverageBuilderTests
{
    [Fact]
    public void CoverageOption_CarriesExactNeededAndCashOutCostsSeparately()
    {
        var option = new MarketCoverageOption(
            CandidateId: "100-10-singleworld-nqorhq-siren",
            Tier: MarketCoverageTier.SingleWorld,
            Kind: MarketCoverageKind.SupportedListings,
            QualityPolicy: MarketCoverageQualityPolicy.NqOrHq,
            QuantityCovered: 10,
            QuantityToPurchase: 12,
            ExcessQuantity: 2,
            ExactNeededCost: 1_000,
            CashOutCost: 1_200,
            AverageUnitCost: 100,
            PriceBand: MarketCoveragePriceBand.Competitive,
            Worlds:
            [
                new MarketCoverageWorld(
                    DataCenter: "Aether",
                    WorldName: "Siren",
                    QuantityCovered: 10,
                    QuantityToPurchase: 12,
                    ExactNeededCost: 1_000,
                    CashOutCost: 1_200)
            ],
            Listings:
            [
                new MarketCoverageListing(
                    DataCenter: "Aether",
                    WorldName: "Siren",
                    QuantityAvailable: 12,
                    QuantityUsed: 10,
                    QuantityPurchased: 12,
                    PricePerUnit: 100,
                    IsHq: false)
            ],
            Friction: new MarketCoverageFriction(
                WorldCount: 1,
                DataCenterCount: 1,
                SmallestContribution: 10,
                LargestContribution: 10,
                ExcessQuantity: 2),
            Savings: MarketCoverageSavings.None,
            IsDefaultEligible: true,
            DegradedReason: null);

        Assert.Equal(1_000, option.ExactNeededCost);
        Assert.Equal(1_200, option.CashOutCost);
        Assert.Equal(2, option.ExcessQuantity);
        Assert.True(option.IsDefaultEligible);
    }

    [Fact]
    public void Build_SelectsCheapestSingleWorldUsingExactNeededCost()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Darksteel Ingot",
            QuantityNeeded = 10,
            WorldOptions =
            [
                World("Aether", "Adamantoise", 10, 500),
                World("Aether", "Siren", 12, 100)
            ]
        };

        var coverage = MarketCoverageBuilder.Build(plan);

        Assert.NotNull(coverage.SingleWorld);
        Assert.Equal(MarketCoverageTier.SingleWorld, coverage.SingleWorld.Tier);
        Assert.Equal("Siren", Assert.Single(coverage.SingleWorld.Worlds).WorldName);
        Assert.Equal(1_000, coverage.SingleWorld.ExactNeededCost);
        Assert.Equal(1_200, coverage.SingleWorld.CashOutCost);
        Assert.True(coverage.SingleWorld.IsDefaultEligible);
    }

    [Fact]
    public void Build_NqCandidateDoesNotExceedHqCandidateForSameListings()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Mixed Quality Material",
            QuantityNeeded = 10,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    Listings =
                    [
                        new ShoppingListingEntry { Quantity = 10, PricePerUnit = 500, IsHq = false },
                        new ShoppingListingEntry { Quantity = 10, PricePerUnit = 100, IsHq = true }
                    ]
                }
            ]
        };

        var coverage = MarketCoverageBuilder.Build(plan);
        var nq = Assert.Single(coverage.AllCandidates, c =>
            c.Tier == MarketCoverageTier.SingleWorld &&
            c.QualityPolicy == MarketCoverageQualityPolicy.NqOrHq);
        var hq = Assert.Single(coverage.AllCandidates, c =>
            c.Tier == MarketCoverageTier.SingleWorld &&
            c.QualityPolicy == MarketCoverageQualityPolicy.HqOnly);

        Assert.True(nq.ExactNeededCost <= hq.ExactNeededCost);
    }

    [Fact]
    public void Build_CompactSplitUsesAtMostTwoWorldsByDefault()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Split Material",
            QuantityNeeded = 100,
            WorldOptions =
            [
                World("Aether", "Adamantoise", 60, 100),
                World("Aether", "Siren", 40, 100),
                World("Aether", "Cactuar", 40, 99)
            ]
        };

        var coverage = MarketCoverageBuilder.Build(plan);

        Assert.NotNull(coverage.CompactSplit);
        Assert.Equal(MarketCoverageTier.CompactSplit, coverage.CompactSplit.Tier);
        Assert.True(coverage.CompactSplit.Friction.WorldCount <= 2);
        Assert.True(coverage.CompactSplit.IsDefaultEligible);
    }

    [Fact]
    public void Build_CompactSplitRejectsTinySupplementalContribution()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Tiny Crumb Material",
            QuantityNeeded = 100,
            WorldOptions =
            [
                World("Aether", "Adamantoise", 95, 100),
                World("Aether", "Siren", 5, 1)
            ]
        };

        var coverage = MarketCoverageBuilder.Build(plan);

        Assert.Null(coverage.CompactSplit);
        Assert.NotNull(coverage.CheapestObserved);
        Assert.False(coverage.CheapestObserved.IsDefaultEligible);
    }

    [Fact]
    public void CoverageSet_AssignedToShoppingPlan_PreservesLegacyRecommendationProjection()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Projected Material",
            QuantityNeeded = 10,
            WorldOptions =
            [
                World("Aether", "Siren", 10, 100)
            ]
        };

        plan.CoverageSet = MarketCoverageBuilder.Build(plan);
        var world = Assert.Single(plan.CoverageSet.SingleWorld!.Worlds);
        plan.RecommendedWorld = plan.WorldOptions.Single(option => option.WorldName == world.WorldName);

        Assert.NotNull(plan.CoverageSet);
        Assert.Equal("Siren", plan.RecommendedWorld.WorldName);
    }

    private static WorldShoppingSummary World(string dataCenter, string worldName, int quantity, long pricePerUnit)
    {
        return new WorldShoppingSummary
        {
            DataCenter = dataCenter,
            WorldName = worldName,
            Listings =
            [
                new ShoppingListingEntry
                {
                    Quantity = quantity,
                    PricePerUnit = pricePerUnit
                }
            ]
        };
    }
}
