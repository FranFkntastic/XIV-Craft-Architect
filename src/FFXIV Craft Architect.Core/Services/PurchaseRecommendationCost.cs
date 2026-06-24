using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class PurchaseRecommendationCost
{
    public static long GetRecommendedCost(DetailedShoppingPlan plan)
    {
        var coverage = GetDefaultCoverageOption(plan);
        if (coverage != null)
        {
            return ToLongSaturating(coverage.CashOutCost);
        }

        if (UsesSplitRecommendation(plan))
        {
            return plan.SplitTotalCost ?? 0;
        }

        return plan.RecommendedWorld?.TotalCost ?? 0;
    }

    public static long GetRecommendedCashOutCost(DetailedShoppingPlan plan)
    {
        var coverage = GetDefaultCoverageOption(plan);
        if (coverage != null)
        {
            return ToLongSaturating(coverage.CashOutCost);
        }

        return GetRecommendedCost(plan);
    }

    public static MarketCoverageOption? GetDefaultCoverageOption(DetailedShoppingPlan plan)
    {
        return MarketCoverageSelection.GetDefaultOption(plan.CoverageSet);
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

    private static long ToLongSaturating(decimal value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(value);
    }
}
