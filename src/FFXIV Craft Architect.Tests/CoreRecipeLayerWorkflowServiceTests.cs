using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Tests;

public class CoreRecipeLayerWorkflowServiceTests
{
    [Fact]
    public void CreateSnapshotIdentity_UsesCurrentCoreSessionVersions()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.MarkPlanStructureChanged("plan built");
        session.MarkPlanDecisionChanged("source changed");
        session.MarkPlanPriceChanged("prices refreshed");
        session.MarkProcurementSettingsChanged("settings changed");
        var service = CreateService(session);

        var identity = service.CreateSnapshotIdentity();

        Assert.Equal(session.PlanSessionVersion, identity.PlanSessionVersion);
        Assert.Equal(session.Versions.PlanCore, identity.PlanStructureVersion);
        Assert.Equal(session.Versions.PlanDecision, identity.PlanDecisionVersion);
        Assert.Equal(session.Versions.PlanPrice, identity.PlanPriceVersion);
        Assert.Equal(session.Versions.SettingsContext, identity.SettingsVersion);
        Assert.Equal("garland-current", identity.RecipeDataIdentity);
    }

    [Fact]
    public async Task BuildCurrentDemandProjectionAsync_UsesLifecycleSnapshotForDemandQuantities()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var plan = CreatePlan();
        session.ActivatePlan(plan, [], new CraftSessionActiveContext(null, null, null, null), "plan loaded");
        var snapshot = CreateSnapshot(plan, expectedChildQuantity: 5);
        var service = CreateService(session, new StubSnapshotLifecycleService(snapshot));

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
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var plan = CreatePlan();
        var service = CreateService(session);

        var items = service.BuildActiveProcurementItems(plan);

        var item = Assert.Single(items);
        Assert.Equal(200, item.ItemId);
        Assert.Equal("Child", item.Name);
        Assert.Equal(2, item.TotalQuantity);
    }

    [Fact]
    public async Task BuildCurrentMarketAnalysisCandidatesAsync_WhenSessionChangesDuringSnapshotBuild_ReturnsNull()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var plan = CreatePlan();
        session.ActivatePlan(plan, [], new CraftSessionActiveContext(null, null, null, null), "plan loaded");
        var snapshot = CreateSnapshot(plan, expectedChildQuantity: 5);
        var lifecycle = new StubSnapshotLifecycleService(
            snapshot,
            beforeCurrentCheck: () => session.MarkPlanStructureChanged("plan changed"));
        var service = CreateService(session, lifecycle);

        var items = await service.BuildCurrentMarketAnalysisCandidatesAsync(plan);

        Assert.Null(items);
    }

    [Fact]
    public async Task BuildCurrentDemandProjectionAsync_WhenOldPlanIsPassedAfterActivePlanChanges_ReturnsNull()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var originalPlan = CreatePlan();
        var replacementPlan = CreatePlan(rootItemId: 300, childItemId: 400);
        session.ActivatePlan(originalPlan, [], new CraftSessionActiveContext(null, null, null, null), "original plan loaded");
        session.ActivatePlan(replacementPlan, [], new CraftSessionActiveContext(null, null, null, null), "replacement plan loaded");
        var service = CreateService(session, new StubSnapshotLifecycleService(CreateSnapshot(originalPlan, expectedChildQuantity: 5)));

        var projection = await service.BuildCurrentDemandProjectionAsync(originalPlan);

        Assert.Null(projection);
    }

    [Fact]
    public async Task BuildCurrentDemandProjectionAsync_WhenSameIdOldPlanIsPassedAfterReplacement_ReturnsNull()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var originalPlan = CreatePlan();
        var replacementPlan = CreatePlan(rootItemId: 300, childItemId: 400);
        replacementPlan.Id = originalPlan.Id;
        session.ActivatePlan(originalPlan, [], new CraftSessionActiveContext(null, null, null, null), "original plan loaded");
        session.ActivatePlan(replacementPlan, [], new CraftSessionActiveContext(null, null, null, null), "replacement plan loaded");
        var service = CreateService(session, new StubSnapshotLifecycleService(CreateSnapshot(originalPlan, expectedChildQuantity: 5)));

        var projection = await service.BuildCurrentDemandProjectionAsync(originalPlan);

        Assert.Null(projection);
    }

    [Fact]
    public async Task BuildCurrentDemandProjectionAsync_WhenActivePlanSnapshotPredatesPlanMutation_ReturnsNull()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var plan = CreatePlan();
        session.ActivatePlan(plan, [], new CraftSessionActiveContext(null, null, null, null), "plan loaded");
        var staleSnapshot = session.ActivePlan!;
        session.MarkPlanDecisionChanged("source changed");
        var service = CreateService(session, new StubSnapshotLifecycleService(CreateSnapshot(staleSnapshot, expectedChildQuantity: 5)));

        var projection = await service.BuildCurrentDemandProjectionAsync(staleSnapshot);

        Assert.Null(projection);
    }

    private static CoreRecipeLayerWorkflowService CreateService(
        CraftSessionState session,
        IRecipeOperationSnapshotLifecycleService? lifecycle = null)
    {
        return new CoreRecipeLayerWorkflowService(
            session,
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
