using System.Globalization;

namespace FFXIV_Craft_Architect.Web.Services;

public static class TradeDisplayFormatter
{
    public static string FormatQuantity(int quantity)
    {
        return $"x{quantity.ToString("N0", CultureInfo.InvariantCulture)}";
    }
}
