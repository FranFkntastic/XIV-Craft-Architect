using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.ViewModels;

namespace FFXIV_Craft_Architect.Tests;

public class MarketCardViewModelTests
{
    [Fact]
    public void SplitWorlds_ExposeStructuredDataCenterForCollapsedTooltips()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 9001,
            Name = "Same Named World Item",
            QuantityNeeded = 2,
            RecommendedSplit = new List<SplitWorldPurchase>
            {
                new()
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    QuantityToBuy = 1,
                    TotalCost = 100,
                    EffectivePricePerNeededUnit = 100
                },
                new()
                {
                    DataCenter = "Primal",
                    WorldName = "Siren",
                    QuantityToBuy = 1,
                    TotalCost = 200,
                    EffectivePricePerNeededUnit = 200
                }
            }
        };

        var viewModel = new MarketCardViewModel(plan);

        Assert.Collection(
            viewModel.SplitWorlds,
            aether =>
            {
                Assert.Equal("Aether", aether.DataCenter);
                Assert.Equal("Siren (Aether)", aether.WorldDisplayName);
            },
            primal =>
            {
                Assert.Equal("Primal", primal.DataCenter);
                Assert.Equal("Siren (Primal)", primal.WorldDisplayName);
            });
    }
}
