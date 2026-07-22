using System.Text.Json;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class RecipeGraphContractTests
{
    [Fact]
    public async Task ExactRecipeEvidence_GeneratesCompleteBoundedGraph()
    {
        var response = await CreateService().BuildAsync(Request());

        Assert.True(response.IsComplete);
        Assert.Empty(response.Diagnostics);
        Assert.Equal(CraftRecipeGraphResponseV1.ExactProviderId, response.ProviderId);
        Assert.Equal("contract-provider-1", response.ProviderVersion);
        Assert.Equal([300u, 400u], response.TerminalMaterialItemIds);
        var recipe = Assert.Single(response.Recipes);
        Assert.Equal((1001u, 100u, 1u), (recipe.RecipeId, recipe.OutputItemId, recipe.OutputQuantity));
        Assert.Equal(CraftRecipeUnlockEvidenceV1.NoUnlockRequired, recipe.UnlockEvidence);
        Assert.Equal(CraftRecipeResolutionConfidenceV1.Exact, recipe.ResolutionConfidence);
    }

    [Fact]
    public async Task CraftableIntermediate_RemainsRecipeEdgeInsteadOfTerminalMaterial()
    {
        var root = Node("root", 100, "Root Gear", true);
        var intermediate = Node("intermediate", 200, "Crafted Plate", true, root);
        intermediate.Quantity = 2;
        var ore = Node("ore", 300, "Ore", false, intermediate);
        ore.Quantity = 6;
        root.Children.Add(intermediate);
        intermediate.Children.Add(ore);
        var rootOperation = Operation(root, recipeId: 1001, depth: 0, ancestors: [], [Ingredient(intermediate, 2)]);
        var intermediateOperation = Operation(
            intermediate, recipeId: 2001, depth: 1, ancestors: [root.NodeId], [Ingredient(ore, 3, 6)]);
        var operations = new[] { rootOperation, intermediateOperation };
        var snapshot = new RecipeOperationSnapshot(
            operations,
            operations.ToDictionary(operation => operation.NodeId),
            operations.ToDictionary(operation => operation.ResultItemId, operation => (IReadOnlyList<RecipeOperation>)[operation]),
            []);
        var service = new CraftRecipeGraphService(
            new FixedPlanBuilder(new CraftingPlan { RootItems = [root] }),
            new FixedSnapshotService(snapshot),
            "contract-provider-1");

        var response = await service.BuildAsync(Request());

        Assert.True(response.IsComplete);
        Assert.Equal([100u, 200u], response.Recipes.Select(recipe => recipe.OutputItemId));
        Assert.Equal((200u, 2u),
            (Assert.Single(response.Recipes[0].Ingredients).ItemId, response.Recipes[0].Ingredients[0].QuantityPerCraft));
        Assert.Equal((300u, 3u),
            (Assert.Single(response.Recipes[1].Ingredients).ItemId, response.Recipes[1].Ingredients[0].QuantityPerCraft));
        Assert.Equal([300u], response.TerminalMaterialItemIds);
    }

    [Fact]
    public async Task ExpandedNodeLimit_RejectsGraphBeyond1024NodesBeforeSnapshotWork()
    {
        var root = Node("node-0", 100, "Root Gear", true);
        for (var index = 1; index <= 1_024; index++)
        {
            root.Children.Add(Node($"node-{index}", 100 + index, $"Item {index}", false, root));
        }
        var snapshotService = new FixedSnapshotService(RecipeOperationSnapshot.Empty);
        var service = new CraftRecipeGraphService(
            new FixedPlanBuilder(new CraftingPlan { RootItems = [root] }),
            snapshotService,
            "contract-provider-1");

        var response = await service.BuildAsync(Request());

        Assert.False(response.IsComplete);
        Assert.Empty(response.Recipes);
        Assert.Empty(response.TerminalMaterialItemIds);
        Assert.Equal("ExpandedNodeLimitExceeded", Assert.Single(response.Diagnostics).Code);
        Assert.Equal(0, snapshotService.BuildCount);
    }

    [Fact]
    public async Task DepthLimit_RejectsGraphBeyond16EdgesBeforeSnapshotWork()
    {
        var root = Node("node-0", 100, "Root Gear", true);
        var parent = root;
        for (var depth = 1; depth <= 17; depth++)
        {
            var child = Node($"node-{depth}", 100 + depth, $"Item {depth}", true, parent);
            parent.Children.Add(child);
            parent = child;
        }
        var snapshotService = new FixedSnapshotService(RecipeOperationSnapshot.Empty);
        var service = new CraftRecipeGraphService(
            new FixedPlanBuilder(new CraftingPlan { RootItems = [root] }),
            snapshotService,
            "contract-provider-1");

        var response = await service.BuildAsync(Request());

        Assert.False(response.IsComplete);
        Assert.Empty(response.Recipes);
        Assert.Equal("DepthLimitExceeded", Assert.Single(response.Diagnostics).Code);
        Assert.Equal(0, snapshotService.BuildCount);
    }

    [Fact]
    public async Task IngredientLimit_RejectsAndBoundsRecipeBeyond32Ingredients()
    {
        var response = await CreateService(ingredientCount: 33).BuildAsync(Request());

        Assert.False(response.IsComplete);
        Assert.Contains(response.Diagnostics, diagnostic => diagnostic.Code == "InvalidIngredientCollection");
        Assert.Equal(32, Assert.Single(response.Recipes).Ingredients.Count);
    }

    [Fact]
    public async Task MalformedIngredient_IsRejectedInsteadOfSilentlyUndercounted()
    {
        var response = await CreateService(malformedIngredient: true).BuildAsync(Request());

        Assert.False(response.IsComplete);
        Assert.Contains(response.Diagnostics, diagnostic => diagnostic.Code == "InvalidIngredientCollection");
        Assert.DoesNotContain(Assert.Single(response.Recipes).Ingredients, ingredient => ingredient.ItemId == 999);
    }

    [Fact]
    public async Task UnknownUnlockEvidence_IsRejectedAsIncomplete()
    {
        var response = await CreateService(unknownUnlock: true).BuildAsync(Request());

        Assert.False(response.IsComplete);
        Assert.Contains(response.Diagnostics, diagnostic => diagnostic.Code == "UnknownRecipeUnlockEvidence");
        var recipe = Assert.Single(response.Recipes);
        Assert.Equal(0u, recipe.RecipeUnlockItemId);
        Assert.Equal(CraftRecipeUnlockEvidenceV1.Unknown, recipe.UnlockEvidence);
    }

    [Fact]
    public async Task ExactRecipeEvidence_ProducesCanonicalKnownHash()
    {
        var response = await CreateService().BuildAsync(Request());

        Assert.Equal(
            "sha256:233570B7F1D21071ADECC360E78260CA80FAB43B4AFBC3A25980862B7A7ED772",
            response.RecipeDataIdentity);
    }

    [Fact]
    public async Task SourceIngredientOrder_DoesNotChangeCanonicalHash()
    {
        var forward = await CreateService(reverseIngredients: false).BuildAsync(Request());
        var reverse = await CreateService(reverseIngredients: true).BuildAsync(Request());

        Assert.Equal(forward.RecipeDataIdentity, reverse.RecipeDataIdentity);
    }

    [Fact]
    public async Task UnsupportedRequestSchema_IsRejectedAtWireBoundary()
    {
        var request = Request() with { SchemaVersion = "craft-architect-exact-recipe-graph-request/v2" };

        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().BuildAsync(request));
    }

    [Fact]
    public void ResponseJson_PreservesVersionedCamelCaseStringEnumShape()
    {
        var response = new CraftRecipeGraphResponseV1
        {
            ProviderVersion = "contract-provider-1",
            RecipeDataIdentity = "sha256:ABC",
            RootItemId = 100,
            RootItemName = "Root Gear",
            Recipes =
            [
                new CraftRecipeDefinitionV1
                {
                    RecipeId = 1001,
                    UnlockEvidence = CraftRecipeUnlockEvidenceV1.NoUnlockRequired,
                    ResolutionConfidence = CraftRecipeResolutionConfidenceV1.Exact,
                    DataSource = CraftRecipeDataSourceV1.GarlandStandardCraft,
                },
            ],
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"schemaVersion\":\"craft-architect-exact-recipe-graph/v1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"providerId\":\"CraftArchitect\"", json, StringComparison.Ordinal);
        Assert.Contains("\"unlockEvidence\":\"NoUnlockRequired\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maximumDepth\":16", json, StringComparison.Ordinal);
    }

    private static CraftRecipeGraphRequestV1 Request() => new()
    {
        ItemId = 100,
        ItemName = "Root Gear",
    };

    private static CraftRecipeGraphService CreateService(
        bool reverseIngredients = false,
        int ingredientCount = 2,
        bool malformedIngredient = false,
        bool unknownUnlock = false)
    {
        var root = Node("root", 100, "Root Gear", true);
        List<PlanNode> children;
        List<RecipeOperationIngredient> ingredients;
        if (ingredientCount == 2)
        {
            var ore = Node("ore", 300, "Ore", false, root);
            var shard = Node("shard", 400, "Shard", false, root);
            children = [ore, shard];
            ingredients = [Ingredient(ore, 3), Ingredient(shard, 4)];
        }
        else
        {
            children = Enumerable.Range(0, ingredientCount)
                .Select(index => Node($"ingredient-{index}", 300 + index, $"Ingredient {index}", false, root))
                .ToList();
            ingredients = children.Select(child => Ingredient(child, 1)).ToList();
        }
        root.Children.AddRange(children);
        if (reverseIngredients)
        {
            ingredients.Reverse();
        }
        if (malformedIngredient)
        {
            ingredients.Add(new RecipeOperationIngredient(
                999,
                "Malformed",
                0,
                0,
                null,
                null,
                false,
                RecipeIngredientLinkStatus.NotLinked,
                0,
                null));
        }

        var operation = new RecipeOperation(
            "root",
            null,
            [],
            0,
            100,
            "Root Gear",
            1,
            AcquisitionSource.Craft,
            AcquisitionSourceReason.SystemDefault,
            false,
            true,
            RecipeOperationState.Active,
            null,
            null,
            RecipeOperationKind.StandardCraft,
            1001,
            8,
            "Carpenter",
            90,
            1,
            1,
            ingredients,
            RecipeResolutionConfidence.Exact,
            RecipeDataSourceKind.GarlandStandardCraft,
            false,
            90,
            unknownUnlock ? null : 0);
        var snapshot = new RecipeOperationSnapshot(
            [operation],
            new Dictionary<string, RecipeOperation> { [operation.NodeId] = operation },
            new Dictionary<int, IReadOnlyList<RecipeOperation>> { [operation.ResultItemId] = [operation] },
            []);
        return new CraftRecipeGraphService(
            new FixedPlanBuilder(new CraftingPlan { RootItems = [root] }),
            new FixedSnapshotService(snapshot),
            "contract-provider-1");
    }

    private static PlanNode Node(string nodeId, int itemId, string name, bool canCraft, PlanNode? parent = null) => new()
    {
        NodeId = nodeId,
        ParentNodeId = parent?.NodeId,
        Parent = parent,
        ItemId = itemId,
        Name = name,
        Quantity = 1,
        CanCraft = canCraft,
        Source = canCraft ? AcquisitionSource.Craft : AcquisitionSource.MarketBuyNq,
    };

    private static RecipeOperationIngredient Ingredient(PlanNode child, int quantity, int? totalQuantity = null) => new(
        child.ItemId,
        child.Name,
        quantity,
        totalQuantity ?? quantity,
        child.NodeId,
        child.Source,
        child.CanCraft,
        RecipeIngredientLinkStatus.Matched,
        totalQuantity ?? quantity,
        totalQuantity ?? quantity);

    private static RecipeOperation Operation(
        PlanNode node,
        uint recipeId,
        int depth,
        IReadOnlyList<string> ancestors,
        IReadOnlyList<RecipeOperationIngredient> ingredients) => new(
        node.NodeId,
        node.ParentNodeId,
        ancestors,
        depth,
        node.ItemId,
        node.Name,
        node.Quantity,
        AcquisitionSource.Craft,
        AcquisitionSourceReason.SystemDefault,
        false,
        true,
        RecipeOperationState.Active,
        null,
        null,
        RecipeOperationKind.StandardCraft,
        recipeId,
        8,
        "Carpenter",
        90,
        1,
        node.Quantity,
        ingredients,
        RecipeResolutionConfidence.Exact,
        RecipeDataSourceKind.GarlandStandardCraft,
        false,
        90,
        0);

    private sealed class FixedPlanBuilder(CraftingPlan plan) : ICoreRecipePlanBuilder
    {
        public Task<CraftingPlan> BuildPlanAsync(
            List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
            string dataCenter,
            string world,
            CancellationToken ct = default) => Task.FromResult(plan);

        public Task FetchVendorPricesAsync(CraftingPlan value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedSnapshotService(RecipeOperationSnapshot snapshot) : IRecipeOperationSnapshotService
    {
        public int BuildCount { get; private set; }

        public Task<RecipeOperationSnapshot> BuildAsync(CraftingPlan? plan, CancellationToken ct = default)
        {
            BuildCount++;
            return Task.FromResult(snapshot);
        }

        public Task<RecipeOperationSnapshot> BuildAsync(
            CraftingPlan? plan,
            RecipeOperationSnapshotIdentity identity,
            RecipeOperationSnapshotBuildOptions? options = null,
            CancellationToken ct = default)
        {
            BuildCount++;
            return Task.FromResult(snapshot);
        }
    }
}
