using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.ViewModels;

namespace FFXIV_Craft_Architect.Tests;

public class SplitWorldWindowViewModelTests
{
    [Fact]
    public void SplitWorldCardViewModel_InitialState_UsesCollapsedViewAndExpectedDisplayValues()
    {
        var split = new SplitWorldPurchase
        {
            WorldName = "Siren",
            QuantityToBuy = 14,
            PricePerUnit = 57,
            TotalCost = 798,
            IsPartial = true,
            TravelContext = "Supplemental",
            ExcessAvailable = 2,
            Listings = new List<ShoppingListingEntry>
            {
                new()
                {
                    Quantity = 16,
                    PricePerUnit = 57,
                    NeededFromStack = 14,
                    ExcessQuantity = 2,
                    RetainerName = "Retainer A"
                }
            }
        };

        var viewModel = new SplitWorldCardViewModel(split, totalQuantity: 2880);

        Assert.False(viewModel.IsExpanded);
        Assert.Equal("Siren", viewModel.WorldName);
        Assert.Equal("×14 of 2880", viewModel.QuantityDisplay);
        Assert.Equal("798g", viewModel.CostDisplay);
        Assert.Equal("@57g/ea", viewModel.PriceDisplay);
        Assert.True(viewModel.HasListings);
        Assert.True(viewModel.HasExcess);
        Assert.Equal("+2 excess", viewModel.ExcessDisplay);
    }

    [Fact]
    public void SplitWorldCardViewModel_ToggleExpandCommand_TogglesExpandedState()
    {
        var split = new SplitWorldPurchase { WorldName = "Siren", QuantityToBuy = 10, TotalCost = 1000 };
        var viewModel = new SplitWorldCardViewModel(split, totalQuantity: 100);

        viewModel.ToggleExpandCommand.Execute(null);
        Assert.True(viewModel.IsExpanded);

        viewModel.ToggleExpandCommand.Execute(null);
        Assert.False(viewModel.IsExpanded);
    }

    [Fact]
    public void SplitWorldWindowViewModel_WithSplitPlan_PopulatesWorldCardsAndTotal()
    {
        var plan = new DetailedShoppingPlan
        {
            Name = "Cedar Lumber",
            QuantityNeeded = 2880,
            DCAveragePrice = 100,
            RecommendedSplit = new List<SplitWorldPurchase>
            {
                new()
                {
                    WorldName = "Siren",
                    QuantityToBuy = 14,
                    PricePerUnit = 57,
                    TotalCost = 798,
                    TravelContext = "Supplemental"
                },
                new()
                {
                    WorldName = "Adamantoise",
                    QuantityToBuy = 2866,
                    PricePerUnit = 60,
                    TotalCost = 171960,
                    TravelContext = "Primary"
                }
            }
        };

        var viewModel = new SplitWorldWindowViewModel(plan);

        Assert.Equal("Cedar Lumber ×2880", viewModel.HeaderText);
        Assert.True(viewModel.IsSplitMode);
        Assert.Equal(172758, viewModel.DetailsTotalCost);
        Assert.Equal(2, viewModel.WorldCards.Count);
        Assert.Contains(viewModel.WorldCards, c => c.WorldName == "Siren");
        Assert.Contains(viewModel.WorldCards, c => c.WorldName == "Adamantoise");
    }

    [Fact]
    public void SplitWorldWindowViewModel_WithSingleWorldPlan_BuildsAllWorldRecommendationCards()
    {
        var recommended = new WorldShoppingSummary
        {
            WorldName = "Sargatanas",
            TotalCost = 12000,
            AveragePricePerUnit = 120,
            TotalQuantityPurchased = 100,
            Listings = new List<ShoppingListingEntry>
            {
                new() { Quantity = 100, NeededFromStack = 100, PricePerUnit = 120, RetainerName = "Ret A" }
            }
        };

        var alternate = new WorldShoppingSummary
        {
            WorldName = "Adamantoise",
            TotalCost = 13000,
            AveragePricePerUnit = 130,
            TotalQuantityPurchased = 100,
            Listings = new List<ShoppingListingEntry>
            {
                new() { Quantity = 100, NeededFromStack = 100, PricePerUnit = 130, RetainerName = "Ret B" }
            }
        };

        var plan = new DetailedShoppingPlan
        {
            Name = "Palm Syrup",
            QuantityNeeded = 100,
            DCAveragePrice = 140,
            RecommendedWorld = recommended,
            WorldOptions = new List<WorldShoppingSummary> { recommended, alternate }
        };

        var viewModel = new SplitWorldWindowViewModel(plan);

        Assert.False(viewModel.IsSplitMode);
        Assert.Equal("World Purchase Options (2 options)", viewModel.RecommendationsHeaderText);
        Assert.Equal(12000, viewModel.DetailsTotalCost);
        Assert.Equal(2, viewModel.WorldCards.Count);
        Assert.Contains(viewModel.WorldCards, c => c.WorldName == "Sargatanas");
        Assert.Contains(viewModel.WorldCards, c => c.WorldName == "Adamantoise");
    }

    [Fact]
    public void SplitWorldWindowViewModel_CloseCommand_RaisesRequestCloseEvent()
    {
        var plan = new DetailedShoppingPlan
        {
            Name = "Cedar Lumber",
            QuantityNeeded = 100,
            RecommendedSplit = new List<SplitWorldPurchase>
            {
                new() { WorldName = "Siren", QuantityToBuy = 100, TotalCost = 10000 }
            }
        };

        var viewModel = new SplitWorldWindowViewModel(plan);
        var eventRaised = false;
        viewModel.RequestClose += (_, _) => eventRaised = true;

        viewModel.CloseCommand.Execute(null);

        Assert.True(eventRaised);
    }
}
