using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public class RecipeOperationSnapshotQueryTests
{
    [Fact]
    public void GetArtisanExportOperations_RootOnly_IncludesOnlyRoots()
    {
        var root = Operation("root", null, 100, RecipeOperationState.InactiveBySource);
        var activeChild = Operation("active-child", "root", 200, RecipeOperationState.Active);
        var snapshot = Snapshot(root, activeChild);

        var operations = snapshot.GetArtisanExportOperations(includePrecrafts: false).ToList();

        Assert.Equal([100], operations.Select(operation => operation.ResultItemId));
    }

    [Fact]
    public void GetArtisanExportOperations_IncludePrecrafts_IncludesActiveChildrenButExcludesSuppressedDescendants()
    {
        var root = Operation("root", null, 100, RecipeOperationState.Active);
        var activeChild = Operation("active-child", "root", 200, RecipeOperationState.Active);
        var suppressedChild = Operation("suppressed-child", "root", 300, RecipeOperationState.SuppressedByAncestor);
        var inactiveChild = Operation("inactive-child", "root", 400, RecipeOperationState.InactiveBySource);
        var unresolvedChild = Operation(
            "unresolved-child",
            "root",
            500,
            RecipeOperationState.Unresolved,
            kind: null,
            recipeId: null);
        var snapshot = Snapshot(root, activeChild, suppressedChild, inactiveChild, unresolvedChild);

        var operations = snapshot.GetArtisanExportOperations(includePrecrafts: true).ToList();

        Assert.Equal([100, 200, 300, 400], operations.Select(operation => operation.ResultItemId));
    }

    [Fact]
    public void GetCraftableReferences_ReturnsInactiveAndSuppressedOperations()
    {
        var root = Operation("root", null, 100, RecipeOperationState.Active);
        var inactive = Operation("inactive", "root", 200, RecipeOperationState.InactiveBySource);
        var suppressed = Operation("suppressed", "inactive", 300, RecipeOperationState.SuppressedByAncestor);
        var snapshot = Snapshot(root, inactive, suppressed);

        var references = snapshot.GetCraftableReferences().ToList();

        Assert.Equal([200, 300], references.Select(operation => operation.ResultItemId));
    }

    [Fact]
    public void GetUnresolvedRequiredCrafts_ReturnsOnlyActiveUnresolvedCraftDemand()
    {
        var unresolvedActive = Operation("missing", null, 100, RecipeOperationState.Unresolved, kind: null, recipeId: null);
        var unresolvedBought = Operation("bought-missing", null, 200, RecipeOperationState.Unresolved, AcquisitionSource.MarketBuyNq, kind: null, recipeId: null);
        var resolved = Operation("resolved", null, 300, RecipeOperationState.Active);
        var snapshot = Snapshot(unresolvedActive, unresolvedBought, resolved);

        var unresolved = snapshot.GetUnresolvedRequiredCrafts().ToList();

        Assert.Equal([100], unresolved.Select(operation => operation.ResultItemId));
    }

    [Fact]
    public void GetOperationsForNode_WhenNodeIndexIncomplete_FallsBackToOperationSequence()
    {
        var first = Operation("duplicate", null, 100, RecipeOperationState.Active);
        var second = Operation("duplicate", null, 200, RecipeOperationState.Active);
        var snapshot = new RecipeOperationSnapshot(
            [first, second],
            new Dictionary<string, RecipeOperation>(),
            new Dictionary<int, IReadOnlyList<RecipeOperation>>(),
            Array.Empty<RecipeOperationDiagnostic>(),
            isNodeIndexComplete: false);

        var operations = snapshot.GetOperationsForNode("duplicate").ToList();

        Assert.Equal([100, 200], operations.Select(operation => operation.ResultItemId));
    }

    private static RecipeOperationSnapshot Snapshot(params RecipeOperation[] operations)
    {
        return new RecipeOperationSnapshot(
            operations,
            operations.ToDictionary(operation => operation.NodeId),
            operations.GroupBy(operation => operation.ResultItemId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<RecipeOperation>)group.ToList()),
            Array.Empty<RecipeOperationDiagnostic>());
    }

    private static RecipeOperation Operation(
        string nodeId,
        string? parentNodeId,
        int itemId,
        RecipeOperationState state,
        AcquisitionSource source = AcquisitionSource.Craft,
        RecipeOperationKind? kind = RecipeOperationKind.StandardCraft,
        uint? recipeId = 1000)
    {
        return new RecipeOperation(
            nodeId,
            parentNodeId,
            Array.Empty<string>(),
            parentNodeId == null ? 0 : 1,
            itemId,
            $"Item {itemId}",
            1,
            source,
            AcquisitionSourceReason.SystemDefault,
            false,
            true,
            state,
            state == RecipeOperationState.SuppressedByAncestor ? "ancestor" : null,
            state == RecipeOperationState.SuppressedByAncestor ? "Ancestor" : null,
            kind,
            recipeId,
            1,
            "Carpenter",
            1,
            1,
            1,
            Array.Empty<RecipeOperationIngredient>(),
            recipeId == null ? RecipeResolutionConfidence.Missing : RecipeResolutionConfidence.Exact,
            recipeId == null ? RecipeDataSourceKind.None : RecipeDataSourceKind.GarlandStandardCraft);
    }
}
