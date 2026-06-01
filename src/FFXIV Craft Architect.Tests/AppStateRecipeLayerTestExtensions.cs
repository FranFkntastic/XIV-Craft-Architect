using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

internal static class AppStateRecipeLayerTestExtensions
{
    public static void ApplyBuiltRecipePlanWithActiveItems(this AppState appState, CraftingPlan plan)
    {
        appState.ApplyBuiltRecipePlan(plan, BuildActiveProcurementItems(plan));
    }

    public static void ActivateRecipePlanWithActiveItems(
        this AppState appState,
        CraftingPlan plan,
        IEnumerable<ProjectItem> projectItems,
        string? selectedDataCenter,
        bool clearCurrentPlanId)
    {
        appState.ActivateRecipePlan(
            plan,
            projectItems,
            selectedDataCenter,
            clearCurrentPlanId,
            BuildActiveProcurementItems(plan));
    }

    public static IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan)
    {
        return new RecipeDemandProjectionService()
            .Build(plan, snapshot: null)
            .ToActiveProcurementMaterialAggregates();
    }
}
