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
}
