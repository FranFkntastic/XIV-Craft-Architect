using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class RecipePlannerCommandServiceTests
{
    [Fact]
    public async Task BuildPlanAsync_EmptyProjectItems_DoesNotMutateCurrentPlan()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(CreatePlan("Existing", 10));
        var builder = new FakeRecipePlanBuilder();
        var service = CreateService(appState, builder);

        var result = await service.BuildPlanAsync(new BuildRecipePlanRequest(
            [],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));

        Assert.False(result.Built);
        Assert.Equal("Existing", appState.CurrentPlan?.RootItems[0].Name);
        Assert.Null(result.SelectedNode);
        Assert.Equal(RecipePlannerCommandMessageLevel.Info, result.MessageLevel);
    }

    [Fact]
    public async Task BuildPlanAsync_SuccessfulBuildPublishesPlanAndClearsStaleMarketState()
    {
        var appState = new AppState();
        AddStaleMarketState(appState);
        var beforeVersions = appState.CurrentVersions;
        var builder = new FakeRecipePlanBuilder
        {
            PlanToReturn = CreatePlan("Built Item", 100, marketPrice: 12)
        };
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
                [(100, "Aether")] = CachedData(100, "Aether", 77)
            });
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;
        var service = CreateService(appState, builder, cache.Object);

        var result = await service.BuildPlanAsync(new BuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Built Item", Quantity = 2 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));

        Assert.True(result.Built);
        Assert.Equal(100, result.SelectedNode?.ItemId);
        Assert.Same(builder.PlanToReturn, appState.CurrentPlan);
        Assert.NotNull(appState.CurrentPlan);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.Equal(77, appState.CurrentPlan.RootItems[0].MarketPrice);
        Assert.Equal(1, appState.CurrentPlan.PriceVersion);
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.ShoppingItems));
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.Equal(beforeVersions.ProcurementOverlayVersion + 1, appState.CurrentVersions.ProcurementOverlayVersion);
        Assert.False(result.PriceRefresh.HasUnavailableItems);
        Assert.Equal(RecipePlannerCommandMessageLevel.Success, result.MessageLevel);
    }

    [Fact]
    public async Task BuildPlanAsync_WhenPriceRefreshDisabled_BuildsPlanWithoutMarketCache()
    {
        var appState = new AppState();
        var builder = new FakeRecipePlanBuilder
        {
            PlanToReturn = CreatePlan("Imported Item", 100)
        };
        var cache = new Mock<IMarketCacheService>(MockBehavior.Strict);
        var service = CreateService(appState, builder, cache.Object);

        var result = await service.BuildPlanAsync(new BuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Imported Item", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter,
            RefreshPrices: false));

        Assert.True(result.Built);
        Assert.Same(builder.PlanToReturn, appState.CurrentPlan);
        Assert.Equal(1, builder.BuildPlanCallCount);
        Assert.Equal(0, builder.FetchVendorCallCount);
        Assert.Equal(0, result.PriceRefresh.RequestedCount);
        Assert.Equal(RecipePlannerCommandMessageLevel.Success, result.MessageLevel);
        cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BuildPlanAsync_WhenPriceRefreshFails_ClearsStaleMarketStateForNewPlan()
    {
        var appState = new AppState();
        AddStaleMarketState(appState);
        var builder = new FakeRecipePlanBuilder
        {
            PlanToReturn = CreatePlan("Built Item", 100)
        };
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cache unavailable"));
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;
        var service = CreateService(appState, builder, cache.Object);

        var result = await service.BuildPlanAsync(new BuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Built Item", Quantity = 2 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));

        Assert.False(result.Built);
        Assert.Same(builder.PlanToReturn, appState.CurrentPlan);
        Assert.Equal(100, result.SelectedNode?.ItemId);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.Equal(RecipePlannerCommandMessageLevel.Error, result.MessageLevel);
    }

    [Fact]
    public async Task ImportProjectItemsAsync_ReplacesProjectItemsClearsPlanAndBuildsWithoutPriceRefresh()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(CreatePlan("Old Plan", 900));
        appState.TrackCurrentPlanIdentity("old-plan", "Old Plan");
        appState.ReplaceProjectItems([new ProjectItem { Id = 900, Name = "Old", Quantity = 1 }]);
        AddStaleMarketState(appState);
        var importedPlan = CreatePlan("Imported Item", 100);
        var builder = new FakeRecipePlanBuilder
        {
            PlanToReturn = importedPlan
        };
        var cache = new Mock<IMarketCacheService>(MockBehavior.Strict);
        var service = CreateService(appState, builder, cache.Object);

        var result = await service.ImportProjectItemsAsync(new ImportProjectItemsRequest(
            [
                new ProjectItem
                {
                    Id = 100,
                    Name = "Imported Item",
                    Quantity = 3,
                    IconId = 123,
                    MustBeHq = true
                }
            ],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));

        Assert.True(result.BuildResult.Built);
        Assert.Same(importedPlan, appState.CurrentPlan);
        var item = Assert.Single(appState.ProjectItems);
        Assert.Equal(100, item.Id);
        Assert.True(item.MustBeHq);
        Assert.Null(appState.CurrentPlanId);
        Assert.Null(appState.CurrentPlanName);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.Equal(1, builder.BuildPlanCallCount);
        Assert.Equal(0, builder.FetchVendorCallCount);
        cache.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BuildPlanAsync_WhenCanceled_ReturnsNeutralResultWithoutFailureStatus()
    {
        var appState = new AppState();
        var cancellation = new CancellationTokenSource();
        var builder = new FakeRecipePlanBuilder
        {
            BuildAsync = ct =>
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(CreatePlan("Canceled", 100));
            }
        };
        var service = CreateService(appState, builder);

        var result = await service.BuildPlanAsync(new BuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Canceled", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter), cancellation.Token);

        Assert.False(result.Built);
        Assert.Equal(RecipePlannerCommandMessageLevel.Info, result.MessageLevel);
        Assert.DoesNotContain("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Ready", appState.StatusMessage);
        Assert.False(appState.IsBusy);
    }

    [Fact]
    public async Task BuildPlanAsync_WhenCanceledDuringPriceRefresh_ReturnsBuiltResultForActivePlan()
    {
        var appState = new AppState();
        var cancellation = new CancellationTokenSource();
        var builtPlan = CreatePlan("Built Plan", 100);
        var builder = new FakeRecipePlanBuilder
        {
            PlanToReturn = builtPlan
        };
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => cancellation.Cancel())
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var service = CreateService(appState, builder, cache.Object);

        var result = await service.BuildPlanAsync(new BuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Built Plan", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter), cancellation.Token);

        Assert.True(result.Built);
        Assert.Same(builtPlan, appState.CurrentPlan);
        Assert.Equal(RecipePlannerCommandMessageLevel.Info, result.MessageLevel);
        Assert.DoesNotContain("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Ready", appState.StatusMessage);
        Assert.False(appState.IsBusy);
    }

    [Fact]
    public async Task BuildPlanAsync_WhenSamePlanSessionChangesDuringCanceledPriceRefresh_ReturnsNeutralResult()
    {
        var appState = new AppState();
        var cancellation = new CancellationTokenSource();
        var builtPlan = CreatePlan("Built Plan", 100);
        var builder = new FakeRecipePlanBuilder
        {
            PlanToReturn = builtPlan
        };
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                appState.ApplyBuiltRecipePlan(builtPlan);
                cancellation.Cancel();
            })
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var service = CreateService(appState, builder, cache.Object);

        var result = await service.BuildPlanAsync(new BuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Built Plan", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter), cancellation.Token);

        Assert.False(result.Built);
        Assert.Same(builtPlan, appState.CurrentPlan);
        Assert.Equal(RecipePlannerCommandMessageLevel.Info, result.MessageLevel);
        Assert.Equal("Plan build canceled.", result.Message);
        Assert.Equal("Ready", appState.StatusMessage);
        Assert.False(appState.IsBusy);
    }

    [Fact]
    public async Task BuildPlanAsync_WhenPlanChangesDuringPriceRefresh_ReturnsNeutralWithoutFollowUpPublish()
    {
        var appState = new AppState();
        var builtPlan = CreatePlan("Built Plan", 100);
        var replacementPlan = CreatePlan("Replacement Plan", 200);
        var builder = new FakeRecipePlanBuilder
        {
            PlanToReturn = builtPlan,
            FetchVendorAsync = _ =>
            {
                appState.ApplyBuiltRecipePlan(replacementPlan);
                return Task.CompletedTask;
            }
        };
        var service = CreateService(appState, builder);

        var result = await service.BuildPlanAsync(new BuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Built Plan", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));

        Assert.False(result.Built);
        Assert.Same(replacementPlan, appState.CurrentPlan);
        Assert.Equal(RecipePlannerCommandMessageLevel.Info, result.MessageLevel);
        Assert.DoesNotContain("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, builtPlan.PriceVersion);
        Assert.Empty(result.PriceRefresh.UnavailableItems);
    }

    [Fact]
    public async Task BuildPlanAsync_WhenSuperseded_DoesNotPublishOlderPlan()
    {
        var appState = new AppState();
        var olderPlan = CreatePlan("Older Plan", 100);
        var newerPlan = CreatePlan("Newer Plan", 200);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var builder = new FakeRecipePlanBuilder
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
        };
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>());
        var service = CreateService(appState, builder, cache.Object);

        var firstTask = service.BuildPlanAsync(new BuildRecipePlanRequest(
            [new ProjectItem { Id = 100, Name = "Older Plan", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));
        await firstStarted.Task;

        var secondResult = await service.BuildPlanAsync(new BuildRecipePlanRequest(
            [new ProjectItem { Id = 200, Name = "Newer Plan", Quantity = 1 }],
            "Aether",
            "North America",
            MarketFetchScope.SelectedDataCenter));
        releaseFirst.SetResult();
        var firstResult = await firstTask;

        Assert.True(secondResult.Built);
        Assert.False(firstResult.Built);
        Assert.Equal(RecipePlannerCommandMessageLevel.Info, firstResult.MessageLevel);
        Assert.Same(newerPlan, appState.CurrentPlan);
    }

    [Fact]
    public async Task RefreshPricesAsync_UpdatesNodePricesAndUnavailableItems()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(CreatePlan("Refresh Item", 100));
        appState.CurrentPlan!.RootItems.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Unavailable Item",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true
        });
        var beforePlanStructureVersion = appState.CurrentVersions.PlanStructureVersion;
        var builder = new FakeRecipePlanBuilder();
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
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;
        var service = CreateService(appState, builder, cache.Object);

        var result = await service.RefreshPricesAsync(new RefreshRecipePlanPricesRequest(
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America"));

        Assert.Equal(88, appState.CurrentPlan.RootItems[0].MarketPrice);
        Assert.Equal(1, appState.CurrentPlan.PriceVersion);
        Assert.Equal(2, result.RequestedCount);
        Assert.Equal(1, result.FetchedCount);
        Assert.Equal(200, Assert.Single(result.UnavailableItems).ItemId);
        Assert.Equal(200, Assert.Single(appState.UnavailableMarketItems).ItemId);
        Assert.Equal(1, appState.CurrentVersions.PlanPriceVersion);
        Assert.Equal(beforePlanStructureVersion, appState.CurrentVersions.PlanStructureVersion);
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.PlanPrice));
        Assert.DoesNotContain(changes, change => change.HasScope(AppStateChangeScope.PlanStructure));
    }

    [Fact]
    public async Task RefreshPricesAsync_WhenPlanChangesDuringFetch_DoesNotMutateNewPlan()
    {
        var oldPlan = CreatePlan("Old Plan", 100);
        var newPlan = CreatePlan("New Plan", 200);
        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(oldPlan);
        var builder = new FakeRecipePlanBuilder
        {
            FetchVendorAsync = _ =>
            {
                appState.ApplyBuiltRecipePlan(newPlan);
                return Task.CompletedTask;
            }
        };
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
                [(200, "Aether")] = CachedData(200, "Aether", 99)
            });
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;
        var service = CreateService(appState, builder, cache.Object);

        var result = await service.RefreshPricesAsync(new RefreshRecipePlanPricesRequest(
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America"));

        Assert.Equal(0, newPlan.RootItems[0].MarketPrice);
        Assert.Equal(0, newPlan.PriceVersion);
        Assert.Equal(0, result.RequestedCount);
        Assert.Equal(0, appState.CurrentVersions.PlanPriceVersion);
        Assert.DoesNotContain(changes, change => change.HasScope(AppStateChangeScope.PlanPrice));
    }

    [Fact]
    public void ApplyPlanEditorEdit_UpdatesSelectedNodesAndPublishesDecisionShoppingAndOverlayScopes()
    {
        var child = new PlanNode
        {
            ItemId = 200,
            Name = "Child Material",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            CanBeHq = true
        };
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = false,
            Children = [child]
        };
        var appState = new AppState();
        appState.ApplyBuiltRecipePlan(new CraftingPlan { RootItems = [root] });
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 999, Name = "Old Route" }]);
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;
        var service = CreateService(appState, new FakeRecipePlanBuilder());

        var result = service.ApplyPlanEditorEdit(new ApplyPlanEditorEditRequest(
            [child.NodeId],
            new PlanBulkEditOptions { Quality = BulkQualitySetting.RequireHq },
            RequireHqMaterials: false));

        Assert.Equal(1, result.EditResult.ChangedNodes);
        Assert.Equal(AcquisitionSource.MarketBuyHq, child.Source);
        Assert.True(child.MustBeHq);
        var shoppingItem = Assert.Single(appState.ShoppingItems);
        Assert.Equal(200, shoppingItem.Id);
        Assert.Equal(3, shoppingItem.Quantity);
        Assert.Empty(appState.ProcurementShoppingPlans);
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.PlanDecision));
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.ShoppingItems));
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.DoesNotContain(changes, change => change.HasScope(AppStateChangeScope.MarketAnalysis));
    }

    [Fact]
    public async Task ActivatePlanAsync_VendorOnlyRefresh_ActivatesPlanClearsStaleStateAndPublishesPriceScope()
    {
        var child = new PlanNode
        {
            ItemId = 200,
            Name = "Child Material",
            Quantity = 4,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true
        };
        var plan = new CraftingPlan
        {
            Name = "Imported Plan",
            DataCenter = "Crystal",
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 100,
                    Name = "Root Craft",
                    Quantity = 2,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true,
                    Children = [child]
                }
            ]
        };
        var appState = new AppState();
        appState.TrackCurrentPlanIdentity("saved-plan", null);
        AddStaleMarketState(appState);
        var builder = new FakeRecipePlanBuilder();
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>());
        var changes = new List<AppStateChange>();
        appState.OnStateChanged += changes.Add;
        var service = CreateService(appState, builder, cache.Object);

        var result = await service.ActivatePlanAsync(new ActivateRecipePlanRequest(
            plan,
            ClearCurrentPlanId: true,
            RefreshVendorPrices: true,
            RefreshMarketPrices: false,
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America"));

        Assert.Same(plan, appState.CurrentPlan);
        Assert.Null(appState.CurrentPlanId);
        Assert.Equal("Crystal", appState.SelectedDataCenter);
        Assert.Equal(100, result.SelectedNode?.ItemId);
        Assert.Equal(100, Assert.Single(appState.ProjectItems).Id);
        Assert.Empty(appState.ShoppingPlans);
        Assert.Empty(appState.MarketItemAnalyses);
        Assert.Empty(appState.ProcurementShoppingPlans);
        var shoppingItem = Assert.Single(appState.ShoppingItems);
        Assert.Equal(200, shoppingItem.Id);
        Assert.Equal(4, shoppingItem.Quantity);
        Assert.Equal(1, builder.FetchVendorCallCount);
        Assert.Equal(1, plan.PriceVersion);
        Assert.Equal(RecipePlannerCommandMessageLevel.Success, result.MessageLevel);
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.PlanStructure));
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.MarketAnalysis));
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.ProcurementOverlay));
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.ShoppingItems));
        Assert.Contains(changes, change => change.HasScope(AppStateChangeScope.PlanPrice));
        Assert.Equal(1, appState.CurrentVersions.PlanPriceVersion);
    }

    [Fact]
    public async Task ActivatePlanAsync_MarketRefreshUsesImportedPlanDataCenter()
    {
        var plan = CreatePlan("Imported Plan", 100);
        plan.DataCenter = "Crystal";
        var appState = new AppState();
        var builder = new FakeRecipePlanBuilder();
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.Is<List<(int itemId, string dataCenter)>>(requests =>
                    requests.Count == 1 && requests[0].itemId == 100 && requests[0].dataCenter == "Crystal"),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        cache.Setup(c => c.GetManyAsync(
                It.Is<IReadOnlyCollection<(int itemId, string dataCenter)>>(requests =>
                    requests.Count == 1 && requests.Any(request =>
                        request.itemId == 100 && request.dataCenter == "Crystal")),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(100, "Crystal")] = CachedData(100, "Crystal", 55)
            });
        var service = CreateService(appState, builder, cache.Object);

        var result = await service.ActivatePlanAsync(new ActivateRecipePlanRequest(
            plan,
            ClearCurrentPlanId: true,
            RefreshVendorPrices: true,
            RefreshMarketPrices: true,
            MarketFetchScope.SelectedDataCenter,
            SelectedDataCenter: "Aether",
            SelectedRegion: "North America"));

        Assert.Equal("Crystal", appState.SelectedDataCenter);
        Assert.Equal(55, plan.RootItems[0].MarketPrice);
        Assert.Equal(1, result.PriceRefresh.FetchedCount);
    }

    [Fact]
    public async Task ActivatePlanAsync_WhenPriceRefreshCanceled_LeavesPlanActiveAndReturnsNeutralResult()
    {
        var plan = CreatePlan("Imported Plan", 100);
        var appState = new AppState();
        var cancellation = new CancellationTokenSource();
        var builder = new FakeRecipePlanBuilder
        {
            FetchVendorAsync = ct =>
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };
        var service = CreateService(appState, builder);

        var result = await service.ActivatePlanAsync(new ActivateRecipePlanRequest(
            plan,
            ClearCurrentPlanId: true,
            RefreshVendorPrices: true,
            RefreshMarketPrices: false,
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America"), cancellation.Token);

        Assert.Same(plan, appState.CurrentPlan);
        Assert.Equal(RecipePlannerCommandMessageLevel.Info, result.MessageLevel);
        Assert.DoesNotContain("failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Ready", appState.StatusMessage);
        Assert.False(appState.IsBusy);
    }

    [Fact]
    public async Task ActivatePlanAsync_WhenPlanChangesDuringMarketRefresh_ReturnsNeutralWithoutSuccess()
    {
        var activatedPlan = CreatePlan("Activated Plan", 100);
        var replacementPlan = CreatePlan("Replacement Plan", 200);
        var appState = new AppState();
        var builder = new FakeRecipePlanBuilder
        {
            FetchVendorAsync = _ =>
            {
                appState.ApplyBuiltRecipePlan(replacementPlan);
                return Task.CompletedTask;
            }
        };
        var service = CreateService(appState, builder);

        var result = await service.ActivatePlanAsync(new ActivateRecipePlanRequest(
            activatedPlan,
            ClearCurrentPlanId: true,
            RefreshVendorPrices: true,
            RefreshMarketPrices: true,
            MarketFetchScope.SelectedDataCenter,
            "Aether",
            "North America"));

        Assert.Same(replacementPlan, appState.CurrentPlan);
        Assert.Equal(RecipePlannerCommandMessageLevel.Info, result.MessageLevel);
        Assert.Contains("canceled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, activatedPlan.PriceVersion);
        Assert.Empty(result.PriceRefresh.UnavailableItems);
    }

    private static RecipePlannerCommandService CreateService(
        AppState appState,
        IRecipePlanBuilder builder,
        IMarketCacheService? marketCache = null)
    {
        return new RecipePlannerCommandService(
            appState,
            builder,
            marketCache ?? Mock.Of<IMarketCacheService>(),
            new CancellableOperationService(appState));
    }

    private static CraftingPlan CreatePlan(string name, int itemId, decimal marketPrice = 0)
    {
        return new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = itemId,
                    Name = name,
                    Quantity = 1,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanBuyFromMarket = true,
                    MarketPrice = marketPrice
                }
            ]
        };
    }

    private static void AddStaleMarketState(AppState appState)
    {
        appState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 900, Name = "Old Analysis" }],
            [new DetailedShoppingPlan { ItemId = 900, Name = "Old Plan" }]);
        appState.ReplaceProcurementOverlay([new DetailedShoppingPlan { ItemId = 900, Name = "Old Route" }]);
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

    private sealed class FakeRecipePlanBuilder : IRecipePlanBuilder
    {
        public CraftingPlan PlanToReturn { get; set; } = CreatePlan("Built", 1);
        public int BuildPlanCallCount { get; private set; }
        public int FetchVendorCallCount { get; private set; }
        public Func<CancellationToken, Task<CraftingPlan>>? BuildAsync { get; set; }
        public Func<CancellationToken, Task>? FetchVendorAsync { get; set; }

        public Task<CraftingPlan> BuildPlanAsync(
            List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
            string dataCenter,
            string world,
            CancellationToken ct = default)
        {
            BuildPlanCallCount++;
            if (BuildAsync != null)
            {
                return BuildAsync(ct);
            }

            return Task.FromResult(PlanToReturn);
        }

        public Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default)
        {
            FetchVendorCallCount++;
            if (FetchVendorAsync != null)
            {
                return FetchVendorAsync(ct);
            }

            return Task.CompletedTask;
        }
    }
}
