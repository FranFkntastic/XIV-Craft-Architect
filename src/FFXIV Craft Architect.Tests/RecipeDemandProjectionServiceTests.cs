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
        Assert.Equal(root.Quantity, row.ParentOutputQuantity);
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
        Assert.Equal(root.Quantity, row.ParentOutputQuantity);
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

    [Fact]
    public void Build_DirectPurchaseRows_PreserveAcquisitionReadDetails()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true
        };
        var vendorChild = new PlanNode
        {
            ItemId = 200,
            Name = "Vendor Child",
            Quantity = 3,
            Yield = 2,
            Source = AcquisitionSource.VendorBuy,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanCraft = true,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            CanBeHq = true,
            MarketPrice = 90,
            HqMarketPrice = 120,
            VendorPrice = 40,
            SelectedVendorIndex = 1,
            Parent = root,
            VendorOptions =
            [
                new VendorInfo
                {
                    Name = "Fallback Vendor",
                    Location = "Gridania",
                    Price = 50,
                    Currency = "gil"
                },
                new VendorInfo
                {
                    Name = "Selected Vendor",
                    Location = "Limsa Lominsa",
                    Price = 40,
                    Currency = "gil"
                },
                new VendorInfo
                {
                    Name = "Token Vendor",
                    Location = "Mor Dhona",
                    Price = 10,
                    Currency = "poetics"
                }
            ]
        };
        root.Children.Add(vendorChild);
        var plan = new CraftingPlan { RootItems = [root] };
        var service = new RecipeDemandProjectionService();

        var projection = service.Build(plan, snapshot: null);

        var row = Assert.Single(projection.ActiveProcurementDemand);
        Assert.True(row.CanCraft);
        Assert.True(row.CanBeHq);
        Assert.Equal(2, row.Yield);
        Assert.True(row.IsDirectPurchase);
        Assert.True(row.IsVendorPurchase);
        Assert.False(row.IsMarketBoardPurchase);
        Assert.Equal(90, row.UnitPrice);
        Assert.Equal(120, row.HqUnitPrice);
        Assert.Equal(40, row.VendorUnitPrice);
        Assert.Equal(1, row.SelectedVendorIndex);
        Assert.Equal(3, row.VendorOptions.Count);
        Assert.Equal("Selected Vendor", row.SelectedVendor?.Name);
        Assert.Equal("Limsa Lominsa", row.SelectedVendor?.Location);
        Assert.Equal(40, row.SelectedVendor?.Price);
        Assert.Contains(row.VendorOptions, vendor => !vendor.IsGilVendor);
    }

    [Fact]
    public void RecipeDemandRow_Deconstruct_PreservesOriginalPositionalShape()
    {
        var row = new RecipeDemandRow(
            RecipeDemandViewKind.ActiveProcurement,
            "node-1",
            101,
            "Walnut Lumber",
            202,
            3,
            RecipeDemandQuantityBasis.RecipeExpectedQuantity,
            true,
            AcquisitionSource.MarketBuyHq,
            AcquisitionSourceReason.UserSelected,
            false,
            true,
            false,
            123,
            "parent-node",
            "Parent Item",
            "operation-parent",
            3001u,
            "operation-node",
            4001u,
            "suppressed-node",
            505,
            "Suppressed Item",
            canCraft: true,
            canBeHq: true,
            yield: 2,
            hqUnitPrice: 456);

        var (
            viewKind,
            nodeId,
            itemId,
            itemName,
            iconId,
            quantity,
            quantityBasis,
            mustBeHq,
            source,
            sourceReason,
            hasChildren,
            canBuyFromMarket,
            canBuyFromVendor,
            unitPrice,
            parentNodeId,
            parentItemName,
            parentOperationNodeId,
            parentRecipeId,
            operationNodeId,
            recipeId,
            suppressedByNodeId,
            suppressedByItemId,
            suppressedByItemName) = row;

        Assert.Equal(RecipeDemandViewKind.ActiveProcurement, viewKind);
        Assert.Equal("node-1", nodeId);
        Assert.Equal(101, itemId);
        Assert.Equal("Walnut Lumber", itemName);
        Assert.Equal(202, iconId);
        Assert.Equal(3, quantity);
        Assert.Equal(RecipeDemandQuantityBasis.RecipeExpectedQuantity, quantityBasis);
        Assert.True(mustBeHq);
        Assert.Equal(AcquisitionSource.MarketBuyHq, source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, sourceReason);
        Assert.False(hasChildren);
        Assert.True(canBuyFromMarket);
        Assert.False(canBuyFromVendor);
        Assert.Equal(123, unitPrice);
        Assert.Equal("parent-node", parentNodeId);
        Assert.Equal("Parent Item", parentItemName);
        Assert.Equal("operation-parent", parentOperationNodeId);
        Assert.Equal(3001u, parentRecipeId);
        Assert.Equal("operation-node", operationNodeId);
        Assert.Equal(4001u, recipeId);
        Assert.Equal("suppressed-node", suppressedByNodeId);
        Assert.Equal(505, suppressedByItemId);
        Assert.Equal("Suppressed Item", suppressedByItemName);
    }

    [Fact]
    public void RecipeDemandRow_OriginalNamedConstructorShape_DefaultsExpandedFields()
    {
        var row = new RecipeDemandRow(
            ViewKind: RecipeDemandViewKind.MarketAnalysisCandidate,
            NodeId: "node-2",
            ItemId: 202,
            ItemName: "Steel Ingot",
            IconId: 303,
            Quantity: 4,
            QuantityBasis: RecipeDemandQuantityBasis.PlanNodeQuantity,
            MustBeHq: false,
            Source: AcquisitionSource.MarketBuyNq,
            SourceReason: AcquisitionSourceReason.SystemDefault,
            HasChildren: true,
            CanBuyFromMarket: true,
            CanBuyFromVendor: true,
            UnitPrice: 75,
            ParentNodeId: "parent-node",
            ParentItemName: "Parent",
            ParentOperationNodeId: "parent-operation",
            ParentRecipeId: 1001u,
            OperationNodeId: "operation-node",
            RecipeId: 2001u,
            SuppressedByNodeId: null,
            SuppressedByItemId: null,
            SuppressedByItemName: null);

        Assert.Equal(RecipeDemandViewKind.MarketAnalysisCandidate, row.ViewKind);
        Assert.Equal("node-2", row.NodeId);
        Assert.Equal(202, row.ItemId);
        Assert.False(row.CanCraft);
        Assert.False(row.CanBeHq);
        Assert.Equal(1, row.Yield);
        Assert.Equal(0, row.HqUnitPrice);
        Assert.Equal(0, row.VendorUnitPrice);
        Assert.Equal(-1, row.SelectedVendorIndex);
        Assert.Empty(row.VendorOptions);
        Assert.Equal(0, row.ParentOutputQuantity);
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
