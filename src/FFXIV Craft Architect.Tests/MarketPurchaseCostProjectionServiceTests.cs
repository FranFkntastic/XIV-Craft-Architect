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
    public void Estimate_NqUnsupportedMarketScope_IncludesHqListings()
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

        Assert.Equal(MarketPurchaseCostEstimateKind.UnsupportedProjection, nqEstimate.Kind);
        Assert.Equal(MarketPurchaseCostEstimateKind.SupportedEvidence, hqEstimate.Kind);
        Assert.Equal(1_000, nqEstimate.Cost);
        Assert.Equal(1_000, hqEstimate.Cost);
        Assert.True(nqEstimate.Cost <= hqEstimate.Cost);
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
}
