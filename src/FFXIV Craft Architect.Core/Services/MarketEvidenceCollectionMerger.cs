using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class MarketEvidenceCollectionMerger
{
    public static List<MarketItemAnalysis> MergeAnalyses(
        IEnumerable<MarketItemAnalysis> existing,
        IEnumerable<MarketItemAnalysis> replacements) =>
        MergeByItemId(existing, replacements, analysis => analysis.ItemId);

    public static List<DetailedShoppingPlan> MergeShoppingPlans(
        IEnumerable<DetailedShoppingPlan> existing,
        IEnumerable<DetailedShoppingPlan> replacements) =>
        MergeByItemId(existing, replacements, plan => plan.ItemId);

    private static List<T> MergeByItemId<T>(
        IEnumerable<T> existing,
        IEnumerable<T> replacements,
        Func<T, int> getItemId)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(replacements);

        var replacementsByItemId = replacements.ToDictionary(getItemId);
        var merged = new List<T>();
        var includedItemIds = new HashSet<int>();
        foreach (var item in existing)
        {
            var itemId = getItemId(item);
            if (!includedItemIds.Add(itemId))
            {
                throw new InvalidOperationException($"Market evidence contains duplicate item ID {itemId}.");
            }

            merged.Add(replacementsByItemId.GetValueOrDefault(itemId, item));
            replacementsByItemId.Remove(itemId);
        }

        merged.AddRange(replacementsByItemId.Values);
        return merged;
    }
}
