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
    private string _currentAccentColor = "#d4af37";

    /// <summary>
    /// Event fired when the accent color changes.
    /// </summary>
    public event EventHandler<AccentColorChangedEventArgs>? AccentColorChanged;

    public ThemeService(SettingsService settingsService, ILogger<ThemeService> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
        
        // Load initial accent color
        _currentAccentColor = _settingsService.Get<string>("ui.accent_color", "#d4af37") ?? "#d4af37";
        
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
        var colorFromSettings = _settingsService.Get<string>("ui.accent_color", "#d4af37") ?? "#d4af37";
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

            // Update or add the accent color resource
            Application.Current.Resources["AccentColor"] = color;
            Application.Current.Resources["AccentBrush"] = brush;
            
            // Also add a faded version for backgrounds
            var fadedColor = Color.FromArgb(30, color.R, color.G, color.B);
            Application.Current.Resources["AccentColorFaded"] = fadedColor;
            Application.Current.Resources["AccentBrushFaded"] = new SolidColorBrush(fadedColor);
            
            // Update border brush
            Application.Current.Resources["AccentBorderBrush"] = brush;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update application resources with accent color {Color}", colorHex);
        }
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
