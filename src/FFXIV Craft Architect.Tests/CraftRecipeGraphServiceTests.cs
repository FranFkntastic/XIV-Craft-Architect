using System.Text.Json;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Tests;

public sealed class CraftRecipeGraphServiceTests
{
    [Fact]
    public async Task BuildAsync_ProjectsCompleteExactGraphFromCorePlanAndSnapshot()
    {
        var fixture = CompleteFixture();
        var planBuilder = new StubPlanBuilder(fixture.Plan);
        var service = new CraftRecipeGraphService(
            planBuilder,
            new StubSnapshotService(fixture.Snapshot),
            "test-provider-1");

        var response = await service.BuildAsync(new CraftRecipeGraphRequestV1
        {
            ItemId = 100,
            ItemName = "Caller Supplied Name",
        });

        Assert.True(response.IsComplete);
        Assert.Empty(response.Diagnostics);
        Assert.Equal(CraftRecipeGraphResponseV1.CurrentSchemaVersion, response.SchemaVersion);
        Assert.Equal("test-provider-1", response.ProviderVersion);
        Assert.StartsWith("sha256:", response.RecipeDataIdentity, StringComparison.Ordinal);
        Assert.Equal(100u, response.RootItemId);
        Assert.Equal("Root Gear", response.RootItemName);
        Assert.Equal([300u, 400u], response.TerminalMaterialItemIds);
        Assert.Equal(2, response.Recipes.Count);
        Assert.Equal(16, response.Limits.MaximumDepth);
        Assert.Equal(1_024, response.Limits.MaximumExpandedNodeCount);

        var rootRecipe = Assert.Single(response.Recipes, recipe => recipe.RecipeId == 1_001);
        Assert.Equal(100u, rootRecipe.OutputItemId);
        Assert.Equal(1u, rootRecipe.OutputQuantity);
        Assert.Equal(8u, rootRecipe.RequiredClassJobId);
        Assert.Equal("Carpenter", rootRecipe.RequiredClassJobName);
        Assert.Equal(90, rootRecipe.RequiredLevel);
        Assert.Equal(0u, rootRecipe.RecipeUnlockItemId);
        Assert.Equal(CraftRecipeUnlockEvidenceV1.NoUnlockRequired, rootRecipe.UnlockEvidence);
        Assert.Equal(CraftRecipeResolutionConfidenceV1.Exact, rootRecipe.ResolutionConfidence);
        Assert.Equal(CraftRecipeDataSourceV1.GarlandStandardCraft, rootRecipe.DataSource);
        Assert.Equal(
            [(200u, 2u), (300u, 3u)],
            rootRecipe.Ingredients.Select(ingredient => (ingredient.ItemId, ingredient.QuantityPerCraft)));

        var target = Assert.Single(planBuilder.TargetItems);
        Assert.Equal((100, "Caller Supplied Name", 1, false), target);
    }

    [Fact]
    public async Task BuildAsync_UnknownUnlockEvidence_ReturnsGraphButFailsClosed()
    {
        var fixture = CompleteFixture();
        var unknownUnlock = fixture.Snapshot with
        {
            Operations = fixture.Snapshot.Operations
                .Select(operation => operation.RecipeId == 1_001
                    ? operation with { RecipeUnlockItemId = null }
                    : operation)
                .ToList(),
        };
        var service = new CraftRecipeGraphService(
            new StubPlanBuilder(fixture.Plan),
            new StubSnapshotService(unknownUnlock),
            "test-provider-1");

        var response = await service.BuildAsync(new CraftRecipeGraphRequestV1 { ItemId = 100 });

        Assert.False(response.IsComplete);
        Assert.Contains(response.Diagnostics, diagnostic => diagnostic.Code == "UnknownRecipeUnlockEvidence");
        var recipe = Assert.Single(response.Recipes, value => value.RecipeId == 1_001);
        Assert.Equal(0u, recipe.RecipeUnlockItemId);
        Assert.Equal(CraftRecipeUnlockEvidenceV1.Unknown, recipe.UnlockEvidence);
    }

