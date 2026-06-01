using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionEvaluationSourceChangeHandlerTests
{
    [Fact]
    public void Apply_SourceOnlyEditPreservesMarketEvidenceAndMarketVersion()
    {
        var appState = CreateState();
        var child = appState.CurrentPlan!.RootItems[0].Children[0];
        var beforeMarketVersion = appState.CurrentVersions.MarketAnalysisVersion;
        var beforeShoppingPlans = appState.ShoppingPlans.ToList();

        AcquisitionEvaluationSourceChangeHandler.Apply(appState, child, AcquisitionSource.VendorBuy);

        Assert.Equal(beforeMarketVersion, appState.CurrentVersions.MarketAnalysisVersion);
        Assert.Same(beforeShoppingPlans[0], appState.ShoppingPlans[0]);
        Assert.Equal(AcquisitionSource.VendorBuy, child.Source);
        Assert.NotEmpty(appState.ShoppingItems);
    }

    private static AppState CreateState()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 5,
            Yield = 1
        };
        var child = new PlanNode
        {
            ItemId = 200,
            Name = "Child",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            MarketPrice = 100,
            VendorPrice = 25,
            Parent = root
        };
        root.Children.Add(child);

        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(new CraftingPlan { RootItems = [root] });
        appState.ReplaceMarketAnalysis(
            [],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 200,
                    Name = "Child",
                    QuantityNeeded = 2,
                    RecommendedWorld = new WorldShoppingSummary
                    {
                        WorldName = "Siren",
                        TotalCost = 200,
                        TotalQuantityPurchased = 2
                    }
                }
            ]);
        return appState;
    }
}
