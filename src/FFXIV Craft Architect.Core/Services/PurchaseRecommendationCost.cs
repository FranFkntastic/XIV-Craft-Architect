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
        if (plan.CoverageSet == null)
        {
            return null;
        }

        return plan.CoverageSet.AllCandidates
            .Concat([
                plan.CoverageSet.SingleWorld,
                plan.CoverageSet.CompactSplit,
                plan.CoverageSet.WideSplit,
                plan.CoverageSet.CheapestObserved
            ])
            .Where(candidate => candidate != null)
            .Cast<MarketCoverageOption>()
            .GroupBy(candidate => candidate.CandidateId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(candidate => candidate.Kind == MarketCoverageKind.SupportedListings)
            .Where(candidate => candidate.IsDefaultEligible)
            .OrderBy(candidate => candidate.ExactNeededCost)
            .ThenBy(candidate => candidate.Friction.WorldCount)
            .ThenBy(candidate => candidate.CashOutCost)
            .FirstOrDefault();
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
