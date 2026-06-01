using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AcquisitionEvaluationWorkflowServiceTests
{
    [Fact]
    public async Task BuildCurrentSnapshotAsync_UsesCurrentRecipeDemandProjection()
    {
        var plan = CreatePlan();
        var projection = CreateProjection(plan, quantity: 5);
        var service = new AcquisitionEvaluationWorkflowService(new StubRecipeLayerWorkflowService(projection));

        var snapshot = await service.BuildCurrentSnapshotAsync(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All);

        Assert.NotNull(snapshot);
        var row = snapshot.Rows.Single(row => row.ItemId == 200);
        Assert.Equal(5, row.TotalQuantity);
        Assert.Equal(5, row.ActiveQuantity);
    }

    [Fact]
    public async Task BuildCurrentSnapshotAsync_WhenRecipeProjectionIsStale_ReturnsNull()
    {
        var plan = CreatePlan();
        var service = new AcquisitionEvaluationWorkflowService(new StubRecipeLayerWorkflowService(projection: null));

        var snapshot = await service.BuildCurrentSnapshotAsync(
            plan,
            shoppingPlans: Array.Empty<DetailedShoppingPlan>(),
            unavailableMarketItems: Array.Empty<MarketDataUnavailableItem>(),
            AcquisitionFilter.All);

        Assert.Null(snapshot);
    }

    private static CraftingPlan CreatePlan()
    {
        var root = new PlanNode
        {
            NodeId = "root",
            ItemId = 100,
            Name = "Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true
        };
        var child = new PlanNode
        {
            NodeId = "child",
            ItemId = 200,
            Name = "Child",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            Parent = root
        };
        root.Children.Add(child);
        return new CraftingPlan { RootItems = [root] };
    }

    private static RecipeDemandProjection CreateProjection(CraftingPlan plan, int quantity)
    {
        var root = plan.RootItems.Single();
        var child = root.Children.Single();
        var row = new RecipeDemandRow(
            RecipeDemandViewKind.ActiveProcurement,
            child.NodeId,
            child.ItemId,
            child.Name,
            child.IconId,
            quantity,
            RecipeDemandQuantityBasis.RecipeExpectedQuantity,
            child.MustBeHq,
            child.Source,
            child.SourceReason,
            child.Children.Count > 0,
            child.CanBuyFromMarket,
            child.CanBuyFromVendor,
            child.MarketPrice,
            root.NodeId,
            root.Name,
            root.NodeId,
            1000,
            child.NodeId,
            2000,
            null,
            null,
            null,
            child.CanCraft,
            child.CanBeHq,
            child.Yield,
            child.HqMarketPrice,
            child.VendorPrice);

        return new RecipeDemandProjection(
            AllPlanDemand: [row with { ViewKind = RecipeDemandViewKind.PlanOccurrence }],
            MarketAnalysisCandidates: [row with { ViewKind = RecipeDemandViewKind.MarketAnalysisCandidate }],
            ActiveProcurementDemand: [row],
            SuppressedDemand: Array.Empty<RecipeDemandRow>());
    }

    private sealed class StubRecipeLayerWorkflowService : IRecipeLayerWorkflowService
    {
        private readonly RecipeDemandProjection? _projection;

        public StubRecipeLayerWorkflowService(RecipeDemandProjection? projection)
        {
            _projection = projection;
        }

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity()
        {
            return new RecipeOperationSnapshotIdentity(0, 0, 0, 0, 0, "test");
        }

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan)
        {
            return _projection ?? new RecipeDemandProjection([], [], [], []);
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
            return Task.FromResult(_projection);
        }

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(_projection?.ToMarketAnalysisMaterialAggregates());
        }

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(_projection?.ToActiveProcurementMaterialAggregates());
        }
    }
}
