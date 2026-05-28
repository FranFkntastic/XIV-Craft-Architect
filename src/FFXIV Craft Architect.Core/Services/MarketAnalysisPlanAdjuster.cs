using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketAnalysisPlanAdjuster
{
    public static List<DetailedShoppingPlan> ReplacePlan(
        IEnumerable<DetailedShoppingPlan> plans,
        DetailedShoppingPlan replacement)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(replacement);

        var replaced = false;
        var result = new List<DetailedShoppingPlan>();
        foreach (var plan in plans)
        {
            if (plan.ItemId == replacement.ItemId)
            {
                result.Add(replacement);
                replaced = true;
                continue;
            }

            result.Add(plan);
        }

        if (!replaced)
        {
            result.Add(replacement);
        }

        return result;
    }

    public static List<DetailedShoppingPlan> ExcludeWorlds(
        IEnumerable<DetailedShoppingPlan> plans,
        IEnumerable<MarketWorldKey> worlds)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(worlds);

        var worldSet = worlds.ToHashSet();
        if (worldSet.Count == 0)
        {
            return plans.ToList();
        }

        return plans
            .Select(plan => ExcludeWorldsFromPlan(plan, worldSet))
            .ToList();
    }

    public static List<DetailedShoppingPlan> ExcludeWorldForItem(
        IEnumerable<DetailedShoppingPlan> plans,
        int itemId,
        MarketWorldKey world)
    {
        ArgumentNullException.ThrowIfNull(plans);

        return plans
            .Select(plan => plan.ItemId == itemId
                ? ExcludeWorldsFromPlan(plan, [world])
                : plan)
            .ToList();
    }

    public static List<DetailedShoppingPlan> ExcludeItemWorlds(
        IEnumerable<DetailedShoppingPlan> plans,
        IEnumerable<MarketItemWorldKey> itemWorlds)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(itemWorlds);

        var worldsByItemId = itemWorlds
            .GroupBy(itemWorld => itemWorld.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(itemWorld => itemWorld.World).ToHashSet());
        if (worldsByItemId.Count == 0)
        {
            return plans.ToList();
        }

        return plans
            .Select(plan => worldsByItemId.TryGetValue(plan.ItemId, out var worlds)
                ? ExcludeWorldsFromPlan(plan, worlds)
                : plan)
            .ToList();
    }

    private static DetailedShoppingPlan ExcludeWorldsFromPlan(
        DetailedShoppingPlan plan,
        HashSet<MarketWorldKey> worlds)
    {
        if (IsVendorPlan(plan))
        {
            return plan;
        }

        var filteredWorldOptions = plan.WorldOptions
            .Where(world => !worlds.Contains(new MarketWorldKey(world.DataCenter, world.WorldName)))
            .ToList();
        var recommendedWorld = plan.RecommendedWorld != null &&
            !worlds.Contains(new MarketWorldKey(plan.RecommendedWorld.DataCenter, plan.RecommendedWorld.WorldName))
                ? plan.RecommendedWorld
                : null;
        var recommendedSplit = plan.RecommendedSplit?
            .Where(split => !worlds.Contains(new MarketWorldKey(split.DataCenter, split.WorldName)))
            .ToList();
        if (recommendedSplit?.Sum(split => split.QuantityToBuy) < plan.QuantityNeeded)
        {
            recommendedSplit = null;
        }

        if (recommendedWorld == null)
        {
            recommendedWorld = filteredWorldOptions
                .Where(world => world.ValueScore < decimal.MaxValue && world.TotalQuantityPurchased >= plan.QuantityNeeded)
                .OrderBy(world => world.ValueScore)
                .ThenBy(world => world.TotalCost)
                .ThenBy(world => world.WorldName)
                .FirstOrDefault();
        }

        return new DetailedShoppingPlan
        {
            ItemId = plan.ItemId,
            Name = plan.Name,
            IconId = plan.IconId,
            QuantityNeeded = plan.QuantityNeeded,
            DCAveragePrice = plan.DCAveragePrice,
            WorldOptions = filteredWorldOptions,
            RecommendedWorld = recommendedWorld,
            RecommendedSplit = recommendedSplit?.Count > 0 ? recommendedSplit : null,
            Error = filteredWorldOptions.Count == 0 && string.IsNullOrWhiteSpace(plan.Error)
                ? "No market listings found after exclusions"
                : plan.Error,
            MarketDataWarning = plan.MarketDataWarning,
            HQAveragePrice = plan.HQAveragePrice,
            Vendors = plan.Vendors.ToList()
        };
    }

    private static bool IsVendorPlan(DetailedShoppingPlan plan)
    {
        return string.Equals(
            plan.RecommendedWorld?.WorldName,
            MarketShoppingConstants.VendorWorldName,
            StringComparison.OrdinalIgnoreCase);
    }
}
