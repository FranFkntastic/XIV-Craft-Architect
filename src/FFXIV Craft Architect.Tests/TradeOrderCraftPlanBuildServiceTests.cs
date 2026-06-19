using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradeOrderCraftPlanBuildServiceTests
{
    [Fact]
    public async Task BuildFromOrderAsync_UsesOrderRootItemsAsRequestedOutputs()
    {
        var builder = new RecordingRecipePlanBuilder();
        var service = new TradeOrderCraftPlanBuildService(
            builder,
            new LightweightRecipeLayerWorkflowForTradeOrderBuildTests());
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                RootItems =
                [
                    new TradeOrderRootItemSnapshot(
                        100,
                        "Order Sword",
                        3,
                        MustBeHq: true,
                        EstimatedSaleValue: 12_000m)
                ]
            }
        };

        var result = await service.BuildFromOrderAsync(order, "Aether", "Siren");

        Assert.True(result.Built);
        var requested = Assert.Single(builder.RequestedTargets);
        Assert.Equal((100, "Order Sword", 3, true), requested);
        Assert.Equal("Aether", builder.DataCenter);
        Assert.Equal("Siren", builder.World);
    }

    [Fact]
    public async Task BuildFromOrderAsync_BuildsWithoutAppStateDependency()
    {
        var builder = new RecordingRecipePlanBuilder();
        var service = new TradeOrderCraftPlanBuildService(
            builder,
            new LightweightRecipeLayerWorkflowForTradeOrderBuildTests());
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                RootItems =
                [
                    new TradeOrderRootItemSnapshot(
                        200,
                        "Order Robe",
                        2,
                        MustBeHq: false,
                        EstimatedSaleValue: 8_000m)
                ]
            }
        };

        var result = await service.BuildFromOrderAsync(order, "Primal", string.Empty);

        Assert.True(result.Built);
        Assert.Equal(200, result.Plan!.RootItems.Single().ItemId);
        Assert.Equal("Order Robe", result.Plan.RootItems.Single().Name);
        Assert.Equal(2, result.Plan.RootItems.Single().Quantity);
    }

    [Fact]
    public async Task BuildFromOrderAsync_WhenPersistedRootItemsAreMissing_ReturnsUnavailable()
    {
        var builder = new RecordingRecipePlanBuilder();
        var service = new TradeOrderCraftPlanBuildService(
            builder,
            new LightweightRecipeLayerWorkflowForTradeOrderBuildTests());
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                RootItems = null!
            }
        };

        var result = await service.BuildFromOrderAsync(order, "Aether", string.Empty);

        Assert.False(result.Built);
        Assert.Null(result.Plan);
        Assert.Empty(builder.RequestedTargets);
    }

    [Fact]
    public async Task BuildAsync_RequiresAtLeastOnePositiveQuantityOutput()
    {
        var builder = new RecordingRecipePlanBuilder();
        var service = new TradeOrderCraftPlanBuildService(
            builder,
            new LightweightRecipeLayerWorkflowForTradeOrderBuildTests());

        var result = await service.BuildAsync(
            new TradeOrderCraftPlanBuildRequest(
                [new TradeRequestedCraftOutput(100, "Zero Item", 0, MustBeHq: false)],
                "Aether",
                string.Empty));

        Assert.False(result.Built);
        Assert.Null(result.Plan);
        Assert.Empty(builder.RequestedTargets);
    }

    [Fact]
    public async Task BuildAsync_WhenRequestedOutputsAreMissing_ReturnsUnavailable()
    {
        var builder = new RecordingRecipePlanBuilder();
        var service = new TradeOrderCraftPlanBuildService(
            builder,
            new LightweightRecipeLayerWorkflowForTradeOrderBuildTests());

        var result = await service.BuildAsync(
            new TradeOrderCraftPlanBuildRequest(
                null!,
                "Aether",
                string.Empty));

        Assert.False(result.Built);
        Assert.Null(result.Plan);
        Assert.Empty(builder.RequestedTargets);
    }

    private sealed class RecordingRecipePlanBuilder : IRecipePlanBuilder
    {
        public List<(int itemId, string name, int quantity, bool isHqRequired)> RequestedTargets { get; private set; } = [];
        public string DataCenter { get; private set; } = string.Empty;
        public string World { get; private set; } = string.Empty;

        public Task<CraftingPlan> BuildPlanAsync(
            List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
            string dataCenter,
            string world,
            CancellationToken ct = default,
            FFXIV_Craft_Architect.Core.Services.Interfaces.IRecipePlanBuildDiagnosticRecorder? diagnostics = null)
        {
            RequestedTargets = targetItems;
            DataCenter = dataCenter;
            World = world;

            return Task.FromResult(new CraftingPlan
            {
                RootItems = targetItems
                    .Select(item => new PlanNode
                    {
                        ItemId = item.itemId,
                        Name = item.name,
                        Quantity = item.quantity,
                        MustBeHq = item.isHqRequired,
                        Source = AcquisitionSource.Craft,
                        CanCraft = true
                    })
                    .ToList()
            });
        }

        public Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class LightweightRecipeLayerWorkflowForTradeOrderBuildTests : IRecipeLayerWorkflowService
    {
        private readonly RecipeDemandProjectionService _projectionService = new();

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity() => RecipeOperationSnapshotIdentity.Unspecified;

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan) =>
            _projectionService.Build(plan, snapshot: null);

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates();

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RecipeDemandProjection?>(BuildDemandProjection(plan));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildMarketAnalysisCandidates(plan));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildActiveProcurementItems(plan));
    }
}
