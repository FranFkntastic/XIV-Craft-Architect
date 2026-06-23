using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class DesktopRecipeCalculationPlanBuilder : IDesktopRecipePlanBuilder
{
    private readonly RecipeCalculationService _recipeCalculationService;

    public DesktopRecipeCalculationPlanBuilder(RecipeCalculationService recipeCalculationService)
    {
        _recipeCalculationService = recipeCalculationService ?? throw new ArgumentNullException(nameof(recipeCalculationService));
    }

    public Task<CraftingPlan> BuildPlanAsync(
        IReadOnlyList<(int ItemId, string Name, int Quantity, bool MustBeHq)> targetItems,
        string dataCenter,
        string world,
        CancellationToken ct = default) =>
        _recipeCalculationService.BuildPlanAsync(
            targetItems
                .Select(item => (item.ItemId, item.Name, item.Quantity, item.MustBeHq))
                .ToList(),
            dataCenter,
            world,
            ct);
}