    [Fact]
    public async Task BuildAsync_FallbackOrStructuralResolution_ReturnsDiagnosticIncompleteResponse()
    {
        var fixture = CompleteFixture();
        var structuralDiagnostic = new RecipeOperationDiagnostic(
            "root",
            300,
            "Ore",
            RecipeOperationDiagnosticSeverity.Error,
            "Ingredient quantity did not match the expanded plan.",
            RecipeOperationDiagnosticCode.IngredientChildQuantityMismatch,
            1_001,
            "root");
        var invalidSnapshot = fixture.Snapshot with
        {
            Operations = fixture.Snapshot.Operations
                .Select(operation => operation.RecipeId == 1_001
                    ? operation with
                    {
                        ResolutionConfidence = RecipeResolutionConfidence.FallbackByJob,
                        HasStructuralDiagnostics = true,
                        Ingredients = operation.Ingredients
                            .Select(ingredient => ingredient.ItemId == 300
                                ? ingredient with { LinkStatus = RecipeIngredientLinkStatus.QuantityMismatch }
                                : ingredient)
                            .ToList(),
                    }
                    : operation)
                .ToList(),
            Diagnostics = [structuralDiagnostic],
        };
        var service = new CraftRecipeGraphService(
            new StubPlanBuilder(fixture.Plan),
            new StubSnapshotService(invalidSnapshot),
            "test-provider-1");

        var response = await service.BuildAsync(new CraftRecipeGraphRequestV1 { ItemId = 100 });

        Assert.False(response.IsComplete);
        Assert.Contains(response.Diagnostics, diagnostic => diagnostic.Code == "IngredientChildQuantityMismatch");
        var recipe = Assert.Single(response.Recipes, value => value.RecipeId == 1_001);
        Assert.Equal(CraftRecipeResolutionConfidenceV1.Fallback, recipe.ResolutionConfidence);
        Assert.Contains(recipe.StructuralDiagnostics, diagnostic => diagnostic.Code == "IngredientChildQuantityMismatch");
    }

    [Fact]
    public async Task BuildAsync_NonPositiveIngredientFailsClosedInsteadOfSilentlyUndercounting()
    {
        var fixture = CompleteFixture();
        var invalidSnapshot = fixture.Snapshot with
        {
            Operations = fixture.Snapshot.Operations
                .Select(operation => operation.RecipeId == 1_001
                    ? operation with
                    {
                        Ingredients =
                        [
                            .. operation.Ingredients,
                            new RecipeOperationIngredient(999, "Invalid", 0, 0, null, null, false,
                                RecipeIngredientLinkStatus.NotLinked, 0, null),
                        ],
                    }
                    : operation)
                .ToList(),
        };
        var service = new CraftRecipeGraphService(
            new StubPlanBuilder(fixture.Plan),
            new StubSnapshotService(invalidSnapshot),
            "test-provider-1");

        var response = await service.BuildAsync(new CraftRecipeGraphRequestV1 { ItemId = 100 });

        Assert.False(response.IsComplete);
        Assert.Contains(response.Diagnostics, diagnostic => diagnostic.Code == "InvalidIngredientCollection");
        Assert.DoesNotContain(
            Assert.Single(response.Recipes, recipe => recipe.RecipeId == 1_001).Ingredients,
            ingredient => ingredient.ItemId == 999);
    }

    [Fact]
    public async Task BuildAsync_RejectsUnsupportedRequestVersionBeforeBuildingPlan()
    {
        var fixture = CompleteFixture();
        var planBuilder = new StubPlanBuilder(fixture.Plan);
        var service = new CraftRecipeGraphService(
            planBuilder,
            new StubSnapshotService(fixture.Snapshot),
            "test-provider-1");

        await Assert.ThrowsAsync<ArgumentException>(() => service.BuildAsync(new CraftRecipeGraphRequestV1
        {
            SchemaVersion = "future/v2",
            ItemId = 100,
        }));

        Assert.Empty(planBuilder.TargetItems);
    }

    [Fact]
    public async Task BuildAsync_DepthLimitExceeded_ReturnsBoundedFailureWithoutSnapshotWork()
    {
        var root = Node("node-0", 100, "Root Gear", 1, canCraft: true);
        var parent = root;
        for (var depth = 1; depth <= 17; depth++)
        {
            var child = Node($"node-{depth}", 100 + depth, $"Item {depth}", 1, canCraft: true, parent);
            parent.Children.Add(child);
            parent = child;
        }

        var snapshotService = new StubSnapshotService(RecipeOperationSnapshot.Empty);
        var service = new CraftRecipeGraphService(
            new StubPlanBuilder(new CraftingPlan { RootItems = [root] }),
            snapshotService,
            "test-provider-1");

        var response = await service.BuildAsync(new CraftRecipeGraphRequestV1 { ItemId = 100 });

        Assert.False(response.IsComplete);
        Assert.Empty(response.Recipes);
        Assert.Empty(response.TerminalMaterialItemIds);
        Assert.Contains(response.Diagnostics, diagnostic => diagnostic.Code == "DepthLimitExceeded");
        Assert.Equal(0, snapshotService.BuildCount);
    }

