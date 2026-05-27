using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record MarketDataUnavailableItem(int ItemId, string Name);

public sealed record MarketPriceRefreshResult(
    int RequestedCount,
    int FetchedCount,
    IReadOnlyList<MarketDataUnavailableItem> UnavailableItems)
{
    public bool HasUnavailableItems => UnavailableItems.Count > 0;
}

public static class MarketPriceAvailability
{
    public static MarketPriceRefreshResult FromResponses(
        CraftingPlan plan,
        IEnumerable<int> requestedItemIds,
        IReadOnlyDictionary<int, UniversalisResponse> responses,
        IEnumerable<int>? skippedItemIds = null)
    {
        var requestedIds = requestedItemIds.Distinct().ToList();
        var skippedIds = skippedItemIds?.Distinct().ToList() ?? new List<int>();
        var unavailableItems = requestedIds
            .Where(id => !responses.ContainsKey(id))
            .Concat(skippedIds)
            .Distinct()
            .Select(id => new MarketDataUnavailableItem(id, ResolveItemName(plan, id)))
            .OrderBy(item => item.Name)
            .ToList();

        return new MarketPriceRefreshResult(
            requestedIds.Count + skippedIds.Count,
            responses.Count,
            unavailableItems);
    }

    public static MarketPriceRefreshResult FromCachedMarketData(
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
            .Select(id => new MarketDataUnavailableItem(id, ResolveItemName(plan, id)))
            .OrderBy(item => item.Name)
            .ToList();

        return new MarketPriceRefreshResult(
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

    public static string FormatUnavailableMessage(IReadOnlyList<MarketDataUnavailableItem> unavailableItems)
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
