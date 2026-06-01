using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.Tests;

public class CoreRecipeCalculationPlanBuilderTests
{
    [Fact]
    public async Task BuildPlanAsync_WithNoTargetItems_ReturnsEmptyPlanWithContext()
    {
        var builder = CreateBuilder();

        var plan = await builder.BuildPlanAsync([], "Aether", "Jenova");

        Assert.Equal("Aether", plan.DataCenter);
        Assert.Equal("Jenova", plan.World);
        Assert.Empty(plan.RootItems);
    }

    [Fact]
    public async Task FetchVendorPricesAsync_WithEmptyPlan_CompletesWithoutExternalLookup()
    {
        var builder = CreateBuilder();

        await builder.FetchVendorPricesAsync(new CraftingPlan());
    }

    private static CoreRecipeCalculationPlanBuilder CreateBuilder()
    {
        var garland = new GarlandService(new HttpClient(), NullLogger<GarlandService>.Instance);
        var vendorCache = new VendorCacheService(garland, NullLogger<VendorCacheService>.Instance);
        var recipeCalculation = new RecipeCalculationService(
            garland,
            vendorCache,
            NullLogger<RecipeCalculationService>.Instance,
            new RecipeResolutionService());
        return new CoreRecipeCalculationPlanBuilder(recipeCalculation);
    }
}
