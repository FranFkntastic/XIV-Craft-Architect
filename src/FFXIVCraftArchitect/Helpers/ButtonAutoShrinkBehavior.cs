using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FFXIVCraftArchitect.Helpers;

/// <summary>
/// Attached behavior that automatically reduces button font size when text would overflow.
/// Works with both standard WPF Button and WPF-UI Button.
/// </summary>
public static class ButtonAutoShrinkBehavior
{
    public static readonly DependencyProperty EnableAutoShrinkProperty =
        DependencyProperty.RegisterAttached(
            "EnableAutoShrink",
            typeof(bool),
            typeof(ButtonAutoShrinkBehavior),
            new PropertyMetadata(false, OnEnableAutoShrinkChanged));

    public static readonly DependencyProperty MinFontSizeProperty =
        DependencyProperty.RegisterAttached(
            "MinFontSize",
            typeof(double),
            typeof(ButtonAutoShrinkBehavior),
            new PropertyMetadata(9.0));

    public static readonly DependencyProperty OriginalFontSizeProperty =
        DependencyProperty.RegisterAttached(
            "OriginalFontSize",
            typeof(double),
            typeof(ButtonAutoShrinkBehavior),
            new PropertyMetadata(0.0));

    public static bool GetEnableAutoShrink(DependencyObject obj)
        => (bool)obj.GetValue(EnableAutoShrinkProperty);

    public static void SetEnableAutoShrink(DependencyObject obj, bool value)
        => obj.SetValue(EnableAutoShrinkProperty, value);

    public static double GetMinFontSize(DependencyObject obj)
        => (double)obj.GetValue(MinFontSizeProperty);

    public static void SetMinFontSize(DependencyObject obj, double value)
        => obj.SetValue(MinFontSizeProperty, value);

    private static double GetOriginalFontSize(DependencyObject obj)
        => (double)obj.GetValue(OriginalFontSizeProperty);

    private static void SetOriginalFontSize(DependencyObject obj, double value)
        => obj.SetValue(OriginalFontSizeProperty, value);

    private static void OnEnableAutoShrinkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        
        // Check if it's a button-like control (has Content property)
        var contentProperty = d.GetType().GetProperty("Content");
        if (contentProperty == null) return;

        if ((bool)e.NewValue)
        {
            // Store original font size on first enable
            if (GetOriginalFontSize(element) == 0)
            {
                var fontSizeProperty = d.GetType().GetProperty("FontSize");
                if (fontSizeProperty != null)
                {
                    var currentSize = (double)(fontSizeProperty.GetValue(d) ?? 12.0);
                    SetOriginalFontSize(element, currentSize);
                }
            }

            element.Loaded += OnElementLoaded;
            element.SizeChanged += OnElementSizeChanged;
            
            // Trigger initial adjustment if already loaded
            if (element.IsLoaded)
            {
                AdjustFontSize(element);
            }
        }
        else
        {
            element.Loaded -= OnElementLoaded;
            element.SizeChanged -= OnElementSizeChanged;
            
            // Restore original font size
            var originalSize = GetOriginalFontSize(element);
            if (originalSize > 0)
            {
                var fontSizeProperty = d.GetType().GetProperty("FontSize");
                fontSizeProperty?.SetValue(d, originalSize);
            }
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            AdjustFontSize(element);
        }
    }

    private static void OnElementSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement element && (e.WidthChanged || e.HeightChanged))
        {
            AdjustFontSize(element);
        }
    }

    private static void AdjustFontSize(FrameworkElement element)
    {
        if (!element.IsLoaded) return;

        var originalSize = GetOriginalFontSize(element);
        if (originalSize == 0) return;

        var minSize = GetMinFontSize(element);
        
        // Get content via reflection
        var contentProperty = element.GetType().GetProperty("Content");
        if (contentProperty == null) return;
        
        var content = contentProperty.GetValue(element)?.ToString();
        
        if (string.IsNullOrEmpty(content))
        {
            SetFontSize(element, originalSize);
            return;
        }

        // Get padding via reflection
        var paddingProperty = element.GetType().GetProperty("Padding");
        Thickness padding = paddingProperty?.GetValue(element) is Thickness t ? t : new Thickness(0);

        // Calculate available width (accounting for padding)
        var availableWidth = element.ActualWidth - padding.Left - padding.Right - 8;
        var availableHeight = element.ActualHeight - padding.Top - padding.Bottom - 4;

        if (availableWidth <= 0 || availableHeight <= 0) return;

        // Get font properties via reflection
        var fontFamily = element.GetType().GetProperty("FontFamily")?.GetValue(element) as FontFamily ?? new FontFamily("Segoe UI");
        var fontWeight = element.GetType().GetProperty("FontWeight")?.GetValue(element) is FontWeight fw ? fw : FontWeights.Normal;
        var fontStyle = element.GetType().GetProperty("FontStyle")?.GetValue(element) is FontStyle fs ? fs : FontStyles.Normal;

        // Start with original size and reduce if needed
        var fontSize = originalSize;
        SetFontSize(element, fontSize);

        // Measure text size
        while (fontSize > minSize && IsTextOverflowing(content, fontFamily, fontSize, fontWeight, fontStyle, availableWidth, availableHeight))
        {
            fontSize -= 0.5;
            SetFontSize(element, fontSize);
        }
    }

    private static void SetFontSize(FrameworkElement element, double fontSize)
    {
        var fontSizeProperty = element.GetType().GetProperty("FontSize");
        fontSizeProperty?.SetValue(element, fontSize);
    }

    private static bool IsTextOverflowing(string text, FontFamily fontFamily, double fontSize, FontWeight fontWeight, FontStyle fontStyle, double maxWidth, double maxHeight)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = fontFamily,
            FontSize = fontSize,
            FontWeight = fontWeight,
            FontStyle = fontStyle,
            TextWrapping = TextWrapping.NoWrap
        };

        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        
        return textBlock.DesiredSize.Width > maxWidth || textBlock.DesiredSize.Height > maxHeight;
    }
}
