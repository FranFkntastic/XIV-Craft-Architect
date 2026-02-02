using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIVCraftArchitect.Services;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect;

/// <summary>
/// Options/Settings window for configuring the application.
/// </summary>
public partial class OptionsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly ILogger<OptionsWindow> _logger;
    private bool _isLoading;

    public OptionsWindow(SettingsService settingsService, ThemeService themeService, ILogger<OptionsWindow> logger)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _themeService = themeService;
        _logger = logger;
        
        LoadSettings();
    }

    private void LoadSettings()
    {
        _isLoading = true;
        
        try
        {
            // UI Settings
            var accentColor = _settingsService.Get<string>("ui.accent_color", "#d4af37") ?? "#d4af37";
            AccentColorTextBox.Text = accentColor;
            UpdateAccentPreview(accentColor);

            // Market Settings
            var defaultDc = _settingsService.Get<string>("market.default_datacenter", "Aether") ?? "Aether";
            DefaultDataCenterCombo.SelectedItem = FindComboBoxItem(DefaultDataCenterCombo, defaultDc);
            
            AutoFetchPricesToggle.IsChecked = _settingsService.Get<bool>("market.auto_fetch_prices", true);
            IncludeCrossWorldToggle.IsChecked = _settingsService.Get<bool>("market.include_cross_world", true);

            // Planning Settings
            var defaultMode = _settingsService.Get<string>("planning.default_recommendation_mode", "MinimizeTotalCost");
            DefaultRecommendationModeCombo.SelectedIndex = defaultMode == "MaximizeValue" ? 1 : 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private ComboBoxItem? FindComboBoxItem(ComboBox comboBox, string content)
    {
        foreach (ComboBoxItem item in comboBox.Items)
        {
            if (item.Content.ToString() == content)
                return item;
        }
        return comboBox.Items[0] as ComboBoxItem;
    }

    private void UpdateAccentPreview(string colorHex)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            AccentColorPreview.Background = brush;
        }
        catch
        {
            AccentColorPreview.Background = new SolidColorBrush(Colors.Gray);
        }
    }

    private void OnPresetColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Background is SolidColorBrush brush)
        {
            var colorHex = $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
            AccentColorTextBox.Text = colorHex;
            UpdateAccentPreview(colorHex);
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        
        // Subscribe to text changes after initial load
        AccentColorTextBox.TextChanged += (s, ev) =>
        {
            if (!_isLoading)
            {
                var text = AccentColorTextBox.Text;
                if (IsValidHexColor(text))
                {
                    UpdateAccentPreview(text);
                }
            }
        };
    }

    private static bool IsValidHexColor(string text)
    {
        return !string.IsNullOrEmpty(text) && 
               Regex.IsMatch(text, "^#[0-9A-Fa-f]{6}$");
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset all settings to default values?",
            "Confirm Reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _settingsService.ResetToDefaults();
            LoadSettings();
            _logger.LogInformation("Settings reset to defaults");
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            // Validate accent color
            var accentColor = AccentColorTextBox.Text;
            if (!IsValidHexColor(accentColor))
            {
                MessageBox.Show(
                    "Please enter a valid hex color (e.g., #d4af37)",
                    "Invalid Color",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Save UI Settings - apply accent color immediately
            _themeService.SetAccentColor(accentColor);

            // Save Market Settings
            var selectedDc = (DefaultDataCenterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Aether";
            _settingsService.Set("market.default_datacenter", selectedDc);
            _settingsService.Set("market.auto_fetch_prices", AutoFetchPricesToggle.IsChecked == true);
            _settingsService.Set("market.include_cross_world", IncludeCrossWorldToggle.IsChecked == true);

            // Save Planning Settings
            var selectedMode = DefaultRecommendationModeCombo.SelectedIndex == 1 ? "MaximizeValue" : "MinimizeTotalCost";
            _settingsService.Set("planning.default_recommendation_mode", selectedMode);
            
            _logger.LogInformation("Settings saved successfully");
            
            // Close silently on success - only show dialogs for errors
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            MessageBox.Show(
                $"Failed to save settings: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
