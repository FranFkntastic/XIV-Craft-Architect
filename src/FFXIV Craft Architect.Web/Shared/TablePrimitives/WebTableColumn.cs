using Microsoft.AspNetCore.Components;

namespace FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

public sealed class WebTableColumn<TItem, TColumnId>
    where TColumnId : struct, Enum
{
    public required TColumnId Id { get; init; }

    public required string Header { get; init; }

    public string? HeaderTooltip { get; init; }

    public WebTableColumnSize Size { get; init; } = new(120);

    public bool Sortable { get; init; } = true;

    public string HeaderCssClass { get; init; } = string.Empty;

    public string CellCssClass { get; init; } = string.Empty;

    public Func<TItem, string>? CellCssClassSelector { get; init; }

    public Func<TItem, string?>? CellTitleSelector { get; init; }

    public required RenderFragment<TItem> CellTemplate { get; init; }

    public static WebTableColumn<TItem, TColumnId> Text(
        TColumnId id,
        string header,
        Func<TItem, string> value,
        int widthPx = 120,
        int minWidthPx = 80,
        string? headerTooltip = null,
        bool sortable = true,
        bool alignEnd = false,
        string cellCssClass = "",
        string headerCssClass = "")
    {
        ArgumentNullException.ThrowIfNull(value);

        var baseCellClass = alignEnd
            ? CombineClasses(cellCssClass, "is-align-end")
            : cellCssClass;

        return new WebTableColumn<TItem, TColumnId>
        {
            Id = id,
            Header = header,
            HeaderTooltip = headerTooltip,
            Size = new WebTableColumnSize(widthPx, minWidthPx),
            Sortable = sortable,
            CellCssClass = baseCellClass,
            HeaderCssClass = headerCssClass,
            CellTemplate = item => builder => builder.AddContent(0, value(item))
        };
    }

    public string GetCellCssClass(TItem item)
    {
        return CombineClasses(CellCssClass, CellCssClassSelector?.Invoke(item));
    }

    internal static string CombineClasses(params string?[] classes)
    {
        return string.Join(
            " ",
            classes.Where(candidate => !string.IsNullOrWhiteSpace(candidate)));
    }
}
