namespace FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

public static class WebTableOrdering
{
    public static IReadOnlyList<TItem> Apply<TItem, TColumnId>(
        IEnumerable<TItem> items,
        WebTableSortState<TColumnId> sortState,
        IEnumerable<WebTableSortRule<TItem, TColumnId>> sortRules,
        Func<IEnumerable<TItem>, IOrderedEnumerable<TItem>> defaultOrder,
        Func<IOrderedEnumerable<TItem>, IOrderedEnumerable<TItem>>? tieBreaker = null)
        where TColumnId : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(sortRules);
        ArgumentNullException.ThrowIfNull(defaultOrder);

        var itemList = items.ToList();
        IOrderedEnumerable<TItem> ordered;
        if (sortState.Column.HasValue)
        {
            var comparer = EqualityComparer<TColumnId>.Default;
            var rule = sortRules.FirstOrDefault(candidate => comparer.Equals(candidate.Column, sortState.Column.Value));
            ordered = rule == null
                ? defaultOrder(itemList)
                : rule.Apply(itemList, sortState.Descending);
        }
        else
        {
            ordered = defaultOrder(itemList);
        }

        return (tieBreaker == null ? ordered : tieBreaker(ordered)).ToList();
    }
}
