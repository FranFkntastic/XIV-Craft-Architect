namespace FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

public readonly record struct WebTableColumnSize
{
    public WebTableColumnSize(int widthPx, int minWidthPx = 80)
    {
        MinWidthPx = Math.Max(1, minWidthPx);
        WidthPx = Math.Max(widthPx, MinWidthPx);
    }

    public int WidthPx { get; }

    public int MinWidthPx { get; }

    public string ToCssWidth()
    {
        return $"{WidthPx}px";
    }
}
