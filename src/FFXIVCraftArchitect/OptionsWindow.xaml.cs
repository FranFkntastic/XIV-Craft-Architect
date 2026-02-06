using System.Diagnostics;
using System.IO;
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
    private readonly WorldStatusService _worldStatusService;
    private readonly ILogger<OptionsWindow> _logger;
    private bool _isLoading;

    public OptionsWindow(
        SettingsService settingsService, 
        ThemeService themeService, 
        WorldStatusService worldStatusService,
        ILogger<OptionsWindow> logger)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _themeService = themeService;
        _worldStatusService = worldStatusService;
        _logger = logger;
        
        LoadSettings();
        UpdateWorldStatusUI();
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
            UseSplitPaneMarketViewToggle.IsChecked = _settingsService.Get<bool>("ui.use_split_pane_market_view", true);

            // Market Settings
            var defaultDc = _settingsService.Get<string>("market.default_datacenter", "Aether") ?? "Aether";
            DefaultDataCenterCombo.SelectedItem = FindComboBoxItem(DefaultDataCenterCombo, defaultDc);
            
            var homeWorld = _settingsService.Get<string>("market.home_world", "");
            HomeWorldTextBox.Text = homeWorld ?? "";
            
            AutoFetchPricesToggle.IsChecked = _settingsService.Get<bool>("market.auto_fetch_prices", true);
            IncludeCrossWorldToggle.IsChecked = _settingsService.Get<bool>("market.include_cross_world", true);
            ExcludeCongestedWorldsToggle.IsChecked = _settingsService.Get<bool>("market.exclude_congested_worlds", true);
            ParallelApiRequestsToggle.IsChecked = _settingsService.Get<bool>("market.parallel_api_requests", true);

            // Planning Settings
            var defaultMode = _settingsService.Get<string>("planning.default_recommendation_mode", "MinimizeTotalCost");
            DefaultRecommendationModeCombo.SelectedIndex = defaultMode == "MaximizeValue" ? 1 : 0;

            // Debugging Settings
            EnableDiagnosticLoggingToggle.IsChecked = _settingsService.Get<bool>("debug.enable_diagnostic_logging", false);
            LogLevelCombo.SelectedIndex = _settingsService.Get<int>("debug.log_level", 1); // Default to Info (index 1)
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
            _settingsService.Set("ui.use_split_pane_market_view", UseSplitPaneMarketViewToggle.IsChecked == true);

            // Save Market Settings
            var selectedDc = (DefaultDataCenterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Aether";
            _settingsService.Set("market.default_datacenter", selectedDc);
            
            var homeWorld = HomeWorldTextBox.Text?.Trim() ?? "";
            _settingsService.Set("market.home_world", homeWorld);
            
            _settingsService.Set("market.auto_fetch_prices", AutoFetchPricesToggle.IsChecked == true);
            _settingsService.Set("market.include_cross_world", IncludeCrossWorldToggle.IsChecked == true);
            _settingsService.Set("market.exclude_congested_worlds", ExcludeCongestedWorldsToggle.IsChecked == true);
            _settingsService.Set("market.parallel_api_requests", ParallelApiRequestsToggle.IsChecked == true);

            // Save Planning Settings
            var selectedMode = DefaultRecommendationModeCombo.SelectedIndex == 1 ? "MaximizeValue" : "MinimizeTotalCost";
            _settingsService.Set("planning.default_recommendation_mode", selectedMode);

            // Save Debugging Settings
            _settingsService.Set("debug.enable_diagnostic_logging", EnableDiagnosticLoggingToggle.IsChecked == true);
            _settingsService.Set("debug.log_level", LogLevelCombo.SelectedIndex);
            
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
    
    /// <summary>
    /// Updates the world status UI with current cache information.
    /// </summary>
    private void UpdateWorldStatusUI()
    {
        try
        {
            var worlds = _worldStatusService.GetAllWorldStatuses();
            var lastUpdated = _worldStatusService.LastUpdated;
            
            if (worlds.Count == 0)
            {
                WorldStatusText.Text = "No world status data loaded";
                WorldStatusText.Foreground = Brushes.IndianRed;
                WorldStatusLastUpdated.Text = "";
            }
            else
            {
                var congestedCount = worlds.Count(w => w.Value.IsCongested);
                WorldStatusText.Text = $"{worlds.Count} worlds loaded ({congestedCount} congested)";
                WorldStatusText.Foreground = Brushes.LightGreen;
                
                if (lastUpdated.HasValue)
                {
                    var localTime = lastUpdated.Value.ToLocalTime();
                    var timeAgo = DateTime.Now - localTime;
                    var timeString = timeAgo.TotalHours < 1 
                        ? $"{timeAgo.Minutes}m ago" 
                        : $"{timeAgo.Hours}h ago";
                    WorldStatusLastUpdated.Text = $"Updated {timeString}";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update world status UI");
            WorldStatusText.Text = "Error loading status";
            WorldStatusText.Foreground = Brushes.IndianRed;
        }
    }
    
    /// <summary>
    /// Refreshes world status from the Lodestone.
    /// </summary>
    private async void OnRefreshWorldStatusClick(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshWorldStatusButton.IsEnabled = false;
            RefreshWorldStatusButton.Content = "Refreshing...";
            WorldStatusText.Text = "Fetching from Lodestone...";
            WorldStatusText.Foreground = Brushes.LightGray;
            
            var success = await _worldStatusService.RefreshStatusAsync();
            
            if (success)
            {
                UpdateWorldStatusUI();
                _logger.LogInformation("World status refreshed successfully");
            }
            else
            {
                WorldStatusText.Text = "Failed to refresh - check logs";
                WorldStatusText.Foreground = Brushes.IndianRed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh world status");
            WorldStatusText.Text = $"Error: {ex.Message}";
            WorldStatusText.Foreground = Brushes.IndianRed;
        }
        finally
        {
            RefreshWorldStatusButton.IsEnabled = true;
            RefreshWorldStatusButton.Content = "Refresh World Status";
        }
    }
    
    /// <summary>
    /// Opens the log viewer window.
    /// </summary>
    private void OnViewLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var logWindow = new LogViewerWindow();
            logWindow.Owner = this;
            logWindow.Show();
            _logger.LogInformation("Opened log viewer");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open log viewer");
            MessageBox.Show(
                $"Failed to open log viewer: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Opens the settings.json file in the default text editor.
    /// </summary>
    private void OnOpenSettingsFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var settingsPath = _settingsService.SettingsFilePath;
            
            // Ensure the file exists before trying to open it
            if (!File.Exists(settingsPath))
            {
                // Save current settings to create the file
                _settingsService.Set("application.version", "0.1.0");
            }
            
            // Open with default editor
            Process.Start(new ProcessStartInfo
            {
                FileName = settingsPath,
                UseShellExecute = true
            });
            
            _logger.LogInformation("Opened settings file: {Path}", settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open settings file");
            MessageBox.Show(
                $"Failed to open settings file: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
