using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class CoreRecipePlannerCommandServiceTests
{
    [Fact]
    public async Task BuildPlanAsync_WithNoProjectItems_ReturnsInfoWithoutActivatingPlan()
    {
        var service = CreateService();

        var result = await service.BuildPlanAsync(new CoreBuildRecipePlanRequest(
            [],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));

        Assert.False(result.Built);
        Assert.Equal("No project items to build.", result.Message);
        Assert.Equal(CoreRecipePlannerCommandMessageLevel.Info, result.MessageLevel);
        Assert.Null(service.Session.ActivePlan);
    }

    [Fact]
    public async Task BuildPlanAsync_ActivatesBuiltPlanAndProjectItemsAfterPriceRefresh()
    {
        var plan = CreatePlan();
        var projectItems = new[]
        {
            new ProjectItem { Id = 100, Name = "Root", Quantity = 2, MustBeHq = true }
        };
        var builder = new FakeRecipePlanBuilder { PlanToBuild = plan };
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(100, "Aether")] = CachedData(100, "Aether", 88)
            });
        var service = CreateService(builder: builder, marketCache: cache.Object);

        var result = await service.BuildPlanAsync(new CoreBuildRecipePlanRequest(
            projectItems,
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));

        Assert.True(result.Built);
        Assert.Equal(CoreRecipePlannerCommandMessageLevel.Success, result.MessageLevel);
        Assert.Equal("Plan built! Prices fetched. Go to Procurement Planner to run market analysis.", result.Message);
        Assert.Equal("Aether", builder.RequestedDataCenter);
        Assert.Empty(builder.RequestedWorld);
        Assert.Equal(100, service.Session.ActivePlan?.RootItems.Single().ItemId);
        Assert.Single(service.Session.ProjectItems);
        Assert.Equal("Root", service.Session.ProjectItems[0].Name);
        Assert.True(service.Session.IsDirty(CraftSessionDirtyBucket.PlanCore));
    }

    [Fact]
    public async Task BuildPlanAsync_WithPriceRefresh_UpdatesPlanPricesAndUnavailableItems()
    {
        var plan = CreatePlan();
        plan.RootItems.Add(new PlanNode
        {
            NodeId = "missing",
            ItemId = 200,
            Name = "Unavailable Item",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true
        });
        var builder = new FakeRecipePlanBuilder { PlanToBuild = plan };
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(100, "Aether")] = CachedData(100, "Aether", 88)
            });
        var service = CreateService(builder: builder, marketCache: cache.Object);

        var result = await service.BuildPlanAsync(new CoreBuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));

        Assert.True(result.Built);
        Assert.Equal(CoreRecipePlannerCommandMessageLevel.Warning, result.MessageLevel);
        Assert.Equal(2, result.PriceRefresh.RequestedCount);
        Assert.Equal(1, result.PriceRefresh.FetchedCount);
        Assert.Equal(200, Assert.Single(result.PriceRefresh.UnavailableItems).ItemId);
        Assert.Equal(88, service.Session.ActivePlan?.RootItems[0].MarketPrice);
        Assert.Equal(1, service.Session.ActivePlan?.PriceVersion);
        Assert.Contains(200, service.Session.MarketEvidence.UnavailableMarketItemIds);
        Assert.Equal(1, service.Session.Versions.PlanPrice);
        Assert.Equal(2, service.Session.Versions.MarketAnalysis);
    }

    [Fact]
    public async Task BuildPlanAsync_ClearsStaleMarketEvidenceForNewPlan()
    {
        var cache = CreateMarketCache(100);
        var service = CreateService(marketCache: cache.Object);
        service.Session.PublishMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 900, Name = "Old Analysis" }],
            [900],
            "old analysis");

        var result = await service.BuildPlanAsync(new CoreBuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));

        Assert.True(result.Built);
        Assert.Empty(service.Session.MarketEvidence.ItemAnalyses);
        Assert.Empty(service.Session.MarketEvidence.UnavailableMarketItemIds);
        Assert.True(service.Session.IsDirty(CraftSessionDirtyBucket.MarketAnalysis));
    }



    [Fact]
    public async Task BuildPlanAsync_WhenBuilderThrows_ClearsOperationBusyState()
    {
        var service = CreateService(builder: new FakeRecipePlanBuilder
        {
            BuildAsync = _ => throw new InvalidOperationException("builder broke")
        });

        var result = await service.BuildPlanAsync(new CoreBuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));

        Assert.False(result.Built);
        Assert.Equal(CoreRecipePlannerCommandMessageLevel.Error, result.MessageLevel);
        Assert.False(service.OperationState.Snapshot().IsBusy);
    }
    [Fact]
    public async Task BuildPlanAsync_WhenSuperseded_DoesNotPublishOlderPlan()
    {
        var olderPlan = CreatePlan();
        olderPlan.RootItems[0].Name = "Older";
        var newerPlan = CreatePlan();
        newerPlan.RootItems[0].ItemId = 200;
        newerPlan.RootItems[0].Name = "Newer";
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var cache = CreateMarketCache(100, 200);
        var service = CreateService(
            builder: new FakeRecipePlanBuilder
            {
                BuildAsync = async ct =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        firstStarted.SetResult();
                        await releaseFirst.Task;
                        ct.ThrowIfCancellationRequested();
                        return olderPlan;
                    }

                    return newerPlan;
                }
            },
            marketCache: cache.Object);

        var firstTask = service.BuildPlanAsync(new CoreBuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Older", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));
        await firstStarted.Task;

        var secondResult = await service.BuildPlanAsync(new CoreBuildRecipePlanRequest(
            [new ProjectItem { Id = 200, Name = "Newer", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));
        releaseFirst.SetResult();
        var firstResult = await firstTask;

        Assert.True(secondResult.Built);
        Assert.False(firstResult.Built);
        Assert.Equal(200, service.Session.ActivePlan?.RootItems[0].ItemId);
    }
    [Fact]
    public void ApplyPlanEditorEdit_WithActivePlan_PublishesDecisionChangeWithoutNewPlanSession()
    {
        var plan = CreatePlan();
        plan.RootItems[0].CanBeHq = true;
        plan.RootItems[0].CanBuyFromMarket = true;
        plan.RootItems[0].Source = AcquisitionSource.MarketBuyNq;
        var service = CreateService();
        service.Session.ActivatePlan(
            plan,
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 2 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "test plan");
        var before = service.Session.CaptureVersionStamp();

        var result = service.ApplyPlanEditorEdit(new CoreApplyPlanEditorEditRequest(
            ["root"],
            new PlanBulkEditOptions { Quality = BulkQualitySetting.RequireHq },
            RequireHqMaterials: false));

        Assert.Equal(1, result.EditResult.ChangedNodes);
        Assert.Equal(before.PlanSession, service.Session.PlanSessionVersion);
        Assert.Equal(before.PlanDecision + 1, service.Session.Versions.PlanDecision);
        Assert.Equal(before.PlanCore, service.Session.Versions.PlanCore);
        Assert.True(service.Session.ActivePlan?.RootItems[0].MustBeHq);
        Assert.Equal(AcquisitionSource.MarketBuyHq, service.Session.ActivePlan?.RootItems[0].Source);
    }
    [Fact]
    public async Task RefreshPricesAsync_WhenPlanChangesDuringFetch_DoesNotMutateReplacementPlan()
    {
        var oldPlan = CreatePlan();
        var replacementPlan = CreatePlan();
        replacementPlan.RootItems[0].ItemId = 300;
        replacementPlan.RootItems[0].Name = "Replacement";
        TestServiceHost? serviceHost = null;
        var service = CreateService(builder: new FakeRecipePlanBuilder
        {
            FetchVendorAsync = _ =>
            {
                serviceHost!.Session.ActivatePlan(
                    replacementPlan,
                    [new ProjectItem { Id = 300, Name = "Replacement", Quantity = 1 }],
                    new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
                    "replacement plan");
                return Task.CompletedTask;
            }
        });
        service.Session.ActivatePlan(
            oldPlan,
            [new ProjectItem { Id = 100, Name = "Root", Quantity = 1 }],
            new CraftSessionActiveContext("North America", "Aether", string.Empty, MarketFetchScope.SelectedDataCenter),
            "old plan");
        serviceHost = service;

        var result = await service.RefreshPricesAsync(new CoreRefreshRecipePlanPricesRequest(
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America"));

        Assert.Equal(CoreMarketPriceRefreshStatus.StalePlan, result.Status);
        Assert.Equal(0, result.RequestedCount);
        Assert.Equal(300, service.Session.ActivePlan?.RootItems[0].ItemId);
        Assert.Equal(0, service.Session.ActivePlan?.PriceVersion);
        Assert.Equal(0, service.Session.Versions.PlanPrice);
    }






    [Fact]
    public async Task ActivatePlanAsync_WithSavedMarketPlans_RestoresCoreMarketEvidence()
    {
        var plan = CreatePlan();
        plan.SavedMarketPlans.Add(CreateShoppingPlan(200));
        var service = CreateService();

        var result = await service.ActivatePlanAsync(new CoreActivateRecipePlanRequest(
            plan,
            ClearCurrentPlanId: true,
            RefreshVendorPricesOnActivation: false,
            RefreshMarketPricesOnActivation: false,
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America"));

        Assert.Equal(CoreRecipePlannerCommandMessageLevel.Success, result.MessageLevel);
        Assert.Empty(service.Session.MarketEvidence.ItemAnalyses);
        var savedPlan = Assert.Single(service.Session.MarketEvidence.ShoppingPlans!);
        Assert.Equal(200, savedPlan.ItemId);
        Assert.Equal(service.Session.PlanSessionVersion, service.Session.MarketEvidence.PublishedAgainstVersion?.PlanSession);
        Assert.True(service.Session.IsDirty(CraftSessionDirtyBucket.MarketAnalysis));
    }

    [Fact]
    public async Task ActivatePlanAsync_WhenClearCurrentPlanId_ClearsSessionSourceIdentity()
    {
        var plan = CreatePlan();
        var service = CreateService();
        service.Session.ReplaceIdentity(new CraftSessionIdentity(
            Guid.NewGuid(),
            "Saved Plan",
            SourcePlanId: "saved-plan",
            SourcePlanName: "Saved Plan"));

        var result = await service.ActivatePlanAsync(new CoreActivateRecipePlanRequest(
            plan,
            ClearCurrentPlanId: true,
            RefreshVendorPricesOnActivation: false,
            RefreshMarketPricesOnActivation: false,
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America"));

        Assert.Equal(CoreRecipePlannerCommandMessageLevel.Success, result.MessageLevel);
        Assert.Null(service.Session.Identity.SourcePlanId);
        Assert.Null(service.Session.Identity.SourcePlanName);
    }

    private static TestServiceHost CreateService(
        FakeRecipePlanBuilder? builder = null,
        FakeRecipeLayerWorkflowService? recipeLayerWorkflow = null,
        IMarketCacheService? marketCache = null)
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var operationState = new CraftOperationState();
        var operationCoordinator = new CraftOperationCoordinator(session, operationState);
        var service = new CoreRecipePlannerCommandService(
            session,
            builder ?? new FakeRecipePlanBuilder(),
            marketCache ?? Mock.Of<IMarketCacheService>(),
            recipeLayerWorkflow ?? new FakeRecipeLayerWorkflowService(),
            operationCoordinator);
        return new TestServiceHost(service, session, operationState);
    }

    private static CraftingPlan CreatePlan()
    {
        return new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    NodeId = "root",
                    ItemId = 100,
                    Name = "Root",
                    Quantity = 2,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true
                }
            ]
        };
    }

    private static Mock<IMarketCacheService> CreateMarketCache(params int[] availableItemIds)
    {
        var available = availableItemIds.ToHashSet();
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                List<(int itemId, string dataCenter)> requests,
                TimeSpan? maxAge,
                IProgress<string>? progress,
                CancellationToken ct) =>
                requests.Count(request => available.Contains(request.itemId)));
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync((IReadOnlyCollection<(int itemId, string dataCenter)> requests, TimeSpan? _) =>
                requests
                    .Where(request => available.Contains(request.itemId))
                    .ToDictionary(
                        request => request,
                        request => CachedData(request.itemId, request.dataCenter, 88)));

        return cache;
    }

    private static DetailedShoppingPlan CreateShoppingPlan(int itemId) =>
        new()
        {
            ItemId = itemId,
            Name = "Saved Market Plan",
            QuantityNeeded = 2,
            RecommendedWorld = new WorldShoppingSummary
            {
                WorldName = "Siren",
                TotalCost = 500,
                TotalQuantityPurchased = 2
            }
        };

    private sealed record TestServiceHost(
        CoreRecipePlannerCommandService Service,
        CraftSessionState Session,
        CraftOperationState OperationState)
    {
        public Task<CoreBuildRecipePlanResult> BuildPlanAsync(CoreBuildRecipePlanRequest request) =>
            Service.BuildPlanAsync(request);

        public Task<CoreImportProjectItemsResult> ImportProjectItemsAsync(CoreImportProjectItemsRequest request) =>
            Service.ImportProjectItemsAsync(request);

        public Task<CoreMarketPriceRefreshResult> RefreshPricesAsync(CoreRefreshRecipePlanPricesRequest request) =>
            Service.RefreshPricesAsync(request);

        public Task<CoreActivateRecipePlanResult> ActivatePlanAsync(CoreActivateRecipePlanRequest request) =>
            Service.ActivatePlanAsync(request);

        public CoreApplyPlanEditorEditResult ApplyPlanEditorEdit(CoreApplyPlanEditorEditRequest request) =>
            Service.ApplyPlanEditorEdit(request);
    }

    private sealed class FakeRecipePlanBuilder : ICoreRecipePlanBuilder
    {
        public CraftingPlan PlanToBuild { get; set; } = CreatePlan();
        public int BuildCalls { get; private set; }
        public int FetchVendorCalls { get; private set; }
        public string RequestedDataCenter { get; private set; } = string.Empty;
        public string RequestedWorld { get; private set; } = "unset";
        public Func<CancellationToken, Task<CraftingPlan>>? BuildAsync { get; init; }
        public Func<CancellationToken, Task>? FetchVendorAsync { get; init; }

        public Task<CraftingPlan> BuildPlanAsync(
            List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
            string dataCenter,
            string world,
            CancellationToken ct = default)
        {
            BuildCalls++;
            RequestedDataCenter = dataCenter;
            RequestedWorld = world;
            if (BuildAsync != null)
            {
                return BuildAsync(ct);
            }

            return Task.FromResult(PlanToBuild);
        }

        public Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default)
        {
            FetchVendorCalls++;
            if (FetchVendorAsync != null)
            {
                return FetchVendorAsync(ct);
            }

            return Task.CompletedTask;
        }
    }

    private static CachedMarketData CachedData(int itemId, string dataCenter, decimal averagePrice)
    {
        return new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            DCAveragePrice = averagePrice,
            FetchedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Worlds =
            [
                new CachedWorldData
                {
                    WorldName = "Siren",
                    Listings =
                    [
                        new CachedListing
                        {
                            Quantity = 10,
                            PricePerUnit = (long)averagePrice,
                            RetainerName = "Retainer"
                        }
                    ]
                }
            ]
        };
    }

    private sealed class FakeRecipeLayerWorkflowService : ICoreRecipeLayerWorkflowService
    {
        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity() => RecipeOperationSnapshotIdentity.Unspecified;

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan) => new([], [], [], []);

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan) => [];

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan) => [];

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RecipeDemandProjection?>(new RecipeDemandProjection([], [], [], []));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>([]);

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>([]);
    }
}
