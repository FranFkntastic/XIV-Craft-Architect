using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Tests;

public class CoreAcquisitionEvaluationWorkflowServiceTests
{
    [Fact]
    public async Task BuildCurrentSnapshotAsync_WithCurrentProjection_ReturnsSnapshot()
    {
        var plan = CreatePlan();
        var service = new CoreAcquisitionEvaluationWorkflowService(
            new StubRecipeLayerWorkflowService(new RecipeDemandProjectionService().Build(plan, snapshot: null)));

        var snapshot = await service.BuildCurrentSnapshotAsync(
            plan,
            shoppingPlans: [],
            unavailableMarketItemIds: new HashSet<int>(),
            CoreAcquisitionFilter.All);

        Assert.NotNull(snapshot);
        Assert.Contains(snapshot.Rows, row => row.ItemId == 200);
    }

    [Fact]
    public async Task BuildCurrentSnapshotAsync_WhenProjectionIsStale_ReturnsNull()
    {
        var service = new CoreAcquisitionEvaluationWorkflowService(
            new StubRecipeLayerWorkflowService(projection: null));

        var snapshot = await service.BuildCurrentSnapshotAsync(
            CreatePlan(),
            shoppingPlans: [],
            unavailableMarketItemIds: new HashSet<int>(),
            CoreAcquisitionFilter.All);

        Assert.Null(snapshot);
    }

    private static CraftingPlan CreatePlan()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Final Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true
        };
        root.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Material",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            MarketPrice = 10,
            Parent = root
        });

        return new CraftingPlan { RootItems = [root] };
    }

    private sealed class StubRecipeLayerWorkflowService : ICoreRecipeLayerWorkflowService
    {
        private readonly RecipeDemandProjection? _projection;

        public StubRecipeLayerWorkflowService(RecipeDemandProjection? projection)
        {
            _projection = projection;
        }

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity() => RecipeOperationSnapshotIdentity.Unspecified;

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan) =>
            _projection ?? new RecipeDemandProjection([], [], [], []);

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates();

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_projection);

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(_projection?.ToMarketAnalysisMaterialAggregates());

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(_projection?.ToActiveProcurementMaterialAggregates());
    }
}
