using System.Windows;
using System.Windows.Media;

namespace FFXIV_Craft_Architect.Helpers;

/// <summary>
/// Helper class for color and brush operations.
/// </summary>
public static class ColorHelper
{
    /// <summary>
    /// Gets the accent color from application resources.
    /// </summary>
    public static Color GetAccentColor()
    {
        if (Application.Current?.TryFindResource("Brush.Accent.Primary") is SolidColorBrush semanticBrush)
            return semanticBrush.Color;
        if (Application.Current?.TryFindResource("AccentBrush") is SolidColorBrush brush)
            return brush.Color;
        // Fallback to default accent color.
        return (Color)ColorConverter.ConvertFromString("#d4a73a")!;
    }

    /// <summary>
    /// Gets the accent brush from application resources.
    /// </summary>
    public static Brush GetAccentBrush()
    {
        if (Application.Current?.TryFindResource("Brush.Accent.Primary") is Brush semanticBrush)
            return semanticBrush;
        if (Application.Current?.TryFindResource("AccentBrush") is Brush brush)
            return brush;
        // Fallback to default accent color.
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#d4a73a")!);
    }

    /// <summary>
    /// Creates a muted/darker version of a color by mixing with a dark background.
    /// </summary>
    public static Color MixWithBackground(Color color, byte backgroundR, byte backgroundG, byte backgroundB, double colorRatio = 0.25)
    {
        var bgRatio = 1.0 - colorRatio;
        return Color.FromRgb(
            (byte)((color.R * colorRatio) + (backgroundR * bgRatio)),
            (byte)((color.G * colorRatio) + (backgroundG * bgRatio)),
            (byte)((color.B * colorRatio) + (backgroundB * bgRatio)));
    }

    /// <summary>
    /// Gets a muted/darker version of the accent color for card backgrounds.
    /// </summary>
    public static Brush GetMutedAccentBrush()
    {
        var color = GetAccentColor();
        // Mix with dark background color (#2d2d2d = 45, 45, 45)
        var muted = MixWithBackground(color, 45, 45, 45, 0.25);
        return new SolidColorBrush(muted);
    }

    /// <summary>
    /// Gets a slightly lighter muted accent for card headers.
    /// </summary>
    public static Brush GetMutedAccentBrushLight()
    {
        var color = GetAccentColor();
        // Mix with a lighter dark color (#4a4a4a = 74, 74, 74)
        var muted = MixWithBackground(color, 74, 74, 74, 0.3);
        return new SolidColorBrush(muted);
    }

    /// <summary>
    /// Gets a lighter accent for expanded card headers.
    /// </summary>
    public static Brush GetMutedAccentBrushExpanded()
    {
        var color = GetAccentColor();
        // Mix with an even lighter dark color (#5a5a5a = 90, 90, 90)
        var muted = MixWithBackground(color, 90, 90, 90, 0.35);
        return new SolidColorBrush(muted);
    }
}
