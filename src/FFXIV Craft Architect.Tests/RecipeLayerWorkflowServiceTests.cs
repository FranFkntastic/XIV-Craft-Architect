using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class RecipeLayerWorkflowServiceTests
{
    [Fact]
    public void CreateSnapshotIdentity_UsesCurrentAppStateVersions()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(CreatePlan());
        appState.NotifyPlanDecisionChanged();
        appState.NotifyPlanPriceChanged();
        appState.SetProcurementSettings(
            searchEntireRegion: true,
            enableSplitWorldPurchases: true,
            travelTolerance: 5,
            temporaryWorldBlacklistDurationMinutes: 60);
        var service = CreateService(appState);

        var identity = service.CreateSnapshotIdentity();

        Assert.Equal(appState.PlanSessionVersion, identity.PlanSessionVersion);
        Assert.Equal(appState.CurrentVersions.PlanStructureVersion, identity.PlanStructureVersion);
        Assert.Equal(appState.CurrentVersions.PlanDecisionVersion, identity.PlanDecisionVersion);
        Assert.Equal(appState.CurrentVersions.PlanPriceVersion, identity.PlanPriceVersion);
        Assert.Equal(appState.CurrentVersions.SettingsVersion, identity.SettingsVersion);
        Assert.Equal("garland-current", identity.RecipeDataIdentity);
    }

    [Fact]
    public async Task BuildCurrentDemandProjectionAsync_UsesLifecycleSnapshotForDemandQuantities()
    {
        var appState = new AppState();
        var plan = CreatePlan();
        appState.ApplyBuiltRecipePlanWithActiveItems(plan);
        var snapshot = CreateSnapshot(plan, expectedChildQuantity: 5);
        var service = CreateService(appState, new StubSnapshotLifecycleService(snapshot));

        var projection = await service.BuildCurrentDemandProjectionAsync(plan);

        Assert.NotNull(projection);
        var childDemand = Assert.Single(projection.ActiveProcurementDemand);
        Assert.Equal("child", childDemand.NodeId);
        Assert.Equal(5, childDemand.Quantity);
        Assert.Equal(RecipeDemandQuantityBasis.RecipeExpectedQuantity, childDemand.QuantityBasis);
    }

    [Fact]
    public void BuildActiveProcurementItems_CentralizesSynchronousActiveDemandAggregation()
    {
        var appState = new AppState();
        var plan = CreatePlan();
        appState.ApplyBuiltRecipePlanWithActiveItems(plan);
        var service = CreateService(appState);

        var items = service.BuildActiveProcurementItems(plan);

        var item = Assert.Single(items);
        Assert.Equal(200, item.ItemId);
        Assert.Equal("Child", item.Name);
        Assert.Equal(2, item.TotalQuantity);
    }

    [Fact]
    public async Task BuildCurrentActiveProcurementItemsAsync_UsesLifecycleSnapshotForQuantities()
    {
        var appState = new AppState();
        var plan = CreatePlan();
        appState.ApplyBuiltRecipePlanWithActiveItems(plan);
        var snapshot = CreateSnapshot(plan, expectedChildQuantity: 5);
        var service = CreateService(appState, new StubSnapshotLifecycleService(snapshot));

        var items = await service.BuildCurrentActiveProcurementItemsAsync(plan);

        Assert.NotNull(items);
        var item = Assert.Single(items);
        Assert.Equal(200, item.ItemId);
        Assert.Equal(5, item.TotalQuantity);
    }

    [Fact]
    public async Task BuildCurrentMarketAnalysisCandidatesAsync_WhenPlanChangesDuringSnapshotBuild_ReturnsNull()
    {
        var appState = new AppState();
        var originalPlan = CreatePlan();
        var replacementPlan = CreatePlan(rootItemId: 300, childItemId: 400);
        appState.ApplyBuiltRecipePlanWithActiveItems(originalPlan);
        var snapshot = CreateSnapshot(originalPlan, expectedChildQuantity: 5);
        var lifecycle = new StubSnapshotLifecycleService(
            snapshot,
            beforeCurrentCheck: () => appState.ApplyBuiltRecipePlanWithActiveItems(replacementPlan));
        var service = CreateService(appState, lifecycle);

        var items = await service.BuildCurrentMarketAnalysisCandidatesAsync(originalPlan);

        Assert.Null(items);
    }

    [Fact]
    public async Task BuildCurrentDemandProjectionAsync_WhenPlanChangesDuringSnapshotBuild_ReturnsNull()
    {
        var appState = new AppState();
        var originalPlan = CreatePlan();
        var replacementPlan = CreatePlan(rootItemId: 300, childItemId: 400);
        appState.ApplyBuiltRecipePlanWithActiveItems(originalPlan);
        var snapshot = CreateSnapshot(originalPlan, expectedChildQuantity: 5);
        var lifecycle = new StubSnapshotLifecycleService(
            snapshot,
            beforeCurrentCheck: () => appState.ApplyBuiltRecipePlanWithActiveItems(replacementPlan));
        var service = CreateService(appState, lifecycle);

        var projection = await service.BuildCurrentDemandProjectionAsync(originalPlan);

        Assert.Null(projection);
    }

    private static RecipeLayerWorkflowService CreateService(
        AppState appState,
        IRecipeOperationSnapshotLifecycleService? lifecycle = null)
    {
        return new RecipeLayerWorkflowService(
            appState,
            lifecycle ?? new StubSnapshotLifecycleService(RecipeOperationSnapshot.Empty),
            new RecipeDemandProjectionService());
    }

    private static CraftingPlan CreatePlan(int rootItemId = 100, int childItemId = 200)
    {
        var root = new PlanNode
        {
            NodeId = "root",
            ItemId = rootItemId,
            Name = "Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true
        };
        var child = new PlanNode
        {
            NodeId = "child",
            ItemId = childItemId,
            Name = "Child",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            Parent = root
        };
        root.Children.Add(child);
        return new CraftingPlan { RootItems = [root] };
    }

    private static RecipeOperationSnapshot CreateSnapshot(CraftingPlan plan, int expectedChildQuantity)
    {
        var root = plan.RootItems.Single();
        var child = root.Children.Single();
        var operations = new[]
        {
            new RecipeOperation(
                root.NodeId,
                null,
                [],
                0,
                root.ItemId,
                root.Name,
                root.Quantity,
                root.Source,
                root.SourceReason,
                root.MustBeHq,
                root.CanCraft,
                RecipeOperationState.Active,
                null,
                null,
                RecipeOperationKind.StandardCraft,
                1000,
                1,
                "Carpenter",
                1,
                1,
                1,
                [
                    new RecipeOperationIngredient(
                        child.ItemId,
                        child.Name,
                        expectedChildQuantity,
                        expectedChildQuantity,
                        child.NodeId,
                        child.Source,
                        child.CanCraft,
                        RecipeIngredientLinkStatus.QuantityMismatch,
                        expectedChildQuantity,
                        child.Quantity)
                ],
                RecipeResolutionConfidence.Exact,
                RecipeDataSourceKind.GarlandStandardCraft),
            new RecipeOperation(
                child.NodeId,
                root.NodeId,
                [root.NodeId],
                1,
                child.ItemId,
                child.Name,
                child.Quantity,
                child.Source,
                child.SourceReason,
                child.MustBeHq,
                child.CanCraft,
                RecipeOperationState.InactiveBySource,
                null,
                null,
                RecipeOperationKind.StandardCraft,
                2000,
                1,
                "Carpenter",
                1,
                1,
                2,
                [],
                RecipeResolutionConfidence.Exact,
                RecipeDataSourceKind.GarlandStandardCraft)
        };

        return new RecipeOperationSnapshot(
            operations,
            operations.ToDictionary(operation => operation.NodeId),
            operations
                .GroupBy(operation => operation.ResultItemId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<RecipeOperation>)group.ToList()),
            []);
    }

    private sealed class StubSnapshotLifecycleService : IRecipeOperationSnapshotLifecycleService
    {
        private readonly RecipeOperationSnapshot _snapshot;
        private readonly Action? _beforeCurrentCheck;

        public StubSnapshotLifecycleService(
            RecipeOperationSnapshot snapshot,
            Action? beforeCurrentCheck = null)
        {
            _snapshot = snapshot;
            _beforeCurrentCheck = beforeCurrentCheck;
        }

        public Task<RecipeOperationSnapshot> GetOrBuildAsync(
            CraftingPlan? plan,
            RecipeOperationSnapshotIdentity identity,
            RecipeOperationSnapshotBuildOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_snapshot);
        }

        public Task<RecipeOperationSnapshot?> GetCurrentOrNullAsync(
            CraftingPlan? plan,
            RecipeOperationSnapshotIdentity identity,
            Func<RecipeOperationSnapshotIdentity, bool> isCurrent,
            RecipeOperationSnapshotBuildOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _beforeCurrentCheck?.Invoke();
            return Task.FromResult(isCurrent(identity) ? _snapshot : null);
        }
    }
}
