using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class CoreMarketAnalysisWorkflowServiceTests
{
    [Fact]
    public async Task RunAnalysisAsync_WithNoPlan_ReturnsUnpublishedWithoutExecution()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>(MockBehavior.Strict);
        var service = CreateService(execution: execution.Object);

        var result = await service.RunAnalysisAsync(CreateRequest());

        Assert.False(result.Published);
        Assert.Equal(0, result.AnalyzedCount);
        execution.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAnalysisAsync_PublishesMarketEvidenceAndShoppingPlans()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(CreateExecutionResult());
        var service = CreateService(execution: execution.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");

        var result = await service.RunAnalysisAsync(CreateRequest(forceRefreshData: true));

        Assert.True(result.Published);
        Assert.Equal(1, result.AnalyzedCount);
        Assert.Equal(1, Assert.Single(service.Session.MarketEvidence.ItemAnalyses).ItemId);
        Assert.Equal(1, Assert.Single(service.Session.MarketEvidence.ShoppingPlans!).ItemId);
        Assert.True(service.Session.IsDirty(CraftSessionDirtyBucket.MarketAnalysis));
        execution.Verify(e => e.ExecuteAsync(
            It.Is<MarketAnalysisExecutionRequest>(request =>
                request.Items.Count == 1 &&
                request.Items[0].ItemId == 1 &&
                request.ForceRefreshData &&
                request.MaxAge == null &&
                request.Scope == MarketFetchScope.SelectedDataCenter &&
                request.SelectedDataCenter == "Aether" &&
                request.SelectedRegion == "North America" &&
                request.RecommendationMode == RecommendationMode.MaximizeValue &&
                request.Lens == MarketAcquisitionLens.MinimumUpfrontCost),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
        Assert.Equal(RecommendationMode.MaximizeValue, service.Session.MarketEvidence.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.MinimumUpfrontCost, service.Session.MarketEvidence.Lens);
        Assert.NotNull(service.Session.MarketEvidence.RecipeBasis);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenPlanChangesDuringExecution_DoesNotPublish()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>();
        var service = CreateService(execution: execution.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => service.Session.ActivatePlan(
                CreatePlan(itemId: 2),
                [new ProjectItem { Id = 2, Name = "Replacement", Quantity = 1 }],
                new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
                "replacement plan"))
            .ReturnsAsync(CreateExecutionResult());

        var result = await service.RunAnalysisAsync(CreateRequest());

        Assert.False(result.Published);
        Assert.Empty(service.Session.MarketEvidence.ItemAnalyses);
        Assert.Empty(service.Session.MarketEvidence.ShoppingPlans!);
    }

    [Fact]
    public async Task RunAnalysisAsync_ClearsExistingMarketEvidenceBeforeExecution()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>();
        var service = CreateService(execution: execution.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        service.Session.PublishMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 99, Name = "Old", QuantityNeeded = 1 }],
            [],
            "old evidence");
        service.Session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(DateTime.UtcNow, [99], "old route", [ShoppingPlan(99)]),
            "old route");
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await service.RunAnalysisAsync(
            CreateRequest(),
            ct: new CancellationToken(canceled: true));

        Assert.False(result.Published);
        Assert.Empty(service.Session.MarketEvidence.ItemAnalyses);
        Assert.Empty(service.Session.MarketEvidence.ShoppingPlans!);
        Assert.Null(service.Session.ProcurementOverlay);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenMarketContextChangesDuringExecution_DoesNotPublish()
    {
        var execution = new Mock<IMarketAnalysisExecutionService>();
        var service = CreateService(execution: execution.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => service.Session.MarkProcurementSettingsChanged("market context changed"))
            .ReturnsAsync(CreateExecutionResult());

        var result = await service.RunAnalysisAsync(CreateRequest());

        Assert.False(result.Published);
        Assert.Empty(service.Session.MarketEvidence.ItemAnalyses);
    }

    [Fact]
    public async Task ApplyLensAsync_ReprojectsExistingAnalysisAndPublishes()
    {
        var ladder = new Mock<IMarketPriceLadderAnalysisService>();
        ladder.Setup(l => l.ProjectToShoppingPlan(
                It.IsAny<MarketItemAnalysis>(),
                MarketAcquisitionLens.BulkValue,
                It.IsAny<MarketAnalysisConfig?>()))
            .Returns(ShoppingPlan(1));
        var service = CreateService(ladder: ladder.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        service.Session.PublishMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 1, Name = "Material", QuantityNeeded = 2 }],
            [],
            "existing analysis",
            RecommendationMode.MaximizeValue,
            MarketAcquisitionLens.MinimumUpfrontCost,
            CreateStoredRecipeBasis());

        var result = await service.ApplyLensAsync(new CoreApplyMarketAnalysisLensRequest(MarketAcquisitionLens.BulkValue));

        Assert.True(result.Published);
        Assert.Equal(1, Assert.Single(service.Session.MarketEvidence.ShoppingPlans!).ItemId);
        Assert.Equal(RecommendationMode.MaximizeValue, service.Session.MarketEvidence.RecommendationMode);
        Assert.Equal(MarketAcquisitionLens.BulkValue, service.Session.MarketEvidence.Lens);
        Assert.NotNull(service.Session.MarketEvidence.RecipeBasis);
        Assert.Null(service.Session.ProcurementOverlay);
        ladder.Verify(l => l.ProjectToShoppingPlan(
            It.Is<MarketItemAnalysis>(analysis => analysis.ItemId == 1),
            MarketAcquisitionLens.BulkValue,
            It.IsAny<MarketAnalysisConfig?>()));
    }

    [Fact]
    public async Task ApplyLensAsync_WhenCanceledBeforePublish_DoesNotMutateEvidence()
    {
        var ladder = new Mock<IMarketPriceLadderAnalysisService>();
        ladder.Setup(l => l.ProjectToShoppingPlan(
                It.IsAny<MarketItemAnalysis>(),
                MarketAcquisitionLens.BulkValue,
                It.IsAny<MarketAnalysisConfig?>()))
            .Returns(ShoppingPlan(2));
        var service = CreateService(ladder: ladder.Object);
        service.Session.ActivatePlan(
            CreatePlan(),
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        service.Session.PublishMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 1, Name = "Material", QuantityNeeded = 2 }],
            [],
            "existing analysis",
            RecommendationMode.MaximizeValue,
            MarketAcquisitionLens.MinimumUpfrontCost);
        var before = service.Session.CaptureVersionStamp();

        var result = await service.ApplyLensAsync(
            new CoreApplyMarketAnalysisLensRequest(MarketAcquisitionLens.BulkValue),
            new CancellationToken(canceled: true));

        Assert.False(result.Published);
        Assert.Equal(before.MarketAnalysis, service.Session.Versions.MarketAnalysis);
        Assert.Empty(service.Session.MarketEvidence.ShoppingPlans!);
    }

    private static TestServiceHost CreateService(
        IMarketAnalysisExecutionService? execution = null,
        IMarketPriceLadderAnalysisService? ladder = null,
        FakeRecipeLayerWorkflowService? recipeLayerWorkflow = null)
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var operationState = new CraftOperationState();
        var operationCoordinator = new CraftOperationCoordinator(session, operationState);
        var marketExecution = execution ?? Mock.Of<IMarketAnalysisExecutionService>();
        var service = new CoreMarketAnalysisWorkflowService(
            session,
            new MarketEvidenceReconciliationService(marketExecution),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()),
            ladder ?? Mock.Of<IMarketPriceLadderAnalysisService>(),
            recipeLayerWorkflow ?? new FakeRecipeLayerWorkflowService(),
            operationCoordinator);
        return new TestServiceHost(service, session, operationState);
    }

    private static CoreMarketAnalysisWorkflowRequest CreateRequest(bool forceRefreshData = false)
    {
        return new CoreMarketAnalysisWorkflowRequest(
            ForceRefreshData: forceRefreshData,
            Scope: MarketFetchScope.SelectedDataCenter,
            SelectedDataCenter: "Aether",
            SelectedRegion: "North America",
            RecommendationMode: RecommendationMode.MaximizeValue,
            Lens: MarketAcquisitionLens.MinimumUpfrontCost,
            ExpectedWorldsByDataCenter: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            ExecutionOptions: MarketAnalysisExecutionOptions.Synchronous);
    }

    private static CraftingPlan CreatePlan(int itemId = 100)
    {
        return new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = itemId,
                    Name = "Root",
                    Quantity = 1,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true
                }
            ]
        };
    }

    private static MarketAnalysisExecutionResult CreateExecutionResult()
    {
        return new MarketAnalysisExecutionResult(
            new MarketEvidenceSet(
                new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
                [(1, "Aether")],
                MarketFetchScope.SelectedDataCenter,
                ["Aether"],
                "Aether",
                "North America",
                TimeSpan.Zero,
                fetchedCount: 1,
                DateTime.UtcNow),
            [new MarketItemAnalysis { ItemId = 1, Name = "Material", QuantityNeeded = 2 }],
            [ShoppingPlan(1)]);
    }

    private static DetailedShoppingPlan ShoppingPlan(int itemId)
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = "Material",
            QuantityNeeded = 2,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalQuantityPurchased = 2,
                TotalCost = 20
            }
        };
    }

    private static StoredRecipeOperationSnapshot CreateStoredRecipeBasis()
    {
        return new StoredRecipeOperationSnapshot
        {
            Operations =
            [
                new StoredRecipeOperation
                {
                    NodeId = "root",
                    ResultItemId = 1,
                    ResultItemName = "Material"
                }
            ],
            MarketAnalysisDemandItems =
            [
                new StoredMarketAnalysisDemandItem
                {
                    ItemId = 1,
                    Name = "Material",
                    TotalQuantity = 2
                }
            ]
        };
    }

    private sealed record TestServiceHost(
        CoreMarketAnalysisWorkflowService Service,
        CraftSessionState Session,
        CraftOperationState OperationState)
    {
        public Task<CoreMarketAnalysisWorkflowResult> RunAnalysisAsync(
            CoreMarketAnalysisWorkflowRequest request,
            CancellationToken ct = default) =>
            Service.RunAnalysisAsync(request, ct: ct);

        public Task<CoreMarketAnalysisWorkflowResult> ApplyLensAsync(
            CoreApplyMarketAnalysisLensRequest request,
            CancellationToken ct = default) =>
            Service.ApplyLensAsync(request, ct);
    }

    private sealed class FakeRecipeLayerWorkflowService : ICoreRecipeLayerWorkflowService
    {
        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity() => RecipeOperationSnapshotIdentity.Unspecified;

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan) => new([], [], [], []);

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan) =>
            [new MaterialAggregate { ItemId = 1, Name = "Material", TotalQuantity = 2 }];

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan) =>
            [new MaterialAggregate { ItemId = 1, Name = "Material", TotalQuantity = 2 }];

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RecipeDemandProjection?>(new RecipeDemandProjection([], [], [], []));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildMarketAnalysisCandidates(plan));

        public Task<CoreMarketAnalysisCandidateBuildResult?> BuildCurrentMarketAnalysisCandidateResultAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<CoreMarketAnalysisCandidateBuildResult?>(
                new CoreMarketAnalysisCandidateBuildResult(
                    BuildMarketAnalysisCandidates(plan),
                    CreateStoredRecipeBasis()));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildActiveProcurementItems(plan));
    }
}