    [Fact]
    public void Response_SerializesVersionedPublicShapeWithStringEvidenceEnums()
    {
        var response = new CraftRecipeGraphResponseV1
        {
            ProviderVersion = "1.2.3",
            RecipeDataIdentity = "sha256:ABC",
            RootItemId = 100,
            RootItemName = "Root Gear",
            Recipes =
            [
                new CraftRecipeDefinitionV1
                {
                    RecipeId = 1_001,
                    UnlockEvidence = CraftRecipeUnlockEvidenceV1.NoUnlockRequired,
                    ResolutionConfidence = CraftRecipeResolutionConfidenceV1.Exact,
                    DataSource = CraftRecipeDataSourceV1.GarlandStandardCraft,
                },
            ],
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        Assert.Contains("\"schemaVersion\":\"craft-architect-exact-recipe-graph/v1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"unlockEvidence\":\"NoUnlockRequired\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resolutionConfidence\":\"Exact\"", json, StringComparison.Ordinal);
        Assert.Contains("\"maximumExpandedNodeCount\":1024", json, StringComparison.Ordinal);
    }

    private static (CraftingPlan Plan, RecipeOperationSnapshot Snapshot) CompleteFixture()
    {
        var root = Node("root", 100, "Root Gear", 1, canCraft: true);
        var precraft = Node("precraft", 200, "Lumber", 2, canCraft: true, root);
        var ore = Node("ore", 300, "Ore", 3, canCraft: false, root);
        var shard = Node("shard", 400, "Shard", 4, canCraft: false, precraft);
        root.Children.Add(precraft);
        root.Children.Add(ore);
        precraft.Children.Add(shard);
        var plan = new CraftingPlan { RootItems = [root] };

        var rootOperation = Operation(
            root,
            recipeId: 1_001,
            jobId: 8,
            jobName: "Carpenter",
            level: 90,
            yield: 1,
            unlockItemId: 0,
            ingredients:
            [
                Ingredient(precraft, 2),
                Ingredient(ore, 3),
            ]);
        var precraftOperation = Operation(
            precraft,
            recipeId: 1_002,
            jobId: 9,
            jobName: "Blacksmith",
            level: 40,
            yield: 2,
            unlockItemId: 12_345,
            ingredients: [Ingredient(shard, 4)]);
        var operations = new List<RecipeOperation> { rootOperation, precraftOperation };
        var snapshot = new RecipeOperationSnapshot(
            operations,
            operations.ToDictionary(operation => operation.NodeId),
            operations.GroupBy(operation => operation.ResultItemId).ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<RecipeOperation>)group.ToList()),
            []);
        return (plan, snapshot);
    }

    private static PlanNode Node(
        string nodeId,
        int itemId,
        string name,
        int quantity,
        bool canCraft,
        PlanNode? parent = null) => new()
        {
            NodeId = nodeId,
            ParentNodeId = parent?.NodeId,
            Parent = parent,
            ItemId = itemId,
            Name = name,
            Quantity = quantity,
            CanCraft = canCraft,
            Source = canCraft ? AcquisitionSource.Craft : AcquisitionSource.MarketBuyNq,
        };

    private static RecipeOperation Operation(
        PlanNode node,
        uint recipeId,
        int jobId,
        string jobName,
        int level,
        int yield,
        int? unlockItemId,
        IReadOnlyList<RecipeOperationIngredient> ingredients) => new(
            NodeId: node.NodeId,
            ParentNodeId: node.ParentNodeId,
            AncestorNodeIds: [],
            Depth: node.Parent == null ? 0 : 1,
            ResultItemId: node.ItemId,
            ResultItemName: node.Name,
            RequestedQuantity: node.Quantity,
            Source: AcquisitionSource.Craft,
            SourceReason: AcquisitionSourceReason.SystemDefault,
            MustBeHq: false,
            CanCraft: true,
            State: RecipeOperationState.Active,
            SuppressedByNodeId: null,
            SuppressedByItemName: null,
            Kind: RecipeOperationKind.StandardCraft,
            RecipeId: recipeId,
            JobId: jobId,
            JobName: jobName,
            RecipeLevel: level,
            Yield: yield,
            CraftCount: 1,
            Ingredients: ingredients,
            ResolutionConfidence: RecipeResolutionConfidence.Exact,
            RecipeDataSource: RecipeDataSourceKind.GarlandStandardCraft,
            HasStructuralDiagnostics: false,
            RecipeDisplayLevel: level,
            RecipeUnlockItemId: unlockItemId);

    private static RecipeOperationIngredient Ingredient(PlanNode child, int amountPerCraft) => new(
        child.ItemId,
        child.Name,
        amountPerCraft,
        child.Quantity,
        child.NodeId,
        child.Source,
        child.CanCraft,
        RecipeIngredientLinkStatus.Matched,
        child.Quantity,
        child.Quantity);

    private sealed class StubPlanBuilder(CraftingPlan plan) : ICoreRecipePlanBuilder
    {
        public List<(int itemId, string name, int quantity, bool isHqRequired)> TargetItems { get; } = [];

        public Task<CraftingPlan> BuildPlanAsync(
            List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
            string dataCenter,
            string world,
            CancellationToken ct = default)
        {
            TargetItems.AddRange(targetItems);
            return Task.FromResult(plan);
        }

        public Task FetchVendorPricesAsync(CraftingPlan value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubSnapshotService(RecipeOperationSnapshot snapshot) : IRecipeOperationSnapshotService
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
