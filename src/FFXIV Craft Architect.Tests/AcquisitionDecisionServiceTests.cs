using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionDecisionServiceTests
{
    [Fact]
    public void ChangeSource_UpdatesAllSameItemOccurrencesAndClearsProcurementOverlay()
    {
        var appState = CreateStateWithDuplicateChildren();
        appState.ReplaceProcurementOverlay(
        [
            new DetailedShoppingPlan
            {
                ItemId = 200,
                Name = "Shared Child",
                QuantityNeeded = 4
            }
        ]);
        var firstChild = appState.CurrentPlan!.RootItems[0].Children[0];
        var beforeMarketVersion = appState.CurrentVersions.MarketAnalysisVersion;

        var service = new AcquisitionDecisionService(appState);
        var result = service.ChangeSource(firstChild, AcquisitionSource.VendorBuy);

        Assert.True(result.Changed);
        Assert.Equal(2, result.NodesUpdated);
        Assert.Equal(beforeMarketVersion, appState.CurrentVersions.MarketAnalysisVersion);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.All(FindNodesByItemId(appState.CurrentPlan, 200), node =>
        {
            Assert.Equal(AcquisitionSource.VendorBuy, node.Source);
            Assert.Equal(AcquisitionSourceReason.UserSelected, node.SourceReason);
        });
    }

    [Fact]
    public void ChangeSource_UpdatesShoppingItemsAndPublishesDecisionWithoutInvalidatingMarketAnalysis()
    {
        var appState = CreateStateWithSingleRoot();
        var root = appState.CurrentPlan!.RootItems[0];
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;
        var beforeVersions = appState.CurrentVersions;

        var service = new AcquisitionDecisionService(appState);
        service.ChangeSource(root, AcquisitionSource.MarketBuyNq);

        var change = Assert.Single(changes);
        Assert.True(change.HasScope(AppStateChangeScope.PlanDecision));
        Assert.True(change.HasScope(AppStateChangeScope.ShoppingItems));
        Assert.True(change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.False(change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.Equal(beforeVersions.MarketAnalysisVersion, change.Versions.MarketAnalysisVersion);
        Assert.Contains(appState.ShoppingItems, item => item.Id == 100);
        Assert.DoesNotContain(appState.ShoppingItems, item => item.Id == 200);
    }

    [Fact]
    public void ChangeMarketHq_TogglesRequirementAndUsesUserSelectedMarketSource()
    {
        var appState = CreateStateWithDuplicateChildren();
        var child = appState.CurrentPlan!.RootItems[0].Children[0];

        var service = new AcquisitionDecisionService(appState);
        var result = service.ChangeMarketHq(child, isHq: true);

        Assert.True(result.Changed);
        Assert.True(child.MustBeHq);
        Assert.Equal(AcquisitionSource.MarketBuyHq, child.Source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, child.SourceReason);
        Assert.All(FindNodesByItemId(appState.CurrentPlan, 200), node => Assert.True(node.MustBeHq));
    }

    [Fact]
    public void ChangeMarketHq_WhenDisablingHq_SwitchesHqMarketBuyToNq()
    {
        var appState = CreateStateWithDuplicateChildren();
        var child = appState.CurrentPlan!.RootItems[0].Children[0];
        child.MustBeHq = true;
        AcquisitionPlanningService.SetAcquisitionSource(
            child,
            AcquisitionSource.MarketBuyHq,
            AcquisitionSourceReason.UserSelected);

        var service = new AcquisitionDecisionService(appState);
        var result = service.ChangeMarketHq(child, isHq: false);

        Assert.True(result.Changed);
        Assert.False(child.MustBeHq);
        Assert.Equal(AcquisitionSource.MarketBuyNq, child.Source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, child.SourceReason);
    }

    [Fact]
    public void ChangeSource_NoOpDoesNotClearProcurementOverlay()
    {
        var appState = CreateStateWithDuplicateChildren();
        var child = appState.CurrentPlan!.RootItems[0].Children[0];
        AcquisitionPlanningService.SetAcquisitionSource(
            child,
            AcquisitionSource.MarketBuyNq,
            AcquisitionSourceReason.UserSelected);
        appState.CurrentPlan.RootItems[1].Children[0].SourceReason = AcquisitionSourceReason.UserSelected;
        appState.ReplaceProcurementOverlay(
        [
            new DetailedShoppingPlan
            {
                ItemId = 200,
                Name = "Shared Child",
                QuantityNeeded = 4
            }
        ]);

        var service = new AcquisitionDecisionService(appState);
        var result = service.ChangeSource(child, AcquisitionSource.MarketBuyNq);

        Assert.False(result.Changed);
        Assert.Single(appState.ProcurementShoppingPlans);
    }

    [Fact]
    public void ChangeSource_ClearsStaleProcurementAnalysisStatus()
    {
        var appState = CreateStateWithDuplicateChildren();
        appState.BeginOperation("Procurement Analysis", "Optimizing procurement route for 2 items...");
        var child = appState.CurrentPlan!.RootItems[0].Children[0];

        var service = new AcquisitionDecisionService(appState);
        service.ChangeSource(child, AcquisitionSource.VendorBuy);

        Assert.False(appState.IsBusy);
        Assert.Null(appState.CurrentOperation);
        Assert.Contains("regenerate procurement route", appState.StatusMessage);
    }

    [Fact]
    public void ChangeMarketHq_ForCraftSourceAppliesSameItemHqRequirement()
    {
        var appState = CreateStateWithDuplicateChildren();
        foreach (var node in FindNodesByItemId(appState.CurrentPlan!, 200))
        {
            node.Source = AcquisitionSource.Craft;
            node.CanCraft = true;
            node.Children.Add(new PlanNode { ItemId = 300, Name = "Nested", Quantity = 1, Source = AcquisitionSource.MarketBuyNq });
        }
        var child = appState.CurrentPlan!.RootItems[0].Children[0];

        var service = new AcquisitionDecisionService(appState);
        var result = service.ChangeMarketHq(child, isHq: true);

        Assert.True(result.Changed);
        Assert.All(FindNodesByItemId(appState.CurrentPlan, 200), node => Assert.True(node.MustBeHq));
    }

    private static AppState CreateStateWithDuplicateChildren()
    {
        var rootA = CreateRoot(100, "Root A");
        var rootB = CreateRoot(101, "Root B");
        var childA = CreateSharedChild(rootA);
        var childB = CreateSharedChild(rootB);
        rootA.Children.Add(childA);
        rootB.Children.Add(childB);

        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(new CraftingPlan { RootItems = [rootA, rootB] });
        appState.ReplaceMarketAnalysis([], [CreateSharedChildShoppingPlan(quantityNeeded: 4, totalCost: 400)]);
        return appState;
    }

    private static AppState CreateStateWithSingleRoot()
    {
        var root = CreateRoot(100, "Root A");
        root.Children.Add(CreateSharedChild(root));

        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(new CraftingPlan { RootItems = [root] });
        appState.ReplaceMarketAnalysis([], [CreateSharedChildShoppingPlan(quantityNeeded: 2, totalCost: 200)]);
        return appState;
    }

    private static DetailedShoppingPlan CreateSharedChildShoppingPlan(int quantityNeeded, long totalCost)
    {
        return new DetailedShoppingPlan
        {
            ItemId = 200,
            Name = "Shared Child",
            QuantityNeeded = quantityNeeded,
            RecommendedWorld = new WorldShoppingSummary
            {
                WorldName = "Siren",
                TotalCost = totalCost,
                TotalQuantityPurchased = quantityNeeded
            }
        };
    }

    private static PlanNode CreateRoot(int itemId, string name)
    {
        return new PlanNode
        {
            ItemId = itemId,
            Name = name,
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true,
            CanBuyFromMarket = true,
            MarketPrice = 1000,
            Yield = 1
        };
    }

    private static PlanNode CreateSharedChild(PlanNode parent)
    {
        return new PlanNode
        {
            ItemId = 200,
            Name = "Shared Child",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            CanBeHq = true,
            MarketPrice = 100,
            HqMarketPrice = 150,
            VendorPrice = 25,
            Parent = parent
        };
    }

    private static IEnumerable<PlanNode> FindNodesByItemId(CraftingPlan plan, int itemId)
    {
        return plan.RootItems.SelectMany(root => FindNodesByItemId(root, itemId));
    }

    private static IEnumerable<PlanNode> FindNodesByItemId(PlanNode node, int itemId)
    {
        if (node.ItemId == itemId)
        {
            yield return node;
        }

        foreach (var child in node.Children)
        {
            foreach (var match in FindNodesByItemId(child, itemId))
            {
                yield return match;
            }
        }
    }
}
