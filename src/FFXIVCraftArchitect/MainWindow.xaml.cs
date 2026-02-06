using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Coordinators;
using FFXIVCraftArchitect.Helpers;
using FFXIVCraftArchitect.Services;
using FFXIVCraftArchitect.UIBuilders;
using FFXIVCraftArchitect.ViewModels;
using FFXIVCraftArchitect.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static FFXIVCraftArchitect.Services.PriceCheckService;
using Window = System.Windows.Window;

// Type aliases for disambiguation
using CraftingPlan = FFXIVCraftArchitect.Core.Models.CraftingPlan;
using PlanNode = FFXIVCraftArchitect.Core.Models.PlanNode;
using AcquisitionSource = FFXIVCraftArchitect.Core.Models.AcquisitionSource;
using DetailedShoppingPlan = FFXIVCraftArchitect.Core.Models.DetailedShoppingPlan;
using MaterialAggregate = FFXIVCraftArchitect.Core.Models.MaterialAggregate;
using PriceSource = FFXIVCraftArchitect.Core.Models.PriceSource;
using PriceInfo = FFXIVCraftArchitect.Services.PriceInfo;
using WatchState = FFXIVCraftArchitect.Models.WatchState;

namespace FFXIVCraftArchitect;

/// <summary>
/// Represents an item in the project list
/// </summary>
public class ProjectItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public bool IsHqRequired { get; set; } = false;
    public override string ToString() => $"{Name} x{Quantity}";
}

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly GarlandService _garlandService;
    private readonly UniversalisService _universalisService;
    private readonly SettingsService _settingsService;
    private readonly ItemCacheService _itemCache;
    private readonly RecipeCalculationService _recipeCalcService;
    private readonly PlanPersistenceService _planPersistence;
    private readonly PriceCheckService _priceCheckService;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly WaitingwayTravelService _waitingwayService;
    private readonly WorldBlacklistService _blacklistService;
    private readonly ILogger<MainWindow> _logger;

    // ViewModels
    private readonly RecipePlannerViewModel _recipeVm;
    private readonly MarketAnalysisViewModel _marketVm;
    private readonly MainViewModel _mainVm;
    
    // UI Builders
    private RecipeTreeUiBuilder? _recipeTreeBuilder;
    
    // Search state
    private List<GarlandSearchResult> _currentSearchResults = new();
    private GarlandSearchResult? _selectedSearchResult;
    
    // Market data status window for real-time fetch visualization
    private MarketDataStatusWindow? _marketDataStatusWindow;
    
    // Backwards compatibility properties (using Core.Models types)
    private Core.Models.CraftingPlan? _currentPlan => _recipeVm.CurrentPlan;
    private List<Core.Models.DetailedShoppingPlan> _currentMarketPlans => _marketVm.ShoppingPlans.Select(vm => vm.Plan).ToList();
    
    // Current plan file path
    private string? _currentPlanPath;
    
    // Split-pane view state
    private DetailedShoppingPlan? _expandedSplitPanePlan;
    
    // Coordinators
    private readonly ImportCoordinator _importCoordinator;
    private readonly ExportCoordinator _exportCoordinator;
    private readonly PlanPersistenceCoordinator _planCoordinator;

    public MainWindow(
        GarlandService garlandService,
        UniversalisService universalisService,
        SettingsService settingsService,
        ItemCacheService itemCache,
        RecipeCalculationService recipeCalcService,
        PlanPersistenceService planPersistence,
        PriceCheckService priceCheckService,
        MarketShoppingService marketShoppingService,
        WaitingwayTravelService waitingwayService,
        WorldBlacklistService blacklistService,
        ILogger<MainWindow> logger,
        ImportCoordinator importCoordinator,
        ExportCoordinator exportCoordinator,
        PlanPersistenceCoordinator planCoordinator)
    {
        InitializeComponent();

        // Services (injected via DI)
        _garlandService = garlandService;
        _universalisService = universalisService;
        _settingsService = settingsService;
        _itemCache = itemCache;
        _recipeCalcService = recipeCalcService;
        _planPersistence = planPersistence;
        _priceCheckService = priceCheckService;
        _marketShoppingService = marketShoppingService;
        _waitingwayService = waitingwayService;
        _blacklistService = blacklistService;
        _logger = logger;
        
        // Coordinators (injected via DI)
        _importCoordinator = importCoordinator;
        _exportCoordinator = exportCoordinator;
        _planCoordinator = planCoordinator;
        
        // Create ViewModels
        _recipeVm = new RecipePlannerViewModel();
        _marketVm = new MarketAnalysisViewModel();
        _mainVm = new MainViewModel(_recipeVm, _marketVm);
        
        // Set DataContext for DataTemplate binding
        DataContext = _mainVm;
        
        // Subscribe to blacklist events
        _blacklistService.WorldUnblacklisted += (s, e) => 
        {
            Dispatcher.Invoke(() => 
            {
                StatusLabel.Text = $"{e.WorldName} removed from blacklist ({e.ExpiresInDisplay})";
                if (IsMarketViewVisible())
                {
                    PopulateProcurementPanel();
                }
            });
        };

        // Inject logger into ViewModels (respect diagnostic setting)
        var diagnosticLoggingEnabled = _settingsService.Get<bool>("debug.enable_diagnostic_logging", false);
        _recipeVm.SetLogger(App.Services.GetRequiredService<ILogger<RecipePlannerViewModel>>(), enableDiagnostics: diagnosticLoggingEnabled);

        // Initialize UI builder with ViewModel callbacks
        _recipeTreeBuilder = new RecipeTreeUiBuilder(
            onAcquisitionChanged: (nodeId, source) => _recipeVm.SetNodeAcquisition(nodeId, source),
            onHqChanged: (nodeId, isHq, mode) => _recipeVm.SetNodeHq(nodeId, isHq, mode)
        );
        
        // Wire up ViewModel events
        _recipeVm.NodeAcquisitionChanged += OnNodeAcquisitionChanged;
        _recipeVm.NodeHqChanged += OnNodeHqChanged;
        _recipeVm.PropertyChanged += OnRecipeVmPropertyChanged;
        _marketVm.PropertyChanged += OnMarketVmPropertyChanged;
        
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        OnLoadedAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnLoadedAsync()
    {
        await LoadWorldDataAsync();
        
        var defaultMode = _settingsService.Get<string>("planning.default_recommendation_mode", "MinimizeTotalCost");
        MarketModeCombo.SelectedIndex = defaultMode == "MaximizeValue" ? 1 : 0;
        
        if (App.RestoredWatchState != null)
        {
            await RestoreWatchStateAsync(App.RestoredWatchState);
            App.RestoredWatchState = null;
        }
    }

    private void OnAsyncError(Exception ex)
    {
        _logger.LogError(ex, "Async operation failed");
        StatusLabel.Text = $"Operation failed: {ex.Message}";
        MessageBox.Show($"Operation failed: {ex.Message}", "Error", 
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
    
    // ========================================================================
    // ViewModel Event Handlers
    // ========================================================================
    
    private void OnRecipeVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RecipePlannerViewModel.CurrentPlan):
                if (_recipeVm.CurrentPlan != null)
                {
                    _recipeTreeBuilder?.BuildTree(_recipeVm.RootNodes, RecipePlanPanel);
                    PopulateShoppingList();
                    ProcurementRefreshButton.IsEnabled = true;
                    RebuildFromCacheButton.IsEnabled = true;
                    RefreshPricesButton.IsEnabled = true;
                    ExpandAllButton.IsEnabled = true;
                    CollapseAllButton.IsEnabled = true;
                }
                else
                {
                    ProcurementRefreshButton.IsEnabled = false;
                    RebuildFromCacheButton.IsEnabled = false;
                    RefreshPricesButton.IsEnabled = false;
                    ExpandAllButton.IsEnabled = false;
                    CollapseAllButton.IsEnabled = false;
                }
                break;
                
            case nameof(RecipePlannerViewModel.ProjectItems):
                ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
                UpdateQuickViewCount();
                BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
                BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
                break;
                
            case nameof(RecipePlannerViewModel.StatusMessage):
                StatusLabel.Text = _recipeVm.StatusMessage;
                break;
        }
    }
    
    private void OnMarketVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MarketAnalysisViewModel.ShoppingPlans):
                ApplyMarketSortAndDisplay();
                break;
                
            case nameof(MarketAnalysisViewModel.StatusMessage):
                StatusLabel.Text = _marketVm.StatusMessage;
                break;
        }
    }
    
    private void OnNodeAcquisitionChanged(object? sender, NodeChangedEventArgs e)
    {
        _recipeTreeBuilder?.UpdateNodeAcquisition(e.NodeId, e.Node.Source);
        PopulateShoppingList();
        
        if (IsMarketViewVisible())
        {
            PopulateProcurementPanel();
        }
    }
    
    private void OnNodeHqChanged(object? sender, NodeChangedEventArgs e)
    {
        _recipeTreeBuilder?.UpdateNodeHqIndicator(e.NodeId, e.Node.MustBeHq);
        PopulateShoppingList();
    }

    private async Task LoadWorldDataAsync()
    {
        StatusLabel.Text = "Loading world data...";
        try
        {
            var worldData = await _universalisService.GetWorldDataAsync();
            
            var worldNameToId = worldData.WorldIdToName.ToDictionary(
                kvp => kvp.Value, 
                kvp => kvp.Key, 
                StringComparer.OrdinalIgnoreCase);
            _marketShoppingService.SetWorldNameToIdMapping(worldNameToId);
            
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

    // ========================================================================
    // Event Handlers
    // ========================================================================

    private void OnDataCenterSelected(object? sender, SelectionChangedEventArgs? e)
    {
        var dc = DcCombo.SelectedItem as string;
        if (dc != null && _universalisService != null)
        {
            var worldData = _universalisService.GetCachedWorldData();
            if (worldData?.DataCenterToWorlds.TryGetValue(dc, out var worlds) == true)
            {
                var worldList = new List<string> { "Entire Data Center" };
                worldList.AddRange(worlds);
                WorldCombo.ItemsSource = worldList;
                WorldCombo.SelectedItem = "Entire Data Center";
            }
        }
    }

    private void OnItemSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSearchItem(sender, e);
        }
    }

    private void OnSearchItem(object sender, RoutedEventArgs e)
    {
        OnSearchItemAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnSearchItemAsync()
    {
        var query = ItemSearch.Text?.Trim();
        if (string.IsNullOrEmpty(query))
            return;

        StatusLabel.Text = $"Searching for '{query}'...";
        SearchResults.ItemsSource = null;
        _selectedSearchResult = null;
        AddToProjectButton.IsEnabled = false;
        
        try
        {
            _currentSearchResults = await _garlandService.SearchAsync(query);
            SearchResults.ItemsSource = _currentSearchResults.Select(r => r.Object?.Name ?? $"Item_{r.Id}").ToList();
            
            _itemCache.StoreItems(_currentSearchResults.Select(r => (r.Id, r.Object?.Name ?? $"Item_{r.Id}", r.Object?.IconId ?? 0)));
            
            SearchResultsPanel.Visibility = Visibility.Visible;
            
            StatusLabel.Text = $"Found {_currentSearchResults.Count} results (cached)";
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
            _selectedSearchResult = _currentSearchResults[index];
            AddToProjectButton.IsEnabled = true;
            var name = _selectedSearchResult.Object?.Name ?? $"Item_{_selectedSearchResult.Id}";
            StatusLabel.Text = $"Selected: {name} (ID: {_selectedSearchResult.Id})";
        }
        else
        {
            _selectedSearchResult = null;
            AddToProjectButton.IsEnabled = false;
        }
    }

    private void OnAddToProject(object sender, RoutedEventArgs e)
    {
        if (_selectedSearchResult == null)
            return;

        var itemName = _selectedSearchResult.Object?.Name ?? $"Item_{_selectedSearchResult.Id}";

        var existing = _recipeVm.ProjectItems.FirstOrDefault(p => p.Id == _selectedSearchResult.Id);
        if (existing != null)
        {
            existing.Quantity++;
            StatusLabel.Text = $"Increased quantity of {existing.Name} to {existing.Quantity}";
        }
        else
        {
            _recipeVm.ProjectItems.Add(new ProjectItem
            {
                Id = _selectedSearchResult.Id,
                Name = itemName,
                Quantity = 1
            });
            StatusLabel.Text = $"Added {itemName} to project";
        }

        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
        UpdateQuickViewCount();
        
        BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
    }

    private void OnBuildProjectPlan(object sender, RoutedEventArgs e)
    {
        OnBuildProjectPlanAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnBuildProjectPlanAsync()
    {
        if (_recipeVm.ProjectItems.Count == 0)
        {
            StatusLabel.Text = "Add items to project first";
            return;
        }

        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var world = WorldCombo.SelectedItem as string ?? "";

        StatusLabel.Text = $"Building plan for {_recipeVm.ProjectItems.Count} items...";
        BuildPlanButton.IsEnabled = false;
        BrowsePlanButton.IsEnabled = false;
        
        try
        {
            var targets = _recipeVm.ProjectItems.Select(p => (p.Id, p.Name, p.Quantity, p.IsHqRequired)).ToList();
            _recipeVm.CurrentPlan = await _recipeCalcService.BuildPlanAsync(targets, dc, world);
            
            DisplayPlanInTreeView(_currentPlan);
            UpdateBuildPlanButtonText();
            
            PopulateShoppingList();
            
            ProcurementRefreshButton.IsEnabled = true;
            RebuildFromCacheButton.IsEnabled = true;
            
            StatusLabel.Text = $"Plan built: {_currentPlan.RootItems.Count} root items, " +
                               $"{_currentPlan.AggregatedMaterials.Count} unique materials";
            
            if (_settingsService.Get<bool>("market.auto_fetch_prices", true))
            {
                StatusLabel.Text += " - Auto-fetching prices...";
                await Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(100);
                    OnFetchPrices(this, new RoutedEventArgs());
                });
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Failed to build plan: {ex.Message}";
        }
        finally
        {
            BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
            BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        }
    }

    private void OnBrowsePlan(object sender, RoutedEventArgs e)
    {
        var dialog = new Views.PlanBrowserDialog(_garlandService, _recipeVm.ProjectItems);
        dialog.Owner = this;
        
        if (dialog.ShowDialog() == true && dialog.HasChanges)
        {
            var modifiedItems = dialog.GetModifiedItems();
            _recipeVm.ProjectItems.Clear();
            foreach (var item in modifiedItems)
            {
                _recipeVm.ProjectItems.Add(item);
            }
            
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
            UpdateQuickViewCount();
            UpdateBuildPlanButtonText();
            
            StatusLabel.Text = $"Plan updated: {_recipeVm.ProjectItems.Count} root items";
        }
    }

    private void PopulateShoppingList()
    {
        if (_currentPlan?.AggregatedMaterials == null || ShoppingListPanel == null)
            return;

        ShoppingListPanel.Children.Clear();
        decimal totalCost = 0;

        foreach (var material in _currentPlan.AggregatedMaterials.OrderBy(m => m.Name))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            var nameBlock = new TextBlock
            {
                Text = material.Name,
                Foreground = Brushes.White,
                Padding = new Thickness(12, 6, 4, 6),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameBlock, 0);
            row.Children.Add(nameBlock);

            var qtyBlock = new TextBlock
            {
                Text = material.TotalQuantity.ToString(),
                Foreground = Brushes.LightGray,
                Padding = new Thickness(4, 6, 4, 6),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(qtyBlock, 1);
            row.Children.Add(qtyBlock);

            var price = material.UnitPrice > 0 ? material.UnitPrice * material.TotalQuantity : 0;
            totalCost += price;
            var priceText = price > 0 ? $"{price:N0}g" : "-";
            var priceBlock = new TextBlock
            {
                Text = priceText,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50")),
                Padding = new Thickness(4, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Grid.SetColumn(priceBlock, 2);
            row.Children.Add(priceBlock);

            if (ShoppingListPanel.Children.Count % 2 == 1)
            {
                row.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));
            }

            ShoppingListPanel.Children.Add(row);
        }

        if (TotalCostText != null)
        {
            TotalCostText.Text = $"{totalCost:N0}g";
        }
    }

    private void OnProjectItemSelected(object sender, SelectionChangedEventArgs e)
    {
    }

    private void UpdateBuildPlanButtonText()
    {
        var hasPlan = _currentPlan != null && _currentPlan.RootItems.Count > 0;
        BuildPlanButton.Content = hasPlan ? "Rebuild Project Plan" : "Build Project Plan";
    }

    private void DisplayPlanInTreeView(CraftingPlan plan)
    {
        _recipeTreeBuilder?.BuildTree(_recipeVm.RootNodes, RecipePlanPanel);
    }
    
    private void AddRootExpandCollapseButtons(StackPanel headerPanel, Expander rootExpander)
    {
        var expandButton = new TextBlock
        {
            Text = "+",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Expand all subcrafts in this recipe"
        };
        expandButton.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            SetExpanderSubtreeState(rootExpander, isExpanded: true);
        };
        
        var collapseButton = new TextBlock
        {
            Text = "\u2212",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 4, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Collapse all subcrafts in this recipe"
        };
        collapseButton.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            SetExpanderSubtreeState(rootExpander, isExpanded: false);
        };
        
        headerPanel.Children.Add(expandButton);
        headerPanel.Children.Add(collapseButton);
    }
    
    private void SetExpanderSubtreeState(Expander expander, bool isExpanded)
    {
        expander.IsExpanded = isExpanded;
        
        if (expander.Content is StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Expander childExpander)
                {
                    SetExpanderSubtreeState(childExpander, isExpanded);
                }
            }
        }
    }
    
    private Brush GetNodeForeground(PlanNode node) => node.Source switch
    {
        AcquisitionSource.Craft => Brushes.White,
        AcquisitionSource.MarketBuyNq => Brushes.LightSkyBlue,
        AcquisitionSource.MarketBuyHq => Brushes.LightGreen,
        AcquisitionSource.VendorBuy => Brushes.LightYellow,
        _ => Brushes.LightGray
    };
    
    private string GetNodePriceDisplay(PlanNode node)
    {
        return node.Source switch
        {
            AcquisitionSource.Craft when node.Children.Any() => 
                $"(~{CalculateNodeCraftCost(node):N0}g)",
            AcquisitionSource.VendorBuy when node.MarketPrice > 0 => 
                $"(~{node.MarketPrice * node.Quantity:N0}g)",
            AcquisitionSource.MarketBuyNq when node.MarketPrice > 0 => 
                $"(~{node.MarketPrice * node.Quantity:N0}g)",
            AcquisitionSource.MarketBuyHq when node.HqMarketPrice > 0 => 
                $"(~{node.HqMarketPrice * node.Quantity:N0}g)",
            _ => ""
        };
    }
    
    private async Task DisplayPlanWithCachedPrices(CraftingPlan plan)
    {
        DisplayPlanInTreeView(plan);
        
        if (plan.SavedMarketPlans?.Count > 0 == true)
        {
            _marketVm.SetShoppingPlans(plan.SavedMarketPlans);
            PopulateProcurementPanel();
            
            RefreshMarketButton.IsEnabled = true;
            RebuildFromCacheButton.IsEnabled = true;
            ViewMarketStatusButton.IsEnabled = true;
            MenuViewMarketStatus.IsEnabled = true;
            
            var savedPrices = ExtractPricesFromPlan(plan);
            if (savedPrices.Count > 0)
            {
                StatusLabel.Text = $"Loaded plan with {savedPrices.Count} cached prices and {_currentMarketPlans.Count} market items.";
            }
            else
            {
                StatusLabel.Text = $"Loaded plan with {_currentMarketPlans.Count} market items.";
            }
        }
        else if (ExtractPricesFromPlan(plan).Count > 0)
        {
            var savedPrices = ExtractPricesFromPlan(plan);
            await UpdateMarketLogisticsAsync(savedPrices, useCachedData: true);
            StatusLabel.Text = $"Loaded plan with {savedPrices.Count} cached prices.";
            
            RebuildFromCacheButton.IsEnabled = true;
        }
        else
        {
            ShowMarketLogisticsPlaceholder();
        }
    }
    
    private Dictionary<int, PriceInfo> ExtractPricesFromPlan(CraftingPlan plan)
    {
        var prices = new Dictionary<int, PriceInfo>();
        
        foreach (var root in plan.RootItems)
        {
            ExtractPricesFromNode(root, prices);
        }
        
        return prices;
    }
    
    private void ExtractPricesFromNode(PlanNode node, Dictionary<int, PriceInfo> prices)
    {
        if (node.MarketPrice > 0 || node.PriceSource != PriceSource.Unknown)
        {
            if (!prices.ContainsKey(node.ItemId))
            {
                prices[node.ItemId] = new PriceInfo
                {
                    ItemId = node.ItemId,
                    ItemName = node.Name,
                    UnitPrice = node.MarketPrice,
                    Source = node.PriceSource,
                    SourceDetails = node.PriceSourceDetails
                };
            }
        }
        
        foreach (var child in node.Children)
        {
            ExtractPricesFromNode(child, prices);
        }
    }

    private void OnSavePlan(object sender, RoutedEventArgs e)
    {
        OnSavePlanAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnSavePlanAsync()
    {
        if (_currentPlan == null)
        {
            StatusLabel.Text = "No plan to save - build a plan first";
            return;
        }
        
        var result = await _planCoordinator.SavePlanAsync(
            this,
            _currentPlan,
            _recipeVm.ProjectItems.ToList(),
            _currentPlanPath);
        
        if (result.Success)
        {
            _currentPlanPath = result.PlanPath;
        }
        StatusLabel.Text = result.Message;
    }

    private void OnViewPlans(object sender, RoutedEventArgs e)
    {
        OnViewPlansAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnViewPlansAsync()
    {
        var (selected, plan, projectItems) = await _planCoordinator.ShowPlanBrowserAsync(
            this,
            _currentPlan,
            _recipeVm.ProjectItems.ToList(),
            _currentPlanPath ?? "");
        
        if (selected && plan != null && projectItems != null)
        {
            _recipeVm.CurrentPlan = plan;
            
            _recipeVm.ProjectItems.Clear();
            foreach (var item in projectItems)
            {
                _recipeVm.ProjectItems.Add(item);
            }
            
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
            UpdateQuickViewCount();
            BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
            BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
            
            await DisplayPlanWithCachedPrices(plan);
            
            StatusLabel.Text = $"Loaded plan: {plan.Name}";
        }
    }

    private void OnRenamePlan(object sender, RoutedEventArgs e)
    {
        OnRenamePlanAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnRenamePlanAsync()
    {
        var result = await _planCoordinator.RenamePlanAsync(this, _currentPlanPath);
        
        if (result.Success)
        {
            _currentPlanPath = result.PlanPath;
        }
        StatusLabel.Text = result.Message;
    }

    private void OnImportTeamcraft(object sender, RoutedEventArgs e)
    {
        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var world = WorldCombo.SelectedItem as string ?? "";
        
        var result = _importCoordinator.ImportFromTeamcraft(this, dc, world);
        
        if (result.Success && result.Plan != null)
        {
            ApplyImportResult(result);
        }
        StatusLabel.Text = result.Message;
    }

    private void ApplyImportResult(ImportCoordinator.ImportResult result)
    {
        if (result.Plan == null || result.ProjectItems == null) return;
        
        _recipeVm.CurrentPlan = result.Plan;
        
        _recipeVm.ProjectItems.Clear();
        foreach (var item in result.ProjectItems)
        {
            _recipeVm.ProjectItems.Add(item);
        }
        
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
        UpdateQuickViewCount();
        
        DisplayPlanInTreeView(_currentPlan);
        BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
    }

    private void OnExportTeamcraft(object sender, RoutedEventArgs e)
    {
        OnExportTeamcraftAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnExportTeamcraftAsync()
    {
        var result = _exportCoordinator.ExportToTeamcraft(_currentPlan);
        
        if (!result.Success)
        {
            StatusLabel.Text = result.Message;
            return;
        }
        
        if (await _exportCoordinator.TrySetClipboardAsync(result.Content))
        {
            StatusLabel.Text = result.Message;
        }
        else
        {
            StatusLabel.Text = "Failed to copy - clipboard may be in use.";
        }
    }

    private void OnExportArtisan(object sender, RoutedEventArgs e)
    {
        OnExportArtisanAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnExportArtisanAsync()
    {
        StatusLabel.Text = "Exporting to Artisan format...";
        
        var result = await _exportCoordinator.ExportToArtisanAsync(_currentPlan);
        
        if (!result.Success)
        {
            StatusLabel.Text = result.Message;
            return;
        }
        
        if (await _exportCoordinator.TrySetClipboardAsync(result.Content))
        {
            StatusLabel.Text = result.Message;
        }
        else
        {
            StatusLabel.Text = "Failed to copy - clipboard may be in use.";
        }
    }

    private void OnImportArtisan(object sender, RoutedEventArgs e)
    {
        OnImportArtisanAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnImportArtisanAsync()
    {
        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var world = WorldCombo.SelectedItem as string ?? "";
        
        StatusLabel.Text = "Importing from Artisan...";
        
        var result = await _importCoordinator.ImportFromArtisanAsync(dc, world);
        
        if (result.Success && result.Plan != null)
        {
            ApplyImportResult(result);
        }
        StatusLabel.Text = result.Message;
    }

    private void OnExportPlainText(object sender, RoutedEventArgs e)
    {
        OnExportPlainTextAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnExportPlainTextAsync()
    {
        var result = _exportCoordinator.ExportToPlainText(_currentPlan);
        
        if (!result.Success)
        {
            StatusLabel.Text = result.Message;
            return;
        }
        
        if (await _exportCoordinator.TrySetClipboardAsync(result.Content))
        {
            StatusLabel.Text = result.Message;
        }
        else
        {
            StatusLabel.Text = "Failed to copy - clipboard may be in use.";
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        OnExportCsvAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnExportCsvAsync()
    {
        var result = _exportCoordinator.ExportToCsv(_currentPlan);
        
        if (!result.Success)
        {
            StatusLabel.Text = result.Message;
            return;
        }
        
        if (await _exportCoordinator.TrySetClipboardAsync(result.Content))
        {
            StatusLabel.Text = result.Message;
        }
        else
        {
            StatusLabel.Text = "Failed to copy - clipboard may be in use.";
        }
    }


    private void OnFetchPrices(object sender, RoutedEventArgs e)
    {
        OnFetchPricesAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnFetchPricesAsync()
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var world = WorldCombo.SelectedItem as string ?? "";
        var worldOrDc = string.IsNullOrEmpty(world) || world == "Entire Data Center" ? dc : world;

        if (_marketDataStatusWindow == null || !_marketDataStatusWindow.IsVisible)
        {
            _marketDataStatusWindow = new MarketDataStatusWindow();
            _marketDataStatusWindow.Owner = this;
        }

        var allItems = new List<(int itemId, string name, int quantity)>();
        CollectAllItemsWithQuantity(_currentPlan.RootItems, allItems);
        
        _marketDataStatusWindow.InitializeItems(allItems);
        _marketDataStatusWindow.Show();
        _marketDataStatusWindow.Activate();

        StatusLabel.Text = $"Fetching prices for {allItems.Count} items...";

        try
        {
            var progress = new Progress<(int current, int total, string itemName, PriceFetchStage stage, string? message)>(p =>
            {
                var statusText = p.message ?? p.stage switch
                {
                    PriceFetchStage.CheckingCache => $"Checking cache... {p.current}/{p.total}",
                    PriceFetchStage.FetchingGarlandData => $"Loading item data: {p.itemName} ({p.current}/{p.total})",
                    PriceFetchStage.FetchingMarketData => $"Fetching market prices... {p.current}/{p.total}",
                    PriceFetchStage.ProcessingResults => $"Processing results... {p.current}/{p.total}",
                    PriceFetchStage.Complete => $"Complete! ({p.total} items)",
                    _ => $"Fetching prices... {p.current}/{p.total}"
                };
                
                StatusLabel.Text = statusText;
                
                if (p.stage == PriceFetchStage.FetchingGarlandData && !string.IsNullOrEmpty(p.itemName))
                {
                    var item = allItems.FirstOrDefault(i => i.name == p.itemName);
                    if (item.itemId > 0)
                    {
                        _marketDataStatusWindow.SetItemFetching(item.itemId);
                    }
                }
            });

            var prices = await _priceCheckService.GetBestPricesBulkAsync(
                allItems.Select(i => (i.itemId, i.name)).ToList(), 
                worldOrDc, 
                default, 
                progress,
                forceRefresh: true);

            int successCount = 0;
            int failedCount = 0;
            int cachedCount = 0;
            
            foreach (var kvp in prices)
            {
                int itemId = kvp.Key;
                var priceInfo = kvp.Value;
                
                if (priceInfo.Source == PriceSource.Unknown)
                {
                    _marketDataStatusWindow.SetItemFailed(itemId, priceInfo.SourceDetails);
                    failedCount++;
                }
                else if (priceInfo.Source == PriceSource.Vendor || priceInfo.Source == PriceSource.Market)
                {
                    _marketDataStatusWindow.SetItemSuccess(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails);
                    successCount++;
                }
                else
                {
                    _marketDataStatusWindow.SetItemCached(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails);
                    cachedCount++;
                }

                UpdateSingleNodePrice(_currentPlan.RootItems, itemId, priceInfo);
            }

            DisplayPlanInTreeView(_currentPlan);
            
            await UpdateMarketLogisticsAsync(prices, useCachedData: false);
            
            if (IsMarketViewVisible())
            {
                PopulateProcurementPanel();
            }
            
            RebuildFromCacheButton.IsEnabled = true;

            var totalCost = _currentPlan.AggregatedMaterials.Sum(m => m.TotalCost);

            if (failedCount > 0 && successCount == 0)
            {
                StatusLabel.Text = $"Price fetch failed! Using cached prices. Total: {totalCost:N0}g";
            }
            else if (failedCount > 0)
            {
                StatusLabel.Text = $"Prices updated! Total: {totalCost:N0}g ({successCount} success, {failedCount} failed, {cachedCount} cached)";
            }
            else
            {
                StatusLabel.Text = $"Prices fetched! Total: {totalCost:N0}g ({successCount} items)";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OnFetchPrices] Failed to fetch prices");
            StatusLabel.Text = $"Failed to fetch prices: {ex.Message}. Cached prices preserved.";
            
            foreach (var item in allItems)
            {
                _marketDataStatusWindow.SetItemFailed(item.itemId, ex.Message);
            }
        }
    }

    private void OnRebuildFromCache(object sender, RoutedEventArgs e)
    {
        OnRebuildFromCacheAsync().SafeFireAndForget(OnAsyncError);
    }

    private async Task OnRebuildFromCacheAsync()
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        var cachedPrices = ExtractPricesFromPlan(_currentPlan);
        
        if (cachedPrices.Count == 0)
        {
            StatusLabel.Text = "No cached prices available. Click 'Refresh Market Data' to fetch prices.";
            return;
        }

        StatusLabel.Text = $"Rebuilding market analysis from {cachedPrices.Count} cached prices...";
        RebuildFromCacheButton.IsEnabled = false;
        
        try
        {
            await UpdateMarketLogisticsAsync(cachedPrices, useCachedData: true);
            
            DisplayPlanInTreeView(_currentPlan);
            
            StatusLabel.Text = $"Market analysis rebuilt from {cachedPrices.Count} cached prices.";
            _logger.LogInformation("[OnRebuildFromCache] Rebuilt market analysis from {Count} cached prices", cachedPrices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OnRebuildFromCache] Failed to rebuild from cache");
            StatusLabel.Text = $"Failed to rebuild from cache: {ex.Message}";
        }
        finally
        {
            RebuildFromCacheButton.IsEnabled = true;
        }
    }

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        SetAllExpandersState(isExpanded: true);
    }
    
    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        SetAllExpandersState(isExpanded: false);
    }
    
    private void SetAllExpandersState(bool isExpanded)
    {
        foreach (var child in RecipePlanPanel.Children)
        {
            if (child is Expander expander)
            {
                expander.IsExpanded = isExpanded;
                SetExpanderChildrenState(expander, isExpanded);
            }
        }
        
        StatusLabel.Text = isExpanded ? "All nodes expanded" : "All nodes collapsed";
    }
    
    private void SetExpanderChildrenState(Expander parent, bool isExpanded)
    {
        if (parent.Content is StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Expander childExpander)
                {
                    childExpander.IsExpanded = isExpanded;
                    SetExpanderChildrenState(childExpander, isExpanded);
                }
            }
        }
    }

    private void OnViewMarketStatus(object sender, RoutedEventArgs e)
    {
        if (_marketDataStatusWindow == null || !_marketDataStatusWindow.IsVisible)
        {
            _marketDataStatusWindow = new MarketDataStatusWindow();
            _marketDataStatusWindow.Owner = this;
            
            if (_currentPlan != null && _currentPlan.RootItems.Count > 0)
            {
                var allItems = new List<(int itemId, string name, int quantity)>();
                CollectAllItemsWithQuantity(_currentPlan.RootItems, allItems);
                _marketDataStatusWindow.InitializeItems(allItems);
                
                MarkExistingPricesInStatusWindow(_currentPlan.RootItems);
            }
        }
        
        _marketDataStatusWindow.Show();
        _marketDataStatusWindow.Activate();
    }

    private void MarkExistingPricesInStatusWindow(List<PlanNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.MarketPrice > 0)
            {
                _marketDataStatusWindow?.SetItemCached(node.ItemId, node.MarketPrice, node.PriceSourceDetails);
            }
            
            if (node.Children?.Any() == true)
            {
                MarkExistingPricesInStatusWindow(node.Children);
            }
        }
    }

    private void UpdateSingleNodePrice(List<PlanNode> nodes, int itemId, PriceInfo priceInfo)
    {
        foreach (var node in nodes)
        {
            if (node.ItemId == itemId)
            {
                node.MarketPrice = priceInfo.UnitPrice;
                if (node.CanBeHq)
                {
                    node.HqMarketPrice = priceInfo.HqUnitPrice ?? 0;
                }
                node.PriceSource = priceInfo.Source;
                node.PriceSourceDetails = priceInfo.SourceDetails;
            }
            
            if (node.Children?.Any() == true)
            {
                UpdateSingleNodePrice(node.Children, itemId, priceInfo);
            }
        }
    }

    private void CollectAllItemsWithQuantity(List<PlanNode> nodes, List<(int itemId, string name, int quantity)> items)
    {
        foreach (var node in nodes)
        {
            if (!items.Any(i => i.itemId == node.ItemId))
            {
                items.Add((node.ItemId, node.Name, node.Quantity));
            }

            if (node.Children?.Any() == true)
            {
                CollectAllItemsWithQuantity(node.Children, items);
            }
        }
    }

    private void UpdatePlanWithPrices(List<PlanNode> nodes, Dictionary<int, PriceInfo> prices)
    {
        foreach (var node in nodes)
        {
            if (prices.TryGetValue(node.ItemId, out var priceInfo))
            {
                node.MarketPrice = priceInfo.UnitPrice;
                if (node.CanBeHq)
                {
                    node.HqMarketPrice = priceInfo.HqUnitPrice ?? 0;
                }
                node.PriceSource = priceInfo.Source;
                node.PriceSourceDetails = priceInfo.SourceDetails;
            }

            if (node.Children?.Any() == true)
            {
                UpdatePlanWithPrices(node.Children, prices);
            }
        }
    }

    private void ShowMarketLogisticsPlaceholder()
    {
        MarketCards.Children.Clear();
        MarketSummaryExpander.Visibility = System.Windows.Visibility.Collapsed;
        RefreshMarketButton.IsEnabled = false;
        
        var placeholderCard = CreateMarketCard("Market Logistics", 
            "Click 'Fetch Prices' to see your purchase plan.\n\n" +
            "This tab will show:\n" +
            "\u2022 Items to buy from vendors (cheapest option)\n" +
            "\u2022 Items to buy from market board (with world listings)\n" +
            "\u2022 Cross-DC travel options (NA only)\n" +
            "\u2022 Untradeable items you need to gather/craft", "#2d3d4a");
        MarketCards.Children.Add(placeholderCard);
    }

    private async Task UpdateMarketLogisticsAsync(Dictionary<int, PriceInfo> prices, bool useCachedData = false)
    {
        _marketVm.Clear();
        MarketCards.Children.Clear();
        
        CraftVsBuyContent.Children.Clear();
        CraftVsBuyExpander.Visibility = Visibility.Collapsed;

        var vendorItems = new List<MaterialAggregate>();
        var marketItems = new List<MaterialAggregate>();
        var untradeableItems = new List<MaterialAggregate>();

        foreach (var material in _currentPlan?.AggregatedMaterials ?? new List<MaterialAggregate>())
        {
            if (prices.TryGetValue(material.ItemId, out var priceInfo))
            {
                switch (priceInfo.Source)
                {
                    case PriceSource.Vendor:
                        vendorItems.Add(material);
                        break;
                    case PriceSource.Market:
                        marketItems.Add(material);
                        break;
                    case PriceSource.Untradeable:
                        untradeableItems.Add(material);
                        break;
                    default:
                        marketItems.Add(material);
                        break;
                }
            }
            else
            {
                marketItems.Add(material);
            }
        }

        UpdateMarketSummaryCard(vendorItems, marketItems, untradeableItems, prices);

        if (vendorItems.Any())
        {
            var vendorText = new System.Text.StringBuilder();
            vendorText.AppendLine("Buy these from vendors (cheapest option):");
            vendorText.AppendLine();
            foreach (var item in vendorItems.OrderByDescending(i => i.TotalCost))
            {
                var source = prices[item.ItemId].SourceDetails;
                vendorText.AppendLine($"\u2022 {item.Name} x{item.TotalQuantity} = {item.TotalCost:N0}g ({source})");
            }
            var vendorCard = CreateMarketCard($"Vendor Items ({vendorItems.Count})", vendorText.ToString(), "#3e4a2d");
            MarketCards.Children.Add(vendorCard);
        }

        if (marketItems.Any())
        {
            if (useCachedData)
            {
                var cachedCard = CreateMarketCard($"Market Board Items ({marketItems.Count})",
                    "Using saved prices. Click 'Refresh Market Data' to fetch current listings.\n\n" +
                    "Items to purchase:\n" +
                    string.Join("\n", marketItems.Select(m => 
                        $"\u2022 {m.Name} x{m.TotalQuantity} = {m.TotalCost:N0}g ({prices[m.ItemId].SourceDetails})")),
                    "#3d3e2d");
                MarketCards.Children.Add(cachedCard);
                RefreshMarketButton.IsEnabled = true;
                RebuildFromCacheButton.IsEnabled = true;
                ViewMarketStatusButton.IsEnabled = true;
                MenuViewMarketStatus.IsEnabled = true;
                
                if (_currentPlan?.SavedMarketPlans?.Any() == true)
                {
                    _marketVm.SetShoppingPlans(_currentPlan.SavedMarketPlans);
                }
                
                AddCraftVsBuyAnalysisCard(prices);
            }
            else
            {
                var dc = DcCombo.SelectedItem as string ?? "Aether";
                var searchAllNA = SearchAllNACheck?.IsChecked ?? false;
                
                var loadingCard = CreateMarketCard("Market Board Items", 
                    $"Fetching detailed listings for {marketItems.Count} items from {(searchAllNA ? "all NA DCs" : dc)}...", "#3d3e2d");
                MarketCards.Children.Add(loadingCard);
                RefreshMarketButton.IsEnabled = false;
                ViewMarketStatusButton.IsEnabled = false;
                MenuViewMarketStatus.IsEnabled = false;
                
                try
                {
                    var progress = new Progress<string>(msg => 
                    {
                        StatusLabel.Text = $"Analyzing market: {msg}";
                    });
                    
                    List<DetailedShoppingPlan> shoppingPlans;
                    var mode = GetCurrentRecommendationMode();
                    
                    if (searchAllNA)
                    {
                        shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansMultiDCAsync(
                            marketItems, progress, mode: mode);
                    }
                    else
                    {
                        shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansAsync(
                            marketItems, dc, progress, mode: mode);
                    }
                    
                    _marketVm.SetShoppingPlans(shoppingPlans);
                    
                    if (_currentPlan != null)
                    {
                        _currentPlan.SavedMarketPlans = shoppingPlans;
                    }
                    
                    MarketCards.Children.Remove(loadingCard);
                    
                    ApplyMarketSortAndDisplay();
                    
                    StatusLabel.Text = $"Market analysis complete. {_currentMarketPlans.Count} items analyzed.";
                }
                catch (Exception ex)
                {
                    MarketCards.Children.Remove(loadingCard);
                    var errorCard = CreateMarketCard("Market Board Items", 
                        $"Error fetching listings: {ex.Message}", "#4a2d2d");
                    MarketCards.Children.Add(errorCard);
                }
                finally
                {
                    RefreshMarketButton.IsEnabled = true;
                    ViewMarketStatusButton.IsEnabled = true;
                    MenuViewMarketStatus.IsEnabled = true;
                }
            }
        }

        if (untradeableItems.Any())
        {
            var untradeText = new System.Text.StringBuilder();
            untradeText.AppendLine("These items must be gathered or crafted:");
            untradeText.AppendLine();
            foreach (var item in untradeableItems)
            {
                untradeText.AppendLine($"\u2022 {item.Name} x{item.TotalQuantity}");
            }
            var untradeCard = CreateMarketCard($"Untradeable Items ({untradeableItems.Count})", untradeText.ToString(), "#4a3d2d");
            MarketCards.Children.Add(untradeCard);
        }
        
        AddCraftVsBuyAnalysisCard(prices);
    }
    
    private void AddCraftVsBuyAnalysisCard(Dictionary<int, PriceInfo> prices)
    {
        if (_currentPlan == null) return;
        
        try
        {
            var analyses = _marketShoppingService.AnalyzeCraftVsBuy(_currentPlan, prices);
            var significantAnalyses = analyses.Where(a => a.IsSignificantSavings).ToList();
            
            CraftVsBuyContent.Children.Clear();
            
            if (significantAnalyses.Count == 0)
            {
                CraftVsBuyExpander.Visibility = Visibility.Collapsed;
                return;
            }
            
            CraftVsBuyExpander.Visibility = Visibility.Visible;
            
            var craftCount = significantAnalyses.Count(a => a.EffectiveRecommendation == CraftRecommendation.Craft);
            var buyCount = significantAnalyses.Count(a => a.EffectiveRecommendation == CraftRecommendation.Buy);
            var hqRequiredCount = significantAnalyses.Count(a => a.IsHqRequired);
            
            var headerText = $"{significantAnalyses.Count} items: {craftCount} craft, {buyCount} buy";
            if (hqRequiredCount > 0)
                headerText += $" ({hqRequiredCount} HQ required)";
            CraftVsBuySummaryText.Text = headerText;
            
            var warningText = hqRequiredCount > 0
                ? "\u26a0\ufe0f Some items require HQ. HQ prices are used for recommendations. NQ may compromise craft quality."
                : "\u26a0\ufe0f For endgame HQ crafts, NQ components may compromise quality. Check HQ prices below.";
            
            var hqWarning = new TextBlock
            {
                Text = warningText,
                Foreground = hqRequiredCount > 0 ? Brushes.Gold : Brushes.Orange,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            CraftVsBuyContent.Children.Add(hqWarning);
            
            foreach (var analysis in significantAnalyses.Take(8))
            {
                var itemPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
                
                var itemName = analysis.IsHqRequired 
                    ? $"{analysis.ItemName} x{analysis.Quantity} [HQ Required]"
                    : $"{analysis.ItemName} x{analysis.Quantity}";
                var nameBlock = new TextBlock
                {
                    Text = itemName,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = analysis.IsHqRequired ? Brushes.Gold : (analysis.HasQualityWarning ? Brushes.Orange : Brushes.White)
                };
                itemPanel.Children.Add(nameBlock);
                
                string primaryText;
                Brush primaryColor;
                
                if (analysis.IsHqRequired && analysis.HasHqData)
                {
                    if (analysis.PotentialSavingsHq > 0)
                    {
                        primaryText = $"  Crafting saves {analysis.PotentialSavingsHq:N0}g ({analysis.SavingsPercentHq:F0}%) - Buy HQ: {analysis.BuyCostHq:N0}g | Craft: {analysis.CraftCost:N0}g";
                        primaryColor = Brushes.LightGreen;
                    }
                    else
                    {
                        primaryText = $"  Crafting costs {Math.Abs(analysis.PotentialSavingsHq):N0}g more ({Math.Abs(analysis.SavingsPercentHq):F0}%) - Buy HQ: {analysis.BuyCostHq:N0}g | Craft: {analysis.CraftCost:N0}g";
                        primaryColor = Brushes.LightCoral;
                    }
                }
                else
                {
                    if (analysis.PotentialSavingsNq > 0)
                    {
                        primaryText = $"  Crafting saves {analysis.PotentialSavingsNq:N0}g ({analysis.SavingsPercentNq:F0}%) - Buy: {analysis.BuyCostNq:N0}g | Craft: {analysis.CraftCost:N0}g";
                        primaryColor = Brushes.LightGreen;
                    }
                    else
                    {
                        primaryText = $"  Crafting costs {Math.Abs(analysis.PotentialSavingsNq):N0}g more ({Math.Abs(analysis.SavingsPercentNq):F0}%) - Buy: {analysis.BuyCostNq:N0}g | Craft: {analysis.CraftCost:N0}g";
                        primaryColor = Brushes.LightCoral;
                    }
                }
                
                var primaryBlock = new TextBlock
                {
                    Text = primaryText,
                    Foreground = primaryColor,
                    FontSize = 11
                };
                itemPanel.Children.Add(primaryBlock);
                
                if (analysis.HasHqData)
                {
                    string altText;
                    Brush altColor;
                    
                    if (analysis.IsHqRequired)
                    {
                        if (analysis.PotentialSavingsNq > 0)
                        {
                            altText = $"  NQ alternative: Save {analysis.PotentialSavingsNq:N0}g ({analysis.SavingsPercentNq:F0}%) - Buy: {analysis.BuyCostNq:N0}g";
                            altColor = Brushes.Gray;
                        }
                        else
                        {
                            altText = $"  NQ alternative: Cost {Math.Abs(analysis.PotentialSavingsNq):N0}g more ({Math.Abs(analysis.SavingsPercentNq):F0}%) - Buy: {analysis.BuyCostNq:N0}g";
                            altColor = Brushes.Gray;
                        }
                    }
                    else
                    {
                        if (analysis.PotentialSavingsHq > 0)
                        {
                            altText = $"  HQ: Crafting saves {analysis.PotentialSavingsHq:N0}g ({analysis.SavingsPercentHq:F0}%) - Buy: {analysis.BuyCostHq:N0}g";
                            altColor = Brushes.LightGreen;
                        }
                        else
                        {
                            altText = $"  HQ: Crafting costs {Math.Abs(analysis.PotentialSavingsHq):N0}g more ({Math.Abs(analysis.SavingsPercentHq):F0}%) - Buy: {analysis.BuyCostHq:N0}g";
                            altColor = Brushes.LightCoral;
                        }
                    }
                    
                    var altBlock = new TextBlock
                    {
                        Text = altText,
                        Foreground = altColor,
                        FontSize = 11,
                        FontStyle = FontStyles.Italic
                    };
                    itemPanel.Children.Add(altBlock);
                    
                    if (analysis.IsEndgameRelevant && !analysis.IsHqRequired)
                    {
                        var warningBlock = new TextBlock
                        {
                            Text = "  \u26a0\ufe0f NQ looks cheap but HQ is costly - may affect HQ craft success",
                            Foreground = Brushes.Orange,
                            FontSize = 10,
                            Margin = new Thickness(0, 2, 0, 0)
                        };
                        itemPanel.Children.Add(warningBlock);
                    }
                }
                
                CraftVsBuyContent.Children.Add(itemPanel);
            }
            
            if (significantAnalyses.Count > 8)
            {
                CraftVsBuyContent.Children.Add(new TextBlock
                {
                    Text = $"... and {significantAnalyses.Count - 8} more items",
                    Foreground = Brushes.Gray,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate craft-vs-buy analysis");
            CraftVsBuyExpander.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateMarketSummaryCard(List<MaterialAggregate> vendorItems, List<MaterialAggregate> marketItems, 
        List<MaterialAggregate> untradeableItems, Dictionary<int, PriceInfo> prices)
    {
        MarketSummaryContent.Children.Clear();
        MarketSummaryExpander.Visibility = System.Windows.Visibility.Visible;
        
        var grandTotal = vendorItems.Sum(i => i.TotalCost) + marketItems.Sum(i => i.TotalCost);
        MarketSummaryHeaderText.Text = $"{vendorItems.Count + marketItems.Count} items \u2022 {grandTotal:N0}g total";
        
        var summaryText = new TextBlock
        {
            Text = $"Vendor: {vendorItems.Count} items ({vendorItems.Sum(i => i.TotalCost):N0}g)  \u2022  " +
                   $"Market: {marketItems.Count} items ({marketItems.Sum(i => i.TotalCost):N0}g)",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        MarketSummaryContent.Children.Add(summaryText);
    }

    private void ApplyMarketSortAndDisplay()
    {
        if (_currentMarketPlans.Count == 0) return;
        
        var cardsToRemove = MarketCards.Children.OfType<Border>()
            .Where(b => b.Tag is DetailedShoppingPlan)
            .ToList();
        foreach (var card in cardsToRemove)
        {
            MarketCards.Children.Remove(card);
        }
        
        var insertIndex = MarketCards.Children.Count;
        
        IEnumerable<DetailedShoppingPlan> sortedPlans = _currentMarketPlans;
        var sortIndex = MarketSortCombo?.SelectedIndex ?? 0;
        
        switch (sortIndex)
        {
            case 0:
                sortedPlans = _currentMarketPlans
                    .OrderBy(p => p.RecommendedWorld?.WorldName ?? "ZZZ")
                    .ThenBy(p => p.Name);
                break;
            case 1:
                sortedPlans = _currentMarketPlans.OrderBy(p => p.Name);
                break;
        }
        
        foreach (var plan in sortedPlans)
        {
            var itemCard = CreateMarketCardFromTemplate(plan);
            itemCard.Tag = plan;
            MarketCards.Children.Insert(insertIndex++, itemCard);
        }
    }


    /// <summary>
    /// Creates a market card from DataTemplate. This replaces CreateExpandableMarketCard().
    /// </summary>
    private Border CreateMarketCardFromTemplate(DetailedShoppingPlan plan)
    {
        // Use the DataTemplate defined in MarketCardTemplates.xaml
        // The template is automatically applied when the content is a MarketCardViewModel
        var viewModel = new MarketCardViewModel(plan);
        
        var border = new Border
        {
            Background = ColorHelper.GetMutedAccentBrush(),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4),
            Tag = plan
        };

        // Create the content from the template
        var contentControl = new ContentControl
        {
            Content = viewModel
        };
        
        border.Child = contentControl;
        return border;
    }

    private Border CreateMarketCard(string title, string content, string backgroundColor)
    {
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(backgroundColor)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var stack = new StackPanel();
        
        var titleBlock = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        
        var contentBlock = new TextBlock
        {
            Text = content,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas")
        };
        
        stack.Children.Add(titleBlock);
        stack.Children.Add(contentBlock);
        border.Child = stack;
        
        return border;
    }

    private void OnMarketSortChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyMarketSortAndDisplay();
    }
    
    private void OnMarketModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_currentPlan == null || _currentMarketPlans.Count == 0) return;
        
        ApplyMarketSortAndDisplay();
    }
    
    private RecommendationMode GetCurrentRecommendationMode()
    {
        return MarketModeCombo.SelectedIndex switch
        {
            1 => RecommendationMode.MaximizeValue,
            _ => RecommendationMode.MinimizeTotalCost
        };
    }

    private void OnRefreshMarketData(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        OnFetchPricesAsync().SafeFireAndForget(OnAsyncError);
    }

    private void OnViewLogs(object sender, RoutedEventArgs e)
    {
        var logWindow = new LogViewerWindow
        {
            Owner = this
        };
        logWindow.Show();
    }

    private void OnRestartApp(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
            "Restart the application? Your current plan will be preserved.",
            "Restart App",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            var state = new WatchState
            {
                CurrentPlan = GetCurrentPlanForWatch(),
                DataCenter = GetCurrentDataCenter(),
                World = GetCurrentWorld()
            };
            state.Save();
            
            var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var exeDirectory = Path.GetDirectoryName(exePath);
            var exeName = "FFXIV_Craft_Architect.exe";
            var fullExePath = Path.Combine(exeDirectory ?? ".", exeName);
            
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fullExePath,
                    WorkingDirectory = exeDirectory,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Could not restart: {ex.Message}. State saved.";
                Environment.ExitCode = 42;
            }
            
            Application.Current.Shutdown();
        }
    }

    private void OnOptions(object sender, RoutedEventArgs e)
    {
        var optionsWindow = App.Services.GetRequiredService<OptionsWindow>();
        optionsWindow.Owner = this;
        var result = optionsWindow.ShowDialog();
        
        if (result == true)
        {
            var diagnosticLoggingEnabled = _settingsService.Get<bool>("debug.enable_diagnostic_logging", false);
            _recipeVm.SetDiagnosticLoggingEnabled(diagnosticLoggingEnabled);
            _logger.LogInformation("Diagnostic logging {Status}", diagnosticLoggingEnabled ? "enabled" : "disabled");
        }
    }

    private void OnDebugOptions(object sender, RoutedEventArgs e)
    {
        OnOptions(sender, e);
    }
    
    private void OnViewBlacklistedWorlds(object sender, RoutedEventArgs e)
    {
        var blacklisted = _blacklistService.GetBlacklistedWorlds();
        
        if (blacklisted.Count == 0)
        {
            MessageBox.Show(
                "No worlds are currently blacklisted.\n\n" +
                "Worlds can be blacklisted from the Market Analysis view when travel is prohibited.",
                "Blacklisted Worlds",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        
        var worldList = string.Join("\n", blacklisted.Select(w => 
            $"\u2022 {w.WorldName} (expires in {w.ExpiresInDisplay})"));
        
        var result = MessageBox.Show(
            $"Currently Blacklisted Worlds ({blacklisted.Count}):\n\n{worldList}\n\n" +
            "Click 'Yes' to clear all blacklisted worlds.",
            "Blacklisted Worlds",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        
        if (result == MessageBoxResult.Yes)
        {
            _blacklistService.ClearBlacklist();
            StatusLabel.Text = "All blacklisted worlds cleared";
            
            if (IsMarketViewVisible())
            {
                PopulateProcurementPanel();
            }
        }
    }
    
    private void OnRecipePlannerTabClick(object sender, MouseButtonEventArgs e)
    {
        SetTabActive(RecipePlannerTab);
        SetTabInactive(MarketAnalysisTab);
        SetTabInactive(ProcurementPlannerTab);
        
        RecipePlannerContent.Visibility = Visibility.Visible;
        MarketAnalysisContent.Visibility = Visibility.Collapsed;
        ProcurementPlannerContent.Visibility = Visibility.Collapsed;
        
        MarketTotalCostText.Text = "";
        
        StatusLabel.Text = "Recipe Planner";
    }
    
    private void OnMarketAnalysisTabClick(object sender, MouseButtonEventArgs e)
    {
        SetTabInactive(RecipePlannerTab);
        SetTabActive(MarketAnalysisTab);
        SetTabInactive(ProcurementPlannerTab);
        
        RecipePlannerContent.Visibility = Visibility.Collapsed;
        MarketAnalysisContent.Visibility = Visibility.Visible;
        ProcurementPlannerContent.Visibility = Visibility.Collapsed;
        
        if (_currentPlan != null)
        {
            PopulateProcurementPanel();
        }
        
        StatusLabel.Text = "Market Analysis";
    }
    
    private void OnProcurementPlannerTabClick(object sender, MouseButtonEventArgs e)
    {
        SetTabInactive(RecipePlannerTab);
        SetTabInactive(MarketAnalysisTab);
        SetTabActive(ProcurementPlannerTab);
        
        RecipePlannerContent.Visibility = Visibility.Collapsed;
        MarketAnalysisContent.Visibility = Visibility.Collapsed;
        ProcurementPlannerContent.Visibility = Visibility.Visible;
        
        MarketTotalCostText.Text = "";
        
        if (_currentPlan != null)
        {
            PopulateProcurementPlanSummary();
        }
        
        StatusLabel.Text = "Procurement Plan";
    }
    
    private void SetTabActive(Border tab)
    {
        tab.Background = (SolidColorBrush)FindResource("GoldAccentBrush");
        ((TextBlock)tab.Child).Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a1a1a"));
    }
    
    private void SetTabInactive(Border tab)
    {
        tab.Background = Brushes.Transparent;
        ((TextBlock)tab.Child).Foreground = (SolidColorBrush)FindResource("GoldAccentBrush");
    }
    
    private bool IsMarketViewVisible()
    {
        return MarketAnalysisContent.Visibility == Visibility.Visible 
            || ProcurementPlannerContent.Visibility == Visibility.Visible;
    }
    
    private void PopulateProcurementPanel()
    {
        var useSplitPane = _settingsService.Get<bool>("ui.use_split_pane_market_view", true);
        
        if (useSplitPane)
        {
            ShowSplitPaneMarketView();
            PopulateProcurementPanelSplitPane();
        }
        else
        {
            ShowLegacyMarketView();
            PopulateProcurementPanelLegacy();
        }
    }
    
    private void ShowSplitPaneMarketView()
    {
        LegacyProcurementScrollViewer.Visibility = Visibility.Collapsed;
        SplitPaneMarketView.Visibility = Visibility.Visible;
    }
    
    private void ShowLegacyMarketView()
    {
        LegacyProcurementScrollViewer.Visibility = Visibility.Visible;
        SplitPaneMarketView.Visibility = Visibility.Collapsed;
    }
    
    private void PopulateProcurementPanelSplitPane()
    {
        SplitPaneExpandedContent.Children.Clear();
        SplitPaneCardsGrid.Children.Clear();
        
        if (_currentPlan == null)
        {
            MarketTotalCostText.Text = "";
            
            SplitPaneCardsGrid.Children.Add(new TextBlock 
            { 
                Text = "Build a plan to see market analysis",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            
            ProcurementPlanPanel.Children.Clear();
            ProcurementPlanPanel.Children.Add(new TextBlock 
            { 
                Text = "No procurement plan available - fetch market data to generate actionable plan",
                Foreground = Brushes.Gray,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        
        if (_currentMarketPlans?.Any() == true)
        {
            PopulateSplitPaneWithMarketPlans();
            PopulateProcurementPlanSummary();
            return;
        }
        
        PopulateSplitPaneWithSimpleMaterials();
        
        ProcurementPlanPanel.Children.Clear();
        ProcurementPlanPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }
    
    private void PopulateProcurementPanelLegacy()
    {
        if (_currentPlan == null)
        {
            ProcurementPanel.Children.Clear();
            ProcurementPanel.Children.Add(new TextBlock 
            { 
                Text = "Build a plan to see market analysis",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            
            ProcurementPlanPanel.Children.Clear();
            ProcurementPlanPanel.Children.Add(new TextBlock 
            { 
                Text = "No procurement plan available - fetch market data to generate actionable plan",
                Foreground = Brushes.Gray,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        
        ProcurementPanel.Children.Clear();
        
        if (_currentMarketPlans?.Any() == true)
        {
            PopulateProcurementWithMarketPlansLegacy();
            PopulateProcurementPlanSummary();
            return;
        }
        
        PopulateProcurementWithSimpleMaterialsLegacy();
        
        ProcurementPlanPanel.Children.Clear();
        ProcurementPlanPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }
    
    private void PopulateProcurementPlanSummary()
    {
        ProcurementPlanPanel.Children.Clear();
        
        if (_currentMarketPlans?.Any() != true)
            return;
        
        var itemsByWorld = _currentMarketPlans
            .Where(p => p.RecommendedWorld != null)
            .GroupBy(p => p.RecommendedWorld!.WorldName)
            .OrderBy(g => g.Key)
            .ToList();
        
        if (!itemsByWorld.Any())
        {
            ProcurementPlanPanel.Children.Add(new TextBlock 
            { 
                Text = "No viable market listings found",
                Foreground = Brushes.Gray,
                FontSize = 12
            });
            return;
        }
        
        foreach (var worldGroup in itemsByWorld)
        {
            var worldName = worldGroup.Key;
            var items = worldGroup.ToList();
            var worldTotal = items.Sum(i => i.RecommendedWorld?.TotalCost ?? 0);
            var isHomeWorld = items.First().RecommendedWorld?.IsHomeWorld ?? false;
            
            var worldHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            
            var worldText = new TextBlock
            {
                Text = worldName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = isHomeWorld ? Brushes.Gold : Brushes.White
            };
            worldHeader.Children.Add(worldText);
            
            if (isHomeWorld)
            {
                worldHeader.Children.Add(new TextBlock
                {
                    Text = " \u2605 HOME",
                    Foreground = Brushes.Gold,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(4, 0, 0, 0)
                });
            }
            
            worldHeader.Children.Add(new TextBlock
            {
                Text = $" - {items.Count} items, {worldTotal:N0}g total",
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            });
            
            ProcurementPlanPanel.Children.Add(worldHeader);
            
            foreach (var item in items.OrderBy(i => i.Name))
            {
                var itemText = new TextBlock
                {
                    Text = $"  \u2022 {item.Name} \u00d7{item.QuantityNeeded} = {item.RecommendedWorld?.TotalCost:N0}g",
                    FontSize = 11,
                    Foreground = Brushes.LightGray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                ProcurementPlanPanel.Children.Add(itemText);
            }
            
            ProcurementPlanPanel.Children.Add(new Border { Height = 12 });
        }
        
        var grandTotal = _currentMarketPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
        var totalText = new TextBlock
        {
            Text = $"Grand Total: {grandTotal:N0}g",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50")),
            Margin = new Thickness(0, 8, 0, 0)
        };
        ProcurementPlanPanel.Children.Add(totalText);
    }

    
    private void PopulateProcurementWithMarketPlansLegacy()
    {
        var grandTotal = _currentMarketPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
        var itemsWithOptions = _currentMarketPlans.Count(p => p.HasOptions);
        var itemsWithoutOptions = _currentMarketPlans.Count(p => !p.HasOptions);
        
        var summaryPanel = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3d3d3d")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 12)
        };
        
        var summaryGrid = new Grid();
        summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        var costText = new TextBlock
        {
            Text = $"Total: {grandTotal:N0}g",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50")),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(costText, 0);
        summaryGrid.Children.Add(costText);
        
        var statsText = new TextBlock
        {
            Text = $"{itemsWithOptions} items with data  \u2022  {itemsWithoutOptions} need fetch",
            Foreground = Brushes.Gray,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(statsText, 1);
        summaryGrid.Children.Add(statsText);
        
        summaryPanel.Child = summaryGrid;
        ProcurementPanel.Children.Add(summaryPanel);
        
        var sortIndex = ProcurementSortCombo?.SelectedIndex ?? 0;
        IEnumerable<DetailedShoppingPlan> sortedPlans = _currentMarketPlans;
        
        switch (sortIndex)
        {
            case 0:
                sortedPlans = _currentMarketPlans
                    .OrderBy(p => p.RecommendedWorld?.WorldName ?? "ZZZ")
                    .ThenBy(p => p.Name);
                break;
            case 1:
                sortedPlans = _currentMarketPlans.OrderBy(p => p.Name);
                break;
            case 2:
                sortedPlans = _currentMarketPlans
                    .OrderByDescending(p => p.RecommendedWorld?.TotalCost ?? 0)
                    .ThenBy(p => p.Name);
                break;
        }
        
        foreach (var plan in sortedPlans)
        {
            var card = CreateMarketCardFromTemplate(plan);
            ProcurementPanel.Children.Add(card);
        }
    }
    
    private void PopulateProcurementWithSimpleMaterialsLegacy()
    {
        var materials = _currentPlan?.AggregatedMaterials;
        
        if (materials?.Any() != true)
        {
            ProcurementPanel.Children.Add(new TextBlock 
            { 
                Text = "No materials to display",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }
        
        var placeholderPanel = new StackPanel 
        { 
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 40, 0, 0)
        };
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = "No market data available",
            Foreground = Brushes.Gray,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to see world recommendations and generate a procurement plan",
            Foreground = Brushes.Gray,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = $"Materials to analyze: {materials.Count}",
            Foreground = Brushes.Gray,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        });
        
        ProcurementPanel.Children.Add(placeholderPanel);
    }
    
    private void PopulateSplitPaneWithMarketPlans()
    {
        SplitPaneCardsGrid.Children.Clear();
        
        var grandTotal = _currentMarketPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
        var itemsWithOptions = _currentMarketPlans.Count(p => p.HasOptions);
        
        MarketTotalCostText.Text = $"Total: {grandTotal:N0}g  \u2022  {itemsWithOptions} items";
        
        var sortIndex = ProcurementSortCombo?.SelectedIndex ?? 0;
        IEnumerable<DetailedShoppingPlan> sortedPlans = _currentMarketPlans;
        
        switch (sortIndex)
        {
            case 0:
                sortedPlans = _currentMarketPlans
                    .OrderBy(p => p.RecommendedWorld?.WorldName ?? "ZZZ")
                    .ThenBy(p => p.Name);
                break;
            case 1:
                sortedPlans = _currentMarketPlans.OrderBy(p => p.Name);
                break;
            case 2:
                sortedPlans = _currentMarketPlans
                    .OrderByDescending(p => p.RecommendedWorld?.TotalCost ?? 0)
                    .ThenBy(p => p.Name);
                break;
        }
        
        foreach (var plan in sortedPlans)
        {
            var card = CreateCollapsedCardFromTemplate(plan);
            SplitPaneCardsGrid.Children.Add(card);
        }
        
        if (_expandedSplitPanePlan != null)
        {
            var planToExpand = _currentMarketPlans.FirstOrDefault(p => p.ItemId == _expandedSplitPanePlan.ItemId);
            if (planToExpand != null)
            {
                BuildExpandedPanel(planToExpand);
            }
            else
            {
                _expandedSplitPanePlan = null;
                SplitPaneExpandedPanel.Visibility = Visibility.Collapsed;
            }
        }
    }
    
    private void PopulateSplitPaneWithSimpleMaterials()
    {
        SplitPaneCardsGrid.Children.Clear();
        SplitPaneExpandedPanel.Visibility = Visibility.Collapsed;
        MarketTotalCostText.Text = "";
        
        var materials = _currentPlan?.AggregatedMaterials;
        
        if (materials?.Any() != true)
        {
            SplitPaneCardsGrid.Children.Add(new TextBlock 
            { 
                Text = "No materials to display",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }
        
        var placeholderPanel = new StackPanel 
        { 
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 40, 0, 0)
        };
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = "No market data available",
            Foreground = Brushes.Gray,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to see world recommendations",
            Foreground = Brushes.Gray,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        });
        
        placeholderPanel.Children.Add(new TextBlock 
        { 
            Text = $"Materials to analyze: {materials.Count}",
            Foreground = Brushes.Gray,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        });
        
        SplitPaneCardsGrid.Children.Add(placeholderPanel);
    }
    
    /// <summary>
    /// Creates a collapsed market card using the DataTemplate.
    /// </summary>
    private Border CreateCollapsedCardFromTemplate(DetailedShoppingPlan plan)
    {
        var isExpanded = _expandedSplitPanePlan?.ItemId == plan.ItemId;
        var viewModel = new MarketCardViewModel(plan);
        
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(isExpanded ? "#3d4a3d" : "#2d2d2d")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 8),
            Width = 320,
            Cursor = Cursors.Hand,
            BorderBrush = isExpanded ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#d4a73a")) : null,
            BorderThickness = isExpanded ? new Thickness(2) : new Thickness(0)
        };
        
        var contentControl = new ContentControl
        {
            Content = viewModel,
            ContentTemplate = (DataTemplate)FindResource("CollapsedMarketCardTemplate")
        };
        
        border.Child = contentControl;
        
        border.MouseLeftButtonDown += (s, e) => OnCollapsedCardClick(plan);
        
        return border;
    }
    
    private void OnCollapsedCardClick(DetailedShoppingPlan plan)
    {
        if (_expandedSplitPanePlan?.ItemId == plan.ItemId)
        {
            _expandedSplitPanePlan = null;
            SplitPaneExpandedPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _expandedSplitPanePlan = plan;
            BuildExpandedPanel(plan);
        }
        
        PopulateSplitPaneWithMarketPlans();
    }
    
    private void BuildExpandedPanel(DetailedShoppingPlan plan)
    {
        SplitPaneExpandedContent.Children.Clear();
        SplitPaneExpandedPanel.Visibility = Visibility.Visible;
        
        var viewModel = new ExpandedPanelViewModel(plan);
        viewModel.CloseRequested += () =>
        {
            _expandedSplitPanePlan = null;
            SplitPaneExpandedPanel.Visibility = Visibility.Collapsed;
            PopulateSplitPaneWithMarketPlans();
        };
        
        var contentControl = new ContentControl
        {
            Content = viewModel
        };
        
        SplitPaneExpandedContent.Children.Add(contentControl);
    }
    
    private void ShowBlacklistConfirmationDialog(string worldName, int worldId)
    {
        var result = MessageBox.Show(
            $"Blacklist {worldName}?\n\n" +
            "This world will be excluded from acquisition recommendations for 30 minutes. " +
            "You can still manually select this world if needed.\n\n" +
            "Use this when a world is currently travel-prohibited (at capacity).",
            "Confirm World Blacklist",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
        {
            _blacklistService.AddToBlacklist(worldId, worldName, "Travel prohibited - user blacklisted");
            StatusLabel.Text = $"{worldName} blacklisted for 30 minutes";
            
            if (IsMarketViewVisible())
            {
                PopulateProcurementPanel();
            }
        }
    }
    
    private void OnProcurementSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentPlan == null)
            return;
            
        if (IsMarketViewVisible() && _currentMarketPlans?.Any() == true)
        {
            PopulateProcurementPanel();
        }
    }
    
    private void OnProcurementModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsService == null)
            return;
            
        if (ProcurementModeCombo.SelectedIndex >= 0)
        {
            var mode = ProcurementModeCombo.SelectedIndex == 1 ? "MaximizeValue" : "MinimizeTotalCost";
            _settingsService.Set("planning.default_recommendation_mode", mode);
            
            if (IsMarketViewVisible() && _currentPlan != null)
            {
                PopulateProcurementPanel();
            }
        }
    }

    private void OnNewPlan(object sender, RoutedEventArgs e)
    {
        _recipeVm.CurrentPlan = null;
        _recipeVm.ProjectItems.Clear();
        ProjectList.ItemsSource = null;
        RecipePlanPanel?.Children.Clear();
        
        BuildPlanButton.IsEnabled = false;
        BrowsePlanButton.IsEnabled = false;
        
        StatusLabel.Text = "New plan created. Add items to get started.";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _itemCache.SaveCache();
        
        _recipeVm.Dispose();
        _marketVm.Dispose();
        
        base.OnClosing(e);
    }

    private void OnQuantityPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void OnQuantityGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void OnQuantityChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            var item = textBox.FindParent<ListBoxItem>();
            if (item?.DataContext is ProjectItem projectItem)
            {
                if (!int.TryParse(textBox.Text, out var quantity) || quantity < 1)
                {
                    quantity = 1;
                    textBox.Text = "1";
                }
                
                projectItem.Quantity = quantity;
                StatusLabel.Text = $"Updated {projectItem.Name} quantity to {quantity}";
                
                ProjectList.Items.Refresh();
            }
        }
    }

    private void OnRemoveProjectItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            var listBoxItem = button.FindParent<ListBoxItem>();
            if (listBoxItem?.DataContext is ProjectItem projectItem)
            {
                _recipeVm.ProjectItems.Remove(projectItem);
                StatusLabel.Text = $"Removed {projectItem.Name} from project";
                
                BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
                BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
                
                ProjectList.ItemsSource = null;
                ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
                UpdateQuickViewCount();
            }
        }
    }

    private void UpdateQuickViewCount()
    {
        if (_recipeVm.ProjectItems.Count <= 5)
        {
            QuickViewCountText.Text = $"({_recipeVm.ProjectItems.Count})";
        }
        else
        {
            QuickViewCountText.Text = $"(showing 5 of {_recipeVm.ProjectItems.Count})";
        }
    }

    private void OnManageItemsClick(object sender, RoutedEventArgs e)
    {
        var planName = _currentPlan?.Name;
        var logger = App.Services.GetRequiredService<ILogger<Views.ProjectItemsWindow>>();
        
        var window = new Views.ProjectItemsWindow(
            _recipeVm.ProjectItems.ToList(),
            planName,
            onItemsChanged: (items) =>
            {
                BuildPlanButton.IsEnabled = items.Count > 0;
                BrowsePlanButton.IsEnabled = items.Count > 0;
            },
            onAddItem: null,
            logger: logger)
        {
            Owner = this
        };
        
        window.ShowDialog();
        
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
        UpdateQuickViewCount();
        BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        
        StatusLabel.Text = $"Project items updated: {_recipeVm.ProjectItems.Count} items";
    }

    private decimal CalculateNodeCraftCost(PlanNode node)
    {
        if (!node.Children.Any())
            return 0;
        
        decimal total = 0;
        foreach (var child in node.Children)
        {
            if (child.Source == AcquisitionSource.MarketBuyNq && child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            else if (child.Source == AcquisitionSource.MarketBuyHq && child.HqMarketPrice > 0)
            {
                total += child.HqMarketPrice * child.Quantity;
            }
            else if (child.Source == AcquisitionSource.VendorBuy && child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            else if (child.Source == AcquisitionSource.Craft && child.Children.Any())
            {
                total += CalculateNodeCraftCost(child);
            }
            else if (child.MarketPrice > 0)
            {
                total += child.MarketPrice * child.Quantity;
            }
            else if (child.Children.Any())
            {
                total += CalculateNodeCraftCost(child);
            }
        }
        return total;
    }

    public CraftingPlan? GetCurrentPlanForWatch()
    {
        if (_currentPlan != null && _currentMarketPlans.Count > 0)
        {
            _currentPlan.SavedMarketPlans = _currentMarketPlans;
        }
        return _currentPlan;
    }
    
    public string? GetCurrentDataCenter()
    {
        return DcCombo.SelectedItem as string;
    }
    
    public string? GetCurrentWorld()
    {
        return WorldCombo.SelectedItem as string;
    }
    
    private async Task PromptToReanalyzeCachedMarketDataAsync()
    {
        if (_currentPlan?.AggregatedMaterials == null)
            return;
        
        var cacheService = App.Services.GetService<Core.Services.IMarketCacheService>();
        if (cacheService == null)
            return;
        
        var dataCenter = DcCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(dataCenter))
            return;
        
        var itemIds = _currentPlan.AggregatedMaterials.Select(m => m.ItemId).ToList();
        var missing = await cacheService.GetMissingAsync(
            itemIds.Select(id => (id, dataCenter)).ToList());
        var cachedCount = itemIds.Count - missing.Count;
        
        if (cachedCount > 0)
        {
            ProcurementPanel.Children.Clear();
            
            var infoPanel = new StackPanel 
            { 
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            };
            
            infoPanel.Children.Add(new TextBlock
            {
                Text = "\ud83d\udce6 Market Data Available in Cache",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = (SolidColorBrush)Resources["GoldAccentBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });
            
            infoPanel.Children.Add(new TextBlock
            {
                Text = $"{cachedCount} of {itemIds.Count} items have cached market data.",
                FontSize = 13,
                Foreground = Brushes.LightGray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            });
            
            infoPanel.Children.Add(new TextBlock
            {
                Text = "Click 'Refresh Market Data' above to re-analyze using cached data.",
                FontSize = 12,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            });
            
            ProcurementPanel.Children.Add(infoPanel);
            
            StatusLabel.Text = $"[Watch] State restored. {cachedCount} items have cached market data.";
        }
    }

    public async Task RestoreWatchStateAsync(WatchState state)
    {
        if (state.CurrentPlan == null)
            return;
        
        if (!string.IsNullOrEmpty(state.DataCenter))
        {
            DcCombo.SelectedItem = state.DataCenter;
            OnDataCenterSelected(null, null);
            
            await Task.Delay(100);
            
            if (!string.IsNullOrEmpty(state.World))
            {
                WorldCombo.SelectedItem = state.World;
            }
        }
        
        _recipeVm.CurrentPlan = state.CurrentPlan;
        
        _recipeVm.ProjectItems = new ObservableCollection<ProjectItem>(_currentPlan.RootItems.Select(r => new ProjectItem
        {
            Id = r.ItemId,
            Name = r.Name,
            Quantity = r.Quantity,
            IsHqRequired = r.MustBeHq
        }));
        
        ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
        UpdateQuickViewCount();
        BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        
        DisplayPlanInTreeView(_currentPlan);
        UpdateBuildPlanButtonText();
        PopulateShoppingList();
        ProcurementRefreshButton.IsEnabled = true;
        
        StatusLabel.Text = "[Watch] State restored from reload";
        
        await PromptToReanalyzeCachedMarketDataAsync();
    }

}
