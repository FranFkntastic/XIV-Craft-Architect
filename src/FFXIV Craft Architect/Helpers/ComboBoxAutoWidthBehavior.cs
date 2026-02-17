using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FFXIV_Craft_Architect.Helpers;

/// <summary>
/// Attached behavior to automatically size ComboBox width based on the widest item.
/// Prevents text cutoff without hardcoding widths.
/// </summary>
public static class ComboBoxAutoWidthBehavior
{
    public static readonly DependencyProperty EnableAutoWidthProperty =
        DependencyProperty.RegisterAttached(
            "EnableAutoWidth",
            typeof(bool),
            typeof(ComboBoxAutoWidthBehavior),
            new PropertyMetadata(false, OnEnableAutoWidthChanged));

    public static bool GetEnableAutoWidth(ComboBox comboBox)
    {
        return (bool)comboBox.GetValue(EnableAutoWidthProperty);
    }

    public static void SetEnableAutoWidth(ComboBox comboBox, bool value)
    {
        comboBox.SetValue(EnableAutoWidthProperty, value);
    }

    private static void OnEnableAutoWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox comboBox) return;

        if ((bool)e.NewValue)
        {
            comboBox.Loaded += OnComboBoxLoaded;
            comboBox.ItemContainerGenerator.ItemsChanged += (_, _) => UpdateWidth(comboBox);
        }
        else
        {
            comboBox.Loaded -= OnComboBoxLoaded;
        }
    }

    private static void OnComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            UpdateWidth(comboBox);
        }
    }

    private static void UpdateWidth(ComboBox comboBox)
    {
        if (comboBox.Items.Count == 0) return;

        double maxWidth = 0;
        var typeface = new Typeface(
            comboBox.FontFamily,
            comboBox.FontStyle,
            comboBox.FontWeight,
            comboBox.FontStretch);

        foreach (var item in comboBox.Items)
        {
            var text = GetItemText(item);
            if (string.IsNullOrEmpty(text)) continue;

            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                comboBox.FontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(comboBox).PixelsPerDip);

            maxWidth = Math.Max(maxWidth, formattedText.Width);
        }

        // Add padding for dropdown arrow and some margin
        comboBox.MinWidth = maxWidth + 40;
    }

    private static string GetItemText(object item)
    {
        return item switch
        {
            ComboBoxItem comboItem => comboItem.Content?.ToString() ?? string.Empty,
            string str => str,
            _ => item?.ToString() ?? string.Empty
        };
    }
}
