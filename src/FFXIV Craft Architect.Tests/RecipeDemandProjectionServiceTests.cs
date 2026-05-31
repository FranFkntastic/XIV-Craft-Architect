using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class RecipeDemandProjectionServiceTests
{
    [Fact]
    public void Build_MarketAnalysisCandidates_MatchesExistingAcquisitionPlanningView()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var snapshot = CreateSnapshot(plan);
        var service = new RecipeDemandProjectionService();

        var projection = service.Build(plan, snapshot);

        AssertMaterialParity(
            AcquisitionPlanningService.GetMarketAnalysisCandidates(plan),
            projection.ToMarketAnalysisMaterialAggregates());
    }

    [Fact]
    public void Build_ActiveProcurementDemand_MatchesExistingAcquisitionPlanningView()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var snapshot = CreateSnapshot(plan);
        var service = new RecipeDemandProjectionService();

        var projection = service.Build(plan, snapshot);

        AssertMaterialParity(
            AcquisitionPlanningService.GetActiveProcurementItems(plan),
            projection.ToActiveProcurementMaterialAggregates());
    }

    [Fact]
    public void Build_ActiveProcurementDemand_PreservesRecipeOperationContextForIngredientRows()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var root = plan.RootItems[0];
        var intermediate = root.Children[0];
        var snapshot = CreateSnapshot(plan);
        var service = new RecipeDemandProjectionService();

        var projection = service.Build(plan, snapshot);

        var row = Assert.Single(projection.ActiveProcurementDemand);
        Assert.Equal(intermediate.NodeId, row.NodeId);
        Assert.Equal(root.NodeId, row.ParentNodeId);
        Assert.Equal(root.NodeId, row.ParentOperationNodeId);
        Assert.Equal(1000u, row.ParentRecipeId);
        Assert.Equal(RecipeDemandQuantityBasis.PlanNodeQuantity, row.QuantityBasis);
    }

    [Fact]
    public void Build_UsesRecipeOperationIngredientQuantityWhenAvailable()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var root = plan.RootItems[0];
        var intermediate = root.Children[0];
        var snapshot = CreateSnapshotWithIngredient(
            plan,
            intermediate,
            expectedQuantity: 3);
        var service = new RecipeDemandProjectionService();

        var projection = service.Build(plan, snapshot);

        var row = Assert.Single(projection.ActiveProcurementDemand);
        Assert.Equal(intermediate.NodeId, row.NodeId);
        Assert.Equal(3, row.Quantity);
        Assert.Equal(RecipeDemandQuantityBasis.RecipeExpectedQuantity, row.QuantityBasis);
    }

    [Fact]
    public void Build_SuppressedDemand_TracksChildrenUnderBoughtAncestor()
    {
        var plan = CreatePlanWithBoughtIntermediate();
        var snapshot = CreateSnapshot(plan);
        var service = new RecipeDemandProjectionService();

        var projection = service.Build(plan, snapshot);

        var suppressed = Assert.Single(projection.SuppressedDemand);
        Assert.Equal(300, suppressed.ItemId);
        Assert.Equal(6, suppressed.Quantity);
        Assert.Equal(200, suppressed.SuppressedByItemId);
    }

    private static void AssertMaterialParity(
        IReadOnlyList<MaterialAggregate> expected,
        IReadOnlyList<MaterialAggregate> actual)
    {
        Assert.Equal(expected.Select(item => item.ItemId), actual.Select(item => item.ItemId));
        foreach (var expectedItem in expected)
        {
            var actualItem = Assert.Single(actual, item => item.ItemId == expectedItem.ItemId);
            Assert.Equal(expectedItem.Name, actualItem.Name);
            Assert.Equal(expectedItem.TotalQuantity, actualItem.TotalQuantity);
            Assert.Equal(expectedItem.RequiresHq, actualItem.RequiresHq);
            Assert.Equal(expectedItem.Sources.Select(source => source.ParentItemName), actualItem.Sources.Select(source => source.ParentItemName));
            Assert.Equal(expectedItem.Sources.Select(source => source.Quantity), actualItem.Sources.Select(source => source.Quantity));
            Assert.Equal(expectedItem.Sources.Select(source => source.IsCrafted), actualItem.Sources.Select(source => source.IsCrafted));
        }
    }

    private static CraftingPlan CreatePlanWithBoughtIntermediate()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            CanBuyFromMarket = true
        };
        var intermediate = new PlanNode
        {
            ItemId = 200,
            Name = "Intermediate",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = true,
            CanBuyFromMarket = true,
            Parent = root
        };
        var raw = new PlanNode
        {
            ItemId = 300,
            Name = "Raw Material",
            Quantity = 6,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            Parent = intermediate
        };
        root.Children.Add(intermediate);
        intermediate.Children.Add(raw);

        return new CraftingPlan { RootItems = [root] };
    }

    private static RecipeOperationSnapshot CreateSnapshot(CraftingPlan plan)
    {
        var root = plan.RootItems[0];
        var intermediate = root.Children[0];
        var operations = new[]
        {
            Operation(root, null, RecipeOperationState.Active, 1000u),
            Operation(intermediate, root.NodeId, RecipeOperationState.InactiveBySource, 2000u)
        };

        return new RecipeOperationSnapshot(
            operations,
            operations.ToDictionary(operation => operation.NodeId),
            operations.GroupBy(operation => operation.ResultItemId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<RecipeOperation>)group.ToList()),
            Array.Empty<RecipeOperationDiagnostic>());
    }

    private static RecipeOperationSnapshot CreateSnapshotWithIngredient(
        CraftingPlan plan,
        PlanNode ingredientNode,
        int expectedQuantity)
    {
        var root = plan.RootItems[0];
        var operations = new[]
        {
            Operation(
                root,
                null,
                RecipeOperationState.Active,
                1000u,
                [
                    new RecipeOperationIngredient(
                        ingredientNode.ItemId,
                        ingredientNode.Name,
                        expectedQuantity,
                        expectedQuantity,
                        ingredientNode.NodeId,
                        ingredientNode.Source,
                        ingredientNode.CanCraft,
                        RecipeIngredientLinkStatus.QuantityMismatch,
                        expectedQuantity,
                        ingredientNode.Quantity)
                ])
        };

        return new RecipeOperationSnapshot(
            operations,
            operations.ToDictionary(operation => operation.NodeId),
            operations.GroupBy(operation => operation.ResultItemId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<RecipeOperation>)group.ToList()),
            Array.Empty<RecipeOperationDiagnostic>());
    }

    private static RecipeOperation Operation(
        PlanNode node,
        string? parentNodeId,
        RecipeOperationState state,
        uint recipeId,
        IReadOnlyList<RecipeOperationIngredient>? ingredients = null)
    {
        return new RecipeOperation(
            node.NodeId,
            parentNodeId,
            Array.Empty<string>(),
            parentNodeId == null ? 0 : 1,
            node.ItemId,
            node.Name,
            node.Quantity,
            node.Source,
            node.SourceReason,
            node.MustBeHq,
            node.CanCraft,
            state,
            null,
            null,
            RecipeOperationKind.StandardCraft,
            recipeId,
            1,
            "Carpenter",
            1,
            1,
            1,
            ingredients ?? Array.Empty<RecipeOperationIngredient>(),
            RecipeResolutionConfidence.Exact,
            RecipeDataSourceKind.GarlandStandardCraft);
    }
}
