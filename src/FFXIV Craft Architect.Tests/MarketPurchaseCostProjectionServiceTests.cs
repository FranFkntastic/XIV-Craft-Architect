using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketPurchaseCostProjectionServiceTests
{
    [Fact]
    public void Estimate_UnsupportedMarketScope_UsesProjectedUnitPriceForFullQuantity()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Cassia Lumber",
            QuantityNeeded = 999,
            DCAveragePrice = 6_408,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Adamantoise",
                    TotalCost = 3_000,
                    TotalQuantityPurchased = 1
                }
            ]
        };

        var estimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 999, hqOnly: false);

        Assert.Equal(MarketPurchaseCostEstimateKind.UnsupportedProjection, estimate.Kind);
        Assert.Equal(6_401_592, estimate.Cost);
        Assert.True(estimate.IsUnsupportedProjection);
    }

    [Fact]
    public void Estimate_FullMarketEvidence_ReturnsSupportedCost()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Supported Ore",
            QuantityNeeded = 10,
            DCAveragePrice = 6_408,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalCost = 1_000,
                TotalQuantityPurchased = 10
            }
        };

        var estimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 7, hqOnly: false);

        Assert.Equal(MarketPurchaseCostEstimateKind.SupportedEvidence, estimate.Kind);
        Assert.Equal(700, estimate.Cost);
        Assert.False(estimate.IsUnsupportedProjection);
    }

    [Fact]
    public void Estimate_NqSupportedEvidence_UsesExactNeededListingCost()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Darksteel Ingot",
            QuantityNeeded = 999,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Crystal",
                WorldName = "Diabolos",
                TotalCost = 6_129_830,
                TotalQuantityPurchased = 1_063,
                Listings =
                [
                    new ShoppingListingEntry { Quantity = 1, PricePerUnit = 5_598 },
                    new ShoppingListingEntry { Quantity = 46, PricePerUnit = 5_599 },
                    new ShoppingListingEntry { Quantity = 2, PricePerUnit = 5_600 },
                    new ShoppingListingEntry { Quantity = 2, PricePerUnit = 5_600 },
                    new ShoppingListingEntry { Quantity = 22, PricePerUnit = 5_774, IsHq = true },
                    new ShoppingListingEntry { Quantity = 99, PricePerUnit = 5_775, IsHq = true },
                    new ShoppingListingEntry { Quantity = 99, PricePerUnit = 5_775, IsHq = true },
                    new ShoppingListingEntry { Quantity = 99, PricePerUnit = 5_775, IsHq = true },
                    new ShoppingListingEntry { Quantity = 99, PricePerUnit = 5_775, IsHq = true },
                    new ShoppingListingEntry { Quantity = 99, PricePerUnit = 5_775, IsHq = true },
                    new ShoppingListingEntry { Quantity = 99, PricePerUnit = 5_775, IsHq = true },
                    new ShoppingListingEntry { Quantity = 99, PricePerUnit = 5_775, IsHq = true },
                    new ShoppingListingEntry { Quantity = 99, PricePerUnit = 5_775, IsHq = true },
                    new ShoppingListingEntry { Quantity = 99, PricePerUnit = 5_775, IsHq = true },
                    new ShoppingListingEntry { Quantity = 99, PricePerUnit = 5_775, IsHq = true }
                ]
            }
        };

        var estimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 999, hqOnly: false);
        var hqEstimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 999, hqOnly: true);

        Assert.Equal(MarketPurchaseCostEstimateKind.SupportedEvidence, estimate.Kind);
        Assert.Equal(5_760_230, estimate.Cost);
        Assert.Equal("Diabolos", estimate.World?.WorldName);
        Assert.Equal(MarketPurchaseCostEstimateKind.SupportedEvidence, hqEstimate.Kind);
        Assert.Equal(5_769_203, hqEstimate.Cost);
        Assert.True(estimate.Cost <= hqEstimate.Cost);
    }

    [Fact]
    public void Estimate_UsesDefaultEligibleCoverageExactNeededCost()
    {
        var coverageOption = CreateCoverageOption(
            "Siren",
            exactNeededCost: 1_000,
            cashOutCost: 1_200,
            isDefaultEligible: true);
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Coverage Material",
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
                "Coverage Material",
                10,
                SingleWorld: coverageOption,
                CompactSplit: null,
                WideSplit: null,
                CheapestObserved: null,
                AllCandidates: [coverageOption])
        };

        var estimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 10, hqOnly: false);

        Assert.Equal(MarketPurchaseCostEstimateKind.SupportedEvidence, estimate.Kind);
        Assert.Equal(1_000, estimate.Cost);
        Assert.Equal(1_200, PurchaseRecommendationCost.GetRecommendedCashOutCost(plan));
        Assert.Equal("Siren", estimate.World?.WorldName);
    }

    [Fact]
    public void Estimate_VendorRecommendation_ReturnsSupportedCost()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Vendor Item",
            QuantityNeeded = 10,
            RecommendedWorld = new WorldShoppingSummary
            {
                WorldName = MarketShoppingConstants.VendorWorldName,
                TotalCost = 1_000,
                TotalQuantityPurchased = 10
            }
        };

        var estimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 7, hqOnly: false);

        Assert.Equal(MarketPurchaseCostEstimateKind.SupportedEvidence, estimate.Kind);
        Assert.Equal(700, estimate.Cost);
        Assert.False(estimate.IsUnsupportedProjection);
    }

    [Fact]
    public void Estimate_HqUnsupportedMarketScope_UsesHqProjectionForFullQuantity()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "HQ Material",
            QuantityNeeded = 10,
            HQAveragePrice = 8_000,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 1,
                            PricePerUnit = 3_000,
                            IsHq = true
                        }
                    ]
                }
            ]
        };

        var estimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 10, hqOnly: true);

        Assert.Equal(MarketPurchaseCostEstimateKind.UnsupportedProjection, estimate.Kind);
        Assert.Equal(80_000, estimate.Cost);
        Assert.True(estimate.IsUnsupportedProjection);
    }

    [Fact]
    public void Estimate_NqListingEvidence_IncludesHqListings()
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
                        new ShoppingListingEntry
                        {
                            Quantity = 10,
                            PricePerUnit = 500,
                            IsHq = false
                        },
                        new ShoppingListingEntry
                        {
                            Quantity = 10,
                            PricePerUnit = 100,
                            IsHq = true
                        }
                    ]
                }
            ]
        };

        var nqEstimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 10, hqOnly: false);
        var hqEstimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 10, hqOnly: true);

        Assert.Equal(MarketPurchaseCostEstimateKind.SupportedEvidence, nqEstimate.Kind);
        Assert.Equal(MarketPurchaseCostEstimateKind.SupportedEvidence, hqEstimate.Kind);
        Assert.Equal(1_000, nqEstimate.Cost);
        Assert.Equal(1_000, hqEstimate.Cost);
        Assert.True(nqEstimate.Cost <= hqEstimate.Cost);
    }

    [Fact]
    public void Estimate_NqEvidence_ChoosesCheapestExplicitWorldOption()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Mixed Recommendation Material",
            QuantityNeeded = 10,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Adamantoise",
                Listings =
                [
                    new ShoppingListingEntry
                    {
                        Quantity = 10,
                        PricePerUnit = 500
                    }
                ]
            },
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 10,
                            PricePerUnit = 100,
                            IsHq = true
                        }
                    ]
                }
            ]
        };

        var nqEstimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 10, hqOnly: false);
        var hqEstimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 10, hqOnly: true);

        Assert.Equal(MarketPurchaseCostEstimateKind.SupportedEvidence, nqEstimate.Kind);
        Assert.Equal(MarketPurchaseCostEstimateKind.SupportedEvidence, hqEstimate.Kind);
        Assert.Equal("Siren", nqEstimate.World?.WorldName);
        Assert.Equal("Siren", hqEstimate.World?.WorldName);
        Assert.Equal(1_000, nqEstimate.Cost);
        Assert.Equal(1_000, hqEstimate.Cost);
        Assert.True(nqEstimate.Cost <= hqEstimate.Cost);
    }

    [Fact]
    public void Estimate_AggregateWorldListingsWithoutExplicitRoute_RemainsUnsupportedProjection()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Split Needed Material",
            QuantityNeeded = 10,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Adamantoise",
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 5,
                            PricePerUnit = 100
                        }
                    ]
                },
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 5,
                            PricePerUnit = 100
                        }
                    ]
                }
            ]
        };

        var estimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 10, hqOnly: false);

        Assert.Equal(MarketPurchaseCostEstimateKind.UnsupportedProjection, estimate.Kind);
        Assert.Equal(1_000, estimate.Cost);
        Assert.Null(estimate.World);
    }

    [Fact]
    public void Estimate_NqUnsupportedAverageProjection_DoesNotExceedHqAverage()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Average Only Material",
            QuantityNeeded = 10,
            DCAveragePrice = 500,
            HQAveragePrice = 100
        };

        var nqEstimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 10, hqOnly: false);
        var hqEstimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 10, hqOnly: true);

        Assert.Equal(MarketPurchaseCostEstimateKind.UnsupportedProjection, nqEstimate.Kind);
        Assert.Equal(MarketPurchaseCostEstimateKind.UnsupportedProjection, hqEstimate.Kind);
        Assert.Equal(1_000, nqEstimate.Cost);
        Assert.Equal(1_000, hqEstimate.Cost);
    }

    [Fact]
    public void Estimate_BlockedPlan_DoesNotUseFallbackProjection()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 100,
            Name = "Blocked Item",
            QuantityNeeded = 10,
            Error = "Suspicious cached market evidence could not be refreshed.",
            DCAveragePrice = 6_408,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    Listings =
                    [
                        new ShoppingListingEntry
                        {
                            Quantity = 10,
                            PricePerUnit = 100
                        }
                    ]
                }
            ]
        };

        var estimate = MarketPurchaseCostProjectionService.Estimate(plan, quantity: 10, hqOnly: false);

        Assert.Equal(MarketPurchaseCostEstimateKind.Unavailable, estimate.Kind);
        Assert.False(estimate.HasCost);
        Assert.False(MarketPurchaseCostProjectionService.IsUnsupportedProjectedCost(plan));
    }

    private static MarketCoverageOption CreateCoverageOption(
        string worldName,
        decimal exactNeededCost,
        decimal cashOutCost,
        bool isDefaultEligible,
        MarketCoverageQualityPolicy qualityPolicy = MarketCoverageQualityPolicy.NqOrHq)
    {
        return new MarketCoverageOption(
            CandidateId: $"100-10-singleworld-{qualityPolicy.ToString().ToLowerInvariant()}-{worldName.ToLowerInvariant()}",
            Tier: MarketCoverageTier.SingleWorld,
            Kind: MarketCoverageKind.SupportedListings,
            QualityPolicy: qualityPolicy,
            QuantityCovered: 10,
            QuantityToPurchase: 12,
            ExcessQuantity: 2,
            ExactNeededCost: exactNeededCost,
            CashOutCost: cashOutCost,
            AverageUnitCost: exactNeededCost / 10,
            PriceBand: MarketCoveragePriceBand.Competitive,
            Worlds:
            [
                new MarketCoverageWorld(
                    DataCenter: "Aether",
                    WorldName: worldName,
                    QuantityCovered: 10,
                    QuantityToPurchase: 12,
                    ExactNeededCost: exactNeededCost,
                    CashOutCost: cashOutCost)
            ],
            Listings:
            [
                new MarketCoverageListing(
                    DataCenter: "Aether",
                    WorldName: worldName,
                    QuantityAvailable: 12,
                    QuantityUsed: 10,
                    QuantityPurchased: 12,
                    PricePerUnit: exactNeededCost / 10,
                    IsHq: qualityPolicy == MarketCoverageQualityPolicy.HqOnly)
            ],
            Friction: new MarketCoverageFriction(
                WorldCount: 1,
                DataCenterCount: 1,
                SmallestContribution: 10,
                LargestContribution: 10,
                ExcessQuantity: 2),
            Savings: MarketCoverageSavings.None,
            IsDefaultEligible: isDefaultEligible,
            DegradedReason: null);
    }
}
