using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisWorkflowServiceTests
{
    [Fact]
    public async Task RunAnalysisAsync_PublishesAnalysisPersistsAndAutosaves()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(CreatePlan());
        appState.TrackCurrentPlanIdentity("saved-plan", null);
        var jsRuntime = new RecordingJsRuntime();
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(CreateExecutionResult());
        var service = CreateService(appState, execution.Object, jsRuntime);

        var result = await service.RunAnalysisAsync(new MarketAnalysisWorkflowRequest(ForceRefreshData: true));

        Assert.True(result.Published);
        Assert.Equal(1, result.AnalyzedCount);
        Assert.Equal(1, Assert.Single(appState.MarketItemAnalyses).ItemId);
        Assert.Equal(1, Assert.Single(appState.ShoppingPlans).ItemId);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.Equal(1, jsRuntime.PatchMarketAnalysisCallCount);
        Assert.Equal(1, jsRuntime.SavePlanCallCount);
        execution.Verify(e => e.ExecuteAsync(
            It.Is<MarketAnalysisExecutionRequest>(request => request.MaxAge == TimeSpan.Zero),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenPlanChangesDuringExecution_DoesNotPublishStaleResults()
    {
        var originalPlan = CreatePlan();
        var replacementPlan = CreatePlan(itemId: 2);
        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(originalPlan);
        var jsRuntime = new RecordingJsRuntime();
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.ApplyBuiltRecipePlan(replacementPlan))
            .ReturnsAsync(CreateExecutionResult());
        var service = CreateService(appState, execution.Object, jsRuntime);

        var result = await service.RunAnalysisAsync(new MarketAnalysisWorkflowRequest(ForceRefreshData: false));

        Assert.False(result.Published);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Equal(0, jsRuntime.PatchMarketAnalysisCallCount);
        Assert.Equal(0, jsRuntime.SavePlanCallCount);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenMarketScopeChangesDuringExecution_DoesNotPublishStaleResults()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(CreatePlan());
        var jsRuntime = new RecordingJsRuntime();
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.SetMarketEvidenceSettings(
                "Aether",
                "North America",
                MarketFetchScope.SelectedDataCenter,
                searchEntireRegion: true,
                autoFetchPricesOnRebuild: true))
            .ReturnsAsync(CreateExecutionResult());
        var service = CreateService(appState, execution.Object, jsRuntime);

        var result = await service.RunAnalysisAsync(new MarketAnalysisWorkflowRequest(ForceRefreshData: false));

        Assert.False(result.Published);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Equal(0, jsRuntime.PatchMarketAnalysisCallCount);
        Assert.Equal(0, jsRuntime.SavePlanCallCount);
    }

    [Fact]
    public async Task RunAnalysisAsync_UsesRecipeDemandProjectionForMarketInputs()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(CreatePlan());
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(CreateExecutionResult(quantityNeeded: 9));
        var projection = CreateProjection(
            marketCandidates:
            [
                CreateDemandRow(1, "Material", quantity: 9, RecipeDemandViewKind.MarketAnalysisCandidate)
            ],
            activeProcurement: []);
        var service = CreateService(
            appState,
            execution.Object,
            new RecordingJsRuntime(),
            new StubRecipeDemandProjectionService(projection));

        await service.RunAnalysisAsync(new MarketAnalysisWorkflowRequest(ForceRefreshData: false));

        execution.Verify(e => e.ExecuteAsync(
            It.Is<MarketAnalysisExecutionRequest>(request =>
                request.Items.Count == 1 &&
                request.Items[0].ItemId == 1 &&
                request.Items[0].TotalQuantity == 9),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
    }

    [Fact]
    public async Task ApplyLensAsync_ReprojectsExistingAnalysisAndPersists()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(CreatePlan());
        appState.TrackCurrentPlanIdentity("saved-plan", null);
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 1, Name = "Material", QuantityNeeded = 2 }],
            []);
        var jsRuntime = new RecordingJsRuntime();
        var service = CreateService(appState, Mock.Of<IMarketAnalysisExecutionService>(), jsRuntime);

        var result = await service.ApplyLensAsync(MarketAcquisitionLens.BulkValue);

        Assert.True(result.Published);
        Assert.Equal(MarketAcquisitionLens.BulkValue, appState.MarketAnalysisLens);
        Assert.Equal(1, Assert.Single(appState.ShoppingPlans).ItemId);
        Assert.Equal(1, jsRuntime.PatchMarketAnalysisCallCount);
        Assert.Equal(1, jsRuntime.SavePlanCallCount);
    }

    private static MarketAnalysisWorkflowService CreateService(
        AppState appState,
        IMarketAnalysisExecutionService execution,
        RecordingJsRuntime jsRuntime,
        IRecipeDemandProjectionService? demandProjectionService = null)
    {
        var indexedDb = new IndexedDbService(jsRuntime);
        var persistence = new WebPlanPersistenceService(
            indexedDb,
            new StoredPlanSnapshotBuilder(appState),
            new PlanSessionLoadService(appState));
        return new MarketAnalysisWorkflowService(
            appState,
            execution,
            new MarketShoppingService(Mock.Of<IMarketCacheService>()),
            new MarketPriceLadderAnalysisService(),
            persistence,
            indexedDb,
            demandProjectionService ?? new RecipeDemandProjectionService());
    }

    private static CraftingPlan CreatePlan(int itemId = 1)
    {
        return new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = itemId,
                    Name = "Material",
                    Quantity = 2,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanBuyFromMarket = true
                }
            ]
        };
    }

    private static MarketAnalysisExecutionResult CreateExecutionResult(int quantityNeeded = 2)
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
            [new MarketItemAnalysis { ItemId = 1, Name = "Material", QuantityNeeded = quantityNeeded }],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 1,
                    Name = "Material",
                    QuantityNeeded = quantityNeeded,
                    RecommendedWorld = new WorldShoppingSummary
                    {
                        WorldName = "Siren",
                        TotalQuantityPurchased = 2,
                        TotalCost = 20
                    }
                }
            ]);
    }

    private static RecipeDemandProjection CreateProjection(
        IReadOnlyList<RecipeDemandRow> marketCandidates,
        IReadOnlyList<RecipeDemandRow> activeProcurement)
    {
        return new RecipeDemandProjection(
            AllPlanDemand: Array.Empty<RecipeDemandRow>(),
            MarketAnalysisCandidates: marketCandidates,
            ActiveProcurementDemand: activeProcurement,
            SuppressedDemand: Array.Empty<RecipeDemandRow>());
    }

    private static RecipeDemandRow CreateDemandRow(
        int itemId,
        string itemName,
        int quantity,
        RecipeDemandViewKind viewKind)
    {
        return new RecipeDemandRow(
            viewKind,
            $"node-{itemId}",
            itemId,
            itemName,
            0,
            quantity,
            RecipeDemandQuantityBasis.PlanNodeQuantity,
            false,
            AcquisitionSource.MarketBuyNq,
            AcquisitionSourceReason.SystemDefault,
            false,
            true,
            false,
            0,
            null,
            "Direct",
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private sealed class StubRecipeDemandProjectionService : IRecipeDemandProjectionService
    {
        private readonly RecipeDemandProjection _projection;

        public StubRecipeDemandProjectionService(RecipeDemandProjection projection)
        {
            _projection = projection;
        }

        public RecipeDemandProjection Build(CraftingPlan? plan, RecipeOperationSnapshot? snapshot)
        {
            return _projection;
        }
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public int SavePlanCallCount { get; private set; }
        public int PatchMarketAnalysisCallCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "IndexedDB.savePlan")
            {
                SavePlanCallCount++;
                return new ValueTask<TValue>((TValue)(object)true);
            }

            if (identifier == "IndexedDB.patchMarketAnalysis")
            {
                PatchMarketAnalysisCallCount++;
                return new ValueTask<TValue>((TValue)(object)true);
            }

            throw new InvalidOperationException($"Unexpected JS invocation: {identifier}");
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }
    }
}
