using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public static class MarketAnalysisGridViewService
{
    public static IReadOnlyList<DetailedShoppingPlan> GetOrderedPlans(
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        IEnumerable<MarketItemAnalysis> analyses,
        MarketAcquisitionLens lens,
        MarketSortOption defaultSort,
        MarketAnalysisGridSortColumn? sortColumn,
        bool sortDescending)
    {
        ArgumentNullException.ThrowIfNull(shoppingPlans);
        ArgumentNullException.ThrowIfNull(analyses);

        var plans = shoppingPlans.ToList();
        var analysisByItemId = analyses.ToDictionary(analysis => analysis.ItemId);
        IOrderedEnumerable<DetailedShoppingPlan> ordered = sortColumn switch
        {
            MarketAnalysisGridSortColumn.Item => Order(plans, plan => plan.Name, sortDescending),
            MarketAnalysisGridSortColumn.Quantity => Order(plans, GetAvailableSortValue, sortDescending),
            MarketAnalysisGridSortColumn.Coverage => Order(plans, plan => GetCoverageSortValue(plan, analysisByItemId), sortDescending),
            MarketAnalysisGridSortColumn.Worlds => Order(plans, GetWorldCount, sortDescending),
            MarketAnalysisGridSortColumn.Total => Order(plans, GetTotalCost, sortDescending),
            _ => GetDefaultOrder(plans, analysisByItemId, lens, defaultSort)
        };

        return ordered
            .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.ItemId)
            .ToList();
    }

    public static DetailedShoppingPlan? ResolveSelectedPlan(
        IReadOnlyList<DetailedShoppingPlan> orderedPlans,
        int? selectedItemId)
    {
        if (orderedPlans.Count == 0)
        {
            return null;
        }

        if (selectedItemId.HasValue)
        {
            var selected = orderedPlans.FirstOrDefault(plan => plan.ItemId == selectedItemId.Value);
            if (selected != null)
            {
                return selected;
            }
        }

        return orderedPlans[0];
    }

    public static MarketAnalysisGridSortState ToggleSort(
        MarketAnalysisGridSortColumn? currentColumn,
        bool currentDescending,
        MarketAnalysisGridSortColumn clickedColumn)
    {
        return new MarketAnalysisGridSortState(
            clickedColumn,
            currentColumn == clickedColumn && !currentDescending);
    }

    public static long GetTotalCost(DetailedShoppingPlan plan)
    {
        if (plan.SplitTotalCost.HasValue)
        {
            return plan.SplitTotalCost.Value;
        }

        if (plan.RecommendedWorld != null)
        {
            return plan.RecommendedWorld.TotalCost;
        }

        if (plan.WorldOptions.Any())
        {
            return plan.WorldOptions.OrderBy(world => world.TotalCost).First().TotalCost;
        }

        return (long)(plan.DCAveragePrice * plan.QuantityNeeded);
    }

    private static IOrderedEnumerable<DetailedShoppingPlan> GetDefaultOrder(
        IEnumerable<DetailedShoppingPlan> plans,
        IReadOnlyDictionary<int, MarketItemAnalysis> analysisByItemId,
        MarketAcquisitionLens lens,
        MarketSortOption defaultSort)
    {
        return defaultSort switch
        {
            MarketSortOption.Alphabetical => plans.OrderBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase),
            MarketSortOption.ByRecommended => plans.OrderBy(plan => GetBestWorldRank(plan, analysisByItemId, lens))
                .ThenBy(plan => plan.Name, StringComparer.OrdinalIgnoreCase),
            _ => plans.OrderBy(plan => plan.ItemId)
        };
    }

    private static IOrderedEnumerable<DetailedShoppingPlan> Order<TKey>(
        IEnumerable<DetailedShoppingPlan> plans,
        Func<DetailedShoppingPlan, TKey> selector,
        bool descending)
    {
        return descending
            ? plans.OrderByDescending(selector)
            : plans.OrderBy(selector);
    }

    private static int GetCoverageSortValue(
        DetailedShoppingPlan plan,
        IReadOnlyDictionary<int, MarketItemAnalysis> analysisByItemId)
    {
        if (!analysisByItemId.TryGetValue(plan.ItemId, out var analysis))
        {
            return plan.HasSufficientStock ? 0 : 1;
        }

        return analysis.Worlds.Count(world => GetDisplayCoverageBucket(world) == MarketCoverageBucket.Full) * -100
            - analysis.Worlds.Count(world => GetDisplayCoverageBucket(world) == MarketCoverageBucket.PartialDeep) * 10
            - analysis.Worlds.Count(world => GetDisplayCoverageBucket(world) == MarketCoverageBucket.PartialThin);
    }

    private static int GetBestWorldRank(
        DetailedShoppingPlan plan,
        IReadOnlyDictionary<int, MarketItemAnalysis> analysisByItemId,
        MarketAcquisitionLens lens)
    {
        return analysisByItemId.TryGetValue(plan.ItemId, out var analysis)
            ? analysis.Worlds
                .Select(world => world.Scores.FirstOrDefault(score => score.Lens == lens)?.Rank ?? int.MaxValue)
                .DefaultIfEmpty(int.MaxValue)
                .Min()
            : int.MaxValue;
    }

    private static int GetWorldCount(DetailedShoppingPlan plan)
    {
        if (plan.RequiresSplitPurchase && plan.RecommendedSplit?.Any() == true)
        {
            return plan.RecommendedSplit.Select(split => split.WorldName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        }

        return plan.RecommendedWorld == null ? 0 : 1;
    }

    private static int GetAvailableSortValue(DetailedShoppingPlan plan)
    {
        return IsVendorPlan(plan)
            ? int.MaxValue
            : plan.TotalAvailableQuantity;
    }

    private static bool IsVendorPlan(DetailedShoppingPlan plan)
    {
        return plan.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName || plan.Vendors.Any();
    }

    private static MarketCoverageBucket GetDisplayCoverageBucket(WorldMarketAnalysis world)
    {
        var saneQuantity = world.SaneThresholdUnitPrice > 0
            ? world.ScopeSaneQuantity
            : world.TotalSaneQuantity;
        if (world.QuantityNeeded <= 0 || saneQuantity <= 0)
        {
            return MarketCoverageBucket.None;
        }

        if (saneQuantity >= world.QuantityNeeded)
        {
            return MarketCoverageBucket.Full;
        }

        var competitiveQuantity = world.SaneThresholdUnitPrice > 0
            ? world.ScopeCompetitiveQuantity
            : world.CompetitiveQuantity;
        return competitiveQuantity >= Math.Max(world.QuantityNeeded / 2, 1)
            ? MarketCoverageBucket.PartialDeep
            : MarketCoverageBucket.PartialThin;
    }
}
