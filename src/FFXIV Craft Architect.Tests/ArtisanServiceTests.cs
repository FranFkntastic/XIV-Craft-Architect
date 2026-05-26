using System.Net;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
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
    public async Task ExportToArtisanAsync_IncludePrecrafts_ExportsCraftedDescendants()
    {
        var service = CreateService();
        var plan = CreatePlan();

        var result = await service.ExportToArtisanAsync(plan, includePrecrafts: true);

        var recipeIds = GetRecipeIds(result.Json);
        Assert.Equal([1000u, 2000u], recipeIds.OrderBy(id => id).ToArray());
    }

    private static ArtisanService CreateService()
    {
        var garlandService = new GarlandService(
            new HttpClient(new GarlandItemHandler()),
            Mock.Of<ILogger<GarlandService>>());

        return new ArtisanService(
            Mock.Of<ILogger<ArtisanService>>(),
            garlandService,
            null!,
            Mock.Of<ITeamcraftRecipeService>(),
            new HttpClient());
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
                "100" => ItemJson(100, "Root Craft", 1000, 90, 1),
                "200" => ItemJson(200, "Crafted Precraft", 2000, 80, 1),
                "300" => ItemJson(300, "Purchased Precraft", 3000, 70, 1),
                _ => """{"item":{"id":0,"name":"Unknown","craft":[]}}"""
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
        }

        private static string ItemJson(int itemId, string name, int recipeId, int recipeLevel, int yield)
        {
            return $$"""
                {
                  "item": {
                    "id": {{itemId}},
                    "name": "{{name}}",
                    "craft": [
                      {
                        "id": "{{recipeId}}",
                        "rlvl": {{recipeLevel}},
                        "yield": {{yield}},
                        "ingredients": []
                      }
                    ]
                  }
                }
                """;
        }
    }
}
