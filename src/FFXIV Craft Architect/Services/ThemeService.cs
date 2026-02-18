using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using SettingsService = FFXIV_Craft_Architect.Core.Services.SettingsService;

namespace FFXIV_Craft_Architect.Services;

/// <summary>
/// Service for managing theme colors and dynamic UI updates.
/// Provides centralized accent color management that updates UI in real-time.
/// </summary>
public class ThemeService
{
    private readonly SettingsService _settingsService;
    private readonly ILogger<ThemeService> _logger;
    private string _currentAccentColor = "#d4a73a";

    /// <summary>
    /// Event fired when the accent color changes.
    /// </summary>
    public event EventHandler<AccentColorChangedEventArgs>? AccentColorChanged;

    public ThemeService(SettingsService settingsService, ILogger<ThemeService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
        
        // Load initial accent color
        _currentAccentColor = _settingsService.Get<string>("ui.accent_color", "#d4a73a") ?? "#d4a73a";
        
        // Initialize application resources
        UpdateApplicationResources(_currentAccentColor);
    }

    /// <summary>
    /// Gets the current accent color.
    /// </summary>
    public string AccentColor => _currentAccentColor;

    /// <summary>
    /// Gets the current accent color as a Brush.
    /// </summary>
    public Brush AccentBrush => new SolidColorBrush((Color)ColorConverter.ConvertFromString(_currentAccentColor));

    /// <summary>
    /// Sets a new accent color and updates the UI immediately.
    /// </summary>
    public void SetAccentColor(string colorHex)
    {
        if (!IsValidHexColor(colorHex))
        {
            _logger.LogWarning("Invalid accent color: {Color}", colorHex);
            return;
        }

        if (_currentAccentColor.Equals(colorHex, StringComparison.OrdinalIgnoreCase))
            return;

        var oldColor = _currentAccentColor;
        _currentAccentColor = colorHex;
        
        // Save to settings
        _settingsService.Set("ui.accent_color", colorHex);
        
        // Update application-wide resources
        UpdateApplicationResources(colorHex);
        
        // Notify subscribers
        AccentColorChanged?.Invoke(this, new AccentColorChangedEventArgs(oldColor, colorHex));
        
        _logger.LogInformation("Accent color changed from {OldColor} to {NewColor}", oldColor, colorHex);
    }

    /// <summary>
    /// Refreshes the theme from settings (useful after settings are modified externally).
    /// </summary>
    public void RefreshFromSettings()
    {
        var colorFromSettings = _settingsService.Get<string>("ui.accent_color", "#d4a73a") ?? "#d4a73a";
        if (!colorFromSettings.Equals(_currentAccentColor, StringComparison.OrdinalIgnoreCase))
        {
            SetAccentColor(colorFromSettings);
        }
    }

    /// <summary>
    /// Updates the Application resources with the new accent color.
    /// This makes the color available via DynamicResource bindings.
    /// </summary>
    private void UpdateApplicationResources(string colorHex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            var brush = new SolidColorBrush(color);
            brush.Freeze(); // Make it thread-safe

            var marketCardColor = MixWithBackground(color, 45, 45, 45, 0.20);
            var marketHeaderColor = MixWithBackground(color, 62, 62, 62, 0.25);
            var marketBorderColor = MixWithBackground(color, 77, 77, 77, 0.35);

            var marketCardBrush = new SolidColorBrush(marketCardColor);
            marketCardBrush.Freeze();
            var marketHeaderBrush = new SolidColorBrush(marketHeaderColor);
            marketHeaderBrush.Freeze();
            var marketBorderBrush = new SolidColorBrush(marketBorderColor);
            marketBorderBrush.Freeze();

            // Update or add the accent color resource
            Application.Current.Resources["Color.Accent.Primary"] = color;
            Application.Current.Resources["Brush.Accent.Primary"] = brush;
            Application.Current.Resources["AccentColor"] = color;
            Application.Current.Resources["AccentBrush"] = brush;
            
            // Also add a faded version for backgrounds
            var fadedColor = Color.FromArgb(30, color.R, color.G, color.B);
            Application.Current.Resources["Color.Accent.Primary.Faded"] = fadedColor;
            Application.Current.Resources["Brush.Accent.Primary.Faded"] = new SolidColorBrush(fadedColor);
            Application.Current.Resources["AccentColorFaded"] = fadedColor;
            Application.Current.Resources["AccentBrushFaded"] = new SolidColorBrush(fadedColor);
            
            // Update border brush
            Application.Current.Resources["Brush.Border.Accent"] = brush;
            Application.Current.Resources["AccentBorderBrush"] = brush;

            // Update market card palette derived from current accent color
            Application.Current.Resources["Brush.Surface.Card.Market"] = marketCardBrush;
            Application.Current.Resources["Brush.Surface.Card.Market.Header"] = marketHeaderBrush;

            // Back-compat resources used by some templates/services
            Application.Current.Resources["MutedAccentBrush"] = marketCardBrush;
            Application.Current.Resources["MutedAccentLightBrush"] = marketHeaderBrush;
            Application.Current.Resources["Brush.Border.Card.Market"] = marketBorderBrush;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update application resources with accent color {Color}", colorHex);
        }
    }

    private static Color MixWithBackground(Color color, byte backgroundR, byte backgroundG, byte backgroundB, double colorRatio)
    {
        var bgRatio = 1.0 - colorRatio;
        return Color.FromRgb(
            (byte)((color.R * colorRatio) + (backgroundR * bgRatio)),
            (byte)((color.G * colorRatio) + (backgroundG * bgRatio)),
            (byte)((color.B * colorRatio) + (backgroundB * bgRatio)));
    }

    private static bool IsValidHexColor(string text)
    {
        return !string.IsNullOrEmpty(text) && 
               System.Text.RegularExpressions.Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$");
    }
}

/// <summary>
/// Event args for accent color change events.
/// </summary>
public class AccentColorChangedEventArgs : EventArgs
{
    public string OldColor { get; }
    public string NewColor { get; }

    public AccentColorChangedEventArgs(string oldColor, string newColor)
    {
        OldColor = oldColor;
        NewColor = newColor;
    }
}
