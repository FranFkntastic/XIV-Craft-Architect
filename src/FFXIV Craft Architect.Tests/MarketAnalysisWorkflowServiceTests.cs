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
        appState.ApplyBuiltRecipePlanWithActiveItems(CreatePlan());
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
            It.Is<MarketAnalysisExecutionRequest>(request => request.ForceRefreshData && request.MaxAge == null),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
    }

    [Fact]
    public async Task RunAnalysisAsync_PublishesAndPersistsRecipeBasis()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(CreatePlan());
        appState.TrackCurrentPlanIdentity("saved-plan", null);
        var jsRuntime = new RecordingJsRuntime();
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(CreateExecutionResult());
        var recipeBasis = CreateStoredRecipeBasis();
        var service = CreateService(
            appState,
            execution.Object,
            jsRuntime,
            new StubRecipeLayerWorkflowService(recipeBasis: recipeBasis));

        var result = await service.RunAnalysisAsync(new MarketAnalysisWorkflowRequest(ForceRefreshData: false));

        Assert.True(result.Published);
        Assert.NotNull(appState.MarketAnalysisRecipeBasis);
        Assert.Contains("\"MarketAnalysisDemandItems\"", jsRuntime.LastPatchedRecipeBasisJson);
        Assert.Contains("\"MarketAnalysisDemandItems\"", jsRuntime.LastSavedPlan?.MarketAnalysisRecipeBasisJson);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenPlanChangesDuringExecution_DoesNotPublishStaleResults()
    {
        var originalPlan = CreatePlan();
        var replacementPlan = CreatePlan(itemId: 2);
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(originalPlan);
        var jsRuntime = new RecordingJsRuntime();
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => appState.ApplyBuiltRecipePlanWithActiveItems(replacementPlan))
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
        appState.ApplyBuiltRecipePlanWithActiveItems(CreatePlan());
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
                searchEntireRegion: false))
            .ReturnsAsync(CreateExecutionResult());
        var service = CreateService(appState, execution.Object, jsRuntime);

        var result = await service.RunAnalysisAsync(new MarketAnalysisWorkflowRequest(ForceRefreshData: false));

        Assert.False(result.Published);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Equal(0, jsRuntime.PatchMarketAnalysisCallCount);
        Assert.Equal(0, jsRuntime.SavePlanCallCount);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("data-center")]
    [InlineData("region")]
    [InlineData("lens")]
    public async Task RunAnalysisAsync_WhenMarketContextChangesDuringExecution_RejectsOldResult(string changedContext)
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(CreatePlan());
        var jsRuntime = new RecordingJsRuntime();
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() => ChangeMarketContext(appState, changedContext))
            .ReturnsAsync(CreateExecutionResult());
        var service = CreateService(appState, execution.Object, jsRuntime);

        var result = await service.RunAnalysisAsync(new MarketAnalysisWorkflowRequest(ForceRefreshData: false));

        Assert.False(result.Published);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.Null(appState.SelectedMarketAnalysisItemId);
        Assert.Empty(appState.ExpandedMarketAnalysisWorlds);
        Assert.Equal(0, jsRuntime.PatchMarketAnalysisCallCount);
        Assert.Equal(0, jsRuntime.SavePlanCallCount);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenNewerMarketStateExists_DoesNotPruneViewStateOrClearProcurementOverlay()
    {
        var originalPlan = CreatePlan(itemId: 1);
        var replacementPlan = CreatePlan(itemId: 2);
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(originalPlan);
        var jsRuntime = new RecordingJsRuntime();
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .Callback(() =>
            {
                appState.ApplyBuiltRecipePlanWithActiveItems(replacementPlan);
                appState.ReplaceMarketAnalysis(
                    [
                        new MarketItemAnalysis
                        {
                            ItemId = 2,
                            Name = "Replacement Material",
                            QuantityNeeded = 2,
                            Worlds =
                            [
                                new WorldMarketAnalysis
                                {
                                    DataCenter = "Aether",
                                    WorldName = "Siren"
                                }
                            ]
                        }
                    ],
                    [ShoppingPlan(itemId: 2)]);
                appState.SelectMarketAnalysisItem(2);
                appState.ToggleMarketAnalysisWorld(2, "Aether", "Siren");
                appState.SetMarketAnalysisGridSort(MarketAnalysisGridSortColumn.Total, descending: true);
                appState.ReplaceProcurementOverlay([ShoppingPlan(itemId: 2, worldName: "Siren")]);
            })
            .ReturnsAsync(CreateExecutionResult());
        var service = CreateService(appState, execution.Object, jsRuntime);

        var result = await service.RunAnalysisAsync(new MarketAnalysisWorkflowRequest(ForceRefreshData: false));

        Assert.False(result.Published);
        Assert.Equal(2, Assert.Single(appState.MarketItemAnalyses).ItemId);
        Assert.Equal(2, Assert.Single(appState.ShoppingPlans).ItemId);
        Assert.Equal(2, Assert.Single(appState.ProcurementShoppingPlans).ItemId);
        Assert.Equal(2, appState.SelectedMarketAnalysisItemId);
        Assert.Equal([new MarketAnalysisExpandedWorldKey(2, "Aether", "Siren")], appState.ExpandedMarketAnalysisWorlds);
        Assert.Equal(MarketAnalysisGridSortColumn.Total, appState.MarketAnalysisGridSortColumn);
        Assert.Equal(0, jsRuntime.PatchMarketAnalysisCallCount);
        Assert.Equal(0, jsRuntime.SavePlanCallCount);
    }

    [Fact]
    public async Task RunAnalysisAsync_WhenPlanChangesAfterCandidateBuild_DoesNotClearNewerAnalysis()
    {
        var originalPlan = CreatePlan(itemId: 1);
        var replacementPlan = CreatePlan(itemId: 2);
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(originalPlan);
        var jsRuntime = new RecordingJsRuntime();
        var workflow = new ChangingRecipeLayerWorkflowService(() =>
        {
            appState.ApplyBuiltRecipePlanWithActiveItems(replacementPlan);
            appState.ReplaceMarketAnalysis(
                [
                    new MarketItemAnalysis
                    {
                        ItemId = 2,
                        Name = "Replacement Material",
                        QuantityNeeded = 2
                    }
                ],
                [ShoppingPlan(itemId: 2)]);
        });
        var execution = new Mock<IMarketAnalysisExecutionService>();
        execution.Setup(e => e.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(CreateExecutionResult());
        var service = CreateService(appState, execution.Object, jsRuntime, workflow);

        var result = await service.RunAnalysisAsync(new MarketAnalysisWorkflowRequest(ForceRefreshData: false));

        Assert.False(result.Published);
        Assert.Equal(2, Assert.Single(appState.MarketItemAnalyses).ItemId);
        Assert.Equal(2, Assert.Single(appState.ShoppingPlans).ItemId);
        execution.Verify(e => e.ExecuteAsync(
            It.IsAny<MarketAnalysisExecutionRequest>(),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAnalysisAsync_UsesRecipeDemandProjectionForMarketInputs()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(CreatePlan());
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
            new StubRecipeLayerWorkflowService(projection));

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
        appState.ApplyBuiltRecipePlanWithActiveItems(CreatePlan());
        appState.TrackCurrentPlanIdentity("saved-plan", null);
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 1, Name = "Material", QuantityNeeded = 2 }],
            [],
            CreateStoredRecipeBasis());
        var jsRuntime = new RecordingJsRuntime();
        var service = CreateService(appState, Mock.Of<IMarketAnalysisExecutionService>(), jsRuntime);

        var result = await service.ApplyLensAsync(MarketAcquisitionLens.BulkValue);

        Assert.True(result.Published);
        Assert.Equal(MarketAcquisitionLens.BulkValue, appState.MarketAnalysisLens);
        Assert.Equal(1, Assert.Single(appState.ShoppingPlans).ItemId);
        Assert.NotNull(appState.MarketAnalysisRecipeBasis);
        Assert.Contains("\"MarketAnalysisDemandItems\"", jsRuntime.LastPatchedRecipeBasisJson);
        Assert.Equal(1, jsRuntime.PatchMarketAnalysisCallCount);
        Assert.Equal(1, jsRuntime.SavePlanCallCount);
    }

    private static MarketAnalysisWorkflowService CreateService(
        AppState appState,
        IMarketAnalysisExecutionService execution,
        RecordingJsRuntime jsRuntime,
        IRecipeLayerWorkflowService? recipeLayerWorkflow = null)
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
            recipeLayerWorkflow ?? new StubRecipeLayerWorkflowService());
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

    private static DetailedShoppingPlan ShoppingPlan(int itemId, string worldName = "Siren")
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = "Replacement Material",
            QuantityNeeded = 2,
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = worldName,
                TotalQuantityPurchased = 2,
                TotalCost = 20
            }
        };
    }

    private static void ChangeMarketContext(AppState appState, string changedContext)
    {
        switch (changedContext)
        {
            case "scope":
                appState.SetMarketEvidenceSettings(
                    "Aether",
                    "North America",
                    MarketFetchScope.SelectedDataCenter,
                    searchEntireRegion: false);
                break;
            case "data-center":
                appState.SetMarketEvidenceSettings(
                    "Primal",
                    "North America",
                    MarketFetchScope.SelectedDataCenter,
                    searchEntireRegion: false);
                break;
            case "region":
                appState.SetMarketEvidenceSettings(
                    "Aether",
                    "Europe",
                    MarketFetchScope.SelectedDataCenter,
                    searchEntireRegion: false);
                break;
            case "lens":
                appState.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedContext), changedContext, null);
        }
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

    private class StubRecipeLayerWorkflowService : IRecipeLayerWorkflowService
    {
        private readonly RecipeDemandProjection? _projection;
        private readonly StoredRecipeOperationSnapshot? _recipeBasis;

        public StubRecipeLayerWorkflowService(
            RecipeDemandProjection? projection = null,
            StoredRecipeOperationSnapshot? recipeBasis = null)
        {
            _projection = projection;
            _recipeBasis = recipeBasis;
        }

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity()
        {
            return RecipeOperationSnapshotIdentity.Unspecified;
        }

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan)
        {
            return _projection ?? new RecipeDemandProjectionService().Build(plan, snapshot: null);
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

        public virtual Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildMarketAnalysisCandidates(plan));
        }

        public virtual Task<MarketAnalysisCandidateBuildResult?> BuildCurrentMarketAnalysisCandidateResultAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<MarketAnalysisCandidateBuildResult?>(
                new MarketAnalysisCandidateBuildResult(BuildMarketAnalysisCandidates(plan), _recipeBasis));
        }

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildActiveProcurementItems(plan));
        }
    }

    private sealed class ChangingRecipeLayerWorkflowService : StubRecipeLayerWorkflowService
    {
        private readonly Action _afterYield;

        public ChangingRecipeLayerWorkflowService(Action afterYield)
        {
            _afterYield = afterYield;
        }

        public override async Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            var candidates = BuildMarketAnalysisCandidates(plan);
            await Task.Yield();
            _afterYield();
            return candidates;
        }

        public override async Task<MarketAnalysisCandidateBuildResult?> BuildCurrentMarketAnalysisCandidateResultAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            var candidates = BuildMarketAnalysisCandidates(plan);
            await Task.Yield();
            _afterYield();
            return new MarketAnalysisCandidateBuildResult(candidates, RecipeBasis: null);
        }
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public int SavePlanCallCount { get; private set; }
        public int PatchMarketAnalysisCallCount { get; private set; }
        public StoredPlan? LastSavedPlan { get; private set; }
        public string? LastPatchedMarketIntelligenceJson { get; private set; }
        public string? LastPatchedRecipeBasisJson { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "IndexedDB.savePlan")
            {
                SavePlanCallCount++;
                LastSavedPlan = Assert.IsType<StoredPlan>(Assert.Single(args ?? []));
                return new ValueTask<TValue>((TValue)(object)true);
            }

            if (identifier == "IndexedDB.patchMarketAnalysis")
            {
                PatchMarketAnalysisCallCount++;
                LastPatchedMarketIntelligenceJson = args?.Length > 3 ? args[3] as string : null;
                LastPatchedRecipeBasisJson = args?.Length > 6 ? args[6] as string : null;
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
