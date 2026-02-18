using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Coordinators;
using FFXIV_Craft_Architect.Helpers;
using FFXIV_Craft_Architect.Services;
using FFXIV_Craft_Architect.Services.Interfaces;
using FFXIV_Craft_Architect.Services.UI;
using FFXIV_Craft_Architect.UIBuilders;
using FFXIV_Craft_Architect.ViewModels;
using FFXIV_Craft_Architect.Views;
using FFXIV_Craft_Architect.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Window = System.Windows.Window;

// Type aliases for disambiguation
using CraftingPlan = FFXIV_Craft_Architect.Core.Models.CraftingPlan;
using PlanNode = FFXIV_Craft_Architect.Core.Models.PlanNode;
using AcquisitionSource = FFXIV_Craft_Architect.Core.Models.AcquisitionSource;
using DetailedShoppingPlan = FFXIV_Craft_Architect.Core.Models.DetailedShoppingPlan;
using MaterialAggregate = FFXIV_Craft_Architect.Core.Models.MaterialAggregate;
using PriceSource = FFXIV_Craft_Architect.Core.Models.PriceSource;
using PriceInfo = FFXIV_Craft_Architect.Core.Models.PriceInfo;
using WatchState = FFXIV_Craft_Architect.Models.WatchState;
using SettingsService = FFXIV_Craft_Architect.Core.Services.SettingsService;
using GarlandService = FFXIV_Craft_Architect.Core.Services.GarlandService;
using UniversalisService = FFXIV_Craft_Architect.Core.Services.UniversalisService;
using PriceCheckService = FFXIV_Craft_Architect.Core.Services.PriceCheckService;
using WorldDataCoordinator = FFXIV_Craft_Architect.Core.Services.WorldDataCoordinator;

namespace FFXIV_Craft_Architect;

