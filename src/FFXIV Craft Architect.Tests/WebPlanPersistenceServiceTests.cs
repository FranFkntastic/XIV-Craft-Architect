using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Tests;

public class WebPlanPersistenceServiceTests
{
    [Fact]
    public async Task SaveCurrentPlanAsync_DoesNotLoadFullPayload()
    {
        var jsRuntime = new RecordingJsRuntime();
        var appState = new AppState();
        appState.ReplaceProjectItems([new ProjectItem { Id = 100, Name = "Saved Item", Quantity = 12 }]);
        var service = CreateService(jsRuntime, appState);

        var saved = await service.SaveCurrentPlanAsync(
            "plan-id",
            "Saved Plan",
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        Assert.True(saved);
        Assert.Equal(0, jsRuntime.LoadPlanCallCount);
        Assert.NotNull(jsRuntime.LastSavedPlan);
        Assert.Equal("plan-id", jsRuntime.LastSavedPlan.Id);
        Assert.Equal("Saved Plan", jsRuntime.LastSavedPlan.Name);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), jsRuntime.LastSavedPlan.SavedAt);
    }

    [Fact]
    public void PlanSessionLoadService_Prepare_InvalidPlanJsonReturnsWarning()
    {
        var storedPlan = new StoredPlan
        {
            Id = "broken",
            Name = "Broken",
            PlanJson = "{ this is not valid plan json"
        };

        var result = PlanSessionLoadService.Prepare(storedPlan);

        Assert.Null(result.Plan);
        Assert.Contains("Could not load full plan data", result.Warning);
    }

    [Fact]
    public void PlanSessionLoadService_Prepare_RestoresPlanNodeParentLinks()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Final Craft",
            NodeId = "root"
        };
        var child = new PlanNode
        {
            ItemId = 200,
            Name = "Intermediate",
            NodeId = "child",
            Parent = root
        };
        var grandchild = new PlanNode
        {
            ItemId = 300,
            Name = "Raw Material",
            NodeId = "grandchild",
            Parent = child
        };
        child.Children.Add(grandchild);
        root.Children.Add(child);

        var storedPlan = new StoredPlan
        {
            Id = "saved",
            Name = "Saved",
            PlanJson = System.Text.Json.JsonSerializer.Serialize(new CraftingPlan { RootItems = [root] })
        };

        var result = PlanSessionLoadService.Prepare(storedPlan);

        var restoredRoot = Assert.Single(result.Plan!.RootItems);
        var restoredChild = Assert.Single(restoredRoot.Children);
        var restoredGrandchild = Assert.Single(restoredChild.Children);
        Assert.Null(restoredRoot.Parent);
        Assert.Same(restoredRoot, restoredChild.Parent);
        Assert.Equal(restoredRoot.NodeId, restoredChild.ParentNodeId);
        Assert.Same(restoredChild, restoredGrandchild.Parent);
        Assert.Equal(restoredChild.NodeId, restoredGrandchild.ParentNodeId);
    }

    [Fact]
    public void PlanSessionLoadService_PrepareSession_UsesRecipeLayerWorkflowForMarketAnalysisValidation()
    {
        var plan = CreateSimplePlan();
        var storedPlan = new StoredPlan
        {
            Id = "saved",
            Name = "Saved",
            PlanJson = System.Text.Json.JsonSerializer.Serialize(plan),
            ProjectItems = [new StoredProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            MarketItemAnalysesJson = System.Text.Json.JsonSerializer.Serialize(new List<MarketItemAnalysis>
            {
                new() { ItemId = 200, Name = "Child", QuantityNeeded = 7 }
            }),
            MarketPlansJson = System.Text.Json.JsonSerializer.Serialize(new List<DetailedShoppingPlan>
            {
                new() { ItemId = 200, Name = "Child", QuantityNeeded = 7 }
            })
        };
        var appState = new AppState();
        var service = new PlanSessionLoadService(
            appState,
            new StubRecipeLayerWorkflowService(
            [
                new MaterialAggregate { ItemId = 200, Name = "Child", TotalQuantity = 7 }
            ]));

        var result = service.PrepareSession(storedPlan);

        Assert.Single(result.MarketItemAnalyses);
        Assert.Single(result.ShoppingPlans);
    }

    [Fact]
    public async Task LoadPlanIntoSessionAsync_ReferenceOnlyMarketSummaryHydratesFromStore()
    {
        var appState = new AppState();
        var publicationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var store = new InMemoryMarketIntelligenceStore();
        await store.SavePublicationAsync(new MarketIntelligencePublicationWrite(
            CreateSummary(publicationId),
            [],
            []));
        var jsRuntime = new RecordingJsRuntime
        {
            PlanToLoad = new StoredPlan
            {
                Id = "saved",
                Name = "Saved",
                ProjectItems = [new StoredProjectItem { Id = 100, Name = "Stored Item", Quantity = 4 }],
                ActiveMarketIntelligencePublicationId = publicationId
            }
        };
        var service = CreateService(jsRuntime, appState, store);

        var result = await service.LoadPlanIntoSessionAsync("saved");

        Assert.NotNull(result);
        Assert.Equal(publicationId, appState.MarketIntelligenceSummary?.PublicationId);
        Assert.Equal(100, Assert.Single(appState.MarketItemAnalyses).ItemId);
        Assert.Equal(100, Assert.Single(appState.ShoppingPlans).ItemId);
    }

    [Fact]
    public void PlanSessionLoadService_Prepare_MismatchedCompactSummaryReferenceWarnsAndSkipsSummary()
    {
        var activePublicationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var embeddedPublicationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var storedPlan = new StoredPlan
        {
            ProjectItems = [new StoredProjectItem { Id = 100, Name = "Stored Item", Quantity = 4 }],
            ActiveMarketIntelligencePublicationId = activePublicationId,
            MarketIntelligenceSummaryJson = System.Text.Json.JsonSerializer.Serialize(CreateSummary(embeddedPublicationId))
        };

        var result = PlanSessionLoadService.Prepare(storedPlan);

        Assert.Null(result.MarketIntelligenceSummary);
        Assert.Empty(result.MarketItemAnalyses);
        Assert.Empty(result.ShoppingPlans);
        Assert.Contains("active publication reference", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    private static WebPlanPersistenceService CreateService(
        RecordingJsRuntime jsRuntime,
        AppState? appState = null,
        IMarketIntelligenceStore? marketIntelligenceStore = null)
    {
        appState ??= new AppState();
        var indexedDb = new IndexedDbService(jsRuntime);
        return new WebPlanPersistenceService(
            indexedDb,
            new StoredPlanSnapshotBuilder(appState),
            new PlanSessionLoadService(appState, new StubRecipeLayerWorkflowService()),
            marketIntelligenceStore);
    }

    private static MarketIntelligencePublicationSummary CreateSummary(Guid publicationId)
    {
        return new MarketIntelligencePublicationSummary
        {
            PublicationId = publicationId,
            PublicationContext = new MarketIntelligencePublicationContext(
                MarketIntelligencePublicationContextKind.Known,
                MarketFetchScope.SelectedDataCenter,
                "Aether",
                "North America",
                ["Aether"],
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                null,
                false,
                RecommendationMode.MinimizeTotalCost,
                MarketAcquisitionLens.BulkValue,
                null,
                7,
                2,
                new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc)),
            Items =
            [
                new MarketItemSummary
                {
                    ItemId = 100,
                    Name = "Stored Item",
                    QuantityNeeded = 4,
                    RecommendedTotalCost = 400,
                    Worlds =
                    [
                        new WorldMarketSummary
                        {
                            World = new MarketWorldKey("Aether", "Siren"),
                            QuantityNeeded = 4,
                            CompetitiveQuantity = 4,
                            TotalListingQuantity = 4,
                            CompetitiveCoverageRatio = 1m,
                            CompetitiveAverageUnitPrice = 100,
                            CoverageBucket = MarketCoverageBucket.Full,
                            DataQualityBucket = MarketDataQualityBucket.Current
                        }
                    ]
                }
            ]
        };
    }

    private static CraftingPlan CreateSimplePlan()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root",
            NodeId = "root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true
        };
        var child = new PlanNode
        {
            ItemId = 200,
            Name = "Child",
            NodeId = "child",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            Parent = root
        };
        root.Children.Add(child);
        return new CraftingPlan { RootItems = [root] };
    }

    private sealed class StubRecipeLayerWorkflowService : IRecipeLayerWorkflowService
    {
        private readonly IReadOnlyList<MaterialAggregate> _marketCandidates;
        private readonly RecipeDemandProjectionService _projectionService = new();

        public StubRecipeLayerWorkflowService(IReadOnlyList<MaterialAggregate>? marketCandidates = null)
        {
            _marketCandidates = marketCandidates ?? [];
        }

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity()
        {
            return RecipeOperationSnapshotIdentity.Unspecified;
        }

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan)
        {
            return _projectionService.Build(plan, snapshot: null);
        }

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan)
        {
            return _marketCandidates.Count > 0
                ? _marketCandidates
                : BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();
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

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildActiveProcurementItems(plan));
        }
    }

    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public int LoadPlanCallCount { get; private set; }
        public StoredPlan? LastSavedPlan { get; private set; }
        public StoredPlan? PlanToLoad { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "IndexedDB.loadPlan")
            {
                LoadPlanCallCount++;
                return new ValueTask<TValue>((TValue)(object?)PlanToLoad!);
            }

            if (identifier == "IndexedDB.savePlan")
            {
                LastSavedPlan = Assert.IsType<StoredPlan>(Assert.Single(args ?? []));
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
