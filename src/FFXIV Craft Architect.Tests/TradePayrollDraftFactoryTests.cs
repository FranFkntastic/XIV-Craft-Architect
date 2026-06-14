using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradePayrollDraftFactoryTests
{
    [Fact]
    public void CreateFromCurrentPlan_IncludesRootCraftedItems()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(
            new CraftingPlan
            {
                RootItems =
                [
                    new PlanNode
                    {
                        ItemId = 100,
                        Name = "Finished Commission",
                        Quantity = 2,
                        MustBeHq = true,
                        Children =
                        [
                            new PlanNode
                            {
                                ItemId = 200,
                                Name = "Base Material",
                                Quantity = 6
                            }
                        ]
                    }
                ]
            });

        var factory = new TradePayrollDraftFactory(
            new CommissionCostBasisResolver(),
            new CommissionPayrollService(),
            new LightweightRecipeLayerWorkflowForTests());

        var result = factory.CreateFromCurrentPlan(appState);

        Assert.True(result.CanCreate);
        var item = Assert.Single(result.Draft!.Source.CraftedItems);
        Assert.Equal(100, item.Id);
        Assert.Equal("Finished Commission", item.Name);
        Assert.Equal(2, item.Quantity);
        Assert.True(item.MustBeHq);
        Assert.DoesNotContain(result.Draft.Source.CraftedItems, item => item.Id == 200);
    }

    [Fact]
    public void CreateFromCurrentPlan_UsesSelectedActiveProcurementDemand()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(
            new CraftingPlan
            {
                RootItems =
                [
                    new PlanNode
                    {
                        ItemId = 100,
                        Name = "Finished Commission",
                        Quantity = 1,
                        Source = AcquisitionSource.Craft,
                        CanCraft = true,
                        Children =
                        [
                            new PlanNode
                            {
                                ItemId = 200,
                                Name = "Selected Intermediate",
                                Quantity = 2,
                                Source = AcquisitionSource.MarketBuyNq,
                                CanBuyFromMarket = true,
                                MarketPrice = 300m,
                                Children =
                                [
                                    new PlanNode
                                    {
                                        ItemId = 300,
                                        Name = "Suppressed Raw Material",
                                        Quantity = 8,
                                        Source = AcquisitionSource.MarketBuyNq,
                                        CanBuyFromMarket = true,
                                        MarketPrice = 10m
                                    }
                                ]
                            }
                        ]
                    }
                ]
            });

        var factory = new TradePayrollDraftFactory(
            new CommissionCostBasisResolver(),
            new CommissionPayrollService(),
            new LightweightRecipeLayerWorkflowForTests());

        var result = factory.CreateFromCurrentPlan(appState);

        Assert.True(result.CanCreate);
        var line = Assert.Single(result.Draft!.Source.Lines);
        Assert.Equal(200, line.ItemId);
        Assert.Equal("Selected Intermediate", line.Name);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(300m, line.UnitCost);
        Assert.DoesNotContain(result.Draft.Source.Lines, line => line.ItemId == 300);
    }

    private sealed class LightweightRecipeLayerWorkflowForTests : IRecipeLayerWorkflowService
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
