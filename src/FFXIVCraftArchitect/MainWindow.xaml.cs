using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FFXIVCraftArchitect.Services;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace FFXIVCraftArchitect;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly GarlandService _garlandService;
    private readonly UniversalisService _universalisService;
    private readonly SettingsService _settingsService;

    // Search state
    private List<Models.GarlandSearchResult> _currentSearchResults = new();
    private int? _selectedItemId;

    public MainWindow()
    {
        InitializeComponent();

        // Get services from DI
        _garlandService = App.Services.GetRequiredService<GarlandService>();
        _universalisService = App.Services.GetRequiredService<UniversalisService>();
        _settingsService = App.Services.GetRequiredService<SettingsService>();

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Load world data
        await LoadWorldDataAsync();
    }

    private async Task LoadWorldDataAsync()
    {
        StatusLabel.Text = "Loading world data...";
        try
        {
            var worldData = await _universalisService.GetWorldDataAsync();
            
            DcCombo.ItemsSource = worldData.DataCenters;
            DcCombo.SelectedItem = _settingsService.Get<string>("market.default_datacenter") ?? "Aether";
            OnDataCenterSelected(null, null);
            
            StatusLabel.Text = "Ready";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Failed to load world data: {ex.Message}";
        }
    }

    // =========================================================================
    // Event Handlers
    // =========================================================================

    private void OnDataCenterSelected(object? sender, SelectionChangedEventArgs? e)
    {
        // This will be implemented with proper ViewModel later
    }

    private void OnSyncInventory(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Sync inventory clicked - not implemented yet";
    }

    private void OnLiveModeToggled(object sender, RoutedEventArgs e)
    {
        var isOn = LiveSwitch.IsChecked == true;
        StatusLabel.Text = isOn ? "Live mode toggled ON - not implemented yet" : "Live mode toggled OFF";
    }

    private void OnItemSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSearchItem(sender, e);
        }
    }

    private async void OnSearchItem(object sender, RoutedEventArgs e)
    {
        var query = ItemSearch.Text?.Trim();
        if (string.IsNullOrEmpty(query))
            return;

        StatusLabel.Text = $"Searching for '{query}'...";
        SearchResults.ItemsSource = null;
        
        try
        {
            _currentSearchResults = await _garlandService.SearchAsync(query);
            SearchResults.ItemsSource = _currentSearchResults.Select(r => r.Object.Name).ToList();
            
            StatusLabel.Text = $"Found {_currentSearchResults.Count} results";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Search failed: {ex.Message}";
        }
    }

    private void OnItemSelected(object sender, SelectionChangedEventArgs e)
    {
        var index = SearchResults.SelectedIndex;
        if (index >= 0 && index < _currentSearchResults.Count)
        {
            _selectedItemId = _currentSearchResults[index].Id;
            BuildPlanButton.IsEnabled = true;
            StatusLabel.Text = $"Selected: {_currentSearchResults[index].Object.Name} (ID: {_selectedItemId})";
        }
        else
        {
            _selectedItemId = null;
            BuildPlanButton.IsEnabled = false;
        }
    }

    private void OnBuildProjectPlan(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "Build project plan clicked - not implemented yet";
    }

    private void OnViewLogs(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "View logs clicked - not implemented yet";
    }

    private void OnViewInventory(object sender, RoutedEventArgs e)
    {
        StatusLabel.Text = "View inventory clicked - not implemented yet";
    }

    private void OnReloadApp(object sender, RoutedEventArgs e)
    {
        // Restart the application
        Process.Start(Process.GetCurrentProcess().MainFileName);
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Cleanup logic here (stop live mode, etc.)
        base.OnClosing(e);
    }
}
