using System.Net;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class RecipeSpecificationTests
{
    [Theory]
    [InlineData(2, 2, 5, 2, 3)]
    [InlineData(1, 0, 5, 1, 5)]
    public void RecipeYieldDeterminesCeilingCraftCount(
        int nodeYield,
        int recipeYield,
        int quantity,
        int expectedYield,
        int expectedCraftCount)
    {
        var result = new RecipeResolutionService().Resolve(
            CraftNode("Blacksmith", level: 80, yield: nodeYield, quantity),
            Item(Craft("1000", level: 80, jobId: 9, yield: recipeYield)));

        Assert.Equal(expectedYield, result.Yield);
        Assert.Equal(expectedCraftCount, result.CraftCount);
    }

    [Theory]
    [InlineData(RecipeResolutionScenario.Exact)]
    [InlineData(RecipeResolutionScenario.Ambiguous)]
    [InlineData(RecipeResolutionScenario.NonNumeric)]
    public void RecipeResolutionUsesStableIdentityAndReportsAmbiguity(
        RecipeResolutionScenario scenario)
    {
        switch (scenario)
        {
            case RecipeResolutionScenario.Exact:
                ExactRecipeResolutionUsesJobLevelAndYieldTogether();
                break;
            case RecipeResolutionScenario.Ambiguous:
                AmbiguousExactRecipeResolutionUsesLowestNumericId();
                break;
            case RecipeResolutionScenario.NonNumeric:
                NonNumericRecipeIdentityRemainsUnresolved();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static void ExactRecipeResolutionUsesJobLevelAndYieldTogether()
    {
        var result = new RecipeResolutionService().Resolve(
            CraftNode("Blacksmith", level: 43, yield: 2, quantity: 4),
            Item(
                Craft("300", level: 43, jobId: 10, yield: 2),
                Craft("200", level: 44, jobId: 9, yield: 2),
                Craft("100", level: 43, jobId: 9, yield: 2)));

        Assert.Equal(RecipeResolutionConfidence.Exact, result.Confidence);
        Assert.Equal(100u, result.RecipeId);
    }

    private static void AmbiguousExactRecipeResolutionUsesLowestNumericId()
    {
        var result = new RecipeResolutionService().Resolve(
            CraftNode("Blacksmith", level: 43, yield: 1, quantity: 2),
            Item(
                Craft("200", level: 43, jobId: 9, yield: 1),
                Craft("100", level: 43, jobId: 9, yield: 1)));

        Assert.Equal(RecipeResolutionConfidence.AmbiguousExact, result.Confidence);
        Assert.Equal(100u, result.RecipeId);
        Assert.Equal(RecipeOperationDiagnosticCode.AmbiguousRecipe, Assert.Single(result.Diagnostics).Code);
    }

    private static void NonNumericRecipeIdentityRemainsUnresolved()
    {
        var result = new RecipeResolutionService().Resolve(
            CraftNode("Blacksmith", level: 43, yield: 1, quantity: 2),
            Item(Craft("not-numeric", level: 43, jobId: 9, yield: 1)));

        Assert.False(result.IsResolved);
        Assert.Equal(RecipeResolutionConfidence.NonNumericRecipeId, result.Confidence);
    }

    [Theory]
    [InlineData(AncestorSourceScenario.BoughtAncestor)]
    [InlineData(AncestorSourceScenario.SuppressedPrecraftIntent)]
    public async Task AncestorSourceChangesPreserveDescendantsAndTheirIntent(
        AncestorSourceScenario scenario)
    {
        switch (scenario)
        {
            case AncestorSourceScenario.BoughtAncestor:
                await BoughtAncestorSuppressesAllCraftableDescendants();
                break;
            case AncestorSourceScenario.SuppressedPrecraftIntent:
                await SuppressedPrecraftSourceIntentActivatesWhenItsAncestorIsCrafted();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static async Task BoughtAncestorSuppressesAllCraftableDescendants()
    {
        var root = CraftNode("Carpenter", level: 90, yield: 2, quantity: 1, itemId: 100, nodeId: "root");
        root.Source = AcquisitionSource.MarketBuyNq;
        var child = CraftNode("Blacksmith", level: 80, yield: 2, quantity: 3, itemId: 200, nodeId: "child");
        var grandchild = CraftNode("Armorer", level: 70, yield: 1, quantity: 6, itemId: 300, nodeId: "grandchild");
        root.Children = [child];
        child.Children = [grandchild];
        child.Parent = root;
        grandchild.Parent = child;

        var snapshot = await SnapshotService().BuildAsync(new CraftingPlan { RootItems = [root] });
        var descendants = snapshot.Operations
            .Where(operation => operation.NodeId != root.NodeId)
            .OrderBy(operation => operation.NodeId)
            .ToList();

        Assert.Equal(2, descendants.Count);
        Assert.Equal(["child", "grandchild"], descendants.Select(operation => operation.NodeId).ToArray());
        Assert.All(descendants, operation => Assert.Equal(RecipeOperationState.SuppressedByAncestor, operation.State));
        Assert.All(descendants, operation => Assert.Equal(root.NodeId, operation.SuppressedByNodeId));
    }

    private static async Task SuppressedPrecraftSourceIntentActivatesWhenItsAncestorIsCrafted()
    {
        var root = CraftNode("Carpenter", level: 90, yield: 2, quantity: 1, itemId: 100, nodeId: "root");
        root.Source = AcquisitionSource.MarketBuyNq;
        var precraft = CraftNode("Blacksmith", level: 80, yield: 2, quantity: 3, itemId: 200, nodeId: "precraft");
        precraft.Source = AcquisitionSource.MarketBuyNq;
        var ingredient = CraftNode("Armorer", level: 70, yield: 1, quantity: 6, itemId: 300, nodeId: "ingredient");
        root.Children = [precraft];
        precraft.Children = [ingredient];
        precraft.Parent = root;
        ingredient.Parent = precraft;
        var plan = new CraftingPlan { RootItems = [root] };

        Assert.Equal(
            1,
            AcquisitionDecisionMutation.ChangeSource(
                plan,
                precraft.ItemId,
                AcquisitionSource.Craft));
        var stillSuppressed = await SnapshotService().BuildAsync(plan);
        Assert.Equal(
            RecipeOperationState.SuppressedByAncestor,
            stillSuppressed.OperationsByNodeId[precraft.NodeId].State);
        Assert.Equal(AcquisitionSource.Craft, precraft.Source);

        AcquisitionDecisionMutation.ChangeSource(
            plan,
            root.ItemId,
            AcquisitionSource.Craft);
        var activated = await SnapshotService().BuildAsync(plan);

        Assert.Equal(RecipeOperationState.Active, activated.OperationsByNodeId[precraft.NodeId].State);
        Assert.Equal(RecipeOperationState.Active, activated.OperationsByNodeId[ingredient.NodeId].State);
    }

    [Fact]
    public async Task RepeatedItemOccurrencesKeepIndependentNodeIdentity()
    {
        var first = CraftNode("Blacksmith", 43, 1, 2, itemId: 5059, nodeId: "occurrence-a");
        var second = CraftNode("Blacksmith", 43, 1, 3, itemId: 5059, nodeId: "occurrence-b");

        var snapshot = await SnapshotService().BuildAsync(
            new CraftingPlan { RootItems = [first, second] });

        Assert.Equal(2, snapshot.OperationsByItemId[5059].Count);
        Assert.Equal(2, snapshot.OperationsByNodeId.Count);
        Assert.Equal(["occurrence-a", "occurrence-b"],
            snapshot.OperationsByItemId[5059].Select(operation => operation.NodeId).Order().ToArray());
    }

    [Fact]
    public async Task IngredientDemandUsesCeilingCraftCount()
    {
        var root = CraftNode("Carpenter", level: 90, yield: 2, quantity: 5, itemId: 100, nodeId: "root");
        var child = new PlanNode
        {
            ItemId = 200,
            Name = "Ingredient",
            NodeId = "ingredient",
            Quantity = 9,
            Source = AcquisitionSource.MarketBuyNq,
            Parent = root
        };
        root.Children = [child];

        var snapshot = await SnapshotService().BuildAsync(new CraftingPlan { RootItems = [root] });
        var operation = snapshot.OperationsByNodeId[root.NodeId];
        var ingredient = Assert.Single(operation.Ingredients);

        Assert.Equal(3, operation.CraftCount);
        Assert.Equal(9, ingredient.TotalQuantity);
        Assert.Equal(RecipeIngredientLinkStatus.Matched, ingredient.LinkStatus);
    }

    private static PlanNode CraftNode(
        string job,
        int level,
        int yield,
        int quantity,
        int itemId = 500,
        string nodeId = "node") => new()
        {
            ItemId = itemId,
            Name = $"Item {itemId}",
            NodeId = nodeId,
            Quantity = quantity,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanCraft = true,
            Job = job,
            RecipeLevel = level,
            Yield = yield
        };

    private static GarlandItem Item(params GarlandCraft[] crafts) => new()
    {
        Id = 500,
        Name = "Recipe item",
        Crafts = [.. crafts]
    };

    private static GarlandCraft Craft(string id, int level, int jobId, int yield) => new()
    {
        Id = id,
        RecipeLevel = level,
        JobId = jobId,
        Yield = yield,
        Ingredients = []
    };

    private static RecipeOperationSnapshotService SnapshotService()
    {
        var garland = new GarlandService(new HttpClient(new RecipeHandler()), null!);
        return new RecipeOperationSnapshotService(garland, new RecipeResolutionService(), null!);
    }

    private sealed class RecipeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var itemId = request.RequestUri?.Segments.LastOrDefault()?
                .Replace(".json", string.Empty, StringComparison.Ordinal);
            var json = itemId switch
            {
                "100" => ItemJson(100, "Root", 1000, 90, 8, 2, [(200, 3, "Ingredient")]),
                "200" => ItemJson(200, "Child", 2000, 80, 9, 2, [(300, 2, "Subingredient")]),
                "300" => ItemJson(300, "Grandchild", 3000, 70, 10, 1, []),
                "5059" => ItemJson(5059, "Cobalt Ingot", 153, 43, 9, 1, []),
                _ => """{"item":{"id":0,"name":"Unknown","craft":[]}}"""
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }

        private static string ItemJson(
            int itemId,
            string name,
            int recipeId,
            int level,
            int jobId,
            int yield,
            IReadOnlyList<(int ItemId, int Amount, string Name)> ingredients)
        {
            var ingredientJson = string.Join(",", ingredients.Select(ingredient =>
                $$"""{"id":{{ingredient.ItemId}},"amount":{{ingredient.Amount}},"name":"{{ingredient.Name}}"}"""));
            return $$$"""
                {"item":{"id":{{{itemId}}},"name":"{{{name}}}","craft":[{"id":"{{{recipeId}}}","rlvl":{{{level}}},"job":{{{jobId}}},"yield":{{{yield}}},"ingredients":[{{{ingredientJson}}}]}]}}
                """;
        }
    }

    public enum RecipeResolutionScenario
    {
        Exact,
        Ambiguous,
        NonNumeric
    }

    public enum AncestorSourceScenario
    {
        BoughtAncestor,
        SuppressedPrecraftIntent
    }
}
