using FFXIV_Craft_Architect.Coordinators;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.ViewModels;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class ExpandedPanelViewModelTests
{
    [Fact]
    public void OpenDetailsCommand_WithCoordinator_CallsOpenDetailsWindow()
    {
        var plan = CreatePlan();
        var coordinator = new Mock<IMarketLogisticsCoordinator>(MockBehavior.Strict);
        coordinator.Setup(c => c.OpenDetailsWindow(plan));

        var viewModel = new ExpandedPanelViewModel(plan, coordinator.Object);

        viewModel.OpenDetailsCommand.Execute(null);

        coordinator.Verify(c => c.OpenDetailsWindow(plan), Times.Once);
    }

    [Fact]
    public void OpenDetailsCommand_WithoutCoordinator_DoesNotThrow()
    {
        var plan = CreatePlan();
        var viewModel = new ExpandedPanelViewModel(plan, null);

        var ex = Record.Exception(() => viewModel.OpenDetailsCommand.Execute(null));

        Assert.Null(ex);
    }

    [Fact]
    public void SingleWorldMode_DefaultExpansion_RecommendedExpandedOthersCollapsed()
    {
        var recommended = new WorldShoppingSummary
        {
            WorldName = "Siren",
            TotalCost = 19000,
            AveragePricePerUnit = 95,
            TotalQuantityPurchased = 200,
            Listings = new List<ShoppingListingEntry>
            {
                new() { Quantity = 200, NeededFromStack = 200, PricePerUnit = 95, RetainerName = "Ret A" }
            }
        };

        var alternate = new WorldShoppingSummary
        {
            WorldName = "Adamantoise",
            TotalCost = 21000,
            AveragePricePerUnit = 105,
            TotalQuantityPurchased = 200,
            Listings = new List<ShoppingListingEntry>
            {
                new() { Quantity = 200, NeededFromStack = 200, PricePerUnit = 105, RetainerName = "Ret B" }
            }
        };

        var plan = new DetailedShoppingPlan
        {
            ItemId = 1001,
            Name = "Cedar Lumber",
            QuantityNeeded = 200,
            DCAveragePrice = 95,
            RecommendedWorld = recommended,
            WorldOptions = new List<WorldShoppingSummary> { recommended, alternate }
        };

        var viewModel = new ExpandedPanelViewModel(plan, null);

        Assert.True(viewModel.ShowSingleWorldOptions);
        Assert.False(viewModel.HasSplitWorldOptions);

        var recommendedVm = Assert.Single(viewModel.WorldOptions, w => w.IsRecommended);
        var nonRecommendedVm = Assert.Single(viewModel.WorldOptions, w => !w.IsRecommended);

        Assert.True(recommendedVm.IsExpanded);
        Assert.False(nonRecommendedVm.IsExpanded);
    }

    [Fact]
    public void SplitWorldMode_IncludesAllRecommendedSplitWorlds()
    {
        var worldSiren = new WorldShoppingSummary
        {
            DataCenter = "Aether",
            WorldName = "Siren",
            Listings = new List<ShoppingListingEntry>()
        };
        var worldAda = new WorldShoppingSummary
        {
            DataCenter = "Aether",
            WorldName = "Adamantoise",
            Listings = new List<ShoppingListingEntry>()
        };

        var plan = new DetailedShoppingPlan
        {
            ItemId = 1002,
            Name = "Palm Syrup",
            QuantityNeeded = 300,
            DCAveragePrice = 120,
            WorldOptions = new List<WorldShoppingSummary> { worldSiren, worldAda },
            RecommendedSplit = new List<SplitWorldPurchase>
            {
                new() { DataCenter = "Aether", WorldName = "Siren", QuantityToBuy = 100, TotalCost = 10000, TravelContext = "Primary" },
                new() { DataCenter = "Aether", WorldName = "Adamantoise", QuantityToBuy = 200, TotalCost = 22000, TravelContext = "Supplemental" }
            }
        };

        var viewModel = new ExpandedPanelViewModel(plan, null);

        Assert.True(viewModel.HasSplitWorldOptions);
        Assert.False(viewModel.ShowSingleWorldOptions);
        Assert.Equal(2, viewModel.SplitWorlds.Count);
        Assert.Contains(viewModel.SplitWorlds, w => w.WorldDisplayName == "Siren (Aether)");
        Assert.Contains(viewModel.SplitWorlds, w => w.WorldDisplayName == "Adamantoise (Aether)");
    }

    [Fact]
    public void SplitWorldMode_MatchesWorldDataByDataCenterAndWorldName()
    {
        var aetherSiren = new WorldShoppingSummary
        {
            DataCenter = "Aether",
            WorldName = "Siren",
            Classification = WorldClassification.Standard,
            Listings = new List<ShoppingListingEntry>()
        };
        var primalSiren = new WorldShoppingSummary
        {
            DataCenter = "Primal",
            WorldName = "Siren",
            Classification = WorldClassification.Congested,
            Listings = new List<ShoppingListingEntry>()
        };

        var plan = new DetailedShoppingPlan
        {
            ItemId = 1003,
            Name = "Ambiguous Siren Item",
            QuantityNeeded = 100,
            DCAveragePrice = 120,
            WorldOptions = new List<WorldShoppingSummary> { aetherSiren, primalSiren },
            RecommendedSplit = new List<SplitWorldPurchase>
            {
                new()
                {
                    DataCenter = "Primal",
                    WorldName = "Siren",
                    QuantityToBuy = 100,
                    TotalCost = 10000,
                    TravelContext = "Primary"
                },
                new()
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    QuantityToBuy = 1,
                    TotalCost = 1,
                    TravelContext = "Supplemental"
                }
            }
        };

        var viewModel = new ExpandedPanelViewModel(plan, null);

        var primal = Assert.Single(viewModel.SplitWorlds, w => w.DataCenter == "Primal");
        Assert.Equal("Siren (Primal)", primal.WorldDisplayName);
        Assert.True(primal.IsCongested);
    }

    private static DetailedShoppingPlan CreatePlan()
    {
        return new DetailedShoppingPlan
        {
            ItemId = 1001,
            Name = "Cedar Lumber",
            QuantityNeeded = 200,
            DCAveragePrice = 95,
            WorldOptions = new List<WorldShoppingSummary>
            {
                new()
                {
                    WorldName = "Siren",
                    TotalCost = 19000,
                    AveragePricePerUnit = 95,
                    TotalQuantityPurchased = 200,
                    Listings = new List<ShoppingListingEntry>
                    {
                        new() { Quantity = 200, NeededFromStack = 200, PricePerUnit = 95, RetainerName = "Ret" }
                    }
                }
            },
            RecommendedWorld = new WorldShoppingSummary
            {
                WorldName = "Siren",
                TotalCost = 19000,
                AveragePricePerUnit = 95,
                TotalQuantityPurchased = 200,
                Listings = new List<ShoppingListingEntry>
                {
                    new() { Quantity = 200, NeededFromStack = 200, PricePerUnit = 95, RetainerName = "Ret" }
                }
            }
        };
    }
}
