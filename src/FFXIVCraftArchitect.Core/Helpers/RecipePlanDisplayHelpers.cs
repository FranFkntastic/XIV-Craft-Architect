using System;
using System.Collections.Generic;
using System.Globalization;

namespace FFXIVCraftArchitect.Core.Models;

/// <summary>
/// Static helper methods for recipe plan display and formatting.
/// Shared between WPF and Web applications to ensure consistent UI rendering.
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
            AcquisitionSource.VendorBuy => "Vendor",
            AcquisitionSource.VendorSpecialCurrency => "Vendor (Currency)",
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
            AcquisitionSource.VendorBuy => "Vend",
            AcquisitionSource.VendorSpecialCurrency => "Currency",
            _ => source.ToString()
        };
    }

    /// <summary>
    /// Gets the color code/brush name for an acquisition source.
    /// Returns platform-agnostic color identifiers that can be mapped to WPF Brushes or CSS colors.
    /// </summary>
    public static string GetSourceColorName(AcquisitionSource source)
    {
        return source switch
        {
            AcquisitionSource.Craft => "White",
            AcquisitionSource.MarketBuyNq => "LightBlue",
            AcquisitionSource.MarketBuyHq => "LightGreen",
            AcquisitionSource.VendorBuy => "LightGreen",
            AcquisitionSource.VendorSpecialCurrency => "Gold",
            _ => "White"
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
            AcquisitionSource.VendorBuy => "#90ee90",   // LightGreen
            AcquisitionSource.VendorSpecialCurrency => "#ffd700", // Gold
            _ => "#ffffff"
        };
    }

    #endregion

    #region Job Icons

    /// <summary>
    /// Gets the emoji icon for a crafting job.
    /// </summary>
    public static string GetJobIcon(string? job)
    {
        return job switch
        {
            "Carpenter" => "🪚",
            "Blacksmith" => "⚒️",
            "Armorer" => "🛡️",
            "Goldsmith" => "💍",
            "Leatherworker" => "🧵",
            "Weaver" => "🧶",
            "Alchemist" => "⚗️",
            "Culinarian" => "🍳",
            "Company Workshop" => "🏢",
            "Phase" => "📋",
            _ => "•"
        };
    }

    /// <summary>
    /// Gets all available crafting jobs with their icons.
    /// </summary>
    public static Dictionary<string, string> GetAllJobIcons()
    {
        return new Dictionary<string, string>
        {
            ["Carpenter"] = "🪚",
            ["Blacksmith"] = "⚒️",
            ["Armorer"] = "🛡️",
            ["Goldsmith"] = "💍",
            ["Leatherworker"] = "🧵",
            ["Weaver"] = "🧶",
            ["Alchemist"] = "⚗️",
            ["Culinarian"] = "🍳",
            ["Company Workshop"] = "🏢",
            ["Phase"] = "📋"
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
            return "-";

        return $"{price:N0}g";
    }

    /// <summary>
    /// Formats a price value with compact notation for large numbers.
    /// </summary>
    public static string FormatPriceCompact(decimal price)
    {
        if (price <= 0)
            return "-";

        if (price >= 1_000_000)
            return $"{(price / 1_000_000m):0.0}M";
        if (price >= 1_000)
            return $"{(price / 1_000m):0.0}k";

        return $"{price:N0}";
    }

    /// <summary>
    /// Formats a price range.
    /// </summary>
    public static string FormatPriceRange(decimal minPrice, decimal maxPrice)
    {
        if (minPrice <= 0 && maxPrice <= 0)
            return "-";

        if (minPrice == maxPrice)
            return FormatPrice(minPrice);

        return $"{FormatPriceCompact(minPrice)} - {FormatPriceCompact(maxPrice)}";
    }

    #endregion

    #region Quantity Formatting

    /// <summary>
    /// Formats a quantity with the item name.
    /// </summary>
    public static string FormatItemQuantity(string name, int quantity)
    {
        return $"{name} x{quantity}";
    }

    #endregion

    #region Tree State Helpers

    /// <summary>
    /// Calculates the total depth of a node tree (for UI indentation).
    /// </summary>
    public static int CalculateMaxDepth(PlanNode node, int currentDepth = 0)
    {
        if (node.Children == null || !node.Children.Any())
            return currentDepth;

        var maxChildDepth = currentDepth;
        foreach (var child in node.Children)
        {
            var childDepth = CalculateMaxDepth(child, currentDepth + 1);
            maxChildDepth = Math.Max(maxChildDepth, childDepth);
        }

        return maxChildDepth;
    }

    /// <summary>
    /// Flattens a node tree into a list for easier iteration.
    /// </summary>
    public static List<(PlanNode Node, int Depth)> FlattenNodeTree(List<PlanNode> rootNodes)
    {
        var result = new List<(PlanNode Node, int Depth)>();

        foreach (var root in rootNodes)
        {
            FlattenNodeRecursive(root, 0, result);
        }

        return result;
    }

    private static void FlattenNodeRecursive(PlanNode node, int depth, List<(PlanNode Node, int Depth)> result)
    {
        result.Add((node, depth));

        if (node.Children != null)
        {
            foreach (var child in node.Children)
            {
                FlattenNodeRecursive(child, depth + 1, result);
            }
        }
    }

    #endregion

    #region Analysis Formatting

    /// <summary>
    /// Formats a savings percentage for display.
    /// </summary>
    public static string FormatSavingsPercent(double percent)
    {
        if (percent <= 0)
            return $"{percent:F1}%";

        return $"+{percent:F1}%";
    }

    /// <summary>
    /// Gets a color indicator for savings (positive/negative).
    /// </summary>
    public static string GetSavingsColor(double percent)
    {
        return percent switch
        {
            > 0 => "#4caf50", // Green (savings)
            < 0 => "#f44336", // Red (loss)
            _ => "#888888"    // Gray (neutral)
        };
    }

    #endregion
}
