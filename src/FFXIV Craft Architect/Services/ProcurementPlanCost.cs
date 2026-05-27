using FFXIV_Craft_Architect.Core.Models;
using CorePurchaseRecommendationCost = FFXIV_Craft_Architect.Core.Services.PurchaseRecommendationCost;

namespace FFXIV_Craft_Architect.Services;

internal static class ProcurementPlanCost
{
    public static long GetRecommendedCost(DetailedShoppingPlan plan)
    {
        return CorePurchaseRecommendationCost.GetRecommendedCost(plan);
    }

    public static bool HasRecommendation(DetailedShoppingPlan plan)
    {
        return CorePurchaseRecommendationCost.HasRecommendation(plan);
    }
}
