using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class CoreAcquisitionDecisionServiceTests
{
    [Fact]
    public void ChangeSource_UpdatesAllSameItemOccurrencesAndClearsProcurementOverlay()
    {
        var host = CreateHostWithDuplicateChildren();
        host.Session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(DateTime.UtcNow, [200], "route", [CreateSharedChildShoppingPlan(4, 400)]),
            "route generated");
        var before = host.Session.CaptureVersionStamp();

        var result = host.Service.ChangeSource(200, AcquisitionSource.VendorBuy);

        Assert.True(result.Changed);
        Assert.Equal(2, result.NodesUpdated);
        Assert.Equal(before.MarketAnalysis, host.Session.Versions.MarketAnalysis);
        Assert.Null(host.Session.ProcurementOverlay);
        Assert.All(FindNodesByItemId(host.Session.ActivePlan!, 200), node =>
        {
            Assert.Equal(AcquisitionSource.VendorBuy, node.Source);
            Assert.Equal(AcquisitionSourceReason.UserSelected, node.SourceReason);
        });
    }

    [Fact]
    public void ChangeSource_NoOpDoesNotClearProcurementOverlay()
    {
        var host = CreateHostWithDuplicateChildren();
        var plan = host.Session.ActivePlan!;
        foreach (var node in FindNodesByItemId(plan, 200))
        {
            AcquisitionPlanningService.SetAcquisitionSource(
                node,
                AcquisitionSource.MarketBuyNq,
                AcquisitionSourceReason.UserSelected);
        }

        Assert.True(host.Session.TryReplaceActivePlanDecisions(
            host.Session.CaptureVersionStamp(),
            plan,
            host.Session.PlanSessionVersion,
            "seed user selected"));
        host.Session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(DateTime.UtcNow, [200], "route", [CreateSharedChildShoppingPlan(4, 400)]),
            "route generated");

        var result = host.Service.ChangeSource(200, AcquisitionSource.MarketBuyNq);

        Assert.False(result.Changed);
        Assert.NotNull(host.Session.ProcurementOverlay);
    }

    [Fact]
    public void ChangeMarketHq_TogglesRequirementAndUsesUserSelectedMarketSource()
    {
        var host = CreateHostWithDuplicateChildren();

        var result = host.Service.ChangeMarketHq(200, isHq: true);

        Assert.True(result.Changed);
        Assert.All(FindNodesByItemId(host.Session.ActivePlan!, 200), node =>
        {
            Assert.True(node.MustBeHq);
            Assert.Equal(AcquisitionSource.MarketBuyHq, node.Source);
            Assert.Equal(AcquisitionSourceReason.UserSelected, node.SourceReason);
        });
    }

    [Fact]
    public void ChangeMarketHq_WhenDisablingHq_SwitchesHqMarketBuyToNq()
    {
        var host = CreateHostWithDuplicateChildren();
        var plan = host.Session.ActivePlan!;
        foreach (var node in FindNodesByItemId(plan, 200))
        {
            node.MustBeHq = true;
            AcquisitionPlanningService.SetAcquisitionSource(
                node,
                AcquisitionSource.MarketBuyHq,
                AcquisitionSourceReason.UserSelected);
        }

        Assert.True(host.Session.TryReplaceActivePlanDecisions(
            host.Session.CaptureVersionStamp(),
            plan,
            host.Session.PlanSessionVersion,
            "seed hq"));

        var result = host.Service.ChangeMarketHq(200, isHq: false);

        Assert.True(result.Changed);
        Assert.All(FindNodesByItemId(host.Session.ActivePlan!, 200), node =>
        {
            Assert.False(node.MustBeHq);
            Assert.Equal(AcquisitionSource.MarketBuyNq, node.Source);
            Assert.Equal(AcquisitionSourceReason.UserSelected, node.SourceReason);
        });
    }

    private static TestHost CreateHostWithDuplicateChildren()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var operationState = new CraftOperationState();
        var operationCoordinator = new CraftOperationCoordinator(session, operationState);
        var rootA = CreateRoot(100, "Root A");
        var rootB = CreateRoot(101, "Root B");
        rootA.Children.Add(CreateSharedChild(rootA));
        rootB.Children.Add(CreateSharedChild(rootB));
        session.ActivatePlan(
            new CraftingPlan { RootItems = [rootA, rootB] },
            [],
            new CraftSessionActiveContext(null, null, null, null),
            "plan loaded");
        session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [],
            [CreateSharedChildShoppingPlan(4, 400)],
            acquisitionDecisionsChanged: false,
            "market analysis");

        return new TestHost(
            session,
            new CoreAcquisitionDecisionService(session, operationCoordinator));
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

    private sealed record TestHost(
        CraftSessionState Session,
        CoreAcquisitionDecisionService Service);
}
