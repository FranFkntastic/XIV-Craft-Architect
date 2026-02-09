using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIVCraftArchitect.Services;
using FFXIVCraftArchitect.Services.Interfaces;
using Microsoft.Extensions.Logging;
using SettingsService = FFXIVCraftArchitect.Core.Services.SettingsService;

namespace FFXIVCraftArchitect;

/// <summary>
/// Options/Settings window for configuring the application.
/// </summary>
public partial class OptionsWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly WorldStatusService _worldStatusService;
    private readonly DialogServiceFactory _dialogFactory;
    private readonly IDialogService _dialogs;
    private readonly ILogger<OptionsWindow> _logger;
    private bool _isLoading;

    public OptionsWindow(
        SettingsService settingsService, 
        ThemeService themeService, 
        WorldStatusService worldStatusService,
        DialogServiceFactory dialogFactory,
        ILogger<OptionsWindow> logger)
    {
        InitializeComponent();
        _settingsService = settingsService;
        _themeService = themeService;
        _worldStatusService = worldStatusService;
        _dialogFactory = dialogFactory;
        _dialogs = dialogFactory.CreateForWindow(this);
        _logger = logger;
        
        LoadSettings();
        UpdateWorldStatusUI();
    }

    private void LoadSettings()
    {
        _logger.LogInformation("[OptionsWindow.LoadSettings] START");
        _isLoading = true;
        
        try
        {
            // UI Settings
            var accentColor = _settingsService.Get<string>("ui.accent_color", "#d4af37") ?? "#d4af37";
            _logger.LogInformation("[OptionsWindow.LoadSettings] ui.accent_color = '{AccentColor}'", accentColor);
            AccentColorTextBox.Text = accentColor;
            UpdateAccentPreview(accentColor);
            
            var splitPane = _settingsService.Get<bool>("ui.use_split_pane_market_view", true);
            _logger.LogInformation("[OptionsWindow.LoadSettings] ui.use_split_pane_market_view = {Value}", splitPane);
            UseSplitPaneMarketViewToggle.IsChecked = splitPane;

            // Market Settings
            var defaultDc = _settingsService.Get<string>("market.default_datacenter", "Aether") ?? "Aether";
            _logger.LogInformation("[OptionsWindow.LoadSettings] market.default_datacenter = '{Value}'", defaultDc);
            DefaultDataCenterCombo.SelectedItem = FindComboBoxItem(DefaultDataCenterCombo, defaultDc);
            
            var homeWorld = _settingsService.Get<string>("market.home_world", "");
            _logger.LogInformation("[OptionsWindow.LoadSettings] market.home_world = '{Value}'", homeWorld);
            HomeWorldTextBox.Text = homeWorld ?? "";
            
            var autoFetch = _settingsService.Get<bool>("market.auto_fetch_prices", true);
            _logger.LogInformation("[OptionsWindow.LoadSettings] market.auto_fetch_prices = {Value}", autoFetch);
            AutoFetchPricesToggle.IsChecked = autoFetch;
            
            var crossWorld = _settingsService.Get<bool>("market.include_cross_world", true);
            _logger.LogInformation("[OptionsWindow.LoadSettings] market.include_cross_world = {Value}", crossWorld);
            IncludeCrossWorldToggle.IsChecked = crossWorld;
            
            var excludeCongested = _settingsService.Get<bool>("market.exclude_congested_worlds", true);
            _logger.LogInformation("[OptionsWindow.LoadSettings] market.exclude_congested_worlds = {Value}", excludeCongested);
            ExcludeCongestedWorldsToggle.IsChecked = excludeCongested;
            
            var parallelApi = _settingsService.Get<bool>("market.parallel_api_requests", true);
            _logger.LogInformation("[OptionsWindow.LoadSettings] market.parallel_api_requests = {Value}", parallelApi);
            ParallelApiRequestsToggle.IsChecked = parallelApi;

            // Planning Settings
            var defaultMode = _settingsService.Get<string>("planning.default_recommendation_mode", "MinimizeTotalCost");
            _logger.LogInformation("[OptionsWindow.LoadSettings] planning.default_recommendation_mode = '{Value}'", defaultMode);
            DefaultRecommendationModeCombo.SelectedIndex = defaultMode == "MaximizeValue" ? 1 : 0;

            // Debugging Settings
            var diagLogging = _settingsService.Get<bool>("debug.enable_diagnostic_logging", false);
            _logger.LogInformation("[OptionsWindow.LoadSettings] debug.enable_diagnostic_logging = {Value}", diagLogging);
            EnableDiagnosticLoggingToggle.IsChecked = diagLogging;
            
            var logLevel = _settingsService.Get<int>("debug.log_level", 1);
            _logger.LogInformation("[OptionsWindow.LoadSettings] debug.log_level = {Value}", logLevel);
            LogLevelCombo.SelectedIndex = logLevel;
            
            _logger.LogInformation("[OptionsWindow.LoadSettings] COMPLETE - Settings file: {Path}", _settingsService.SettingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OptionsWindow.LoadSettings] EXCEPTION: {Message}", ex.Message);
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

    private async void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (!await _dialogs.ConfirmAsync(
            "Reset all settings to default values?",
            "Confirm Reset"))
        {
            return;
        }

        _settingsService.ResetToDefaults();
        LoadSettings();
        _logger.LogInformation("Settings reset to defaults");
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("[OptionsWindow.OnSave] START - Saving settings to: {Path}", _settingsService.SettingsFilePath);
        
        try
        {
            // Validate accent color
            var accentColor = AccentColorTextBox.Text;
            if (!IsValidHexColor(accentColor))
            {
                await _dialogs.ShowErrorAsync(
                    "Please enter a valid hex color (e.g., #d4af37)",
                    "Invalid Color");
                return;
            }

            // Save UI Settings - apply accent color immediately
            _logger.LogInformation("[OptionsWindow.OnSave] Saving ui.accent_color = '{Value}'", accentColor);
            _themeService.SetAccentColor(accentColor);
            _settingsService.Set("ui.use_split_pane_market_view", UseSplitPaneMarketViewToggle.IsChecked == true);

            // Save Market Settings
            var selectedDc = (DefaultDataCenterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Aether";
            _logger.LogInformation("[OptionsWindow.OnSave] Saving market.default_datacenter = '{Value}'", selectedDc);
            _settingsService.Set("market.default_datacenter", selectedDc);
            
            var homeWorld = HomeWorldTextBox.Text?.Trim() ?? "";
            _logger.LogInformation("[OptionsWindow.OnSave] Saving market.home_world = '{Value}'", homeWorld);
            _settingsService.Set("market.home_world", homeWorld);
            
            _settingsService.Set("market.auto_fetch_prices", AutoFetchPricesToggle.IsChecked == true);
            _settingsService.Set("market.include_cross_world", IncludeCrossWorldToggle.IsChecked == true);
            _settingsService.Set("market.exclude_congested_worlds", ExcludeCongestedWorldsToggle.IsChecked == true);
            _settingsService.Set("market.parallel_api_requests", ParallelApiRequestsToggle.IsChecked == true);

            // Save Planning Settings
            var selectedMode = DefaultRecommendationModeCombo.SelectedIndex == 1 ? "MaximizeValue" : "MinimizeTotalCost";
            _logger.LogInformation("[OptionsWindow.OnSave] Saving planning.default_recommendation_mode = '{Value}'", selectedMode);
            _settingsService.Set("planning.default_recommendation_mode", selectedMode);

            // Save Debugging Settings
            _settingsService.Set("debug.enable_diagnostic_logging", EnableDiagnosticLoggingToggle.IsChecked == true);
            _settingsService.Set("debug.log_level", LogLevelCombo.SelectedIndex);
            
            _logger.LogInformation("[OptionsWindow.OnSave] COMPLETE - Settings saved successfully");
            
            // Close silently on success - only show dialogs for errors
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            await _dialogs.ShowErrorAsync(
                $"Failed to save settings: {ex.Message}",
                ex,
                "Error");
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
    private async void OnViewLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var logWindow = new LogViewerWindow(_dialogFactory);
            logWindow.Owner = this;
            logWindow.Show();
            _logger.LogInformation("Opened log viewer");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open log viewer");
            await _dialogs.ShowErrorAsync(
                $"Failed to open log viewer: {ex.Message}",
                ex,
                "Error");
        }
    }

    /// <summary>
    /// Opens the settings.json file in the default text editor.
    /// </summary>
    private async void OnOpenSettingsFile(object sender, RoutedEventArgs e)
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
            await _dialogs.ShowErrorAsync(
                $"Failed to open settings file: {ex.Message}",
                ex,
                "Error");
        }
    }
}
