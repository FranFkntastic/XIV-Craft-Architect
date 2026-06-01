using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CoreRecipeCalculationPlanBuilder : ICoreRecipePlanBuilder
{
    private readonly RecipeCalculationService _recipeCalculationService;

    public CoreRecipeCalculationPlanBuilder(RecipeCalculationService recipeCalculationService)
    {
        _recipeCalculationService = recipeCalculationService ?? throw new ArgumentNullException(nameof(recipeCalculationService));
    }

    public Task<CraftingPlan> BuildPlanAsync(
        List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default)
    {
        return _recipeCalculationService.BuildPlanAsync(targetItems, dataCenter, world, ct);
    }

    public Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default)
    {
        return _recipeCalculationService.FetchVendorPricesAsync(plan, ct);
    }
}
