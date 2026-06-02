namespace FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

public readonly record struct WebTableColumnSize
{
    private WebTableColumnSize(string width, int minWidthPx, int widthPx, bool isPixelWidth)
    {
        Width = string.IsNullOrWhiteSpace(width) ? "120px" : width;
        MinWidthPx = Math.Max(0, minWidthPx);
        WidthPx = Math.Max(0, widthPx);
        IsPixelWidth = isPixelWidth;
    }

    public WebTableColumnSize(int widthPx, int minWidthPx = 80)
        : this(
            $"{Math.Max(widthPx, Math.Max(1, minWidthPx))}px",
            Math.Max(1, minWidthPx),
            Math.Max(widthPx, Math.Max(1, minWidthPx)),
            isPixelWidth: true)
    {
    }

    public string Width { get; }

    public int WidthPx { get; }

    public int MinWidthPx { get; }

    public bool IsPixelWidth { get; }

    public static WebTableColumnSize Percent(decimal percent, int minWidthPx = 0)
    {
        return new WebTableColumnSize(
            $"{percent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}%",
            Math.Max(0, minWidthPx),
            Math.Max(0, minWidthPx),
            isPixelWidth: false);
    }

    public static WebTableColumnSize Css(string width, int minWidthPx = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(width);

        return new WebTableColumnSize(
            width,
            Math.Max(0, minWidthPx),
            Math.Max(0, minWidthPx),
            isPixelWidth: false);
    }

    public string ToCssWidth()
    {
        return Width;
    }
}
