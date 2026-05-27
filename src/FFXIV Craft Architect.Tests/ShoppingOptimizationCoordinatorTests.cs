using FFXIV_Craft_Architect.Core.Coordinators;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class ShoppingOptimizationCoordinatorTests
{
    [Fact]
    public void SortPlans_PriceHighToLow_UsesRecommendedSplitCost()
    {
        var coordinator = new ShoppingOptimizationCoordinator(
            new MarketShoppingService(Mock.Of<IMarketCacheService>()),
            Mock.Of<ILogger<ShoppingOptimizationCoordinator>>());
        var splitPlan = new DetailedShoppingPlan
        {
            ItemId = 1,
            Name = "Split Plan",
            QuantityNeeded = 10,
            RecommendedWorld = new WorldShoppingSummary
            {
                WorldName = "Siren",
                TotalCost = 10_000,
                TotalQuantityPurchased = 10
            },
            RecommendedSplit =
            [
                new SplitWorldPurchase
                {
                    WorldName = "Leviathan",
                    QuantityToBuy = 10,
                    TotalCost = 4_000,
                    EffectivePricePerNeededUnit = 400
                }
            ]
        };
        var worldPlan = new DetailedShoppingPlan
        {
            ItemId = 2,
            Name = "World Plan",
            QuantityNeeded = 10,
            RecommendedWorld = new WorldShoppingSummary
            {
                WorldName = "Cactuar",
                TotalCost = 7_000,
                TotalQuantityPurchased = 10
            }
        };

        var sorted = coordinator.SortPlans([splitPlan, worldPlan], ShoppingPlanSortMode.PriceHighToLow);

        Assert.Equal([worldPlan, splitPlan], sorted);
    }

    [Fact]
    public void CalculateTotalCost_UsesRecommendedSplitCost()
    {
        var splitPlan = new DetailedShoppingPlan
        {
            ItemId = 1,
            Name = "Split Plan",
            QuantityNeeded = 10,
            RecommendedWorld = new WorldShoppingSummary
            {
                WorldName = "Siren",
                TotalCost = 10_000,
                TotalQuantityPurchased = 10
            },
            RecommendedSplit =
            [
                new SplitWorldPurchase
                {
                    WorldName = "Leviathan",
                    QuantityToBuy = 10,
                    TotalCost = 4_000,
                    EffectivePricePerNeededUnit = 400
                }
            ]
        };
        var worldPlan = new DetailedShoppingPlan
        {
            ItemId = 2,
            Name = "World Plan",
            QuantityNeeded = 10,
            RecommendedWorld = new WorldShoppingSummary
            {
                WorldName = "Cactuar",
                TotalCost = 7_000,
                TotalQuantityPurchased = 10
            }
        };

        var totalCost = ShoppingOptimizationCoordinator.CalculateTotalCost([splitPlan, worldPlan]);

        Assert.Equal(11_000, totalCost);
    }
}
