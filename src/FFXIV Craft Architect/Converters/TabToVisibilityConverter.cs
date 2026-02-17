using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FFXIV_Craft_Architect.Services.Interfaces;

namespace FFXIV_Craft_Architect.Converters;

/// <summary>
/// Converts an ApplicationTab value to Visibility based on whether it matches the target tab.
/// </summary>
public class TabToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// Gets or sets the target tab to compare against.
    /// </summary>
    public ApplicationTab TargetTab { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to invert the result.
    /// When true, returns Visible when tabs don't match.
    /// </summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ApplicationTab currentTab)
        {
            var matches = currentTab == TargetTab;
            if (Invert)
            {
                matches = !matches;
            }
            return matches ? Visibility.Visible : Visibility.Collapsed;
        }

        return Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("TabToVisibilityConverter does not support ConvertBack");
    }
}

/// <summary>
/// Converts an ApplicationTab value to a boolean indicating whether it matches the target tab.
/// Used for tab active state styling.
/// </summary>
public class TabToBoolConverter : IValueConverter
{
    /// <summary>
    /// Gets or sets the target tab to compare against.
    /// </summary>
    public ApplicationTab TargetTab { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to invert the result.
    /// </summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ApplicationTab currentTab)
        {
            var matches = currentTab == TargetTab;
            return Invert ? !matches : matches;
        }

        return Invert;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("TabToBoolConverter does not support ConvertBack");
    }
}

/// <summary>
/// Converts an ApplicationTab value to a boolean indicating whether the market view should be visible
/// (true for MarketAnalysis or ProcurementPlanner tabs).
/// </summary>
public class TabToMarketViewVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ApplicationTab currentTab)
        {
            return currentTab == ApplicationTab.MarketAnalysis || 
                   currentTab == ApplicationTab.ProcurementPlanner;
        }

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("TabToMarketViewVisibleConverter does not support ConvertBack");
    }
}
