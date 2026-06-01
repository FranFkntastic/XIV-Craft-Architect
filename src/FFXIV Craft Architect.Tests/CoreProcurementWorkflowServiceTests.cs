using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class CoreProcurementWorkflowServiceTests
{
    [Fact]
    public async Task RunAnalysisAsync_WithNoActivePlan_ReturnsNoPlanWithoutExecution()
    {
        var execution = new Mock<IProcurementRouteExecutionService>(MockBehavior.Strict);
        var service = CreateService(procurementExecution: execution.Object);

        var result = await service.RunAnalysisAsync(CreateRequest());

        Assert.Equal(CoreProcurementWorkflowStatus.NoPlan, result.Status);
        execution.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAnalysisAsync_WithNoActiveProcurementItems_ReturnsNoActiveItemsWithoutExecution()
    {
        var execution = new Mock<IProcurementRouteExecutionService>(MockBehavior.Strict);
        var service = CreateService(
            procurementExecution: execution.Object,
            recipeLayerWorkflow: new FakeRecipeLayerWorkflowService(activeProcurementItems: []));
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");

        var result = await service.RunAnalysisAsync(CreateRequest());

        Assert.Equal(CoreProcurementWorkflowStatus.NoActiveProcurementItems, result.Status);
        execution.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAnalysisAsync_PublishesProcurementOverlay()
    {
        var routePlans = new List<DetailedShoppingPlan>
        {
            ShoppingPlan(101, "Siren"),
            ShoppingPlan(202, "Faerie")
        };
        var execution = new Mock<IProcurementRouteExecutionService>();
        execution.Setup(e => e.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(new ProcurementRouteExecutionResult(routePlans, [], [], [], []));
        var service = CreateService(procurementExecution: execution.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        var before = service.Session.CaptureVersionStamp();

        var result = await service.RunAnalysisAsync(CreateRequest(
            sourceShoppingPlans: [ShoppingPlan(101), ShoppingPlan(202)]));

        Assert.Equal(CoreProcurementWorkflowStatus.Published, result.Status);
        Assert.Equal(2, result.ShoppingPlanCount);
        Assert.Equal(before.Procurement + 1, service.Session.Versions.Procurement);
        Assert.Equal([101, 202], service.Session.ProcurementOverlay?.ActiveItemIds);
        Assert.Equal([101, 202], service.Session.ProcurementOverlay?.ShoppingPlans.Select(plan => plan.ItemId));
        execution.Verify(e => e.AnalyzeAsync(
            It.Is<ProcurementRouteExecutionRequest>(request =>
                request.ActiveProcurementItems.Count == 2 &&
                request.SourceShoppingPlans.Count == 2 &&
                request.Scope == MarketFetchScope.SelectedDataCenter &&
                request.SelectedDataCenter == "Aether" &&
                request.SelectedRegion == "North America" &&
                request.Lens == MarketAcquisitionLens.MinimumUpfrontCost &&
                request.ProcurementConfig.TravelTolerance == 7),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenDecisionChangesDuringExecution_DoesNotPublish()
    {
        var execution = new Mock<IProcurementRouteExecutionService>();
        var service = CreateService(procurementExecution: execution.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        var before = service.Session.CaptureVersionStamp();
        execution.Setup(e => e.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => service.Session.MarkPlanDecisionChanged("test decision change"))
            .ReturnsAsync(new ProcurementRouteExecutionResult([ShoppingPlan(101)], [], [], [], []));

        var result = await service.RunAnalysisAsync(CreateRequest());

        Assert.Equal(CoreProcurementWorkflowStatus.StaleDecision, result.Status);
        Assert.Null(service.Session.ProcurementOverlay);
        Assert.Equal(before.Procurement, service.Session.Versions.Procurement);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenOperationNoLongerCurrent_DoesNotPublish()
    {
        var isCurrent = true;
        var execution = new Mock<IProcurementRouteExecutionService>();
        execution.Setup(e => e.AnalyzeAsync(
                It.IsAny<ProcurementRouteExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => isCurrent = false)
            .ReturnsAsync(new ProcurementRouteExecutionResult([ShoppingPlan(101)], [], [], [], []));
        var service = CreateService(procurementExecution: execution.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");

        var result = await service.RunAnalysisAsync(CreateRequest(isCurrentOperation: () => isCurrent));

        Assert.Equal(CoreProcurementWorkflowStatus.Superseded, result.Status);
        Assert.Null(service.Session.ProcurementOverlay);
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_ReplacesSingleMarketAnalysisItemAndInvalidatesProcurement()
    {
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        marketExecution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(fetchedCount: 1),
                [new MarketItemAnalysis { ItemId = 101, Name = "Item 101", QuantityNeeded = 5 }],
                [ShoppingPlan(101, "Siren")]));
        var service = CreateService(marketExecution: marketExecution.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        Assert.True(service.Session.TryPublishMarketAnalysis(
            service.Session.CaptureVersionStamp(),
            service.Session.ActivePlan!,
            service.Session.PlanSessionVersion,
            [new MarketItemAnalysis { ItemId = 202, Name = "Item 202", QuantityNeeded = 3 }],
            [ShoppingPlan(202, "Faerie")],
            acquisitionDecisionsChanged: false,
            "existing analysis",
            [404]));
        service.Session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(DateTime.UtcNow, [202], "existing route", [ShoppingPlan(202)]),
            "existing route");

        var result = await service.RefreshItemMarketDataAsync(CreateRefreshRequest(101));

        Assert.Equal(CoreProcurementItemRefreshStatus.Refreshed, result.Status);
        Assert.Equal("Item 101", result.ItemName);
        Assert.Equal([202, 101], service.Session.MarketEvidence.ItemAnalyses.Select(analysis => analysis.ItemId));
        Assert.Equal([202, 101], service.Session.MarketEvidence.ShoppingPlans!.Select(plan => plan.ItemId));
        Assert.Contains(404, service.Session.MarketEvidence.UnavailableMarketItemIds);
        Assert.Null(service.Session.ProcurementOverlay);
        marketExecution.Verify(e => e.ExecuteAsync(
            It.Is<MarketAnalysisExecutionRequest>(request =>
                request.Items.Count == 1 &&
                request.Items[0].ItemId == 101 &&
                request.Items[0].TotalQuantity == 5 &&
                request.MaxAge == TimeSpan.Zero &&
                request.Scope == MarketFetchScope.SelectedDataCenter &&
                request.SelectedDataCenter == "Aether"),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_WhenPlanChangesAfterFetch_DoesNotPublish()
    {
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        var service = CreateService(marketExecution: marketExecution.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        marketExecution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => service.Session.ActivatePlan(
                CreatePlan(),
                [new ProjectItem { Id = 303, Name = "Replacement", Quantity = 1 }],
                new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
                "replacement plan"))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [new MarketItemAnalysis { ItemId = 101, Name = "Item 101", QuantityNeeded = 5 }],
                [ShoppingPlan(101)]));

        var result = await service.RefreshItemMarketDataAsync(CreateRefreshRequest(101));

        Assert.Equal(CoreProcurementItemRefreshStatus.StalePlan, result.Status);
        Assert.Empty(service.Session.MarketEvidence.ItemAnalyses);
        Assert.Empty(service.Session.MarketEvidence.ShoppingPlans!);
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_WhenMarketEvidenceChangesAfterFetch_DoesNotPublish()
    {
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        var service = CreateService(marketExecution: marketExecution.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        marketExecution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => service.Session.PublishMarketAnalysis(
                [new MarketItemAnalysis { ItemId = 303, Name = "New Evidence", QuantityNeeded = 2 }],
                [],
                "new evidence"))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [new MarketItemAnalysis { ItemId = 101, Name = "Item 101", QuantityNeeded = 5 }],
                [ShoppingPlan(101)]));

        var result = await service.RefreshItemMarketDataAsync(CreateRefreshRequest(101));

        Assert.Equal(CoreProcurementItemRefreshStatus.StaleConfiguration, result.Status);
        Assert.Equal(303, Assert.Single(service.Session.MarketEvidence.ItemAnalyses).ItemId);
        Assert.Empty(service.Session.MarketEvidence.ShoppingPlans!);
    }

    private static TestServiceHost CreateService(
        IProcurementRouteExecutionService? procurementExecution = null,
        IMarketAnalysisExecutionService? marketExecution = null,
        FakeRecipeLayerWorkflowService? recipeLayerWorkflow = null)
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var operationState = new CraftOperationState();
        var operationCoordinator = new CraftOperationCoordinator(session, operationState);
        var service = new CoreProcurementWorkflowService(
            session,
            procurementExecution ?? Mock.Of<IProcurementRouteExecutionService>(),
            marketExecution ?? Mock.Of<IMarketAnalysisExecutionService>(),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()),
            recipeLayerWorkflow ?? new FakeRecipeLayerWorkflowService(),
            operationCoordinator);
        return new TestServiceHost(service, session, operationState);
    }

    private static CoreProcurementWorkflowRequest CreateRequest(
        IReadOnlyList<DetailedShoppingPlan>? sourceShoppingPlans = null,
        Func<bool>? isCurrentOperation = null)
    {
        return new CoreProcurementWorkflowRequest(
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America",
            MarketAcquisitionLens.MinimumUpfrontCost,
            new MarketAnalysisConfig { TravelTolerance = 7 },
            IncludeSplitPurchases: false,
            sourceShoppingPlans ?? [],
            BlacklistedWorlds: new HashSet<MarketWorldKey>(),
            ExcludedItemWorlds: new HashSet<MarketItemWorldKey>(),
            ExpectedWorldsByDataCenter: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            isCurrentOperation,
            MarketAnalysisExecutionOptions.Synchronous);
    }

    private static CoreProcurementItemRefreshWorkflowRequest CreateRefreshRequest(
        int itemId,
        Func<bool>? isCurrentOperation = null)
    {
        return new CoreProcurementItemRefreshWorkflowRequest(
            itemId,
            $"Item {itemId}",
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America",
            MarketAcquisitionLens.MinimumUpfrontCost,
            ExpectedWorldsByDataCenter: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            isCurrentOperation,
            MarketAnalysisExecutionOptions.Synchronous);
    }

    private static CraftingPlan CreatePlan()
    {
        return new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 100,
                    Name = "Root",
                    Quantity = 1,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true
                }
            ]
        };
    }

    private static DetailedShoppingPlan ShoppingPlan(int itemId, string worldName = "Siren")
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = $"Item {itemId}",
            QuantityNeeded = 5,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = worldName,
                TotalCost = itemId * 10,
                TotalQuantityPurchased = 5
            }
        };
    }

    private sealed record TestServiceHost(
        CoreProcurementWorkflowService Service,
        CraftSessionState Session,
        CraftOperationState OperationState)
    {
        public Task<CoreProcurementWorkflowResult> RunAnalysisAsync(CoreProcurementWorkflowRequest request) =>
            Service.RunAnalysisAsync(request);

        public Task<CoreProcurementItemRefreshWorkflowResult> RefreshItemMarketDataAsync(
            CoreProcurementItemRefreshWorkflowRequest request) =>
            Service.RefreshItemMarketDataAsync(request);
    }

    private sealed class FakeRecipeLayerWorkflowService : ICoreRecipeLayerWorkflowService
    {
        private readonly IReadOnlyList<MaterialAggregate> _activeProcurementItems;
        private readonly IReadOnlyList<MaterialAggregate> _marketAnalysisCandidates;

        public FakeRecipeLayerWorkflowService(
            IReadOnlyList<MaterialAggregate>? activeProcurementItems = null,
            IReadOnlyList<MaterialAggregate>? marketAnalysisCandidates = null)
        {
            _activeProcurementItems = activeProcurementItems ??
                [
                    new MaterialAggregate { ItemId = 101, Name = "Item 101", TotalQuantity = 5 },
                    new MaterialAggregate { ItemId = 202, Name = "Item 202", TotalQuantity = 3 }
                ];
            _marketAnalysisCandidates = marketAnalysisCandidates ??
                [
                    new MaterialAggregate { ItemId = 101, Name = "Item 101", TotalQuantity = 5 },
                    new MaterialAggregate { ItemId = 202, Name = "Item 202", TotalQuantity = 3 }
                ];
        }

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity() => RecipeOperationSnapshotIdentity.Unspecified;

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan) => new([], [], [], []);

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan) => _marketAnalysisCandidates;

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan) => _activeProcurementItems;

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RecipeDemandProjection?>(new RecipeDemandProjection([], [], [], []));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(_marketAnalysisCandidates);

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(_activeProcurementItems);
    }

    private static MarketEvidenceSet CreateEmptyEvidence(int fetchedCount = 0)
    {
        return new MarketEvidenceSet(
            new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
            [],
            MarketFetchScope.SelectedDataCenter,
            ["Aether"],
            "Aether",
            "North America",
            maxAge: TimeSpan.Zero,
            fetchedCount: fetchedCount,
            loadedAtUtc: DateTime.UtcNow);
    }
}
