using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Services;

internal static class ProcurementPlanCost
{
    public static long GetRecommendedCost(DetailedShoppingPlan plan)
    {
        if (plan.RequiresSplitPurchase && plan.SplitTotalCost.HasValue)
        {
            return plan.SplitTotalCost.Value;
        }

        return plan.RecommendedWorld?.TotalCost ?? plan.SplitTotalCost ?? 0;
    }

    public static bool HasRecommendation(DetailedShoppingPlan plan)
    {
        return plan.HasOptions ||
            plan.RecommendedWorld != null ||
            plan.RecommendedSplit?.Any() == true;
    }
}
