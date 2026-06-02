namespace FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

public readonly record struct WebTableSortState<TColumnId>(
    TColumnId? Column,
    bool Descending)
    where TColumnId : struct, Enum
{
    public static WebTableSortState<TColumnId> Unsorted { get; } = new(null, false);

    public bool IsSortedBy(TColumnId column)
    {
        return Column.HasValue && EqualityComparer<TColumnId>.Default.Equals(Column.Value, column);
    }

    public WebTableSortState<TColumnId> Toggle(TColumnId column)
    {
        return new WebTableSortState<TColumnId>(
            column,
            IsSortedBy(column) && !Descending);
    }

    public string GetAriaSort(TColumnId column)
    {
        if (!IsSortedBy(column))
        {
            return "none";
        }

        return Descending ? "descending" : "ascending";
    }
}
