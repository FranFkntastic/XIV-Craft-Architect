using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
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

    [Fact]
    public void Apply_ChildSourceChangeUpdatesParentCraftCostWithoutMarketRerun()
    {
        var appState = CreateState();
        var root = appState.CurrentPlan!.RootItems[0];
        var child = root.Children[0];
        var contextBefore = AcquisitionPlanningService.CreateCostContext(appState.ShoppingPlans);
        Assert.True(AcquisitionPlanningService.TryGetSelectedAcquisitionCost([root], contextBefore, out var beforeCost));

        AcquisitionEvaluationSourceChangeHandler.Apply(appState, child, AcquisitionSource.VendorBuy);
        var contextAfter = AcquisitionPlanningService.CreateCostContext(appState.ShoppingPlans);

        Assert.True(AcquisitionPlanningService.TryGetSelectedAcquisitionCost([root], contextAfter, out var afterCost));
        Assert.NotEqual(beforeCost, afterCost);
        Assert.Equal(0, appState.CurrentVersions.MarketAnalysisVersion);
    }

    [Fact]
    public void Apply_ParentMarketBuyKeepsChildrenSuppressedInSnapshot()
    {
        var appState = CreateState();
        var root = appState.CurrentPlan!.RootItems[0];

        AcquisitionEvaluationSourceChangeHandler.Apply(appState, root, AcquisitionSource.MarketBuyNq);
        var snapshot = AcquisitionEvaluationSnapshotBuilder.Build(
            appState.CurrentPlan,
            appState.ShoppingPlans,
            Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All);

        var childRow = snapshot.Rows.Single(row => row.Node.ItemId == 200);
        Assert.True(childRow.IsFullySuppressed);
        Assert.DoesNotContain(snapshot.ActiveProcurementItems, item => item.ItemId == 200);
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

        return new AppState
        {
            CurrentPlan = new CraftingPlan { RootItems = [root] },
            ShoppingPlans =
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
            ]
        };
    }
}
