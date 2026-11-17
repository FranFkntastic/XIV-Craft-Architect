namespace FFXIV_Craft_Architect.Core.Models;

/// <summary>
/// Static helper methods for recipe plan display and formatting.
/// Shared across UI surfaces to ensure consistent rendering.
/// </summary>
public static class RecipePlanDisplayHelpers
{
    #region Acquisition Source Display

    /// <summary>
    /// Gets the human-readable display name for an acquisition source.
    /// </summary>
    public static string GetSourceDisplayName(AcquisitionSource source)
    {
        return source switch
        {
            AcquisitionSource.Craft => "Craft",
            AcquisitionSource.MarketBuyNq => "Buy NQ",
            AcquisitionSource.MarketBuyHq => "Buy HQ",
            AcquisitionSource.UnknownSource => "Figure it out",
            AcquisitionSource.VendorBuy => "Vendor",
            AcquisitionSource.VendorSpecialCurrency => "Vendor (Currency)",
            AcquisitionSource.OnHand => "On hand",
            _ => source.ToString()
        };
    }

    /// <summary>
    /// Gets a short display name for acquisition sources (for constrained UI spaces).
    /// </summary>
    public static string GetSourceShortName(AcquisitionSource source)
    {
        return source switch
        {
            AcquisitionSource.Craft => "Craft",
            AcquisitionSource.MarketBuyNq => "NQ",
            AcquisitionSource.MarketBuyHq => "HQ",
            AcquisitionSource.UnknownSource => "?",
            AcquisitionSource.VendorBuy => "Vend",
            AcquisitionSource.VendorSpecialCurrency => "Currency",
            AcquisitionSource.OnHand => "Stock",
            _ => source.ToString()
        };
    }

    /// <summary>
    /// Gets the hex color for an acquisition source (for web/Blazor use).
    /// </summary>
    public static string GetSourceHexColor(AcquisitionSource source)
    {
        return source switch
        {
            AcquisitionSource.Craft => "#ffffff",
            AcquisitionSource.MarketBuyNq => "#87cefa", // LightSkyBlue
            AcquisitionSource.MarketBuyHq => "#90ee90", // LightGreen
            AcquisitionSource.UnknownSource => "#b0b0b0",
            AcquisitionSource.VendorBuy => "#90ee90",   // LightGreen
            AcquisitionSource.VendorSpecialCurrency => "#ffd700", // Gold
            AcquisitionSource.OnHand => "#8eb9ce",
            _ => "#ffffff"
        };
    }

    #endregion

    #region Price Formatting

    /// <summary>
    /// Formats a price value with the "g" suffix for gil.
    /// </summary>
    public static string FormatPrice(decimal price)
    {
        if (price <= 0)
        {
            return "-";
        }

        return $"{price:N0}g";
    }

    /// <summary>
    /// Formats a price value with compact notation for large numbers.
    /// </summary>
    public static string FormatPriceCompact(decimal price)
    {
        if (price <= 0)
        {
            return "-";
        }

        if (price >= 1_000_000)
        {
            return $"{(price / 1_000_000m):0.0}M";
        }

        if (price >= 1_000)
        {
            return $"{(price / 1_000m):0.0}k";
        }

        return $"{price:N0}";
    }

    #endregion

}
