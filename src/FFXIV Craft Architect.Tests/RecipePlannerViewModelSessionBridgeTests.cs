using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Coordinators;
using FFXIV_Craft_Architect.ViewModels;
using System.Reflection;

namespace FFXIV_Craft_Architect.Tests;

public class RecipePlannerViewModelSessionBridgeTests
{
    [Fact]
    public void CurrentPlan_WhenSet_PublishesPlanAndProjectItemsToCoreSession()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        viewModel.ProjectItems.Add(new ProjectItem
        {
            Id = 100,
            Name = "Final Craft",
            Quantity = 3,
            IsHqRequired = true
        });

        viewModel.CurrentPlan = CreatePlan();

        var sessionPlan = session.ActivePlan;
        Assert.NotNull(sessionPlan);
        Assert.Equal("Final Craft", Assert.Single(sessionPlan.RootItems).Name);
        var projectItem = Assert.Single(session.ProjectItems);
        Assert.Equal(100, projectItem.Id);
        Assert.Equal(3, projectItem.Quantity);
        Assert.True(projectItem.MustBeHq);
    }

    [Fact]
    public void Clear_ClearsCoreSessionActivePlan()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        viewModel.CurrentPlan = CreatePlan();

        viewModel.Clear();

        Assert.Null(session.ActivePlan);
        Assert.Empty(session.ProjectItems);
    }

    [Fact]
    public void SetNodeAcquisition_PublishesDecisionChangeToCoreSessionAndClearsProcurement()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        var plan = CreatePlan();
        viewModel.CurrentPlan = plan;
        var child = Assert.Single(Assert.Single(plan.RootItems).Children);
        session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(DateTime.UtcNow, [child.ItemId], "route", []),
            "route generated");

        viewModel.SetNodeAcquisition(child.NodeId, AcquisitionSource.VendorBuy);

        var sessionChild = FindNode(session.ActivePlan!, child.NodeId);
        Assert.Equal(AcquisitionSource.VendorBuy, sessionChild.Source);
        Assert.Null(session.ProcurementOverlay);
    }

    [Fact]
    public void SetNodeAcquisition_WithCoreDecisionService_UpdatesAllMatchingOccurrences()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        var plan = CreatePlanWithDuplicateMaterial();
        viewModel.CurrentPlan = plan;
        var firstMaterial = plan.RootItems[0].Children[0];

        viewModel.SetNodeAcquisition(firstMaterial.NodeId, AcquisitionSource.VendorBuy);

        Assert.All(FindNodesByItemId(session.ActivePlan!, firstMaterial.ItemId), node =>
        {
            Assert.Equal(AcquisitionSource.VendorBuy, node.Source);
            Assert.Equal(AcquisitionSourceReason.UserSelected, node.SourceReason);
        });
    }

    [Fact]
    public void SetNodeHq_PublishesDecisionChangeToCoreSessionAndClearsProcurement()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        var plan = CreatePlan();
        viewModel.CurrentPlan = plan;
        var child = Assert.Single(Assert.Single(plan.RootItems).Children);
        session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(DateTime.UtcNow, [child.ItemId], "route", []),
            "route generated");

        viewModel.SetNodeHq(child.NodeId, mustBeHq: true);

        var sessionChild = FindNode(session.ActivePlan!, child.NodeId);
        Assert.True(sessionChild.MustBeHq);
        Assert.Null(session.ProcurementOverlay);
    }

    [Fact]
    public void SetNodeHq_WithCoreDecisionService_UpdatesAllMatchingMarketOccurrences()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        var plan = CreatePlanWithDuplicateMaterial();
        viewModel.CurrentPlan = plan;
        var firstMaterial = plan.RootItems[0].Children[0];

        viewModel.SetNodeHq(firstMaterial.NodeId, mustBeHq: true);

        Assert.All(FindNodesByItemId(session.ActivePlan!, firstMaterial.ItemId), node =>
        {
            Assert.True(node.MustBeHq);
            Assert.Equal(AcquisitionSource.MarketBuyHq, node.Source);
            Assert.Equal(AcquisitionSourceReason.UserSelected, node.SourceReason);
        });
    }

    [Fact]
    public void ToggleNodeHq_WithCoreDecisionService_UsesCoreDecisionPath()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        var plan = CreatePlanWithDuplicateMaterial();
        viewModel.CurrentPlan = plan;
        var firstMaterial = plan.RootItems[0].Children[0];

        viewModel.ToggleNodeHq(firstMaterial.NodeId);

        Assert.All(FindNodesByItemId(session.ActivePlan!, firstMaterial.ItemId), node =>
        {
            Assert.True(node.MustBeHq);
            Assert.Equal(AcquisitionSource.MarketBuyHq, node.Source);
            Assert.Equal(AcquisitionSourceReason.UserSelected, node.SourceReason);
        });
    }

    [Fact]
    public void ApplyImportResult_PublishesImportedProjectItemsToCoreSession()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        viewModel.ProjectItems.Add(new ProjectItem
        {
            Id = 999,
            Name = "Old Item",
            Quantity = 1
        });
        viewModel.CurrentPlan = CreatePlan();

        var importedPlan = CreatePlan("Imported Plan", 300, "Imported Root", 7);
        var importedItems = new List<ProjectItem>
        {
            new()
            {
                Id = 300,
                Name = "Imported Root",
                Quantity = 7,
                IsHqRequired = true
            }
        };

        ApplyImportResult(
            viewModel,
            new ImportCoordinator.ImportResult(
                true,
                importedPlan,
                importedItems,
                "Imported plan"));

        var visibleItem = Assert.Single(viewModel.ProjectItems);
        Assert.Equal(300, visibleItem.Id);
        Assert.Equal(7, visibleItem.Quantity);

        var sessionItem = Assert.Single(session.ProjectItems);
        Assert.Equal(300, sessionItem.Id);
        Assert.Equal(7, sessionItem.Quantity);
        Assert.True(sessionItem.MustBeHq);
    }

    private static RecipePlannerViewModel CreateViewModel(CraftSessionState session)
    {
        var operationState = new CraftOperationState();
        var operationCoordinator = new CraftOperationCoordinator(session, operationState);
        return new(
            null!,
            null!,
            null!,
            session,
            new CoreAcquisitionDecisionService(session, operationCoordinator));
    }

    private static CraftingPlan CreatePlan(
        string name = "Session Plan",
        int rootItemId = 100,
        string rootName = "Final Craft",
        int rootQuantity = 3) =>
        new()
        {
            Name = name,
            DataCenter = "Aether",
            RootItems =
            [
                new PlanNode
                {
                    ItemId = rootItemId,
                    Name = rootName,
                    Quantity = rootQuantity,
                    MustBeHq = true,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true,
                    Children =
                    [
                        new PlanNode
                        {
                            ItemId = 200,
                            Name = "Material",
                            Quantity = 6,
                            Source = AcquisitionSource.MarketBuyNq,
                            CanBuyFromMarket = true,
                            CanBuyFromVendor = true,
                            CanBeHq = true
                        }
                    ]
                }
            ]
        };

    private static CraftingPlan CreatePlanWithDuplicateMaterial()
    {
        var firstRoot = CreateRoot(100, "First Root");
        var secondRoot = CreateRoot(101, "Second Root");
        firstRoot.Children.Add(CreateSharedMaterial(firstRoot));
        secondRoot.Children.Add(CreateSharedMaterial(secondRoot));

        return new CraftingPlan
        {
            Name = "Duplicate Material Plan",
            DataCenter = "Aether",
            RootItems = [firstRoot, secondRoot]
        };
    }

    private static PlanNode CreateRoot(int itemId, string name) =>
        new()
        {
            ItemId = itemId,
            Name = name,
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true,
            Yield = 1
        };

    private static PlanNode CreateSharedMaterial(PlanNode parent) =>
        new()
        {
            ItemId = 200,
            Name = "Shared Material",
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

    private static PlanNode FindNode(CraftingPlan plan, string nodeId) =>
        plan.RootItems.Select(root => FindNode(root, nodeId)).First(node => node != null)!;

    private static IEnumerable<PlanNode> FindNodesByItemId(CraftingPlan plan, int itemId) =>
        plan.RootItems.SelectMany(root => FindNodesByItemId(root, itemId));

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

    private static void ApplyImportResult(
        RecipePlannerViewModel viewModel,
        ImportCoordinator.ImportResult result)
    {
        var method = typeof(RecipePlannerViewModel).GetMethod(
            "ApplyImportResult",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(viewModel, [result]);
    }

    private static PlanNode? FindNode(PlanNode node, string nodeId)
    {
        if (node.NodeId == nodeId)
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var match = FindNode(child, nodeId);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }
}
