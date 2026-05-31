using System.Net;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class ArtisanServiceTests
{
    [Fact]
    public async Task ExportToArtisanAsync_DefaultsToRootRecipesOnly()
    {
        var service = CreateService();
        var plan = CreatePlan();

        var result = await service.ExportToArtisanAsync(plan);

        var recipeIds = GetRecipeIds(result.Json);
        Assert.Equal([1000u], recipeIds);
    }

    [Fact]
    public async Task ExportToArtisanAsync_DefaultsToRootRecipesEvenWhenRootSourceIsNotCraft()
    {
        var service = CreateService();
        var plan = CreatePlan();
        plan.RootItems[0].Source = AcquisitionSource.MarketBuyNq;

        var result = await service.ExportToArtisanAsync(plan);

        var recipeIds = GetRecipeIds(result.Json);
        Assert.Equal([1000u], recipeIds);
    }

    [Fact]
    public async Task ExportToArtisanAsync_DefaultExportPreservesListItemOptions()
    {
        var service = CreateService();
        var plan = CreatePlan();

        var result = await service.ExportToArtisanAsync(plan);

        var artisanList = JsonSerializer.Deserialize<ArtisanCraftingList>(result.Json);
        var recipe = Assert.Single(artisanList!.Recipes);
        Assert.False(recipe.ListItemOptions.NQOnly);
        Assert.False(recipe.ListItemOptions.Skipping);
    }

    [Fact]
    public async Task ExportToArtisanAsync_IncludePrecrafts_ExportsCraftedDescendants()
    {
        var service = CreateService();
        var plan = CreatePlan();

        var result = await service.ExportToArtisanAsync(plan, includePrecrafts: true);

        var recipeIds = GetRecipeIds(result.Json);
        Assert.Equal([1000u, 2000u], recipeIds.OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task ExportToArtisanAsync_CompanyCraft_ExportsCompanyCraftRecipe()
    {
        var service = CreateService();
        var plan = new CraftingPlan
        {
            Name = "Company Craft Plan",
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 400,
                    Name = "Workshop Part",
                    Quantity = 8,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true,
                    RecipeLevel = 1,
                    Yield = 1
                }
            ]
        };

        var result = await service.ExportToArtisanAsync(plan);

        var artisanList = JsonSerializer.Deserialize<ArtisanCraftingList>(result.Json);
        var recipe = Assert.Single(artisanList!.Recipes);
        Assert.Equal(4000u, recipe.ID);
        Assert.Equal(8, recipe.Quantity);
    }

    [Fact]
    public async Task ExportToArtisanAsync_IncludePrecrafts_PreservesRecipeIdentityForSameResultItem()
    {
        var service = CreateService();
        var blacksmithRoot = new PlanNode
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
        var armorerRoot = new PlanNode
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
        var plan = new CraftingPlan
        {
            Name = "Cobalt Ingot Plan",
            RootItems = [blacksmithRoot, armorerRoot]
        };

        var result = await service.ExportToArtisanAsync(plan, includePrecrafts: true);

        var artisanList = JsonSerializer.Deserialize<ArtisanCraftingList>(result.Json);
        Assert.Collection(
            artisanList!.Recipes.OrderBy(recipe => recipe.ID),
            recipe =>
            {
                Assert.Equal(153u, recipe.ID);
                Assert.Equal(432, recipe.Quantity);
            },
            recipe =>
            {
                Assert.Equal(273u, recipe.ID);
                Assert.Equal(384, recipe.Quantity);
            });
    }

    [Fact]
    public async Task ImportThenExportToArtisanAsync_IncludePrecrafts_PreservesRecipeQuantities()
    {
        var service = CreateService(new Dictionary<int, int>
        {
            [6000] = 600,
            [7000] = 700
        });
        var artisanJson = """
            {
              "ID": 123,
              "Name": "Roundtrip Quantity List",
              "Recipes": [
                {
                  "ID": 6000,
                  "Quantity": 4,
                  "ListItemOptions": { "NQOnly": false, "Skipping": false }
                },
                {
                  "ID": 7000,
                  "Quantity": 12,
                  "ListItemOptions": { "NQOnly": false, "Skipping": false }
                }
              ],
              "ExpandedList": []
            }
            """;

        var plan = await service.ImportFromArtisanAsync(artisanJson, "Aether", string.Empty);
        var result = await service.ExportToArtisanAsync(plan!, includePrecrafts: true);

        var artisanList = JsonSerializer.Deserialize<ArtisanCraftingList>(result.Json);
        var quantitiesByRecipe = artisanList!.Recipes.ToDictionary(recipe => recipe.ID, recipe => recipe.Quantity);
        Assert.Equal(4, quantitiesByRecipe[6000]);
        Assert.Equal(12, quantitiesByRecipe[7000]);
    }

    [Fact]
    public async Task ExportToArtisanAsync_LowConfidenceRecipeResolution_DoesNotExportGuessedRecipeId()
    {
        var service = CreateService();
        var plan = new CraftingPlan
        {
            Name = "Low Confidence Plan",
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 5059,
                    Name = "Cobalt Ingot",
                    Quantity = 4,
                    Source = AcquisitionSource.Craft,
                    CanCraft = true,
                    RecipeLevel = 99,
                    Job = "Weaver",
                    Yield = 9
                }
            ]
        };

        var result = await service.ExportToArtisanAsync(plan);

        var artisanList = JsonSerializer.Deserialize<ArtisanCraftingList>(result.Json);
        Assert.Empty(artisanList!.Recipes);
        Assert.Contains("Cobalt Ingot", result.MissingRecipes);
    }

    [Fact]
    public async Task ExportToArtisanAsync_UncraftableRoot_ReportsMissingRecipe()
    {
        var service = CreateService();
        var plan = new CraftingPlan
        {
            Name = "Uncraftable Root Plan",
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 800,
                    Name = "Roundtrip Material",
                    Quantity = 3,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanCraft = false
                }
            ]
        };

        var result = await service.ExportToArtisanAsync(plan);

        var artisanList = JsonSerializer.Deserialize<ArtisanCraftingList>(result.Json);
        Assert.Empty(artisanList!.Recipes);
        Assert.Contains("Roundtrip Material", result.MissingRecipes);
    }

    private static ArtisanService CreateService(Dictionary<int, int>? recipeToItemIds = null)
    {
        var garlandService = new GarlandService(
            new HttpClient(new GarlandItemHandler()),
            Mock.Of<ILogger<GarlandService>>());
        var teamcraftService = new Mock<ITeamcraftRecipeService>();
        teamcraftService
            .Setup(service => service.GetItemIdForRecipeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int recipeId, CancellationToken _) =>
                recipeToItemIds?.TryGetValue(recipeId, out var itemId) == true ? itemId : null);

        return new ArtisanService(
            Mock.Of<ILogger<ArtisanService>>(),
            garlandService,
            new RecipeCalculationService(
                garlandService,
                new StubVendorCacheService(),
                Mock.Of<ILogger<RecipeCalculationService>>()),
            teamcraftService.Object,
            new HttpClient(),
            new RecipeOperationSnapshotService(
                garlandService,
                new RecipeResolutionService(),
                Mock.Of<ILogger<RecipeOperationSnapshotService>>()));
    }

    private static CraftingPlan CreatePlan()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Root Craft",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 90,
            Job = "Carpenter",
            Yield = 1
        };

        root.Children.Add(new PlanNode
        {
            ItemId = 200,
            Name = "Crafted Precraft",
            Quantity = 2,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            RecipeLevel = 80,
            Job = "Carpenter",
            Yield = 1,
            Parent = root
        });

        root.Children.Add(new PlanNode
        {
            ItemId = 300,
            Name = "Purchased Precraft",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            CanCraft = true,
            RecipeLevel = 70,
            Job = "Carpenter",
            Yield = 1,
            Parent = root
        });

        return new CraftingPlan
        {
            Name = "Artisan Test Plan",
            RootItems = [root]
        };
    }

    private static uint[] GetRecipeIds(string json)
    {
        var artisanList = JsonSerializer.Deserialize<ArtisanCraftingList>(json);
        return artisanList?.Recipes.Select(recipe => recipe.ID).ToArray() ?? [];
    }

    private sealed class GarlandItemHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var itemId = request.RequestUri?.Segments.LastOrDefault()?.Replace(".json", string.Empty, StringComparison.Ordinal) ?? string.Empty;
            var json = itemId switch
            {
                "100" => ItemJson(100, "Root Craft", 1000, 90, 1, jobId: 1),
                "200" => ItemJson(200, "Crafted Precraft", 2000, 80, 1, jobId: 1),
                "300" => ItemJson(300, "Purchased Precraft", 3000, 70, 1, jobId: 1),
                "400" => CompanyCraftItemJson(400, "Workshop Part", 4000),
                "5059" => CobaltIngotJson(),
                "600" => ItemJson(600, "Roundtrip Root", 6000, 90, 2, [(700, 3, "Roundtrip Precraft")], jobId: 1),
                "700" => ItemJson(700, "Roundtrip Precraft", 7000, 80, 1, [(800, 1, "Roundtrip Material")], jobId: 1),
                "800" => ItemJson(800, "Roundtrip Material", []),
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
            int recipeLevel,
            int yield,
            IReadOnlyList<(int ItemId, int Amount, string Name)>? ingredients = null,
            int jobId = 1)
        {
            var ingredientJson = string.Join(
                ",",
                (ingredients ?? []).Select(ingredient =>
                    $$"""{ "id": {{ingredient.ItemId}}, "amount": {{ingredient.Amount}}, "name": "{{ingredient.Name}}" }"""));

            return $$"""
                {
                  "item": {
                    "id": {{itemId}},
                    "name": "{{name}}",
                    "craft": [
                      {
                        "id": "{{recipeId}}",
                        "rlvl": {{recipeLevel}},
                        "job": {{jobId}},
                        "yield": {{yield}},
                        "ingredients": [{{ingredientJson}}]
                      }
                    ]
                  }
                }
                """;
        }

        private static string ItemJson(
            int itemId,
            string name,
            IReadOnlyList<(int ItemId, int Amount, string Name)> ingredients)
        {
            return $$"""
                {
                  "item": {
                    "id": {{itemId}},
                    "name": "{{name}}",
                    "craft": []
                  }
                }
                """;
        }

        private static string CobaltIngotJson()
        {
            return """
                {
                  "item": {
                    "id": 5059,
                    "name": "Cobalt Ingot",
                    "craft": [
                      {
                        "id": "153",
                        "rlvl": 43,
                        "job": 2,
                        "yield": 1,
                        "ingredients": []
                      },
                      {
                        "id": "273",
                        "rlvl": 43,
                        "job": 3,
                        "yield": 1,
                        "ingredients": []
                      }
                    ]
                  }
                }
                """;
        }

        private static string CompanyCraftItemJson(int itemId, string name, int recipeId)
        {
            return $$"""
                {
                  "item": {
                    "id": {{itemId}},
                    "name": "{{name}}",
                    "craft": [],
                    "companyCraft": [
                      {
                        "id": {{recipeId}},
                        "phaseCount": 1,
                        "phases": []
                      }
                    ]
                  }
                }
                """;
        }
    }

    private sealed class StubVendorCacheService : IVendorCacheService
    {
        public int Count => 0;

        public void Clear()
        {
        }

        public VendorCacheEntry? Get(int itemId)
        {
            return null;
        }

        public Task<VendorCacheEntry?> GetOrFetchAsync(int itemId, CancellationToken ct = default)
        {
            return Task.FromResult<VendorCacheEntry?>(null);
        }

        public Task<Dictionary<int, VendorCacheEntry>> GetOrFetchBatchAsync(
            IEnumerable<int> itemIds,
            CancellationToken ct = default)
        {
            return Task.FromResult(new Dictionary<int, VendorCacheEntry>());
        }

        public Task LoadAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void Set(int itemId, VendorCacheEntry entry)
        {
        }
    }
}
