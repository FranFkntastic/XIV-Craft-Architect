using FFXIV_Craft_Architect.Core.Models;

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
}
