using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
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
    public async Task SaveCurrentPlanAsync_WritesFullMarketAnalysisStateWithoutCompactSummary()
    {
        var jsRuntime = new RecordingJsRuntime();
        var appState = new AppState();
        appState.ReplaceProjectItems([new ProjectItem { Id = 100, Name = "Saved Item", Quantity = 12 }]);
        appState.ReplaceMarketAnalysis(
            [
                new MarketItemAnalysis
                {
                    ItemId = 200,
                    Name = "Saved Material",
                    QuantityNeeded = 7
                }
            ],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 200,
                    Name = "Saved Material",
                    QuantityNeeded = 7
                }
            ],
            publishedScope: new PublishedMarketAnalysisScopeSnapshot(
                MarketFetchScope.SelectedDataCenter,
                "Aether",
                "North America",
                ["Aether"],
                MarketAcquisitionLens.MinimumUpfrontCost,
                1,
                new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc)));
        var service = CreateService(jsRuntime, appState);

        var saved = await service.SaveCurrentPlanAsync(
            "plan-id",
            "Saved Plan",
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        Assert.True(saved);
        Assert.NotNull(jsRuntime.LastSavedPlan);
        Assert.NotNull(jsRuntime.LastSavedPlan.MarketItemAnalysesJson);
        Assert.NotNull(jsRuntime.LastSavedPlan.MarketPlansJson);
        Assert.NotNull(jsRuntime.LastSavedPlan.MarketIntelligenceJson);
        var serialized = System.Text.Json.JsonSerializer.Serialize(jsRuntime.LastSavedPlan);
        Assert.DoesNotContain("MarketIntelligenceSummary", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ActiveMarketIntelligencePublicationId", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveGeneratedOrderPlanAsync_WritesLeanSavedPlanWithoutMarketAnalysisPayloads()
    {
        var jsRuntime = new RecordingJsRuntime();
        var service = CreateService(jsRuntime);
        var savedAt = new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc);

        var saved = await service.SaveGeneratedOrderPlanAsync(
            "order-plan-id",
            "Order - Cobalt Ingot Commission",
            new CraftingPlan
            {
                DataCenter = "Aether",
                RootItems =
                [
                    new PlanNode
                    {
                        ItemId = 100,
                        Name = "Cobalt Ingot",
                        NodeId = "root",
                        Quantity = 999
                    }
                ]
            },
            [
                new TradeOrderRootItemSnapshot(
                    100,
                    "Cobalt Ingot",
                    999,
                    MustBeHq: false,
                    EstimatedSaleValue: 1_000_000m)
            ],
            savedAt);

        Assert.True(saved);
        Assert.NotNull(jsRuntime.LastSavedPlan);
        Assert.Equal("order-plan-id", jsRuntime.LastSavedPlan.Id);
        Assert.Equal("Order - Cobalt Ingot Commission", jsRuntime.LastSavedPlan.Name);
        Assert.Equal("Aether", jsRuntime.LastSavedPlan.DataCenter);
        Assert.Equal(savedAt, jsRuntime.LastSavedPlan.SavedAt);
        var projectItem = Assert.Single(jsRuntime.LastSavedPlan.ProjectItems);
        Assert.Equal(100, projectItem.Id);
        Assert.Equal("Cobalt Ingot", projectItem.Name);
        Assert.Equal(999, projectItem.Quantity);
        Assert.NotNull(jsRuntime.LastSavedPlan.PlanJson);
        Assert.Null(jsRuntime.LastSavedPlan.MarketPlansJson);
        Assert.Null(jsRuntime.LastSavedPlan.MarketIntelligenceJson);
        Assert.Null(jsRuntime.LastSavedPlan.MarketItemAnalysesJson);
        Assert.Null(jsRuntime.LastSavedPlan.MarketAnalysisRecipeBasisJson);
        Assert.Null(jsRuntime.LastSavedPlan.MarketAnalysisScopeSnapshotJson);
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

    private static WebPlanPersistenceService CreateService(RecordingJsRuntime jsRuntime, AppState? appState = null)
    {
        appState ??= new AppState();
        var indexedDb = new IndexedDbService(jsRuntime);
        return new WebPlanPersistenceService(
            indexedDb,
            new StoredPlanSnapshotBuilder(appState),
            new PlanSessionLoadService(appState, new StubRecipeLayerWorkflowService()));
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

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "IndexedDB.loadPlan")
            {
                LoadPlanCallCount++;
                return new ValueTask<TValue>((TValue)(object?)null!);
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
