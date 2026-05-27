using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class ProcurementWorldCardBuilderTests
{
    [Fact]
    public void BuildWorldCards_SplitPurchasesWithSameWorldNameDifferentDataCenters_RemainDistinct()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 123,
            Name = "Route Item",
            QuantityNeeded = 5,
            WorldOptions =
            {
                World("Aether", "Siren", isCongested: true),
                World("Primal", "Siren")
            },
            RecommendedSplit = new List<SplitWorldPurchase>
            {
                Split("Aether", "Siren", quantity: 2, totalCost: 200),
                Split("Primal", "Siren", quantity: 3, totalCost: 450)
            }
        };

        var cards = ProcurementWorldCardBuilder.BuildWorldCards([plan], "Crystal");

        Assert.Equal(2, cards.Count);
        Assert.Collection(
            cards.OrderBy(card => card.DataCenter),
            aether =>
            {
                Assert.Equal("Aether", aether.DataCenter);
                Assert.Equal("Siren", aether.WorldName);
                Assert.True(aether.IsCongested);
                Assert.Equal(2, Assert.Single(aether.Items).QuantityOnThisWorld);
            },
            primal =>
            {
                Assert.Equal("Primal", primal.DataCenter);
                Assert.Equal("Siren", primal.WorldName);
                Assert.False(primal.IsCongested);
                Assert.Equal(3, Assert.Single(primal.Items).QuantityOnThisWorld);
            });
    }

    [Fact]
    public void BuildWorldCards_SplitPurchasesUseEffectiveCostLabel()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 123,
            Name = "Effective Cost Item",
            QuantityNeeded = 2,
            RecommendedSplit = new List<SplitWorldPurchase>
            {
                new()
                {
                    DataCenter = "Aether",
                    WorldName = "Cactuar",
                    QuantityToBuy = 1,
                    TotalCost = 500,
                    PricePerUnit = 100,
                    EffectivePricePerNeededUnit = 500,
                    TravelContext = TravelContextConstants.Primary
                },
                new()
                {
                    DataCenter = "Primal",
                    WorldName = "Leviathan",
                    QuantityToBuy = 1,
                    TotalCost = 700,
                    PricePerUnit = 100,
                    EffectivePricePerNeededUnit = 700,
                    TravelContext = TravelContextConstants.Supplemental
                }
            }
        };

        var cards = ProcurementWorldCardBuilder.BuildWorldCards([plan], "Aether");

        Assert.All(cards, card =>
        {
            var item = Assert.Single(card.Items);
            Assert.True(item.PriceIsEffectiveCost);
            Assert.EndsWith("eff.", item.PriceDisplay);
        });
    }

    [Fact]
    public void BuildWorldCards_SplitAndSingleWorldPurchases_SurfaceAllCostsForProcurementTotals()
    {
        var singleWorld = World("Aether", "Cactuar");
        singleWorld.TotalCost = 300;
        singleWorld.AveragePricePerUnit = 150;

        var singlePlan = new DetailedShoppingPlan
        {
            ItemId = 124,
            Name = "Single Item",
            QuantityNeeded = 2,
            RecommendedWorld = singleWorld,
            WorldOptions = { singleWorld }
        };

        var splitPlan = new DetailedShoppingPlan
        {
            ItemId = 125,
            Name = "Split Item",
            QuantityNeeded = 2,
            RecommendedSplit = new List<SplitWorldPurchase>
            {
                Split("Aether", "Cactuar", quantity: 1, totalCost: 500),
                Split("Primal", "Leviathan", quantity: 1, totalCost: 700)
            }
        };

        var cards = ProcurementWorldCardBuilder.BuildWorldCards([singlePlan, splitPlan], "Aether");

        Assert.Equal(1500, cards.Sum(card => card.TotalCost));
        Assert.Contains(cards, card => card.WorldName == "Cactuar" && card.TotalCost == 800);
        Assert.Contains(cards, card => card.WorldName == "Leviathan" && card.TotalCost == 700);
    }

    [Fact]
    public void BuildWorldCards_SplitRecommendationWithoutRecommendedWorld_StillSurfacesCost()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 126,
            Name = "Split Only Item",
            QuantityNeeded = 1,
            RecommendedWorld = null,
            RecommendedSplit = new List<SplitWorldPurchase>
            {
                Split("Primal", "Leviathan", quantity: 1, totalCost: 700)
            }
        };

        var cards = ProcurementWorldCardBuilder.BuildWorldCards([plan], "Aether");

        var card = Assert.Single(cards);
        Assert.Equal("Primal", card.DataCenter);
        Assert.Equal("Leviathan", card.WorldName);
        Assert.Equal(700, card.TotalCost);
    }

    private static WorldShoppingSummary World(string dataCenter, string worldName, bool isCongested = false)
    {
        return new WorldShoppingSummary
        {
            DataCenter = dataCenter,
            WorldName = worldName,
            Classification = isCongested ? WorldClassification.Congested : WorldClassification.Standard
        };
    }

    private static SplitWorldPurchase Split(string dataCenter, string worldName, int quantity, long totalCost)
    {
        return new SplitWorldPurchase
        {
            DataCenter = dataCenter,
            WorldName = worldName,
            QuantityToBuy = quantity,
            TotalCost = totalCost,
            PricePerUnit = totalCost / (decimal)quantity,
            EffectivePricePerNeededUnit = totalCost / (decimal)quantity
        };
    }
}
