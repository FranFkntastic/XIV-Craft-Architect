using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class NativePlanImportClassifier
{
    private readonly GarlandService _garlandService;

    public NativePlanImportClassifier(GarlandService garlandService)
    {
        _garlandService = garlandService;
    }

    public Task<bool> RequiresRecipeGraphBuildAsync(
        CraftingPlan plan,
        CancellationToken cancellationToken = default) =>
        RequiresRecipeGraphBuildAsync(plan, _garlandService.GetRecipeAsync, cancellationToken);

    public static async Task<bool> RequiresRecipeGraphBuildAsync(
        CraftingPlan plan,
        Func<int, CancellationToken, Task<Recipe?>> recipeLookup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(recipeLookup);

        if (plan.RootItems.Count == 0 || plan.RootItems.Any(HasRecipeEdges))
        {
            return false;
        }

        // A flat plan can legitimately represent direct purchases. Rebuild only when
        // Garland proves that at least one root has a recipe which the payload omitted.
        foreach (var root in plan.RootItems)
        {
            if (await recipeLookup(root.ItemId, cancellationToken) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRecipeEdges(PlanNode node) =>
        node.Children.Count > 0 || node.Children.Any(HasRecipeEdges);
}
