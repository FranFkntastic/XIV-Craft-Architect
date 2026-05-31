using System.Net;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class RecipeOperationSnapshotServiceTests
{
    [Fact]
    public async Task BuildAsync_SameResultItemWithDifferentJobs_PreservesRecipeIdentityPerNode()
    {
        var service = CreateService();
        var blacksmithNode = new PlanNode
        {
            ItemId = 5059,
            Name = "Cobalt Ingot",
            Quantity = 432,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 43,
            Job = "Blacksmith",
            Yield = 1
        };
        var armorerNode = new PlanNode
        {
            ItemId = 5059,
            Name = "Cobalt Ingot",
            Quantity = 384,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 43,
            Job = "Armorer",
            Yield = 1
        };
        var plan = new CraftingPlan { RootItems = [blacksmithNode, armorerNode] };

        var snapshot = await service.BuildAsync(plan);

        Assert.Collection(
            snapshot.Operations.OrderBy(operation => operation.JobId),
            operation =>
            {
                Assert.Equal(153u, operation.RecipeId);
                Assert.Equal(432, operation.CraftCount);
            },
            operation =>
            {
                Assert.Equal(273u, operation.RecipeId);
                Assert.Equal(384, operation.CraftCount);
            });
    }

    [Fact]
    public async Task BuildAsync_RepeatedItemIds_TracksMetadataAndPerBuildCache()
    {
        var handler = new GarlandItemHandler();
        var service = CreateService(handler);
        var first = new PlanNode
        {
            ItemId = 200,
            Name = "First Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 80,
            Job = "Blacksmith",
            Yield = 2
        };
        var second = new PlanNode
        {
            ItemId = 200,
            Name = "Second Craft",
            Quantity = 2,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 80,
            Job = "Blacksmith",
            Yield = 2
        };
        var identity = new RecipeOperationSnapshotIdentity(
            PlanSessionVersion: 12,
            PlanStructureVersion: 4,
            PlanDecisionVersion: 5,
            PlanPriceVersion: 6,
            SettingsVersion: 7,
            RecipeDataIdentity: "test-garland");
        var plan = new CraftingPlan { RootItems = [first, second] };

        var snapshot = await service.BuildAsync(plan, identity);

        Assert.Equal(identity, snapshot.Metadata.Identity);
        Assert.Equal(2, snapshot.Metadata.NodeCount);
        Assert.Equal(1, snapshot.Metadata.UniqueItemIdCount);
        Assert.Equal(1, snapshot.Metadata.RecipeDataCalls);
        Assert.Equal(1, snapshot.Metadata.RecipeDataCacheHits);
        Assert.Equal(1, handler.RequestCountByItemId[200]);
        Assert.True(snapshot.Metadata.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task BuildAsync_CraftTree_TracksActiveAndSuppressedOperationSnippets()
    {
        var service = CreateService();
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root Craft",
            Quantity = 3,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 90,
            Job = "Carpenter",
            Yield = 1
        };
        var craftedChild = new PlanNode
        {
            ItemId = 200,
            Name = "Crafted Child",
            Quantity = 6,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 80,
            Job = "Blacksmith",
            Yield = 2,
            Parent = root
        };
        var boughtChild = new PlanNode
        {
            ItemId = 300,
            Name = "Bought Child",
            Quantity = 9,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = true,
            RecipeLevel = 70,
            Job = "Armorer",
            Yield = 1,
            Parent = root
        };
        root.Children.Add(craftedChild);
        root.Children.Add(boughtChild);
        var plan = new CraftingPlan { RootItems = [root] };

        var snapshot = await service.BuildAsync(plan);

        var activeOperations = snapshot.GetActiveOperations().OrderBy(operation => operation.ResultItemId).ToList();
        Assert.Equal([100, 200], activeOperations.Select(operation => operation.ResultItemId));

        var boughtOperation = Assert.Single(snapshot.Operations, operation => operation.ResultItemId == 300);
        Assert.Equal(RecipeOperationState.InactiveBySource, boughtOperation.State);
        Assert.True(boughtOperation.IsCraftableReference);
    }

    [Fact]
    public async Task BuildAsync_CraftOperation_CalculatesIngredientDemandsFromCraftCount()
    {
        var service = CreateService();
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root Craft",
            Quantity = 5,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 90,
            Job = "Carpenter",
            Yield = 2
        };
        var child = new PlanNode
        {
            ItemId = 900,
            Name = "Ingredient",
            Quantity = 9,
            Source = AcquisitionSource.MarketBuyNq,
            Parent = root
        };
        root.Children.Add(child);
        var plan = new CraftingPlan { RootItems = [root] };

        var snapshot = await service.BuildAsync(plan);

        var operation = Assert.Single(snapshot.Operations);
        Assert.Equal(3, operation.CraftCount);
        var ingredient = Assert.Single(operation.Ingredients);
        Assert.Equal(900, ingredient.ItemId);
        Assert.Equal(3, ingredient.AmountPerCraft);
        Assert.Equal(9, ingredient.TotalQuantity);
        Assert.Equal(child.NodeId, ingredient.ChildNodeId);
    }

    [Fact]
    public async Task BuildAsync_AmbiguousRecipe_PropagatesResolutionConfidenceAndDiagnostic()
    {
        var service = CreateService();
        var root = new PlanNode
        {
            ItemId = 400,
            Name = "Ambiguous Craft",
            Quantity = 2,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 50,
            Job = "Blacksmith",
            Yield = 1
        };
        var plan = new CraftingPlan { RootItems = [root] };

        var snapshot = await service.BuildAsync(plan);

        var operation = Assert.Single(snapshot.Operations);
        Assert.Equal(4100u, operation.RecipeId);
        Assert.Equal(RecipeResolutionConfidence.AmbiguousExact, operation.ResolutionConfidence);
        Assert.Equal(RecipeDataSourceKind.GarlandStandardCraft, operation.RecipeDataSource);
        Assert.True(operation.HasStructuralDiagnostics);
        var diagnostic = Assert.Single(snapshot.Diagnostics);
        Assert.Equal(RecipeOperationDiagnosticCode.AmbiguousRecipe, diagnostic.Code);
        Assert.Equal(operation.NodeId, diagnostic.OperationNodeId);
        Assert.Equal(4100u, diagnostic.RecipeId);
    }

    [Fact]
    public async Task BuildAsync_IngredientQuantityMismatch_AddsReadOnlyStructuralDiagnostic()
    {
        var service = CreateService();
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root Craft",
            Quantity = 5,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 90,
            Job = "Carpenter",
            Yield = 2
        };
        var child = new PlanNode
        {
            ItemId = 900,
            Name = "Ingredient",
            Quantity = 8,
            Source = AcquisitionSource.MarketBuyNq,
            Parent = root
        };
        root.Children.Add(child);
        var plan = new CraftingPlan { RootItems = [root] };

        var snapshot = await service.BuildAsync(plan);

        var operation = Assert.Single(snapshot.Operations);
        Assert.True(operation.HasStructuralDiagnostics);
        var ingredient = Assert.Single(operation.Ingredients);
        Assert.Equal(RecipeIngredientLinkStatus.QuantityMismatch, ingredient.LinkStatus);
        Assert.Equal(9, ingredient.ExpectedTotalQuantity);
        Assert.Equal(8, ingredient.PlanChildQuantity);
        var diagnostic = Assert.Single(snapshot.Diagnostics);
        Assert.Equal(RecipeOperationDiagnosticCode.IngredientChildQuantityMismatch, diagnostic.Code);
        Assert.Equal(RecipeOperationDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public async Task BuildAsync_ChildWithoutParentPointer_UsesTraversalParent()
    {
        var service = CreateService();
        var root = new PlanNode
        {
            ItemId = 200,
            Name = "Root Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 80,
            Job = "Blacksmith",
            Yield = 2
        };
        var child = new PlanNode
        {
            ItemId = 300,
            Name = "Child Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 70,
            Job = "Armorer",
            Yield = 1
        };
        root.Children.Add(child);
        var plan = new CraftingPlan { RootItems = [root] };

        var snapshot = await service.BuildAsync(plan);

        var childOperation = Assert.Single(snapshot.Operations, operation => operation.ResultItemId == 300);
        Assert.Equal(root.NodeId, childOperation.ParentNodeId);
        Assert.False(childOperation.IsRoot);
    }

    [Fact]
    public async Task BuildAsync_StaleParentPointer_AddsParentMismatchDiagnostic()
    {
        var service = CreateService();
        var root = new PlanNode
        {
            ItemId = 200,
            Name = "Root Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 80,
            Job = "Blacksmith",
            Yield = 2
        };
        var staleParent = new PlanNode
        {
            ItemId = 999,
            Name = "Stale Parent",
            Quantity = 1
        };
        var child = new PlanNode
        {
            ItemId = 300,
            Name = "Child Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 70,
            Job = "Armorer",
            Yield = 1,
            Parent = staleParent
        };
        root.Children.Add(child);
        var plan = new CraftingPlan { RootItems = [root] };

        var snapshot = await service.BuildAsync(plan);

        var diagnostic = Assert.Single(
            snapshot.Diagnostics,
            item => item.Code == RecipeOperationDiagnosticCode.ParentLinkMismatch);
        Assert.Equal(child.NodeId, diagnostic.NodeId);
        Assert.Equal(root.NodeId, diagnostic.Details!["expectedParentNodeId"]);
        Assert.Equal(staleParent.NodeId, diagnostic.Details["actualParentNodeId"]);
    }

    [Fact]
    public async Task BuildAsync_DuplicateNodeIds_MarksNodeIndexIncompleteAndAddsDiagnostics()
    {
        var service = CreateService();
        var first = new PlanNode
        {
            ItemId = 200,
            Name = "First Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 80,
            Job = "Blacksmith",
            Yield = 2
        };
        var second = new PlanNode
        {
            ItemId = 300,
            Name = "Second Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 70,
            Job = "Armorer",
            Yield = 1
        };
        second.NodeId = first.NodeId;
        var plan = new CraftingPlan { RootItems = [first, second] };

        var snapshot = await service.BuildAsync(plan);

        Assert.False(snapshot.IsNodeIndexComplete);
        Assert.Equal(2, snapshot.Operations.Count);
        Assert.Empty(snapshot.OperationsByNodeId);
        Assert.Contains(snapshot.Diagnostics, diagnostic =>
            diagnostic.Code == RecipeOperationDiagnosticCode.DuplicateNodeId &&
            diagnostic.NodeId == first.NodeId);
    }

    [Fact]
    public async Task BuildAsync_ExtraChildNotInRecipe_AddsStructuralDiagnostic()
    {
        var service = CreateService();
        var root = new PlanNode
        {
            ItemId = 200,
            Name = "Root Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 80,
            Job = "Blacksmith",
            Yield = 2
        };
        root.Children.Add(new PlanNode
        {
            ItemId = 901,
            Name = "Extra Child",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq
        });
        var plan = new CraftingPlan { RootItems = [root] };

        var snapshot = await service.BuildAsync(plan);

        var operation = Assert.Single(snapshot.Operations);
        Assert.True(operation.HasStructuralDiagnostics);
        var diagnostic = Assert.Single(snapshot.Diagnostics);
        Assert.Equal(RecipeOperationDiagnosticCode.ExtraChildNotInRecipe, diagnostic.Code);
        Assert.Equal(901, diagnostic.ItemId);
    }

    [Fact]
    public async Task BuildAsync_CancellationDuringItemLoad_PropagatesCancellation()
    {
        var service = CreateService(new CancelingItemHandler());
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 100,
                    Name = "Root Craft",
                    Quantity = 1,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true
                }
            ]
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.BuildAsync(plan));
    }

    [Fact]
    public async Task BuildAsync_CancellationBeforeTraversal_PropagatesCancellation()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 100,
                    Name = "Root Craft",
                    Quantity = 1,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true
                }
            ]
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.BuildAsync(plan, cts.Token));
    }

    [Fact]
    public async Task BuildAsync_SameItemChildren_MatchesExpectedQuantitiesBeforeTraversalOrder()
    {
        var service = CreateService();
        var root = new PlanNode
        {
            ItemId = 1000,
            Name = "Duplicate Ingredient Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 90,
            Job = "Carpenter",
            Yield = 1
        };
        var childForTwo = new PlanNode
        {
            ItemId = 900,
            Name = "Ingredient For Two",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyNq,
            Parent = root
        };
        var childForOne = new PlanNode
        {
            ItemId = 900,
            Name = "Ingredient For One",
            Quantity = 1,
            Source = AcquisitionSource.MarketBuyNq,
            Parent = root
        };
        root.Children.Add(childForTwo);
        root.Children.Add(childForOne);
        var plan = new CraftingPlan { RootItems = [root] };

        var snapshot = await service.BuildAsync(plan);

        var operation = Assert.Single(snapshot.Operations);
        Assert.Collection(
            operation.Ingredients.OrderBy(ingredient => ingredient.AmountPerCraft),
            ingredient =>
            {
                Assert.Equal(1, ingredient.ExpectedTotalQuantity);
                Assert.Equal(childForOne.NodeId, ingredient.ChildNodeId);
                Assert.Equal(RecipeIngredientLinkStatus.Matched, ingredient.LinkStatus);
            },
            ingredient =>
            {
                Assert.Equal(2, ingredient.ExpectedTotalQuantity);
                Assert.Equal(childForTwo.NodeId, ingredient.ChildNodeId);
                Assert.Equal(RecipeIngredientLinkStatus.Matched, ingredient.LinkStatus);
            });
    }

    private static RecipeOperationSnapshotService CreateService(HttpMessageHandler? handler = null)
    {
        var garlandService = new GarlandService(
            new HttpClient(handler ?? new GarlandItemHandler()),
            Mock.Of<ILogger<GarlandService>>());

        return new RecipeOperationSnapshotService(
            garlandService,
            new RecipeResolutionService(),
            Mock.Of<ILogger<RecipeOperationSnapshotService>>());
    }

    private sealed class GarlandItemHandler : HttpMessageHandler
    {
        public Dictionary<int, int> RequestCountByItemId { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var itemId = request.RequestUri?.Segments.LastOrDefault()?.Replace(".json", string.Empty, StringComparison.Ordinal) ?? string.Empty;
            if (int.TryParse(itemId, out var parsedItemId))
            {
                RequestCountByItemId[parsedItemId] = RequestCountByItemId.GetValueOrDefault(parsedItemId) + 1;
            }

            var json = itemId switch
            {
                "100" => ItemJson(100, "Root Craft", [(1000, 90, 1, 2, new[] { (900, 3, "Ingredient") })]),
                "200" => ItemJson(200, "Crafted Child", [(2000, 80, 2, 2, Array.Empty<(int, int, string)>())]),
                "300" => ItemJson(300, "Bought Child", [(3000, 70, 3, 1, Array.Empty<(int, int, string)>())]),
                "400" => ItemJson(
                    400,
                    "Ambiguous Craft",
                    [
                        (4200, 50, 2, 1, Array.Empty<(int, int, string)>()),
                        (4100, 50, 2, 1, Array.Empty<(int, int, string)>())
                    ]),
                "5059" => ItemJson(
                    5059,
                    "Cobalt Ingot",
                    [
                        (153, 43, 2, 1, Array.Empty<(int, int, string)>()),
                        (273, 43, 3, 1, Array.Empty<(int, int, string)>())
                    ]),
                "900" => ItemJson(900, "Ingredient", []),
                "901" => ItemJson(901, "Extra Ingredient", []),
                "1000" => ItemJson(
                    1000,
                    "Duplicate Ingredient Craft",
                    [(9000, 90, 1, 1, new[] { (900, 1, "Ingredient"), (900, 2, "Ingredient") })]),
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
            IReadOnlyList<(int RecipeId, int RecipeLevel, int JobId, int Yield, IReadOnlyList<(int ItemId, int Amount, string Name)> Ingredients)> crafts)
        {
            var craftJson = string.Join(
                ",",
                crafts.Select(craft =>
                {
                    var ingredients = string.Join(
                        ",",
                        craft.Ingredients.Select(ingredient =>
                            $$"""{ "id": {{ingredient.ItemId}}, "amount": {{ingredient.Amount}}, "name": "{{ingredient.Name}}" }"""));

                    return $$"""
                        {
                          "id": "{{craft.RecipeId}}",
                          "rlvl": {{craft.RecipeLevel}},
                          "job": {{craft.JobId}},
                          "yield": {{craft.Yield}},
                          "ingredients": [{{ingredients}}]
                        }
                        """;
                }));

            return $$"""
                {
                  "item": {
                    "id": {{itemId}},
                    "name": "{{name}}",
                    "craft": [{{craftJson}}]
                  }
                }
                """;
        }
    }

    private sealed class CancelingItemHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
