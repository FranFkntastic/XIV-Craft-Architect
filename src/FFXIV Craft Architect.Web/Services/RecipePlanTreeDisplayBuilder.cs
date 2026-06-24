using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record RecipeNodeDisplayState(
    string NodeId,
    string SourceColor,
    string PriceText,
    string HqPrefix,
    string? RecipeInfo);

public static class RecipePlanTreeDisplayBuilder
{
    public static IReadOnlyDictionary<string, RecipeNodeDisplayState> Build(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        if (plan == null)
        {
            return new Dictionary<string, RecipeNodeDisplayState>();
        }

        var context = AcquisitionPlanningService.CreateCostContext(shoppingPlans);
        var states = new Dictionary<string, RecipeNodeDisplayState>(StringComparer.Ordinal);
        foreach (var root in plan.RootItems)
        {
            AddNode(root, context, states);
        }

        return states;
    }

    public static RecipeNodeDisplayState BuildWithoutCost(PlanNode node)
    {
        return CreateState(node, priceText: string.Empty);
    }

    private static void AddNode(
        PlanNode node,
        AcquisitionCostContext context,
        IDictionary<string, RecipeNodeDisplayState> states)
    {
        states[node.NodeId] = CreateState(node, GetPriceText(node, context));
        foreach (var child in node.Children)
        {
            AddNode(child, context, states);
        }
    }

    private static RecipeNodeDisplayState CreateState(PlanNode node, string priceText)
    {
        var recipeInfo = !string.IsNullOrEmpty(node.Job) && node.Job != "Company Workshop"
            ? FormatRecipeInfo(node)
            : null;

        return new RecipeNodeDisplayState(
            node.NodeId,
            RecipePlanDisplayHelpers.GetSourceHexColor(node.Source),
            priceText,
            node.MustBeHq ? "\u2605 " : string.Empty,
            recipeInfo);
    }

    private static string FormatRecipeInfo(PlanNode node)
    {
        var displayLevel = node.RecipeDisplayLevel > 0 ? node.RecipeDisplayLevel : node.RecipeLevel;
        var stars = node.RecipeStars > 0 ? new string('\u2605', node.RecipeStars) : string.Empty;
        var master = node.RecipeUnlockItemId > 0 ? " (Master)" : string.Empty;
        return $"Lv.{displayLevel}{stars} {node.Job}{master}";
    }

    private static string GetPriceText(PlanNode node, AcquisitionCostContext context)
    {
        return AcquisitionPlanningService.TryGetAcquisitionCost(node, node.Source, context, out var cost)
            ? $"{cost:N0}g"
            : string.Empty;
    }
}
