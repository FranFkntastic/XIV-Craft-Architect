using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace FFXIVCraftArchitect.Views.DataTemplates;

/// <summary>
/// Converts a boolean to a Visibility value (inverted).
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility visibility)
        {
            return visibility != Visibility.Visible;
        }
        return false;
    }
}

/// <summary>
/// Converts null to Visibility.Collapsed and non-null to Visibility.Visible.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts null or empty string to Visibility.Visible (for showing error messages).
/// </summary>
public class StringNullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var str = value as string;
        return !string.IsNullOrEmpty(str) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a boolean to FontStyle.Italic or FontStyle.Normal.
/// </summary>
public class BoolToFontStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue && boolValue)
        {
            return FontStyles.Italic;
        }
        return FontStyles.Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FontStyle fontStyle)
        {
            return fontStyle == FontStyles.Italic;
        }
        return false;
    }
}

/// <summary>
/// Converts a string to FontWeight.Bold or FontWeight.Normal.
/// </summary>
public class StringToFontWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string str && str.Equals("Bold", StringComparison.OrdinalIgnoreCase))
        {
            return FontWeights.Bold;
        }
        return FontWeights.Normal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FontWeight fontWeight)
        {
            return fontWeight == FontWeights.Bold ? "Bold" : "Normal";
        }
        return "Normal";
    }
}

/// <summary>
/// Converts a boolean to a string value.
/// </summary>
public class BoolToStringConverter : IValueConverter
{
    public string TrueValue { get; set; } = string.Empty;
    public string FalseValue { get; set; } = string.Empty;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolValue && boolValue)
        {
            return TrueValue;
        }
        return FalseValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts a hex color string to a SolidColorBrush.
/// </summary>
public class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string colorString && !string.IsNullOrEmpty(colorString))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorString);
                return new SolidColorBrush(color);
            }
            catch
            {
                // Return default brush if conversion fails
                return Brushes.Transparent;
            }
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is SolidColorBrush brush)
        {
            return brush.Color.ToString();
        }
        return null!;
    }
}

/// <summary>
/// Converts a value to Visibility based on whether it's less than multiplier * quantity.
/// Used for showing savings indicator.
/// </summary>
public class SavingsVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 3 && 
            values[0] is IConvertible totalCostValue && 
            values[1] is IConvertible dcAvgValue &&
            values[2] is IConvertible qtyValue)
        {
            try
            {
                var totalCost = System.Convert.ToDecimal(totalCostValue);
                var dcAveragePrice = System.Convert.ToDecimal(dcAvgValue);
                var quantityNeeded = System.Convert.ToInt32(qtyValue);
                
                return totalCost < dcAveragePrice * quantityNeeded ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                return Visibility.Collapsed;
            }
        }
        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
