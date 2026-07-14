using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisItemRefreshServiceTests
{
    [Fact]
    public async Task RefreshItemMarketDataAsync_ReplacesOnlyRequestedItemAndForcesRefresh()
    {
        var appState = CreateAppStateWithMarketAnalysis();
        appState.ReplaceProcurementOverlay([ShoppingPlan(101, "Old Route")]);
        appState.SelectMarketAnalysisItem(101);
        MarketAnalysisExecutionRequest? capturedRequest = null;
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback<MarketAnalysisExecutionRequest, IProgress<string>?, CancellationToken, MarketAnalysisExecutionOptions?>(
                (request, _, _, _) => capturedRequest = request)
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEvidenceSet(101),
                [new MarketItemAnalysis { ItemId = 101, Name = "Fresh Item 101" }],
                [ShoppingPlan(101, "Fresh Item 101")]));
        var service = CreateService(appState, execution.Object);

        var result = await service.RefreshItemMarketDataAsync(
            new MarketAnalysisItemRefreshWorkflowRequest(101, IsCurrentOperation: () => true));

        Assert.Equal(MarketAnalysisItemRefreshStatus.Refreshed, result.Status);
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.ForceRefreshData);
        Assert.Null(capturedRequest.MaxAge);
        Assert.Collection(capturedRequest.Items, item => Assert.Equal(101, item.ItemId));
        Assert.Contains(appState.MarketItemAnalyses, analysis => analysis.ItemId == 101 && analysis.Name == "Fresh Item 101");
        Assert.Contains(appState.MarketItemAnalyses, analysis => analysis.ItemId == 202 && analysis.Name == "Old Item 202");
        Assert.Equal(101, appState.SelectedMarketAnalysisItemId);
        Assert.Collection(appState.ProcurementShoppingPlans, plan => Assert.Equal("Old Route", plan.Name));
        Assert.True(appState.IsProcurementRouteStale);
        Assert.Equal("Market evidence changed.", appState.ProcurementRouteStaleReason);
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_WhenExecutionReturnsNoData_DoesNotReplaceExistingAnalysis()
    {
        var appState = CreateAppStateWithMarketAnalysis();
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEvidenceSet(101),
                [],
                []));
        var service = CreateService(appState, execution.Object);

        var result = await service.RefreshItemMarketDataAsync(
            new MarketAnalysisItemRefreshWorkflowRequest(101, IsCurrentOperation: () => true));

        Assert.Equal(MarketAnalysisItemRefreshStatus.NoData, result.Status);
        Assert.Contains(appState.MarketItemAnalyses, analysis => analysis.ItemId == 101 && analysis.Name == "Old Item 101");
        Assert.Contains(appState.ShoppingPlans, plan => plan.ItemId == 101 && plan.Name == "Old Item 101");
    }

    [Fact]
    public async Task RefreshItemMarketDataAsync_WhenMarketAnalysisChangesDuringFetch_DoesNotPublishOrSave()
    {
        var appState = CreateAppStateWithMarketAnalysis();
        appState.TrackCurrentPlanIdentity("plan-1", null);
        var jsRuntime = new RecordingJsRuntime();
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.ReplaceMarketAnalysis(
                [new MarketItemAnalysis { ItemId = 303, Name = "Newer Analysis" }],
                [ShoppingPlan(303, "Newer Plan")]))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEvidenceSet(101),
                [new MarketItemAnalysis { ItemId = 101, Name = "Fresh Item 101" }],
                [ShoppingPlan(101, "Fresh Item 101")]));
        var service = CreateService(appState, execution.Object, jsRuntime);

        var result = await service.RefreshItemMarketDataAsync(
            new MarketAnalysisItemRefreshWorkflowRequest(101, IsCurrentOperation: () => true));

        Assert.Equal(MarketAnalysisItemRefreshStatus.StaleConfiguration, result.Status);
        Assert.DoesNotContain(appState.MarketItemAnalyses, analysis => analysis.ItemId == 101 && analysis.Name == "Fresh Item 101");
        Assert.Contains(appState.MarketItemAnalyses, analysis => analysis.ItemId == 303);
        Assert.Empty(jsRuntime.PatchedPlanIds);
    }

    private static MarketAnalysisItemRefreshService CreateService(
        AppState appState,
        IMarketAnalysisExecutionService execution,
        RecordingJsRuntime? jsRuntime = null,
        IRecipeLayerWorkflowService? recipeLayerWorkflow = null)
    {
        jsRuntime ??= new RecordingJsRuntime();
        var indexedDb = new IndexedDbService(jsRuntime);
        var persistence = new WebPlanPersistenceService(
            indexedDb,
            new StoredPlanSnapshotBuilder(appState),
            new PlanSessionLoadService(appState));

        var subsetRefreshService = new MarketAnalysisSubsetRefreshService(
            appState,
            new MarketEvidenceReconciliationService(execution),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()),
            persistence,
            indexedDb,
            recipeLayerWorkflow ?? new StubRecipeLayerWorkflowService());

        return new MarketAnalysisItemRefreshService(subsetRefreshService);
    }

    private static AppState CreateAppStateWithMarketAnalysis()
    {
        var appState = new AppState();
        appState.SetMarketEvidenceSettings(
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter,
            searchEntireRegion: false);
        appState.ApplyBuiltRecipePlanWithActiveItems(CreatePlan(101, 202));
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis { ItemId = 101, Name = "Old Item 101" },
                new MarketItemAnalysis { ItemId = 202, Name = "Old Item 202" }
            ],
            [
                ShoppingPlan(101, "Old Item 101"),
                ShoppingPlan(202, "Old Item 202")
            ],
            CreateStoredRecipeBasis());
        return appState;
    }

    private static CraftingPlan CreatePlan(params int[] itemIds)
    {
        return new CraftingPlan
        {
            RootItems = itemIds
                .Select(itemId => new PlanNode
                {
                    ItemId = itemId,
                    Name = $"Item {itemId}",
                    Quantity = 5,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanBuyFromMarket = true
                })
                .ToList()
        };
    }

    private static DetailedShoppingPlan ShoppingPlan(int itemId, string name)
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 5,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalCost = itemId * 10,
                TotalQuantityPurchased = 5
            }
        };
    }

    private static MarketEvidenceSet CreateEvidenceSet(int itemId)
    {
        return new MarketEvidenceSet(
            new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
            [(itemId, "Aether")],
            MarketFetchScope.SelectedDataCenter,
            ["Aether"],
            "Aether",
            "North America",
            maxAge: null,
            fetchedCount: 1,
            loadedAtUtc: DateTime.UtcNow);
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
                    ResultItemId = 101,
                    ResultItemName = "Item 101"
                }
            ],
            MarketAnalysisDemandItems =
            [
                new StoredMarketAnalysisDemandItem
                {
                    ItemId = 101,
                    Name = "Item 101",
                    TotalQuantity = 5
                },
                new StoredMarketAnalysisDemandItem
                {
                    ItemId = 202,
                    Name = "Item 202",
                    TotalQuantity = 5
                }
            ]
        };
    }

    private sealed class StubRecipeLayerWorkflowService : IRecipeLayerWorkflowService
    {
        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity()
        {
            return RecipeOperationSnapshotIdentity.Unspecified;
        }

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan)
        {
            return new RecipeDemandProjectionService().Build(plan, snapshot: null);
        }

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan)
        {
            return BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();
        }

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan)
        {
            return BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates();
        }

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<RecipeDemandProjection?>(BuildDemandProjection(plan));
        }

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildMarketAnalysisCandidates(plan));
        }

        public Task<MarketAnalysisCandidateBuildResult?> BuildCurrentMarketAnalysisCandidateResultAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<MarketAnalysisCandidateBuildResult?>(
                new MarketAnalysisCandidateBuildResult(BuildMarketAnalysisCandidates(plan), CreateStoredRecipeBasis()));
        }

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildActiveProcurementItems(plan));
        }
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public List<string> PatchedPlanIds { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "IndexedDB.patchMarketAnalysis")
            {
                PatchedPlanIds.Add((string)args![0]!);
                return new ValueTask<TValue>((TValue)(object)true);
            }

            if (identifier == "IndexedDB.savePlan")
            {
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
