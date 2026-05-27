using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class PurchaseRecommendationCost
{
    public static long GetRecommendedCost(DetailedShoppingPlan plan)
    {
        if (UsesSplitRecommendation(plan))
        {
            return plan.SplitTotalCost ?? 0;
        }

        return plan.RecommendedWorld?.TotalCost ?? 0;
    }

    public static bool HasRecommendation(DetailedShoppingPlan plan)
    {
        return plan.HasOptions ||
            plan.RecommendedWorld != null ||
            plan.RecommendedSplit?.Any() == true;
    }

    public static bool UsesSplitRecommendation(DetailedShoppingPlan plan)
    {
        return plan.RecommendedSplit?.Any() == true;
    }
}
