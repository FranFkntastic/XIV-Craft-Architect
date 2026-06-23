using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class PurchaseSummaryServiceTests
{
    [Fact]
    public void CreateSummary_RecommendedSplitAvailable_PrefersSplitTotalCost()
    {
        var service = new PurchaseSummaryService();
        var plan = new DetailedShoppingPlan
        {
            ItemId = 123,
            Name = "Route Item",
            QuantityNeeded = 5,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalCost = 5_000,
                AveragePricePerUnit = 1_000,
                TotalQuantityPurchased = 5
            },
            RecommendedSplit =
            [
                new SplitWorldPurchase
                {
                    DataCenter = "Primal",
                    WorldName = "Leviathan",
                    QuantityToBuy = 5,
                    TotalCost = 3_000,
                    EffectivePricePerNeededUnit = 600
                }
            ]
        };

        var summary = service.CreateSummary(plan);

        Assert.Equal(3_000, summary.TotalCost);
        Assert.Equal(600, summary.AveragePricePerUnit);
    }

    [Fact]
    public void CreateSummary_UsesCoverageCashOutForExecutionCost()
    {
        var service = new PurchaseSummaryService();
        var coverageOption = CreateCoverageOption(
            exactNeededCost: 1_000,
            cashOutCost: 1_200,
            quantityCovered: 10,
            quantityToPurchase: 12);
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Execution Material",
            QuantityNeeded = 10,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Adamantoise",
                TotalCost = 5_000,
                TotalQuantityPurchased = 10
            },
            CoverageSet = new MarketCoverageSet(
                100,
                "Execution Material",
                10,
                SingleWorld: coverageOption,
                CompactSplit: null,
                WideSplit: null,
                CheapestObserved: null,
                AllCandidates: [coverageOption])
        };

        var summary = service.CreateSummary(plan);

        Assert.Equal(12, summary.QuantityToPurchase);
        Assert.Equal(2, summary.ExcessQuantity);
        Assert.Equal(1_200, summary.TotalCost);
        Assert.Equal(120, summary.AveragePricePerUnit);
    }

    private static MarketCoverageOption CreateCoverageOption(
        decimal exactNeededCost,
        decimal cashOutCost,
        int quantityCovered,
        int quantityToPurchase)
    {
        return new MarketCoverageOption(
            CandidateId: "100-10-singleworld-nqorhq-siren",
            Tier: MarketCoverageTier.SingleWorld,
            Kind: MarketCoverageKind.SupportedListings,
            QualityPolicy: MarketCoverageQualityPolicy.NqOrHq,
            QuantityCovered: quantityCovered,
            QuantityToPurchase: quantityToPurchase,
            ExcessQuantity: quantityToPurchase - quantityCovered,
            ExactNeededCost: exactNeededCost,
            CashOutCost: cashOutCost,
            AverageUnitCost: exactNeededCost / quantityCovered,
            PriceBand: MarketCoveragePriceBand.Competitive,
            Worlds:
            [
                new MarketCoverageWorld(
                    DataCenter: "Aether",
                    WorldName: "Siren",
                    QuantityCovered: quantityCovered,
                    QuantityToPurchase: quantityToPurchase,
                    ExactNeededCost: exactNeededCost,
                    CashOutCost: cashOutCost)
            ],
            Listings:
            [
                new MarketCoverageListing(
                    DataCenter: "Aether",
                    WorldName: "Siren",
                    QuantityAvailable: quantityToPurchase,
                    QuantityUsed: quantityCovered,
                    QuantityPurchased: quantityToPurchase,
                    PricePerUnit: exactNeededCost / quantityCovered,
                    IsHq: false)
            ],
            Friction: new MarketCoverageFriction(
                WorldCount: 1,
                DataCenterCount: 1,
                SmallestContribution: quantityCovered,
                LargestContribution: quantityCovered,
                ExcessQuantity: quantityToPurchase - quantityCovered),
            Savings: MarketCoverageSavings.None,
            IsDefaultEligible: true,
            DegradedReason: null);
    }
}
