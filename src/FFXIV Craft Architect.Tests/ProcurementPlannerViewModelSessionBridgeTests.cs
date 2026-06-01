using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.ViewModels;

namespace FFXIV_Craft_Architect.Tests;

public class ProcurementPlannerViewModelSessionBridgeTests
{
    [Fact]
    public void BuildFromCurrentMarketEvidence_PublishesCoreProcurementOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = new ProcurementPlannerViewModel(session);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [],
            [CreateShoppingPlan(200)],
            acquisitionDecisionsChanged: false,
            "market analysis"));

        var result = viewModel.BuildFromCurrentMarketEvidence();

        Assert.True(result.Published);
        Assert.Equal(1, result.ShoppingPlanCount);
        Assert.Equal("Procurement plan built from current market evidence", viewModel.StatusMessage);
        var overlay = session.ProcurementOverlay;
        Assert.NotNull(overlay);
        Assert.Equal([200], overlay.ActiveItemIds);
        Assert.Equal(200, Assert.Single(overlay.ShoppingPlans!).ItemId);
    }

    [Fact]
    public void BuildFromCurrentMarketEvidence_WithNoMarketEvidence_DoesNotPublishOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = new ProcurementPlannerViewModel(session);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");

        var result = viewModel.BuildFromCurrentMarketEvidence();

        Assert.False(result.Published);
        Assert.Equal(0, result.ShoppingPlanCount);
        Assert.Equal("No market analysis data found. Run Conduct Analysis in Market Analysis first.", viewModel.StatusMessage);
        Assert.Null(session.ProcurementOverlay);
    }

    [Fact]
    public void BuildFromCurrentMarketEvidence_WhenDecisionsChangedAfterMarketEvidence_DoesNotPublishOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = new ProcurementPlannerViewModel(session);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [],
            [CreateShoppingPlan(200)],
            acquisitionDecisionsChanged: false,
            "market analysis"));
        session.MarkPlanDecisionChanged("decision changed");

        var result = viewModel.BuildFromCurrentMarketEvidence();

        Assert.False(result.Published);
        Assert.Equal(0, result.ShoppingPlanCount);
        Assert.Equal("Procurement plan needs fresh market analysis after acquisition changes.", viewModel.StatusMessage);
        Assert.Null(session.ProcurementOverlay);
    }

    [Fact]
    public void BuildFromCurrentMarketEvidence_WhenMarketSettingsChangedAfterEvidence_DoesNotPublishOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = new ProcurementPlannerViewModel(session);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [],
            [CreateShoppingPlan(200)],
            acquisitionDecisionsChanged: false,
            "market analysis"));
        session.MarkMarketAnalysisSettingsChanged("lens changed");

        var result = viewModel.BuildFromCurrentMarketEvidence();

        Assert.False(result.Published);
        Assert.Equal(0, result.ShoppingPlanCount);
        Assert.Equal("Procurement plan needs fresh market analysis after market settings changes.", viewModel.StatusMessage);
        Assert.Null(session.ProcurementOverlay);
    }

    [Fact]
    public void BuildFromCurrentMarketEvidence_WhenMarketAnalysisReconciledDecisions_PublishesOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = new ProcurementPlannerViewModel(session);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [],
            [CreateShoppingPlan(200)],
            acquisitionDecisionsChanged: true,
            "market analysis"));

        var result = viewModel.BuildFromCurrentMarketEvidence();

        Assert.True(result.Published);
        Assert.Equal(1, result.ShoppingPlanCount);
        Assert.NotNull(session.ProcurementOverlay);
    }

    [Fact]
    public void BuildFromCurrentMarketEvidence_WithNoActivePlan_DoesNotPublishOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = new ProcurementPlannerViewModel(session);

        var result = viewModel.BuildFromCurrentMarketEvidence();

        Assert.False(result.Published);
        Assert.Equal("No plan - build a plan first", viewModel.StatusMessage);
        Assert.Null(session.ProcurementOverlay);
    }

    private static CraftingPlan CreatePlan()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Final Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true
        };
        root.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Material",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            Parent = root
        });

        return new CraftingPlan
        {
            RootItems = [root]
        };
    }

    private static DetailedShoppingPlan CreateShoppingPlan(int itemId) =>
        new()
        {
            ItemId = itemId,
            Name = "Material",
            QuantityNeeded = 2,
            RecommendedWorld = new WorldShoppingSummary
            {
                WorldName = "Siren",
                TotalCost = 500,
                TotalQuantityPurchased = 2
            }
        };
}
