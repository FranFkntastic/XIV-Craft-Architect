using Bunit;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FFXIV_Craft_Architect.Tests;

[Trait(TestTraits.Surface, TestTraits.DeployWeb)]
public sealed class RecipePlanNavigationTests
{
    [Fact]
    public void RecipeQuote_SeparatesDecisionPriceAndProcurementTargets()
    {
        using var context = CreateContext();
        var node = new PlanNode
        {
            NodeId = "root/darksteel-ore",
            ItemId = 5_121,
            Name = "Darksteel Ore",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq
        };
        var route = new RecipePlanProcurementRouteSummary(5_121, 2, 1, "2 stops · 1 DC");

        var component = context.Render<RecipeNodeView>(parameters => parameters
            .Add(view => view.Node, node)
            .Add(view => view.DisplayStates, new Dictionary<string, RecipeNodeDisplayState>())
            .Add(view => view.ProcurementRoutes, new Dictionary<int, RecipePlanProcurementRouteSummary>
            {
                [5_121] = route
            }));

        Assert.Equal(
            "/acquisition?nodeId=root%2Fdarksteel-ore",
            component.Find("a.rp-node-quote-method").GetAttribute("href"));
        Assert.Equal("market", component.Find("a.rp-node-quote-amount").GetAttribute("href"));
        Assert.Equal(
            "/procurement?itemId=5121",
            component.Find("a.rp-node-quote-route").GetAttribute("href"));
        Assert.Equal("2 stops · 1 DC", component.Find("a.rp-node-quote-route").TextContent.Trim());
    }

    [Fact]
    public void RouteSummary_DescribesEveryDestinationForAnItem()
    {
        var summaries = RecipePlanProcurementRouteSummaryBuilder.Build([CreateSplitPlan()], "Aether");

        var summary = Assert.Single(summaries).Value;
        Assert.Equal(3, summary.DestinationCount);
        Assert.Equal(2, summary.DataCenterCount);
        Assert.Equal("3 stops · 2 DC", summary.Label);
    }

    [Fact]
    public void ProcurementFocus_ExpandsEveryMatchingDestinationAndScrollsToItem()
    {
        using var context = CreateContext();

        var component = context.Render<ProcurementRouteTreePanel>(parameters => parameters
            .Add(panel => panel.ShoppingPlans, [CreateSplitPlan()])
            .Add(panel => panel.FocusItemId, 5_121));

        Assert.Equal(3, component.FindAll("tr[data-procurement-item-id='5121']").Count);
        Assert.Equal(3, component.FindAll("tr.is-focused").Count);
        Assert.Contains(
            context.JSInterop.Invocations,
            invocation => invocation.Identifier == "mudScrollManager.scrollIntoView" &&
                invocation.Arguments.Any(argument => Equals(argument, "[data-procurement-item-id='5121']")));
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddMudServices();
        context.Services.AddSingleton(new AppState());
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static DetailedShoppingPlan CreateSplitPlan()
    {
        return new DetailedShoppingPlan
        {
            ItemId = 5_121,
            Name = "Darksteel Ore",
            QuantityNeeded = 30,
            RecommendedSplit =
            [
                new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Siren", QuantityToBuy = 10, TotalCost = 1_000 },
                new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Faerie", QuantityToBuy = 10, TotalCost = 1_100 },
                new SplitWorldPurchase { DataCenter = "Crystal", WorldName = "Coeurl", QuantityToBuy = 10, TotalCost = 900 }
            ]
        };
    }
}