// ProjectItem is now defined in FFXIV_Craft_Architect.Core.Models

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private StackPanel RecipePlannerLeftPanel => RecipePlannerSidebarModule.RecipePlannerLeftPanel;
    private StackPanel MarketAnalysisLeftPanel => MarketAnalysisSidebarModule.MarketAnalysisLeftPanel;
    private StackPanel ProcurementPlannerLeftPanel => ProcurementPlannerSidebarModule.ProcurementPlannerLeftPanel;
    private Wpf.Ui.Controls.TextBox ItemSearch => RecipePlannerSidebarModule.ItemSearchControl;
    private Border SearchResultsPanel => RecipePlannerSidebarModule.SearchResultsPanelControl;
    private ListBox SearchResults => RecipePlannerSidebarModule.SearchResultsControl;
    private Wpf.Ui.Controls.Button AddToProjectButton => RecipePlannerSidebarModule.AddToProjectButtonControl;
    private ListBox ProjectList => RecipePlannerSidebarModule.ProjectListControl;
    private TextBlock QuickViewCountText => RecipePlannerSidebarModule.QuickViewCountTextControl;
    private Wpf.Ui.Controls.Button BuildPlanButton => RecipePlannerSidebarModule.BuildPlanButtonControl;
    private Wpf.Ui.Controls.Button BrowsePlanButton => RecipePlannerSidebarModule.BrowsePlanButtonControl;
    private Wpf.Ui.Controls.Button LeftPanelConductAnalysisButton => MarketAnalysisSidebarModule.LeftPanelConductAnalysisButtonControl;
    private Wpf.Ui.Controls.Button LeftPanelViewMarketStatusButton => MarketAnalysisSidebarModule.LeftPanelViewMarketStatusButtonControl;

    private Border RecipePlannerContent => RecipePlannerModule.RecipePlannerContent;
    private Border MarketAnalysisContent => MarketAnalysisModule.MarketAnalysisContent;
    private Border ProcurementPlannerContent => ProcurementPlannerModule.ProcurementPlannerContent;
    private StackPanel RecipePlanPanel => RecipePlannerModule.RecipePlanPanel;
    private Button ExpandAllButton => RecipePlannerModule.ExpandAllButton;
    private Button CollapseAllButton => RecipePlannerModule.CollapseAllButton;
    private Wpf.Ui.Controls.Button ProcurementRefreshButton => MarketAnalysisModule.ProcurementRefreshButton;
    private Wpf.Ui.Controls.Button RebuildFromCacheButton => MarketAnalysisModule.RebuildFromCacheButton;
    private ComboBox DcCombo => MarketAnalysisModule.DcCombo;
    private ComboBox WorldCombo => MarketAnalysisModule.WorldCombo;
    private ComboBox ProcurementSortCombo => MarketAnalysisModule.ProcurementSortCombo;
    private ComboBox ProcurementModeCombo => MarketAnalysisModule.ProcurementModeCombo;
    private CheckBox ProcurementSearchAllNaCheck => MarketAnalysisModule.ProcurementSearchAllNaCheck;
    private Panel ProcurementPanel => MarketAnalysisModule.SplitPaneCardsGrid;
    private Grid SplitPaneMarketView => MarketAnalysisModule.SplitPaneMarketView;
    private Border SplitPaneExpandedPanel => MarketAnalysisModule.SplitPaneExpandedPanel;
    private StackPanel SplitPaneExpandedContent => MarketAnalysisModule.SplitPaneExpandedContent;
    private WrapPanel SplitPaneCardsGrid => MarketAnalysisModule.SplitPaneCardsGrid;
    private StackPanel ProcurementPlanPanel => ProcurementPlannerModule.ProcurementPlanPanel;

    private readonly GarlandService _garlandService;
    private readonly UniversalisService _universalisService;
    private readonly SettingsService _settingsService;
    private readonly ItemCacheService _itemCache;
    private readonly RecipeCalculationService _recipeCalcService;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly WorldBlacklistService _blacklistService;
    private readonly IDialogService _dialogs;
    private readonly ILogger<MainWindow> _logger;
    private readonly DialogServiceFactory _dialogFactory;

    // ViewModels
    private readonly RecipePlannerViewModel _recipeVm;
    private readonly MarketAnalysisViewModel _marketVm;
    private readonly MainViewModel _mainVm;
    
    // UI Builders
    private RecipeTreeUiBuilder? _recipeTreeBuilder;
    private readonly ShoppingListBuilder _shoppingListBuilder;
    private ProcurementPanelBuilder? _procurementBuilder;
    private readonly InfoPanelBuilder _infoPanelBuilder;
    
    // Factories
    private readonly ICardFactory _cardFactory;
    
    // Search state
    private List<GarlandSearchResult> _currentSearchResults = new();
    private GarlandSearchResult? _selectedSearchResult;
    
    // Market data status window for real-time fetch visualization
    private MarketDataStatusWindow? _marketDataStatusWindow;
    private readonly MarketDataStatusSession _marketDataStatusSession = new();
    
    // Backwards compatibility properties (using Core.Models types)
    private Core.Models.CraftingPlan? _currentPlan => _recipeVm?.CurrentPlan;
    private List<Core.Models.DetailedShoppingPlan> _currentMarketPlans => _marketVm?.ShoppingPlans.Select(vm => vm.Plan).ToList() ?? new List<Core.Models.DetailedShoppingPlan>();
    
    // Split-pane view state
    private DetailedShoppingPlan? _expandedSplitPanePlan;
    
    // Coordinators
    private readonly WatchStateCoordinator _watchStateCoordinator;
    private readonly WorldDataCoordinator _worldDataCoordinator;
    private readonly IPriceRefreshCoordinator _priceRefreshCoordinator;
    private readonly IShoppingOptimizationCoordinator _shoppingOptimizationCoordinator;
    private readonly IMarketLogisticsCoordinator _marketLogisticsCoordinator;

    public MainWindow(
        GarlandService garlandService,
        UniversalisService universalisService,
        SettingsService settingsService,
        ItemCacheService itemCache,
        RecipeCalculationService recipeCalcService,
        MarketShoppingService marketShoppingService,
        WorldBlacklistService blacklistService,
        DialogServiceFactory dialogFactory,
        ILogger<MainWindow> logger,
        ILoggerFactory loggerFactory,
        WatchStateCoordinator watchStateCoordinator,
        WorldDataCoordinator worldDataCoordinator,
        IPriceRefreshCoordinator priceRefreshCoordinator,
        IShoppingOptimizationCoordinator shoppingOptimizationCoordinator,
        IMarketLogisticsCoordinator marketLogisticsCoordinator,
        ICardFactory cardFactory,
        RecipePlannerViewModel recipeVm,
        MarketAnalysisViewModel marketVm,
        MainViewModel mainVm,
        InfoPanelBuilder infoPanelBuilder)
    {
        // Services (injected via DI)
        _garlandService = garlandService;
        _universalisService = universalisService;
        _settingsService = settingsService;
        _itemCache = itemCache;
        _recipeCalcService = recipeCalcService;
        _marketShoppingService = marketShoppingService;
        _blacklistService = blacklistService;
        _dialogFactory = dialogFactory;
        _dialogs = _dialogFactory.CreateForWindow(this);
        _logger = logger;
        
        // Coordinators (injected via DI)
        _watchStateCoordinator = watchStateCoordinator;
        _worldDataCoordinator = worldDataCoordinator;
        _priceRefreshCoordinator = priceRefreshCoordinator;
        _shoppingOptimizationCoordinator = shoppingOptimizationCoordinator;
        _marketLogisticsCoordinator = marketLogisticsCoordinator;
        _cardFactory = cardFactory;
        
        // ViewModels (injected via DI as singletons)
        _recipeVm = recipeVm;
        _marketVm = marketVm;
        _mainVm = mainVm;
        
        // Create UI Builders (ProcurementBuilder deferred until InitializeComponent)
        _shoppingListBuilder = new ShoppingListBuilder(loggerFactory?.CreateLogger<ShoppingListBuilder>());
        _infoPanelBuilder = infoPanelBuilder;
        
        InitializeComponent();

        RecipePlannerSidebarModule.ItemSearchKeyDownForwarded += OnItemSearchKeyDown;
        RecipePlannerSidebarModule.SearchClicked += OnSearchItem;
        RecipePlannerSidebarModule.ItemSelected += OnItemSelected;
        RecipePlannerSidebarModule.AddToProjectClicked += OnAddToProject;
        RecipePlannerSidebarModule.ProjectItemSelected += OnProjectItemSelected;
        RecipePlannerSidebarModule.QuantityGotFocusForwarded += OnQuantityGotFocus;
        RecipePlannerSidebarModule.QuantityPreviewTextInputForwarded += OnQuantityPreviewTextInput;
        RecipePlannerSidebarModule.QuantityChangedForwarded += OnQuantityChanged;
        RecipePlannerSidebarModule.RemoveProjectItemClicked += OnRemoveProjectItem;
        RecipePlannerSidebarModule.BuildProjectPlanClicked += OnBuildProjectPlan;
        RecipePlannerSidebarModule.BrowsePlanClicked += OnBrowsePlan;

        MarketAnalysisSidebarModule.ConductAnalysisClicked += OnConductAnalysis;
        MarketAnalysisSidebarModule.ViewMarketStatusClicked += OnViewMarketStatus;

        ProcurementPlannerSidebarModule.ProcurementSortChanged += OnProcurementSortChanged;
        ProcurementPlannerSidebarModule.ConductAnalysisClicked += OnConductAnalysis;

        ExpandAllButton.Click += OnExpandAll;
        CollapseAllButton.Click += OnCollapseAll;
        ProcurementRefreshButton.Click += OnFetchPrices;
        RebuildFromCacheButton.Click += OnRebuildFromCache;
        DcCombo.SelectionChanged += OnDataCenterSelected;
        ProcurementSortCombo.SelectionChanged += OnProcurementSortChanged;
        ProcurementModeCombo.SelectionChanged += OnProcurementModeChanged;
        
        // Initialize ProcurementBuilder after InitializeComponent (UI elements exist)
        _procurementBuilder = new ProcurementPanelBuilder(
            _infoPanelBuilder,
            SplitPaneCardsGrid,
            SplitPaneExpandedContent,
            SplitPaneExpandedPanel,
            MarketTotalCostText,
            ProcurementPlanPanel,
            loggerFactory?.CreateLogger<ProcurementPanelBuilder>());
        
        // Wire up ViewModel events
        _recipeVm.NodeAcquisitionChanged += OnNodeAcquisitionChanged;
        _recipeVm.NodeHqChanged += OnNodeHqChanged;
        _recipeVm.PropertyChanged += OnRecipeVmPropertyChanged;
        _marketVm.PropertyChanged += OnMarketVmPropertyChanged;
        
        Loaded += OnLoaded;
        
        // Set DataContext for DataTemplate binding
        DataContext = _mainVm;
        
        // Subscribe to blacklist events (deferred to Loaded to ensure UI is initialized)
        Loaded += (s, e) =>
        {
            _blacklistService.WorldUnblacklisted += OnWorldUnblacklisted;
        };

        // Inject logger into ViewModels (respect diagnostic setting)
        var diagnosticLoggingEnabled = _settingsService.Get<bool>("debug.enable_diagnostic_logging", false);
        _recipeVm.SetLogger(App.Services.GetRequiredService<ILogger<RecipePlannerViewModel>>(), enableDiagnostics: diagnosticLoggingEnabled);

        // Initialize UI builder with ViewModel callbacks
        _recipeTreeBuilder = new RecipeTreeUiBuilder(
            onAcquisitionChanged: (nodeId, source, vendorIndex) => _recipeVm.SetNodeAcquisition(nodeId, source, vendorIndex),
            onHqChanged: (nodeId, isHq, mode) => _recipeVm.SetNodeHq(nodeId, isHq, mode)
        );
    }

    /// <summary>
    /// Entry point after window is loaded. Delegates to async initialization.
    /// Note: UI-specific - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - requires direct UI element access for initialization.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        OnLoadedAsync().SafeFireAndForget(OnAsyncError);
    }

    /// <summary>
    /// Async initialization: loads world data, applies settings, restores watch state.
    /// Note: Coordination logic - could move to a StartupCoordinator if DI becomes complex.
    /// MVVM: Not an ICommand candidate - startup coordination needs UI context.
    /// </summary>
    private async Task OnLoadedAsync()
    {
        await LoadWorldDataAsync();
        
        var defaultMode = _settingsService.Get<string>("planning.default_recommendation_mode", "MinimizeTotalCost");
        var procModeIndex = defaultMode == "MaximizeValue" ? 1 : 0;
        _logger.LogInformation("[OnLoaded] planning.default_recommendation_mode = '{Value}', Setting ProcurementModeCombo.SelectedIndex = {Index}", defaultMode, procModeIndex);
        ProcurementModeCombo.SelectedIndex = procModeIndex;
        
        if (App.RestoredWatchState != null)
        {
            await RestoreWatchStateAsync(App.RestoredWatchState);
            App.RestoredWatchState = null;
        }
    }

    /// <summary>
    /// Global async error handler - displays errors to user and logs them.
    /// Note: UI-specific error presentation - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - error handler needs StatusLabel access.
    /// </summary>
    private async void OnAsyncError(Exception ex)
    {
        _logger.LogError(ex, "Async operation failed");
        StatusLabel.Text = $"Operation failed: {ex.Message}";
        await _dialogs.ShowErrorAsync($"Operation failed: {ex.Message}", ex);
    }
    
    // ========================================================================
    // ViewModel Event Handlers
    // ========================================================================
    
    /// <summary>
    /// Handles RecipePlannerViewModel property changes and updates UI state accordingly.
    /// Note: UI coordination - must remain in MainWindow as it controls specific UI elements.
    /// MVVM: Not an ICommand candidate - reacts to VM changes, not user action.
    /// </summary>
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
                    ExpandAllButton.IsEnabled = true;
                    CollapseAllButton.IsEnabled = true;
                }
                else
                {
                    ProcurementRefreshButton.IsEnabled = false;
                    RebuildFromCacheButton.IsEnabled = false;
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
    
    /// <summary>
    /// Handles MarketAnalysisViewModel property changes.
    /// Note: UI coordination - must remain in MainWindow as it triggers UI updates.
    /// MVVM: Not an ICommand candidate - reacts to VM changes, not user action.
    /// </summary>
    private void OnMarketVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MarketAnalysisViewModel.ShoppingPlans):
                if (IsMarketViewVisible())
                {
                    PopulateProcurementPanel();
                }
                break;
                
            case nameof(MarketAnalysisViewModel.StatusMessage):
                StatusLabel.Text = _marketVm.StatusMessage;
                break;
        }
    }
    
    /// <summary>
    /// Handles node acquisition source changes (e.g., Craft -> Market Buy).
    /// Updates the recipe tree display and shopping list.
    /// Note: UI coordination - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - event callback from ViewModel, not user command.
    /// </summary>
    private void OnNodeAcquisitionChanged(object? sender, NodeChangedEventArgs e)
    {
        _recipeTreeBuilder?.UpdateNodeAcquisition(e.NodeId, e.Node.Source, e.Node.SelectedVendorIndex);
        PopulateShoppingList();
        
        if (IsMarketViewVisible())
        {
            PopulateProcurementPanel();
        }
    }
    
    /// <summary>
    /// Handles node HQ requirement changes.
    /// Updates the recipe tree display and shopping list.
    /// Note: UI coordination - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - event callback from ViewModel, not user command.
    /// </summary>
    private void OnNodeHqChanged(object? sender, NodeChangedEventArgs e)
    {
        _recipeTreeBuilder?.UpdateNodeHqIndicator(e.NodeId, e.Node.MustBeHq);
        PopulateShoppingList();
    }
    
    /// <summary>
    /// Handles world unblacklist event.
    /// </summary>
    private void OnWorldUnblacklisted(object? sender, WorldBlacklistEventArgs e)
    {
        Dispatcher.Invoke(() => 
        {
            StatusLabel.Text = $"{e.WorldName} removed from blacklist";
            if (IsMarketViewVisible())
            {
                PopulateProcurementPanel();
            }
        });
    }

    /// <summary>
    /// Loads world/DC data for the market region selector.
    /// Uses WorldDataCoordinator for shared initialization logic between WPF and Web apps.
    /// UI-specific aspects (ComboBox binding, status updates) remain in MainWindow.
    /// </summary>
    private async Task LoadWorldDataAsync()
    {
        StatusLabel.Text = "Loading world data...";
        try
        {
            var worldData = await _worldDataCoordinator.InitializeWorldDataAsync();
            
            DcCombo.ItemsSource = worldData.DataCenters;
            var savedDc = _settingsService.Get<string>("market.default_datacenter");
            _logger.LogInformation("[LoadWorldDataAsync] market.default_datacenter = '{Value}', Using: {Selected}", savedDc, savedDc ?? "Aether (default)");
            DcCombo.SelectedItem = savedDc ?? "Aether";
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

    /// <summary>
    /// Updates the world dropdown when a data center is selected.
    /// Note: UI-specific - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - directly manipulates ComboBox ItemsSource.
    /// </summary>
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

    /// <summary>
    /// Handles Enter key in item search box.
    /// Note: UI-specific - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - key event handler needs direct UI element.
    /// </summary>
    private void OnItemSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSearchItem(sender, e);
        }
    }

    /// <summary>
    /// Initiates item search via GarlandService and displays results.
    /// Note: MUST REMAIN in MainWindow - directly manipulates SearchResults, SearchResultsPanel, ItemSearch UI elements.
    ///       ViewModel cannot access these UI-specific elements.
    /// </summary>
    private void OnSearchItem(object sender, RoutedEventArgs e)
    {
        OnSearchItemAsync().SafeFireAndForget(OnAsyncError);
    }

    /// <summary>
    /// Performs the actual item search against Garland Tools.
    /// Note: MUST REMAIN in MainWindow - updates SearchResults.ItemsSource, SearchResultsPanel.Visibility,
    ///       AddToProjectButton.IsEnabled, and StatusLabel directly. These are UI elements not accessible from ViewModel.
    /// </summary>
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

    /// <summary>
    /// Handles selection of an item from search results.
    /// Note: UI-specific - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - SelectionChanged event, not a command.
    /// </summary>
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

    /// <summary>
    /// Adds selected item to the project list, or increments quantity if already present.
    /// Note: Delegates to RecipePlannerViewModel.AddProjectItemCommand.
    /// </summary>
    private void OnAddToProject(object sender, RoutedEventArgs e)
    {
        if (_selectedSearchResult == null)
            return;

        var itemName = _selectedSearchResult.Object?.Name ?? $"Item_{_selectedSearchResult.Id}";
        
        // Use ViewModel command
        _recipeVm.AddProjectItem(_selectedSearchResult.Id, itemName, 1, false);
        
        StatusLabel.Text = _recipeVm.ProjectItems.Any(p => p.Id == _selectedSearchResult.Id && p.Quantity > 1) 
            ? $"Increased quantity of {itemName}" 
            : $"Added {itemName} to project";

        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
        UpdateQuickViewCount();
        
        BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
    }

    /// <summary>
    /// Initiates building the crafting plan from current project items.
    /// Note: MUST REMAIN in MainWindow - reads DcCombo/WorldCombo SelectedItem (UI elements),
    ///       updates BuildPlanButton/BrowsePlanButton states, and triggers auto price fetch.
    ///       Complex UI coordination not suitable for ViewModel.
    /// </summary>
    private void OnBuildProjectPlan(object sender, RoutedEventArgs e)
    {
        OnBuildProjectPlanAsync().SafeFireAndForget(OnAsyncError);
    }

    /// <summary>
    /// Builds the crafting plan by calling RecipeCalculationService.
    /// Note: MUST REMAIN in MainWindow - extensive UI coordination: BuildPlanButton/BrowsePlanButton state,
    ///       ProcurementRefreshButton/RebuildFromCacheButton enablement, auto-fetch prices trigger,
    ///       StatusLabel updates, and tree view display. Too UI-heavy for ViewModel.
    /// </summary>
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
             
            if (_currentPlan != null)
            {
                DisplayPlanInTreeView(_currentPlan);
            }
            UpdateBuildPlanButtonText();
            
            PopulateShoppingList();
            
            ProcurementRefreshButton.IsEnabled = true;
            RebuildFromCacheButton.IsEnabled = true;
            
            StatusLabel.Text = $"Plan built: {_currentPlan.RootItems.Count} root items, " +
                               $"{_currentPlan.AggregatedMaterials.Count} unique materials";
            
            var autoFetch = _settingsService.Get<bool>("market.auto_fetch_prices", true);
            _logger.LogInformation("[OnBuildProjectPlan] market.auto_fetch_prices = {Value}", autoFetch);
            if (autoFetch)
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

    /// <summary>
    /// Opens the Plan Browser dialog for managing project items.
    /// Note: UI-specific dialog handling - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - dialog owner must be the window (this).
    /// </summary>
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

    /// <summary>
    /// Populates the shopping list panel using ShoppingListBuilder.
    /// Note: Uses ShoppingListBuilder - refactor successful, UI-specific coordination only.
    /// MVVM: Not an ICommand candidate - internal coordination method, triggered by plan changes.
    /// </summary>
    private void PopulateShoppingList()
    {
        if (_currentPlan?.AggregatedMaterials == null || ShoppingListPanel == null)
            return;

        _shoppingListBuilder.PopulatePanel(ShoppingListPanel, _currentPlan.AggregatedMaterials);
        
        if (TotalCostText != null)
        {
            var totalCost = _shoppingListBuilder.CalculateTotalCost(_currentPlan.AggregatedMaterials);
            TotalCostText.Text = $"{totalCost:N0}g";
        }
    }

    /// <summary>
    /// Handles selection change in project items list (currently no-op).
    /// Note: Placeholder for future functionality.
    /// MVVM: Not an ICommand candidate - SelectionChanged event, not a command.
    /// </summary>
    private void OnProjectItemSelected(object sender, SelectionChangedEventArgs e)
    {
    }

    /// <summary>
    /// Updates the Build Plan button text based on whether a plan exists.
    /// Note: UI-specific - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - UI helper method, not an event handler.
    /// </summary>
    private void UpdateBuildPlanButtonText()
    {
        var hasPlan = _currentPlan != null && _currentPlan.RootItems.Count > 0;
        BuildPlanButton.Content = hasPlan ? "Rebuild Project Plan" : "Build Project Plan";
    }

    /// <summary>
    /// Displays the crafting plan in the recipe tree view.
    /// Note: Delegates to RecipeTreeUiBuilder - refactor successful.
    /// MVVM: Not an ICommand candidate - UI helper method, not an event handler.
    /// </summary>
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
    
    /// <summary>
    /// Recursively sets expand/collapse state for an expander and its children.
    /// Note: UI helper - could be moved to RecipeTreeUiBuilder but kept here for tree operations.
    /// MVVM: Not an ICommand candidate - UI helper method, not an event handler.
    /// </summary>
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
    
    /// <summary>
    /// Gets the display color for a node based on its acquisition source.
    /// Note: Presentation logic - could be a value converter in XAML.
    /// MVVM: Not an ICommand candidate - pure function, not an event handler.
    /// </summary>
    private Brush GetNodeForeground(PlanNode node) => node.Source switch
    {
        AcquisitionSource.Craft => Brushes.White,
        AcquisitionSource.MarketBuyNq => Brushes.LightSkyBlue,
        AcquisitionSource.MarketBuyHq => Brushes.LightGreen,
        AcquisitionSource.VendorBuy => Brushes.LightYellow,
        _ => Brushes.LightGray
    };
    
    /// <summary>
    /// Gets the price display text for a node.
    /// Note: Uses RecipeCalculationService.CalculateNodeCraftCost - business logic extracted.
    /// MVVM: Not an ICommand candidate - pure function, not an event handler.
    /// </summary>
    private string GetNodePriceDisplay(PlanNode node)
    {
        return node.Source switch
        {
            AcquisitionSource.Craft when node.Children.Any() => 
                $"(~{_recipeCalcService.CalculateNodeCraftCost(node):N0}g)",
            AcquisitionSource.VendorBuy when node.VendorPrice > 0 => 
                $"(~{node.VendorPrice * node.Quantity:N0}g)",
            AcquisitionSource.MarketBuyNq when node.MarketPrice > 0 => 
                $"(~{node.MarketPrice * node.Quantity:N0}g)",
            AcquisitionSource.MarketBuyHq when node.HqMarketPrice > 0 => 
                $"(~{node.HqMarketPrice * node.Quantity:N0}g)",
            _ => ""
        };
    }
    
    /// <summary>
    /// Displays a plan with cached prices and restores market state.
    /// Note: Coordination logic - could be moved to a PlanDisplayCoordinator.
    /// MVVM: Not an ICommand candidate - internal coordination method, not directly triggered by user.
    /// </summary>
    private async Task DisplayPlanWithCachedPrices(CraftingPlan plan)
    {
        DisplayPlanInTreeView(plan);
        
        if (plan.SavedMarketPlans?.Count > 0 == true)
        {
            _marketVm.SetShoppingPlans(plan.SavedMarketPlans);
            PopulateProcurementPanel();
            
            ProcurementRefreshButton.IsEnabled = true;
            LeftPanelConductAnalysisButton.IsEnabled = true;
            RebuildFromCacheButton.IsEnabled = true;
            LeftPanelViewMarketStatusButton.IsEnabled = true;
            MenuViewMarketStatus.IsEnabled = true;
            
            var savedPrices = _recipeCalcService.ExtractPricesFromPlan(plan);
            if (savedPrices.Count > 0)
            {
                StatusLabel.Text = $"Loaded plan with {savedPrices.Count} cached prices and {_currentMarketPlans.Count} market items.";
            }
            else
            {
                StatusLabel.Text = $"Loaded plan with {_currentMarketPlans.Count} market items.";
            }
        }
        else if (_recipeCalcService.ExtractPricesFromPlan(plan).Count > 0)
        {
            var savedPrices = _recipeCalcService.ExtractPricesFromPlan(plan);
            await UpdateMarketLogisticsAsync(savedPrices, useCachedData: true);
            StatusLabel.Text = $"Loaded plan with {savedPrices.Count} cached prices.";
            
            RebuildFromCacheButton.IsEnabled = true;
        }
        else
        {
            ShowMarketLogisticsPlaceholder();
        }
    }

    /// <summary>
    /// Initiates saving the current plan.
    /// Note: Delegates to RecipePlannerViewModel.SavePlanCommand.
    /// </summary>
    private void OnSavePlan(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null)
        {
            StatusLabel.Text = "No plan to save - build a plan first";
            return;
        }
        
        _recipeVm.SavePlanCommand.Execute(null);
        
        // Sync status from ViewModel
        if (!string.IsNullOrEmpty(_recipeVm.StatusMessage))
        {
            StatusLabel.Text = _recipeVm.StatusMessage;
        }
    }

    /// <summary>
    /// Opens the plan browser to load a saved plan.
    /// Note: Delegates to RecipePlannerViewModel.LoadPlanCommand.
    /// </summary>
    private void OnViewPlans(object sender, RoutedEventArgs e)
    {
        _recipeVm.LoadPlanCommand.Execute(null);
        
        // UI updates after plan load
        if (_currentPlan != null)
        {
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
            UpdateQuickViewCount();
            BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
            BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
            
            DisplayPlanWithCachedPrices(_currentPlan).SafeFireAndForget(OnAsyncError);
            
            StatusLabel.Text = $"Loaded plan: {_currentPlan.Name}";
        }
    }

    /// <summary>
    /// Initiates renaming the current plan.
    /// Note: Delegates to RecipePlannerViewModel.RenamePlanCommand.
    /// </summary>
    private void OnRenamePlan(object sender, RoutedEventArgs e)
    {
        _recipeVm.RenamePlanCommand.Execute(null);
        
        // Sync status from ViewModel
        if (!string.IsNullOrEmpty(_recipeVm.StatusMessage))
        {
            StatusLabel.Text = _recipeVm.StatusMessage;
        }
    }

    /// <summary>
    /// Imports a plan from Teamcraft.
    /// Note: Delegates to RecipePlannerViewModel.ImportFromTeamcraftCommand.
    /// </summary>
    private void OnImportTeamcraft(object sender, RoutedEventArgs e)
    {
        _recipeVm.ImportFromTeamcraftCommand.Execute(null);
        
        // UI updates after import
        if (_currentPlan != null)
        {
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
            UpdateQuickViewCount();
            BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
            BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
            
            DisplayPlanInTreeView(_currentPlan);
        }
        
        if (!string.IsNullOrEmpty(_recipeVm.StatusMessage))
            StatusLabel.Text = _recipeVm.StatusMessage;
    }

    /// <summary>
    /// Exports current plan to native Craft Architect format.
    /// Note: Delegates to RecipePlannerViewModel.ExportToNativeCommand.
    /// </summary>
    private async void OnExportNative(object sender, RoutedEventArgs e)
    {
        await _recipeVm.ExportToNativeCommand.ExecuteAsync(null);
        if (!string.IsNullOrEmpty(_recipeVm.StatusMessage))
            StatusLabel.Text = _recipeVm.StatusMessage;
    }

    /// <summary>
    /// Exports current plan to Teamcraft format.
    /// Note: Delegates to RecipePlannerViewModel.ExportToTeamcraftCommand.
    /// </summary>
    private void OnExportTeamcraft(object sender, RoutedEventArgs e)
    {
        _recipeVm.ExportToTeamcraftCommand.Execute(null);
        if (!string.IsNullOrEmpty(_recipeVm.StatusMessage))
            StatusLabel.Text = _recipeVm.StatusMessage;
    }

    /// <summary>
    /// Exports current plan to Artisan format.
    /// Note: Delegates to RecipePlannerViewModel.ExportToArtisanCommand.
    /// </summary>
    private void OnExportArtisan(object sender, RoutedEventArgs e)
    {
        _recipeVm.ExportToArtisanCommand.Execute(null);
        if (!string.IsNullOrEmpty(_recipeVm.StatusMessage))
            StatusLabel.Text = _recipeVm.StatusMessage;
    }

    /// <summary>
    /// Imports a plan from native Craft Architect format.
    /// </summary>
    private async void OnImportNative(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Craft Architect Plan (*.craftplan)|*.craftplan|JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "craftplan"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var plan = _recipeCalcService.DeserializePlan(json);
            
            if (plan == null)
            {
                StatusLabel.Text = "Failed to import - invalid plan file";
                return;
            }

            // Load the plan into the ViewModel
            _recipeVm.LoadPlan(plan);
            
            // Update UI
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
            UpdateQuickViewCount();
            BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
            BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
             
            if (_currentPlan != null)
            {
                DisplayPlanInTreeView(_currentPlan);
            }
             
            StatusLabel.Text = $"Imported native plan '{plan.Name}' with {plan.RootItems.Count} items";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Import failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Imports a plan from Artisan format.
    /// Note: Delegates to RecipePlannerViewModel.ImportFromArtisanCommand.
    /// </summary>
    private void OnImportArtisan(object sender, RoutedEventArgs e)
    {
        _recipeVm.ImportFromArtisanCommand.Execute(null);
        
        // UI updates after import
        if (_currentPlan != null)
        {
            ProjectList.ItemsSource = null;
            ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
            UpdateQuickViewCount();
            BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
            BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
            
            DisplayPlanInTreeView(_currentPlan);
        }
        
        if (!string.IsNullOrEmpty(_recipeVm.StatusMessage))
            StatusLabel.Text = _recipeVm.StatusMessage;
    }

    /// <summary>
    /// Exports current plan to plain text.
    /// Note: Delegates to RecipePlannerViewModel.ExportToPlainTextCommand.
    /// </summary>
    private void OnExportPlainText(object sender, RoutedEventArgs e)
    {
        _recipeVm.ExportToPlainTextCommand.Execute(null);
        if (!string.IsNullOrEmpty(_recipeVm.StatusMessage))
            StatusLabel.Text = _recipeVm.StatusMessage;
    }

    /// <summary>
    /// Exports current plan to CSV.
    /// Note: Delegates to RecipePlannerViewModel.ExportToCsvCommand.
    /// </summary>
    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        _recipeVm.ExportToCsvCommand.Execute(null);
        if (!string.IsNullOrEmpty(_recipeVm.StatusMessage))
            StatusLabel.Text = _recipeVm.StatusMessage;
    }

    /// <summary>
    /// Initiates fetching market prices for all items in the plan.
    /// Note: MUST REMAIN in MainWindow - creates/shows MarketDataStatusWindow, reads DcCombo/WorldCombo,
    ///       coordinates with MarketAnalysisContent visibility, updates multiple UI elements.
    ///       Requires direct window ownership and UI control access.
    /// </summary>
    private void OnFetchPrices(object sender, RoutedEventArgs e)
    {
        OnFetchPricesAsync(forceRefresh: true).SafeFireAndForget(OnAsyncError);
    }

    /// <summary>
    /// Conducts market analysis using cached data (fetches only if needed).
    /// Note: MUST REMAIN in MainWindow - uses cache-first approach for faster analysis.
    /// </summary>
    private void OnConductAnalysis(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        OnFetchPricesAsync(forceRefresh: false).SafeFireAndForget(OnAsyncError);
    }

    /// <summary>
    /// Handles refresh request from MarketDataStatusWindow.
    /// Performs a force refresh of all market data.
    /// </summary>
    private void OnMarketDataStatusRefreshRequested(object? sender, EventArgs e)
    {
        if (_marketDataStatusWindow != null && _marketDataStatusWindow.IsVisible)
        {
            // Re-initialize the status window and do a force refresh
            if (_currentPlan != null && _currentPlan.RootItems.Count > 0)
            {
                var allItems = new List<(int itemId, string name, int quantity)>();
                _recipeCalcService.CollectAllItemsWithQuantity(_currentPlan.RootItems, allItems);
                _marketDataStatusSession.InitializeItems(allItems);
                _marketDataStatusWindow.RefreshView();
            }
        }
        
        OnFetchPricesAsync(forceRefresh: true).SafeFireAndForget(OnAsyncError);
    }

    /// <summary>
    /// Fetches market prices via PriceCheckService and updates the plan.
    /// Note: MUST REMAIN in MainWindow - manages MarketDataStatusWindow lifecycle (Show/Activate),
    ///       handles progress updates to StatusLabel, updates recipe tree, procurement panel,
    ///       and multiple button enablement states. Heavy UI coordination unsuitable for ViewModel.
    /// </summary>
    private async Task OnFetchPricesAsync(bool forceRefresh = false)
    {
        _logger.LogInformation("[OnFetchPricesAsync] START - forceRefresh={ForceRefresh}", forceRefresh);
        
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            _logger.LogWarning("[OnFetchPricesAsync] ABORT - No plan available");
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        var dc = DcCombo.SelectedItem as string ?? "Aether";
        var world = WorldCombo.SelectedItem as string ?? "";
        var worldOrDc = string.IsNullOrEmpty(world) || world == "Entire Data Center" ? dc : world;
        var searchAllNA = ProcurementSearchAllNaCheck?.IsChecked ?? false;

        _logger.LogInformation("[OnFetchPricesAsync] DC={DC}, World={World}, SearchAllNA={SearchAllNA}, PlanItems={ItemCount}", 
            dc, world, searchAllNA, _currentPlan.RootItems.Count);

        if (_marketDataStatusWindow == null || !_marketDataStatusWindow.IsVisible)
        {
            _marketDataStatusWindow = new MarketDataStatusWindow(_dialogFactory, _marketDataStatusSession);
            _marketDataStatusWindow.Owner = this;
            _marketDataStatusWindow.RefreshMarketDataRequested += OnMarketDataStatusRefreshRequested;
        }

        var allItems = new List<(int itemId, string name, int quantity)>();
        _recipeCalcService.CollectAllItemsWithQuantity(_currentPlan.RootItems, allItems);
        
        _logger.LogInformation("[OnFetchPricesAsync] RootItems.Count={RootCount}, AggregatedMaterials.Count={AggCount}",
            _currentPlan.RootItems.Count, _currentPlan.AggregatedMaterials?.Count ?? 0);
        _logger.LogInformation("[OnFetchPricesAsync] Collected {Count} items for price check: [{Items}]",
            allItems.Count, string.Join(", ", allItems.Select(i => $"{i.name}({i.itemId})x{i.quantity}")));
        
        if (_currentPlan.AggregatedMaterials?.Any() == true)
        {
            _logger.LogInformation("[OnFetchPricesAsync] AggregatedMaterials: [{Materials}]",
                string.Join(", ", _currentPlan.AggregatedMaterials.Select(m => $"{m.Name}({m.ItemId})x{m.TotalQuantity}")));
        }
        
        _marketDataStatusSession.InitializeItems(allItems);
        _marketDataStatusWindow.RefreshView();
        _marketDataStatusWindow.Show();
        _marketDataStatusWindow.Activate();

        StatusLabel.Text = $"Fetching prices for {allItems.Count} items...";

        try
        {
            var progress = new Progress<PriceRefreshProgress>(p =>
            {
                var statusText = p.Message ?? p.Stage switch
                {
                    PriceRefreshStage.Starting => $"Checking cache... {p.Current}/{p.Total}",
                    PriceRefreshStage.Fetching => string.IsNullOrWhiteSpace(p.ItemName)
                        ? $"Fetching market prices... {p.Current}/{p.Total}"
                        : $"Loading item data: {p.ItemName} ({p.Current}/{p.Total})",
                    PriceRefreshStage.Updating => $"Processing results... {p.Current}/{p.Total}",
                    PriceRefreshStage.Complete => $"Complete! ({p.Total} items)",
                    _ => $"Fetching prices... {p.Current}/{p.Total}"
                };

                StatusLabel.Text = statusText;

                if (p.Stage == PriceRefreshStage.Fetching && !string.IsNullOrEmpty(p.ItemName))
                {
                    var item = allItems.FirstOrDefault(i => i.name == p.ItemName);
                    if (item.itemId > 0)
                    {
                        _marketDataStatusSession.SetItemFetching(item.itemId);
                        _marketDataStatusWindow.RefreshView();
                    }
                }
            });

            _logger.LogInformation("[OnFetchPricesAsync] Fetching plan prices via PriceRefreshCoordinator");
            var refreshContext = await _priceRefreshCoordinator.FetchPlanPricesAsync(
                _currentPlan,
                dc,
                worldOrDc,
                searchAllNA,
                progress,
                default);

            allItems = refreshContext.AllItems;
            var prices = refreshContext.Prices;
            var cacheCandidateItemIds = refreshContext.CacheCandidateItemIds;
            var warmCacheForCraftedItems = refreshContext.WarmCacheForCraftedItems;
            var fetchedThisRunKeys = refreshContext.FetchedThisRunKeys;
            var dataScopeByItemId = refreshContext.DataScopeByItemId;
            var scopeDataCenters = refreshContext.ScopeDataCenters;

            _logger.LogInformation("[OnFetchPricesAsync] Got {Count} prices from cache", prices.Count);

            int successCount = 0;
            int failedCount = 0;
            int skippedCount = 0;
            int cachedCount = 0;
            
            foreach (var kvp in prices)
            {
                int itemId = kvp.Key;
                var priceInfo = kvp.Value;
                
                if (priceInfo.Source == PriceSource.Unknown)
                {
                    if (!warmCacheForCraftedItems && !cacheCandidateItemIds.Contains(itemId))
                    {
                        _marketDataStatusSession.SetItemSkipped(itemId, "Skipped (crafted item; market warming disabled)");
                        skippedCount++;
                    }
                    else
                    {
                        _marketDataStatusSession.SetItemFailed(itemId, priceInfo.SourceDetails, dataTypeText: "Unknown");
                        failedCount++;
                    }
                }
                else if (priceInfo.Source == PriceSource.Market)
                {
                    var isFetchedThisRun = scopeDataCenters.Any(itemDc => fetchedThisRunKeys.Contains((itemId, itemDc)));
                    var dataScopeText = BuildMarketDataScopeText(itemId, dataScopeByItemId, scopeDataCenters.Count);
                    
                    if (isFetchedThisRun)
                    {
                        _marketDataStatusSession.SetItemSuccess(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails, dataScopeText, "Universalis (this run)", "Market");
                        successCount++;
                    }
                    else
                    {
                        _marketDataStatusSession.SetItemCached(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails, dataScopeText, "Market", priceInfo.LastUpdated);
                        cachedCount++;
                    }
                }
                else if (priceInfo.Source == PriceSource.Vendor)
                {
                    _marketDataStatusSession.SetItemSuccess(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails, "N/A", "Garland", "Vendor");
                    successCount++;
                }
                else if (priceInfo.Source == PriceSource.Untradeable)
                {
                    _marketDataStatusSession.SetItemSuccess(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails, "N/A", "N/A", "Untradeable");
                    successCount++;
                }
                else
                {
                    _marketDataStatusSession.SetItemCached(itemId, priceInfo.UnitPrice, priceInfo.SourceDetails, "N/A", GetDataTypeText(priceInfo.Source), priceInfo.LastUpdated);
                    cachedCount++;
                }

                _recipeCalcService.UpdateSingleNodePrice(_currentPlan?.RootItems, itemId, priceInfo);
            }

            _marketDataStatusWindow?.RefreshView();

            _logger.LogInformation("[OnFetchPricesAsync] Price results: Success={Success}, Failed={Failed}, Skipped={Skipped}, Cached={Cached}", 
                successCount, failedCount, skippedCount, cachedCount);
            
            if (_currentPlan != null)
            {
                DisplayPlanInTreeView(_currentPlan);
            }
            
            _logger.LogInformation("[OnFetchPricesAsync] Updating market logistics from refreshed prices...");
            await UpdateMarketLogisticsAsync(prices, useCachedData: false, searchAllNA: searchAllNA);
            _logger.LogInformation("[OnFetchPricesAsync] Market logistics update complete. _currentMarketPlans.Count={Count}", _currentMarketPlans?.Count ?? 0);
            
            // Always populate procurement panel after analysis so data is ready when user switches to market view
            _logger.LogInformation("[OnFetchPricesAsync] Calling PopulateProcurementPanel...");
            PopulateProcurementPanel();
            _logger.LogInformation("[OnFetchPricesAsync] PopulateProcurementPanel complete");
            
            RebuildFromCacheButton.IsEnabled = true;

            var totalCost = _currentPlan.AggregatedMaterials.Sum(m => m.TotalCost);

            if (failedCount > 0 && successCount == 0)
            {
                StatusLabel.Text = $"Price fetch failed! Using cached prices. Total: {totalCost:N0}g";
            }
            else if (failedCount > 0)
            {
                StatusLabel.Text = $"Prices updated! Total: {totalCost:N0}g ({successCount} success, {failedCount} failed, {skippedCount} skipped, {cachedCount} cached)";
            }
            else
            {
                StatusLabel.Text = $"Prices fetched! Total: {totalCost:N0}g ({successCount} success, {skippedCount} skipped, {cachedCount} cached)";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OnFetchPricesAsync] FAILED - Exception: {Message}\nStackTrace: {StackTrace}", ex.Message, ex.StackTrace);
            StatusLabel.Text = $"Failed to fetch prices: {ex.Message}. Cached prices preserved.";
            
            foreach (var item in allItems)
            {
                _marketDataStatusSession.SetItemFailed(item.itemId, ex.Message, dataTypeText: "Unknown");
            }
            _marketDataStatusWindow?.RefreshView();
        }
        
        _logger.LogInformation("[OnFetchPricesAsync] END");
    }

    /// <summary>
    /// Rebuilds market analysis from cached prices without fetching new data.
    /// Note: MUST REMAIN in MainWindow - updates RebuildFromCacheButton state, calls DisplayPlanInTreeView,
    ///       and updates StatusLabel. UI coordination requires MainWindow context.
    /// </summary>
    private void OnRebuildFromCache(object sender, RoutedEventArgs e)
    {
        OnRebuildFromCacheAsync().SafeFireAndForget(OnAsyncError);
    }

    /// <summary>
    /// Performs cache-based rebuild of market analysis.
    /// Note: MUST REMAIN in MainWindow - updates StatusLabel, RebuildFromCacheButton.IsEnabled,
    ///       calls DisplayPlanInTreeView and UpdateMarketLogisticsAsync. UI coordination pattern.
    /// </summary>
    private async Task OnRebuildFromCacheAsync()
    {
        _logger.LogInformation("[OnRebuildFromCacheAsync] START");
        
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            _logger.LogWarning("[OnRebuildFromCacheAsync] ABORT - No plan available");
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        var cachedPrices = _recipeCalcService.ExtractPricesFromPlan(_currentPlan);
        _logger.LogInformation("[OnRebuildFromCacheAsync] Extracted {Count} cached prices from plan", cachedPrices.Count);
        
        if (cachedPrices.Count == 0)
        {
            _logger.LogWarning("[OnRebuildFromCacheAsync] ABORT - No cached prices available");
            StatusLabel.Text = "No cached prices available. Click 'Refresh Market Data' to fetch prices.";
            return;
        }

        StatusLabel.Text = $"Rebuilding market analysis from {cachedPrices.Count} cached prices...";
        RebuildFromCacheButton.IsEnabled = false;
        
        try
        {
            _logger.LogInformation("[OnRebuildFromCacheAsync] Calling UpdateMarketLogisticsAsync...");
            await UpdateMarketLogisticsAsync(cachedPrices, useCachedData: true);
            _logger.LogInformation("[OnRebuildFromCacheAsync] UpdateMarketLogisticsAsync complete. _currentMarketPlans.Count={Count}", _currentMarketPlans?.Count ?? 0);
            
            DisplayPlanInTreeView(_currentPlan);
            
            // Populate procurement panel after rebuilding from cache
            _logger.LogInformation("[OnRebuildFromCacheAsync] Calling PopulateProcurementPanel...");
            PopulateProcurementPanel();
            _logger.LogInformation("[OnRebuildFromCacheAsync] PopulateProcurementPanel complete");
            
            StatusLabel.Text = $"Market analysis rebuilt from {cachedPrices.Count} cached prices.";
            _logger.LogInformation("[OnRebuildFromCacheAsync] SUCCESS - Rebuilt market analysis from {Count} cached prices", cachedPrices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OnRebuildFromCacheAsync] FAILED - Exception: {Message}", ex.Message);
            StatusLabel.Text = $"Failed to rebuild from cache: {ex.Message}";
        }
        finally
        {
            RebuildFromCacheButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Expands all nodes in the recipe tree.
    /// Note: UI-specific - delegates to RecipePlannerViewModel.ExpandAllCommand.
    /// </summary>
    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        _recipeVm.ExpandAllCommand.Execute(null);
        SetAllExpandersState(true);
    }
    
    /// <summary>
    /// Collapses all nodes in the recipe tree.
    /// Note: UI-specific - delegates to RecipePlannerViewModel.CollapseAllCommand.
    /// </summary>
    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        _recipeVm.CollapseAllCommand.Execute(null);
        SetAllExpandersState(false);
    }
    
    /// <summary>
    /// Sets expand/collapse state for all expanders in the recipe tree.
    /// Note: UI helper - could be part of RecipeTreeUiBuilder.
    /// MVVM: Not an ICommand candidate - internal helper method, not directly triggered by user.
    /// </summary>
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
    
    /// <summary>
    /// Recursively sets expand/collapse state for child expanders.
    /// Note: UI helper - could be part of RecipeTreeUiBuilder.
    /// MVVM: Not an ICommand candidate - internal helper method, not directly triggered by user.
    /// </summary>
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

    /// <summary>
    /// Handles window closing - saves cache and disposes ViewModels.
    /// Note: Lifecycle method - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - lifecycle override, not a user command.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _itemCache.SaveCache();
        
        _recipeVm.Dispose();
        _marketVm.Dispose();
        
        base.OnClosing(e);
    }

}
