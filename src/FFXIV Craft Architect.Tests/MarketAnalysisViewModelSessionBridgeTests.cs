using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.ViewModels;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisViewModelSessionBridgeTests
{
    [Fact]
    public void SetShoppingPlans_PublishesCoreMarketEvidenceAndClearsProcurement()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var workflow = CreateCoreMarketWorkflow(session, Mock.Of<IMarketAnalysisExecutionService>());
        var viewModel = CreateViewModel(session, workflow);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(DateTime.UtcNow, [200], "old route", [CreateShoppingPlan(200)]),
            "old route");

        viewModel.SetShoppingPlans([CreateShoppingPlan(200)]);

        var shoppingPlan = Assert.Single(session.MarketEvidence.ShoppingPlans!);
        Assert.Equal(200, shoppingPlan.ItemId);
        Assert.Equal("Material", shoppingPlan.Name);
        Assert.Null(session.ProcurementOverlay);
    }

    [Fact]
    public void Clear_ClearsCoreMarketEvidence()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        viewModel.SetShoppingPlans([CreateShoppingPlan(200)]);

        viewModel.Clear();

        Assert.Empty(session.MarketEvidence.ShoppingPlans!);
    }

    [Fact]
    public void DisplayShoppingPlans_DoesNotReplaceCoreMarketAnalyses()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [new MarketItemAnalysis { ItemId = 200, Name = "Material", QuantityNeeded = 2 }],
            [CreateShoppingPlan(200)],
            acquisitionDecisionsChanged: false,
            "core market analysis"));
        var before = session.CaptureVersionStamp();

        viewModel.DisplayShoppingPlans([CreateShoppingPlan(200)]);

        Assert.Equal(before.MarketAnalysis, session.Versions.MarketAnalysis);
        Assert.Equal(200, Assert.Single(session.MarketEvidence.ItemAnalyses).ItemId);
        Assert.Equal(200, Assert.Single(viewModel.ShoppingPlans).ItemId);
    }

    [Fact]
    public void SetShoppingPlans_DoesNotReplaceExistingCoreMarketAnalyses()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [new MarketItemAnalysis { ItemId = 200, Name = "Material", QuantityNeeded = 2 }],
            [CreateShoppingPlan(200)],
            acquisitionDecisionsChanged: false,
            "core market analysis"));
        var before = session.CaptureVersionStamp();

        viewModel.SetShoppingPlans([CreateShoppingPlan(200)]);

        Assert.Equal(before.MarketAnalysis, session.Versions.MarketAnalysis);
        Assert.Equal(200, Assert.Single(session.MarketEvidence.ItemAnalyses).ItemId);
        Assert.Equal(200, Assert.Single(viewModel.ShoppingPlans).ItemId);
    }

    [Fact]
    public void ClearDisplay_DoesNotClearCoreMarketEvidence()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [new MarketItemAnalysis { ItemId = 200, Name = "Material", QuantityNeeded = 2 }],
            [CreateShoppingPlan(200)],
            acquisitionDecisionsChanged: false,
            "core market analysis"));
        viewModel.DisplayShoppingPlans([CreateShoppingPlan(200)]);
        var before = session.CaptureVersionStamp();

        viewModel.ClearDisplay();

        Assert.Empty(viewModel.ShoppingPlans);
        Assert.Equal(before.MarketAnalysis, session.Versions.MarketAnalysis);
        Assert.Equal(200, Assert.Single(session.MarketEvidence.ItemAnalyses).ItemId);
    }

    [Fact]
    public void MarkMarketContextChanged_ClearsDisplayAndCoreMarketEvidence()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var viewModel = CreateViewModel(session);
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [new MarketItemAnalysis { ItemId = 200, Name = "Material", QuantityNeeded = 2 }],
            [CreateShoppingPlan(200)],
            acquisitionDecisionsChanged: false,
            "core market analysis"));
        viewModel.DisplayShoppingPlans([CreateShoppingPlan(200)]);

        viewModel.MarkMarketContextChanged("test market context changed");

        Assert.Empty(viewModel.ShoppingPlans);
        Assert.Empty(session.MarketEvidence.ItemAnalyses);
        Assert.Empty(session.MarketEvidence.ShoppingPlans!);
    }

    [Fact]
    public async Task RunCoreMarketAnalysisAsync_PublishesAndHydratesDisplayWithoutSecondPublication()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(CreateExecutionResult());
        var workflow = CreateCoreMarketWorkflow(session, execution.Object);
        var viewModel = CreateViewModel(session, workflow);
        var before = session.CaptureVersionStamp();

        var result = await viewModel.RunCoreMarketAnalysisAsync(CreateWorkflowRequest());

        Assert.True(result.Published);
        Assert.Equal(before.MarketAnalysis + 2, session.Versions.MarketAnalysis);
        Assert.Equal(200, Assert.Single(session.MarketEvidence.ItemAnalyses).ItemId);
        Assert.Equal(200, Assert.Single(session.MarketEvidence.ShoppingPlans!).ItemId);
        Assert.Equal(200, Assert.Single(viewModel.ShoppingPlans).ItemId);
        Assert.Contains("1 items analyzed", viewModel.StatusMessage);
    }

    [Fact]
    public async Task MarkMarketContextChanged_DuringCoreAnalysis_PreventsStalePublication()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        MarketAnalysisViewModel? viewModel = null;
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => viewModel!.MarkMarketContextChanged("test market context changed"))
            .ReturnsAsync(CreateExecutionResult());
        var workflow = CreateCoreMarketWorkflow(session, execution.Object);
        viewModel = CreateViewModel(session, workflow);

        var result = await viewModel.RunCoreMarketAnalysisAsync(CreateWorkflowRequest());

        Assert.False(result.Published);
        Assert.Empty(session.MarketEvidence.ItemAnalyses);
        Assert.Empty(session.MarketEvidence.ShoppingPlans!);
        Assert.Empty(viewModel.ShoppingPlans);
    }

    [Fact]
    public async Task ApplyCoreMarketLensAsync_ReprojectsExistingAnalysisAndHydratesDisplay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [new MarketItemAnalysis { ItemId = 200, Name = "Material", QuantityNeeded = 2 }],
            [CreateShoppingPlan(200)],
            acquisitionDecisionsChanged: false,
            "core market analysis"));
        var projectedPlan = CreateShoppingPlan(200);
        projectedPlan.RecommendedWorld!.TotalCost = 777;
        var ladder = new Mock<IMarketPriceLadderAnalysisService>();
        ladder.Setup(l => l.ProjectToShoppingPlan(
                It.IsAny<MarketItemAnalysis>(),
                MarketAcquisitionLens.BulkValue,
                It.IsAny<MarketAnalysisConfig?>()))
            .Returns(projectedPlan);
        var workflow = CreateCoreMarketWorkflow(session, Mock.Of<IMarketAnalysisExecutionService>(), ladder.Object);
        var viewModel = CreateViewModel(session, workflow);
        var before = session.CaptureVersionStamp();

        var result = await viewModel.ApplyCoreMarketLensAsync(MarketAcquisitionLens.BulkValue);

        Assert.True(result.Published);
        Assert.True(session.Versions.SettingsContext > before.SettingsContext);
        Assert.Equal(200, Assert.Single(session.MarketEvidence.ItemAnalyses).ItemId);
        Assert.Equal(777, Assert.Single(session.MarketEvidence.ShoppingPlans!).RecommendedWorld!.TotalCost);
        Assert.Equal(777, Assert.Single(viewModel.ShoppingPlans).RecommendedWorld!.TotalCost);
    }

    [Fact]
    public async Task ApplyCoreMarketLensAsync_PreservesVendorDisplayRows()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        Assert.True(session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            session.ActivePlan!,
            session.PlanSessionVersion,
            [new MarketItemAnalysis { ItemId = 200, Name = "Material", QuantityNeeded = 2 }],
            [CreateShoppingPlan(200)],
            acquisitionDecisionsChanged: false,
            "core market analysis"));
        var vendorPlan = CreateShoppingPlan(300);
        vendorPlan.Name = "Vendor Material";
        vendorPlan.RecommendedWorld!.WorldName = MarketShoppingConstants.VendorWorldName;
        var projectedPlan = CreateShoppingPlan(200);
        projectedPlan.RecommendedWorld!.TotalCost = 777;
        var ladder = new Mock<IMarketPriceLadderAnalysisService>();
        ladder.Setup(l => l.ProjectToShoppingPlan(
                It.IsAny<MarketItemAnalysis>(),
                MarketAcquisitionLens.BulkValue,
                It.IsAny<MarketAnalysisConfig?>()))
            .Returns(projectedPlan);
        var workflow = CreateCoreMarketWorkflow(session, Mock.Of<IMarketAnalysisExecutionService>(), ladder.Object);
        var viewModel = CreateViewModel(session, workflow);
        viewModel.DisplayShoppingPlans([vendorPlan, CreateShoppingPlan(200)]);

        var result = await viewModel.ApplyCoreMarketLensAsync(MarketAcquisitionLens.BulkValue);

        Assert.True(result.Published);
        Assert.Equal([200, 300], viewModel.ShoppingPlans.Select(plan => plan.ItemId).Order().ToArray());
        var displayedVendorPlan = Assert.Single(
            viewModel.ShoppingPlans,
            plan => plan.ItemId == 300);
        var displayedMarketPlan = Assert.Single(
            viewModel.ShoppingPlans,
            plan => plan.ItemId == 200);
        Assert.Equal(MarketShoppingConstants.VendorWorldName, displayedVendorPlan.RecommendedWorld!.WorldName);
        Assert.Equal(777, displayedMarketPlan.RecommendedWorld!.TotalCost);
    }

    [Fact]
    public async Task ApplyCoreMarketLensAsync_WithShoppingPlansButNoAnalyses_PreservesDisplayAndBumpsSettings()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Final Craft", Quantity = 1 }],
            new CraftSessionActiveContext(null, "Aether", "Siren", null),
            "plan loaded");
        var workflow = CreateCoreMarketWorkflow(session, Mock.Of<IMarketAnalysisExecutionService>());
        var viewModel = CreateViewModel(session, workflow);
        viewModel.SetShoppingPlans([CreateShoppingPlan(200)]);
        var before = session.CaptureVersionStamp();

        var result = await viewModel.ApplyCoreMarketLensAsync(MarketAcquisitionLens.BulkValue);

        Assert.False(result.Published);
        Assert.True(session.Versions.SettingsContext > before.SettingsContext);
        Assert.Empty(session.MarketEvidence.ItemAnalyses);
        Assert.Equal(200, Assert.Single(session.MarketEvidence.ShoppingPlans!).ItemId);
        Assert.Equal(200, Assert.Single(viewModel.ShoppingPlans).ItemId);
    }

    private static MarketAnalysisViewModel CreateViewModel(
        CraftSessionState session,
        CoreMarketAnalysisWorkflowService? workflow = null) =>
        new(null!, null!, null!, session: session, coreMarketAnalysisWorkflow: workflow);

    private static CoreMarketAnalysisWorkflowService CreateCoreMarketWorkflow(
        CraftSessionState session,
        IMarketAnalysisExecutionService execution,
        IMarketPriceLadderAnalysisService? ladder = null)
    {
        var operationState = new CraftOperationState();
        var operationCoordinator = new CraftOperationCoordinator(session, operationState);
        var recipeLayerWorkflow = new Mock<ICoreRecipeLayerWorkflowService>();
        recipeLayerWorkflow.Setup(w => w.BuildCurrentMarketAnalysisCandidatesAsync(
                It.IsAny<CraftingPlan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MaterialAggregate { ItemId = 200, Name = "Material", TotalQuantity = 2 }]);

        return new CoreMarketAnalysisWorkflowService(
            session,
            execution,
            new MarketShoppingService(Mock.Of<IMarketCacheService>()),
            ladder ?? Mock.Of<IMarketPriceLadderAnalysisService>(),
            recipeLayerWorkflow.Object,
            operationCoordinator);
    }

    private static CoreMarketAnalysisWorkflowRequest CreateWorkflowRequest() =>
        new(
            ForceRefreshData: false,
            Scope: MarketFetchScope.SelectedDataCenter,
            SelectedDataCenter: "Aether",
            SelectedRegion: "North America",
            Lens: MarketAcquisitionLens.MinimumUpfrontCost,
            ExpectedWorldsByDataCenter: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous);

    private static MarketAnalysisExecutionResult CreateExecutionResult() =>
        new(
            new MarketEvidenceSet(
                new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
                [(200, "Aether")],
                MarketFetchScope.SelectedDataCenter,
                ["Aether"],
                "Aether",
                "North America",
                TimeSpan.Zero,
                fetchedCount: 1,
                DateTime.UtcNow),
            [new MarketItemAnalysis { ItemId = 200, Name = "Material", QuantityNeeded = 2 }],
            [CreateShoppingPlan(200)]);

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
            Name = "Market Bridge Plan",
            DataCenter = "Aether",
            World = "Siren",
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
