using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record CoreMarketDataUnavailableItem(int ItemId, string Name);

public enum CoreMarketPriceRefreshStatus
{
    Success,
    NoPlan,
    StalePlan
}

public sealed record CoreMarketPriceRefreshResult(
    int RequestedCount,
    int FetchedCount,
    IReadOnlyList<CoreMarketDataUnavailableItem> UnavailableItems,
    CoreMarketPriceRefreshStatus Status = CoreMarketPriceRefreshStatus.Success)
{
    public bool HasUnavailableItems => UnavailableItems.Count > 0;
    public bool Published => Status == CoreMarketPriceRefreshStatus.Success;
}

public static class CoreMarketPriceAvailability
{
    public static CoreMarketPriceRefreshResult Empty { get; } =
        new(0, 0, Array.Empty<CoreMarketDataUnavailableItem>());

    public static CoreMarketPriceRefreshResult NoPlan { get; } =
        new(0, 0, Array.Empty<CoreMarketDataUnavailableItem>(), CoreMarketPriceRefreshStatus.NoPlan);

    public static CoreMarketPriceRefreshResult StalePlan { get; } =
        new(0, 0, Array.Empty<CoreMarketDataUnavailableItem>(), CoreMarketPriceRefreshStatus.StalePlan);

    public static CoreMarketPriceRefreshResult FromCachedMarketData(
        CraftingPlan plan,
        IEnumerable<int> requestedItemIds,
        IReadOnlyDictionary<int, CachedMarketData> entries,
        IEnumerable<int>? skippedItemIds = null)
    {
        var requestedIds = requestedItemIds.Distinct().ToList();
        var skippedIds = skippedItemIds?.Distinct().ToList() ?? new List<int>();
        var unavailableItems = requestedIds
            .Where(id => !entries.ContainsKey(id))
            .Concat(skippedIds)
            .Distinct()
            .Select(id => new CoreMarketDataUnavailableItem(id, ResolveItemName(plan, id)))
            .OrderBy(item => item.Name)
            .ToList();

        return new CoreMarketPriceRefreshResult(
            requestedIds.Count + skippedIds.Count,
            entries.Count,
            unavailableItems);
    }

    public static List<int> GetMarketListableItemIds(CraftingPlan plan, IEnumerable<int> itemIds)
    {
        return itemIds
            .Where(id => plan.FindNode(id)?.CanBuyFromMarket != false)
            .Distinct()
            .ToList();
    }

    public static List<int> GetNonMarketListableItemIds(CraftingPlan plan, IEnumerable<int> itemIds)
    {
        return itemIds
            .Where(id => plan.FindNode(id)?.CanBuyFromMarket == false)
            .Distinct()
            .ToList();
    }

    public static string FormatUnavailableMessage(IReadOnlyList<CoreMarketDataUnavailableItem> unavailableItems)
    {
        if (unavailableItems.Count == 0)
        {
            return string.Empty;
        }

        var itemText = unavailableItems.Count == 1
            ? unavailableItems[0].Name
            : $"{unavailableItems.Count} items, including {unavailableItems[0].Name}";

        return $"Market data unavailable for {itemText}. It may not be market-board listable; the plan is using available prices.";
    }

    private static string ResolveItemName(CraftingPlan plan, int itemId)
    {
        return plan.FindNode(itemId)?.Name ?? $"Item {itemId}";
    }
}
