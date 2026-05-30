using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class RecipePlanTreeDisplayBuilderTests
{
    [Fact]
    public void Build_ProjectsPriceAndRecipeInfoForEveryNode()
    {
        var child = new PlanNode
        {
            ItemId = 200,
            NodeId = "child",
            Name = "Child",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 50,
            Job = "Carpenter",
            RecipeLevel = 12
        };
        var root = new PlanNode
        {
            ItemId = 100,
            NodeId = "root",
            Name = "Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Children = [child]
        };
        var plan = new CraftingPlan { RootItems = [root] };

        var states = RecipePlanTreeDisplayBuilder.Build(plan, Array.Empty<DetailedShoppingPlan>());

        Assert.Equal(2, states.Count);
        Assert.Equal("150g", states["child"].PriceText);
        Assert.Equal("Lv.12 Carpenter", states["child"].RecipeInfo);
    }

    [Fact]
    public void Build_DoesNotValidateOrMutateInvalidAcquisitionSource()
    {
        var node = new PlanNode
        {
            ItemId = 100,
            NodeId = "node",
            Name = "Node",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanCraft = false,
            CanBuyFromMarket = true,
            MarketPrice = 10
        };
        var plan = new CraftingPlan { RootItems = [node] };

        RecipePlanTreeDisplayBuilder.Build(plan, Array.Empty<DetailedShoppingPlan>());

        Assert.Equal(AcquisitionSource.Craft, node.Source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, node.SourceReason);
    }

    [Fact]
    public void Build_UsesEvidenceAwareCraftCost()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            NodeId = "root",
            Name = "Root Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        var child = new PlanNode
        {
            ItemId = 200,
            NodeId = "child",
            Name = "Market Material",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 999,
            Parent = root
        };
        root.Children.Add(child);
        var plan = new CraftingPlan { RootItems = [root] };
        var shoppingPlans = new List<DetailedShoppingPlan>
        {
            new()
            {
                ItemId = 200,
                Name = "Market Material",
                QuantityNeeded = 10,
                RecommendedWorld = new WorldShoppingSummary
                {
                    WorldName = "Siren",
                    TotalCost = 1234,
                    TotalQuantityPurchased = 10
                }
            }
        };

        var states = RecipePlanTreeDisplayBuilder.Build(plan, shoppingPlans);

        Assert.Equal("1,234g", states["root"].PriceText);
    }

    [Fact]
    public void BuildWithoutCost_ProjectsDisplayWithoutAcquisitionCosting()
    {
        var node = new PlanNode
        {
            ItemId = 100,
            NodeId = "node",
            Name = "Fallback",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyHq,
            MustBeHq = true,
            CanBuyFromMarket = true,
            Job = "Goldsmith",
            RecipeLevel = 50
        };

        var state = RecipePlanTreeDisplayBuilder.BuildWithoutCost(node);

        Assert.Equal("\u2605 ", state.HqPrefix);
        Assert.Equal("Lv.50 Goldsmith", state.RecipeInfo);
        Assert.Equal(string.Empty, state.PriceText);
    }
}
