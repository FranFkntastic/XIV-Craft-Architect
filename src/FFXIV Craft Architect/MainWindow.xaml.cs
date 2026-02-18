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
    private enum MainTab
    {
        RecipePlanner,
        MarketAnalysis,
        ProcurementPlanner
    }

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
    private ComboBox LeftPanelProcurementSortCombo => ProcurementPlannerSidebarModule.LeftPanelProcurementSortComboControl;
    private Wpf.Ui.Controls.Button LeftPanelProcurementAnalysisButton => ProcurementPlannerSidebarModule.LeftPanelProcurementAnalysisButtonControl;

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
    private ScrollViewer LegacyProcurementScrollViewer => MarketAnalysisModule.LegacyProcurementScrollViewer;
    private StackPanel ProcurementPanel => MarketAnalysisModule.ProcurementPanel;
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
    private readonly PlanPersistenceService _planPersistence;
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
    private MainTab _activeTab = MainTab.RecipePlanner;
    
    // Coordinators
    private readonly ImportCoordinator _importCoordinator;
    private readonly ExportCoordinator _exportCoordinator;
    private readonly PlanPersistenceCoordinator _planCoordinator;
    private readonly WatchStateCoordinator _watchStateCoordinator;
    private readonly WorldDataCoordinator _worldDataCoordinator;
    private readonly IPriceRefreshCoordinator _priceRefreshCoordinator;
    private readonly IShoppingOptimizationCoordinator _shoppingOptimizationCoordinator;

    public MainWindow(
        GarlandService garlandService,
        UniversalisService universalisService,
        SettingsService settingsService,
        ItemCacheService itemCache,
        RecipeCalculationService recipeCalcService,
        PlanPersistenceService planPersistence,
        MarketShoppingService marketShoppingService,
        WorldBlacklistService blacklistService,
        DialogServiceFactory dialogFactory,
        ILogger<MainWindow> logger,
        ILoggerFactory loggerFactory,
        ImportCoordinator importCoordinator,
        ExportCoordinator exportCoordinator,
        PlanPersistenceCoordinator planCoordinator,
        WatchStateCoordinator watchStateCoordinator,
        WorldDataCoordinator worldDataCoordinator,
        IPriceRefreshCoordinator priceRefreshCoordinator,
        IShoppingOptimizationCoordinator shoppingOptimizationCoordinator,
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
        _planPersistence = planPersistence;
        _marketShoppingService = marketShoppingService;
        _blacklistService = blacklistService;
        _dialogFactory = dialogFactory;
        _dialogs = _dialogFactory.CreateForWindow(this);
        _logger = logger;
        
        // Coordinators (injected via DI)
        _importCoordinator = importCoordinator;
        _exportCoordinator = exportCoordinator;
        _planCoordinator = planCoordinator;
        _watchStateCoordinator = watchStateCoordinator;
        _worldDataCoordinator = worldDataCoordinator;
        _priceRefreshCoordinator = priceRefreshCoordinator;
        _shoppingOptimizationCoordinator = shoppingOptimizationCoordinator;
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
            _settingsService,
            _infoPanelBuilder,
            SplitPaneCardsGrid,
            SplitPaneExpandedContent,
            SplitPaneExpandedPanel,
            MarketTotalCostText,
            ProcurementPlanPanel,
            ProcurementPanel,
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
        _logger.LogInformation("[OnLoaded] planning.default_recommendation_mode = '{Value}', Setting combo index: {Index}", defaultMode, defaultMode == "MaximizeValue" ? 1 : 0);
        MarketModeCombo.SelectedIndex = defaultMode == "MaximizeValue" ? 1 : 0;
        
        // Also initialize ProcurementModeCombo from same setting
        var procModeIndex = defaultMode == "MaximizeValue" ? 1 : 0;
        _logger.LogInformation("[OnLoaded] Setting ProcurementModeCombo.SelectedIndex = {Index}", procModeIndex);
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
                ApplyMarketSortAndDisplay();
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
            
            RefreshMarketButton.IsEnabled = true;
            RebuildFromCacheButton.IsEnabled = true;
            ViewMarketStatusButton.IsEnabled = true;
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
        var searchAllNA = SearchAllNACheck?.IsChecked ?? false;

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
            
            // Step 3: Categorize by resolved price source and compute shopping plans.
            _logger.LogInformation("[OnFetchPricesAsync] Displaying market analysis...");
            ClearMarketLogisticsPanels();
            var categorized = _marketShoppingService.CategorizeMaterials(_currentPlan?.AggregatedMaterials ?? new(), prices);

            List<DetailedShoppingPlan> shoppingPlans;
            if (categorized.MarketItems.Any())
            {
                var marketProgress = new Progress<string>(msg =>
                {
                    StatusLabel.Text = $"Analyzing market: {msg}";
                });

                if (searchAllNA)
                {
                    shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansMultiDCAsync(
                        categorized.MarketItems, marketProgress, mode: GetCurrentRecommendationMode());
                }
                else
                {
                    shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansAsync(
                        categorized.MarketItems, dc, marketProgress, mode: GetCurrentRecommendationMode());
                }

                _logger.LogInformation("[OnFetchPricesAsync] Got {Count} detailed shopping plans", shoppingPlans.Count);
                _marketShoppingService.ApplyVendorPurchaseOverrides(_currentPlan, shoppingPlans);
                _marketVm.SetShoppingPlans(shoppingPlans);
            }
            else
            {
                shoppingPlans = new List<DetailedShoppingPlan>();
                _marketVm.SetShoppingPlans(shoppingPlans);
            }

            if (_currentPlan != null)
            {
                _currentPlan.SavedMarketPlans = shoppingPlans;
            }

            UpdateMarketSummaryCard(categorized.VendorItems, categorized.MarketItems, categorized.UntradeableItems, prices);
            if (categorized.VendorItems.Any())
            {
                AddVendorItemsCard(categorized.VendorItems, prices);
            }
            if (categorized.MarketItems.Any())
            {
                await FetchAndDisplayLiveMarketDataAsync(categorized.MarketItems, searchAllNA, shoppingPlans);
            }
            if (categorized.UntradeableItems.Any())
            {
                AddUntradeableItemsCard(categorized.UntradeableItems);
            }
            _logger.LogInformation("[OnFetchPricesAsync] Market analysis display complete. _currentMarketPlans.Count={Count}", _currentMarketPlans?.Count ?? 0);
            
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
    /// Opens or activates the market data status window.
    /// Note: UI-specific window management - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - needs direct window reference and Owner assignment.
    /// </summary>
    private void OnViewMarketStatus(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null || _currentPlan.RootItems.Count == 0)
        {
            StatusLabel.Text = "No plan - build a plan first";
            return;
        }

        if (_marketDataStatusWindow == null || !_marketDataStatusWindow.IsVisible)
        {
            _marketDataStatusWindow = new MarketDataStatusWindow(_dialogFactory, _marketDataStatusSession);
            _marketDataStatusWindow.Owner = this;
            _marketDataStatusWindow.RefreshMarketDataRequested += OnMarketDataStatusRefreshRequested;

            if (_marketDataStatusSession.TotalCount == 0)
            {
                var allItems = new List<(int itemId, string name, int quantity)>();
                _recipeCalcService.CollectAllItemsWithQuantity(_currentPlan.RootItems, allItems);
                _marketDataStatusSession.InitializeItems(allItems);
                MarkExistingPricesInStatusWindow(_currentPlan.RootItems);
            }

            _marketDataStatusWindow.RefreshView();
        }
        
        _marketDataStatusWindow.Show();
        _marketDataStatusWindow.Activate();
    }

    /// <summary>
    /// Marks items with existing prices in the status window.
    /// Note: UI helper for status window - could be moved to MarketDataStatusWindow.
    /// MVVM: Not an ICommand candidate - internal helper method, not directly triggered by user.
    /// </summary>
    private void MarkExistingPricesInStatusWindow(List<PlanNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.MarketPrice > 0)
            {
                _marketDataStatusSession.SetItemCached(node.ItemId, node.MarketPrice, node.PriceSourceDetails, "-", "Market");
            }
            
            if (node.Children?.Any() == true)
            {
                MarkExistingPricesInStatusWindow(node.Children);
            }
        }
    }

    private static string BuildMarketDataScopeText(
        int itemId,
        IReadOnlyDictionary<int, (int CachedDataCenterCount, int CachedWorldCount)> scopeByItemId,
        int totalDataCenterCount)
    {
        if (!scopeByItemId.TryGetValue(itemId, out var scope) || scope.CachedDataCenterCount == 0)
        {
            return $"0/{totalDataCenterCount} DC / 0 worlds";
        }

        return $"{scope.CachedDataCenterCount}/{totalDataCenterCount} DC / {scope.CachedWorldCount} worlds";
    }

    private static string GetDataTypeText(PriceSource source)
    {
        return source switch
        {
            PriceSource.Market => "Market",
            PriceSource.Vendor => "Vendor",
            PriceSource.Untradeable => "Untradeable",
            PriceSource.Unknown => "Unknown",
            _ => source.ToString()
        };
    }

    /// <summary>
    /// Shows placeholder in market logistics panel when no market data is available.
    /// Uses ICardFactory for consistent styling with other market logistics cards.
    /// MVVM: Not an ICommand candidate - internal UI state method, not directly triggered by user.
    /// </summary>
    private void ShowMarketLogisticsPlaceholder()
    {
        MarketCards.Children.Clear();
        MarketSummaryExpander.Visibility = System.Windows.Visibility.Collapsed;
        RefreshMarketButton.IsEnabled = false;
        
        var placeholderCard = _cardFactory.CreateInfoCard("Market Logistics",
            "Click 'Fetch Prices' to see your purchase plan.\n\n" +
            "This tab will show:\n" +
            "\u2022 Items to buy from vendors (cheapest option)\n" +
            "\u2022 Items to buy from market board (with world listings)\n" +
            "\u2022 Cross-DC travel options (NA only)\n" +
            "\u2022 Untradeable items you need to gather/craft",
            CardType.Market);
        MarketCards.Children.Add(placeholderCard);
    }

    /// <summary>
    /// Updates the market logistics display with categorized items and shopping plans.
    /// Uses ICardFactory for consistent card styling across vendor, market, and untradeable item displays.
    /// Note: Coordinates between MarketShoppingService (categorization), ICardFactory (UI cards), and
    ///       MarketPlansRenderer (detailed plan display). Async path fetches live market data with progress.
    /// MVVM: Not an ICommand candidate - internal coordination method, triggered by price fetch operations.
    /// </summary>
    /// <param name="prices">Dictionary of item prices from cache or fresh fetch.</param>
    /// <param name="useCachedData">If true, displays cached data immediately without API calls.</param>
    /// <param name="searchAllNA">If true, searches all NA DCs for market listings.</param>
    private async Task UpdateMarketLogisticsAsync(Dictionary<int, PriceInfo> prices, bool useCachedData = false, bool searchAllNA = false)
    {
        _logger.LogInformation("[UpdateMarketLogisticsAsync] START - Prices.Count={Count}, UseCachedData={UseCached}", prices.Count, useCachedData);
        
        ClearMarketLogisticsPanels();

        var aggMaterials = _currentPlan?.AggregatedMaterials ?? new List<MaterialAggregate>();
        _logger.LogInformation("[UpdateMarketLogisticsAsync] AggregatedMaterials.Count={Count}, Items=[{Items}]",
            aggMaterials.Count, string.Join(", ", aggMaterials.Select(m => $"{m.Name}({m.ItemId})x{m.TotalQuantity}")));

        var categorized = _marketShoppingService.CategorizeMaterials(aggMaterials, prices);
        
        _logger.LogInformation("[UpdateMarketLogisticsAsync] Categorized: Vendor={VendorCount}, Market={MarketCount}, Untradeable={UntradeableCount}",
            categorized.VendorItems.Count, categorized.MarketItems.Count, categorized.UntradeableItems.Count);
        _logger.LogInformation("[UpdateMarketLogisticsAsync] MarketItems: [{Items}]",
            string.Join(", ", categorized.MarketItems.Select(m => $"{m.Name}({m.ItemId})x{m.TotalQuantity}")));
        _logger.LogInformation("[UpdateMarketLogisticsAsync] VendorItems: [{Items}]",
            string.Join(", ", categorized.VendorItems.Select(m => $"{m.Name}({m.ItemId})x{m.TotalQuantity}")));

        UpdateMarketSummaryCard(categorized.VendorItems, categorized.MarketItems, categorized.UntradeableItems, prices);

        if (categorized.VendorItems.Any())
        {
            AddVendorItemsCard(categorized.VendorItems, prices);
        }

        if (categorized.MarketItems.Any())
        {
            if (useCachedData)
            {
                _logger.LogInformation("[UpdateMarketLogisticsAsync] Using cached data path");
                AddCachedMarketDataCard(categorized.MarketItems, prices);
                RestoreShoppingPlansFromCache();
            }
            else
            {
                _logger.LogInformation("[UpdateMarketLogisticsAsync] Fetching live market data for {Count} items (SearchAllNA={SearchAllNA})", categorized.MarketItems.Count, searchAllNA);
                await FetchAndDisplayLiveMarketDataAsync(categorized.MarketItems, searchAllNA);
            }
        }
        else
        {
            // No market items to fetch - create empty shopping plans list
            // so that procurement panel can still display vendor/untradeable items
            _logger.LogWarning("[UpdateMarketLogisticsAsync] No market items to fetch - setting empty shopping plans");
            _marketVm.SetShoppingPlans(new List<DetailedShoppingPlan>());
        }

        if (categorized.UntradeableItems.Any())
        {
            AddUntradeableItemsCard(categorized.UntradeableItems);
        }
        
        _logger.LogInformation("[UpdateMarketLogisticsAsync] END - _marketVm.ShoppingPlans.Count={Count}", _marketVm.ShoppingPlans.Count);
    }

    /// <summary>
    /// Clears all panels and view models in preparation for new market logistics data.
    /// </summary>
    private void ClearMarketLogisticsPanels()
    {
        _marketVm.Clear();
        MarketCards.Children.Clear();
    }

    /// <summary>
    /// Adds a card displaying vendor items to the MarketCards panel.
    /// </summary>
    private void AddVendorItemsCard(List<MaterialAggregate> vendorItems, Dictionary<int, PriceInfo> prices)
    {
        var vendorText = new System.Text.StringBuilder();
        vendorText.AppendLine("Buy these from vendors (cheapest option):");
        vendorText.AppendLine();
        foreach (var item in vendorItems.OrderByDescending(i => i.TotalCost))
        {
            var source = prices[item.ItemId].SourceDetails;
            vendorText.AppendLine($"\u2022 {item.Name} x{item.TotalQuantity} = {item.TotalCost:N0}g ({source})");
        }

        var vendorCard = _cardFactory.CreateInfoCard(
            $"Vendor Items ({vendorItems.Count})",
            vendorText.ToString(),
            CardType.Vendor);
        MarketCards.Children.Add(vendorCard);
    }

    /// <summary>
    /// Adds a card displaying cached market data and enables relevant UI controls.
    /// Side effects: Enables RefreshMarketButton, RebuildFromCacheButton, ViewMarketStatusButton.
    /// </summary>
    private void AddCachedMarketDataCard(List<MaterialAggregate> marketItems, Dictionary<int, PriceInfo> prices)
    {
        var cachedCard = _cardFactory.CreateInfoCard(
            $"Market Board Items ({marketItems.Count})",
            "Using saved prices. Click 'Refresh Market Data' to fetch current listings.\n\n" +
            "Items to purchase:\n" +
            string.Join("\n", marketItems.Select(m =>
                $"\u2022 {m.Name} x{m.TotalQuantity} = {m.TotalCost:N0}g ({prices[m.ItemId].SourceDetails})")),
            CardType.Cached);
        MarketCards.Children.Add(cachedCard);

        RefreshMarketButton.IsEnabled = true;
        RebuildFromCacheButton.IsEnabled = true;
        ViewMarketStatusButton.IsEnabled = true;
        MenuViewMarketStatus.IsEnabled = true;
    }

    /// <summary>
    /// Restores shopping plans from cache to the view model if available.
    /// </summary>
    private void RestoreShoppingPlansFromCache()
    {
        _logger.LogInformation("[RestoreShoppingPlansFromCache] START - SavedMarketPlans.Count={Count}", _currentPlan?.SavedMarketPlans?.Count ?? 0);
        
        if (_currentPlan?.SavedMarketPlans?.Any() == true)
        {
            _logger.LogInformation("[RestoreShoppingPlansFromCache] Restoring {Count} plans from cache", _currentPlan.SavedMarketPlans.Count);
            _marketVm.SetShoppingPlans(_currentPlan.SavedMarketPlans);
            _logger.LogInformation("[RestoreShoppingPlansFromCache] Restore complete");
        }
        else
        {
            _logger.LogWarning("[RestoreShoppingPlansFromCache] No saved plans to restore");
        }
    }

    /// <summary>
    /// Fetches live market data and displays results. Handles loading state, errors, and button management.
    /// Side effects: Manages RefreshMarketButton/ViewMarketStatusButton enabled state, updates StatusLabel.
    /// </summary>
    /// <param name="marketItems">Items to fetch market data for.</param>
    /// <param name="searchAllNA">If true, searches all NA DCs instead of just the selected one.</param>
    /// <param name="preFetchedPlans">Optional pre-fetched shopping plans. If provided, no API calls are made.</param>
    private async Task FetchAndDisplayLiveMarketDataAsync(List<MaterialAggregate> marketItems, bool searchAllNA = false, List<DetailedShoppingPlan>? preFetchedPlans = null)
    {
        _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] START - {Count} market items, SearchAllNA={SearchAllNA}, HasPreFetched={HasPreFetched}", 
            marketItems.Count, searchAllNA, preFetchedPlans != null);
        
        var dc = DcCombo.SelectedItem as string ?? "Aether";
        
        _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] DC={DC}, SearchAllNA={SearchAllNA}", dc, searchAllNA);

        // If pre-fetched plans are provided, skip loading UI and use them directly
        if (preFetchedPlans != null)
        {
            _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] Using pre-fetched plans, skipping fetch");
            
            _marketVm.SetShoppingPlans(preFetchedPlans);
            _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] SetShoppingPlans complete. _currentMarketPlans.Count={Count}", _currentMarketPlans?.Count ?? 0);

            if (_currentPlan != null)
            {
                _currentPlan.SavedMarketPlans = preFetchedPlans;
            }

            ApplyMarketSortAndDisplay();

            StatusLabel.Text = $"Market analysis complete. {_currentMarketPlans.Count} items analyzed.";
            _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] SUCCESS (pre-fetched) - {Count} items analyzed", _currentMarketPlans?.Count ?? 0);
            return;
        }

        var loadingCard = _cardFactory.CreateInfoCard(
            "Market Board Items",
            $"Fetching detailed listings for {marketItems.Count} items from {(searchAllNA ? "all NA DCs" : dc)}...",
            CardType.Loading);
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

            _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] Calling MarketShoppingService...");
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
            _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] Got {Count} shopping plans from service", shoppingPlans.Count);

            _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] Calling _marketVm.SetShoppingPlans...");
            _marketVm.SetShoppingPlans(shoppingPlans);
            _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] SetShoppingPlans complete. _currentMarketPlans.Count={Count}", _currentMarketPlans?.Count ?? 0);

            if (_currentPlan != null)
            {
                _currentPlan.SavedMarketPlans = shoppingPlans;
            }

            MarketCards.Children.Remove(loadingCard);
            ApplyMarketSortAndDisplay();

            StatusLabel.Text = $"Market analysis complete. {_currentMarketPlans.Count} items analyzed.";
            _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] SUCCESS - {Count} items analyzed", _currentMarketPlans?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FetchAndDisplayLiveMarketDataAsync] FAILED - Exception: {Message}", ex.Message);
            MarketCards.Children.Remove(loadingCard);
            var errorCard = _cardFactory.CreateErrorCard(
                "Market Board Items",
                $"Error fetching listings: {ex.Message}");
            MarketCards.Children.Add(errorCard);
        }
        finally
        {
            RefreshMarketButton.IsEnabled = true;
            ViewMarketStatusButton.IsEnabled = true;
            MenuViewMarketStatus.IsEnabled = true;
            _logger.LogInformation("[FetchAndDisplayLiveMarketDataAsync] END");
        }
    }

    /// <summary>
    /// Adds a card displaying untradeable items to the MarketCards panel.
    /// </summary>
    private void AddUntradeableItemsCard(List<MaterialAggregate> untradeableItems)
    {
        var untradeText = new System.Text.StringBuilder();
        untradeText.AppendLine("These items must be gathered or crafted:");
        untradeText.AppendLine();
        foreach (var item in untradeableItems)
        {
            untradeText.AppendLine($"\u2022 {item.Name} x{item.TotalQuantity}");
        }

        var untradeCard = _cardFactory.CreateInfoCard(
            $"Untradeable Items ({untradeableItems.Count})",
            untradeText.ToString(),
            CardType.Untradeable);
        MarketCards.Children.Add(untradeCard);
    }

    /// <summary>
    /// Updates the market summary expander with vendor/market item counts and total cost.
    /// Displays grand total and breakdown of vendor vs market costs in the summary panel.
    /// Called during UpdateMarketLogisticsAsync to refresh summary information.
    /// </summary>
    /// <param name="vendorItems">Items available from vendors.</param>
    /// <param name="marketItems">Items to purchase from market board.</param>
    /// <param name="untradeableItems">Items that must be gathered/crafted.</param>
    /// <param name="prices">Price information for cost calculations.</param>
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

    /// <summary>
    /// Applies current sort selection and updates market cards display.
    /// Uses CreateMarketCardFromTemplate for card generation.
    /// Note: UI coordination - manages MarketCards children directly. Removes existing plan cards,
    ///       sorts plans by selected criteria, and re-inserts cards at the end of the panel.
    /// MVVM: Not an ICommand candidate - internal UI method, manages Panel children.
    /// </summary>
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
        
        var sortMode = (MarketSortCombo?.SelectedIndex ?? 0) switch
        {
            1 => ShoppingPlanSortMode.Alphabetical,
            _ => ShoppingPlanSortMode.RecommendedWorld
        };
        var sortedPlans = _shoppingOptimizationCoordinator.SortPlans(_currentMarketPlans, sortMode);
        
        foreach (var plan in sortedPlans)
        {
            var itemCard = CreateMarketCardFromTemplate(plan);
            itemCard.Tag = plan;
            MarketCards.Children.Insert(insertIndex++, itemCard);
        }
    }


    /// <summary>
    /// Creates a legacy (full-width) market card from DataTemplate for the Market Logistics tab.
    /// Uses ColorHelper for consistent accent theming. For split-pane collapsed cards,
    /// see CreateCollapsedCardFromTemplate which uses ICardFactory.
    /// </summary>
    /// <param name="plan">The shopping plan to display in the card.</param>
    /// <returns>A Border containing the data-bound card content.</returns>
    private Border CreateMarketCardFromTemplate(DetailedShoppingPlan plan)
    {
        // Use the DataTemplate defined in MarketCardTemplates.xaml
        // The template is automatically applied when the content is a MarketCardViewModel
        var viewModel = new MarketCardViewModel(plan);
        
        var border = new Border
        {
            Background = TryFindResource("Brush.Surface.Card.Market") as Brush
                ?? TryFindResource("Brush.Surface.Card") as Brush
                ?? TryFindResource("CardBackgroundBrush") as Brush
                ?? ColorHelper.GetMutedAccentBrush(),
            BorderBrush = TryFindResource("Brush.Border.Card.Market") as Brush
                ?? TryFindResource("Brush.Border.Default") as Brush,
            BorderThickness = new Thickness(1),
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

    /// <summary>
    /// Handles market sort selection change.
    /// Note: Event handler - minimal logic, delegates to ApplyMarketSortAndDisplay.
    /// MVVM: Not an ICommand candidate - SelectionChanged event, but could be replaced with binding.
    /// </summary>
    private void OnMarketSortChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyMarketSortAndDisplay();
    }
    
    /// <summary>
    /// Handles market mode (MinimizeTotalCost/MaximizeValue) selection change.
    /// Note: Event handler - minimal logic, triggers re-display.
    /// MVVM: Not an ICommand candidate - SelectionChanged event, but could be replaced with binding.
    /// </summary>
    private void OnMarketModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_currentPlan == null || _currentMarketPlans.Count == 0) return;
        
        ApplyMarketSortAndDisplay();
    }
    
    /// <summary>
    /// Gets the current recommendation mode from the combo box.
    /// Note: UI value extraction - could use binding with a converter.
    /// MVVM: Not an ICommand candidate - helper method for value extraction, not a command.
    /// </summary>
    private RecommendationMode GetCurrentRecommendationMode()
    {
        return MarketModeCombo.SelectedIndex switch
        {
            1 => RecommendationMode.MaximizeValue,
            _ => RecommendationMode.MinimizeTotalCost
        };
    }

    /// <summary>
    /// Refreshes market data by triggering price fetch.
    /// Note: MUST REMAIN in MainWindow - validates CurrentPlan existence, delegates to OnFetchPricesAsync.
    ///       Simple wrapper that belongs with related UI coordination code.
    /// </summary>
    /// <summary>
    /// Opens the log viewer window.
    /// Note: UI-specific window management - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - needs Owner assignment and window creation.
    /// </summary>
    private void OnViewLogs(object sender, RoutedEventArgs e)
    {
        var logWindow = new LogViewerWindow(_dialogFactory)
        {
            Owner = this
        };
        logWindow.Show();
    }

    /// <summary>
    /// Initiates application restart with state preservation.
    /// Note: MUST REMAIN in MainWindow - shows MessageBox (Owner=this), calls WatchStateCoordinator,
    ///       manipulates Application.Current.Shutdown, starts new process, manages application lifecycle.
    ///       Window-level operation not suitable for ViewModel.
    /// </summary>
    private async void OnRestartApp(object sender, RoutedEventArgs e)
    {
        if (!await _dialogs.ConfirmAsync(
            "Restart the application? Your current plan will be preserved.",
            "Restart App"))
        {
            return;
        }

        {
            var state = _watchStateCoordinator.PrepareWatchState(
                GetCurrentDataCenter(),
                GetCurrentWorld());
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

    /// <summary>
    /// Opens the options/settings dialog.
    /// Note: MUST REMAIN in MainWindow - creates OptionsWindow, sets Owner=this for dialog modality,
    ///       shows dialog and handles result. Requires window ownership context.
    /// </summary>
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

    /// <summary>
    /// Opens options dialog (alias for OnOptions).
    /// Note: Menu item handler - delegates to OnOptions.
    /// MVVM: Not an ICommand candidate - just calls another handler, use same command.
    /// </summary>
    private void OnDebugOptions(object sender, RoutedEventArgs e)
    {
        OnOptions(sender, e);
    }
    
    /// <summary>
    /// Opens the cache diagnostics window to view market cache database contents.
    /// </summary>
    private void OnCacheDiagnostics(object sender, RoutedEventArgs e)
    {
        var window = new CacheDiagnosticsWindow();
        window.Show();
    }
    
    /// <summary>
    /// Shows blacklisted worlds dialog and allows clearing the blacklist.
    /// Note: MUST REMAIN in MainWindow - shows MessageBox (Owner=this), displays blacklist status.
    ///       Delegates clearing to MainViewModel.ClearBlacklistCommand when user confirms.
    ///       Dialog presentation requires window context.
    /// </summary>
    private async void OnViewBlacklistedWorlds(object sender, RoutedEventArgs e)
    {
        var blacklisted = _blacklistService.GetBlacklistedWorlds();
        
        if (blacklisted.Count == 0)
        {
            await _dialogs.ShowInfoAsync(
                "No worlds are currently blacklisted.\n\n" +
                "Worlds can be blacklisted from the Market Analysis view when travel is prohibited.",
                "Blacklisted Worlds");
            return;
        }
        
        var worldList = string.Join("\n", blacklisted.Select(w => 
            $"\u2022 {w.WorldName} (expires in {w.ExpiresInDisplay})"));
        
        if (!await _dialogs.ConfirmAsync(
            $"Currently Blacklisted Worlds ({blacklisted.Count}):\n\n{worldList}\n\n" +
            "Click 'Yes' to clear all blacklisted worlds.",
            "Blacklisted Worlds"))
        {
            return;
        }

        _blacklistService.ClearBlacklist();
        StatusLabel.Text = "All blacklisted worlds cleared";
        
        if (IsMarketViewVisible())
        {
            PopulateProcurementPanel();
        }
    }
    
    /// <summary>
    /// Switches to the Recipe Planner tab.
    /// Note: UI-specific tab management - must remain in MainWindow.
    /// MVVM: ICommand candidate - SwitchTabCommand with "RecipePlanner" parameter.
    /// </summary>
    private void OnRecipePlannerTabClick(object sender, MouseButtonEventArgs e)
    {
        ActivateTab(MainTab.RecipePlanner);
    }
    
    /// <summary>
    /// Switches to the Market Analysis tab.
    /// Note: UI-specific tab management - must remain in MainWindow.
    /// MVVM: ICommand candidate - SwitchTabCommand with "MarketAnalysis" parameter.
    /// </summary>
    private void OnMarketAnalysisTabClick(object sender, MouseButtonEventArgs e)
    {
        ActivateTab(MainTab.MarketAnalysis);
    }
    
    /// <summary>
    /// Switches to the Procurement Planner tab.
    /// Note: UI-specific tab management - must remain in MainWindow.
    /// MVVM: ICommand candidate - SwitchTabCommand with "ProcurementPlanner" parameter.
    /// </summary>
    private void OnProcurementPlannerTabClick(object sender, MouseButtonEventArgs e)
    {
        ActivateTab(MainTab.ProcurementPlanner);
    }

    /// <summary>
    /// Centralized tab activation for shell navigation and side-panel visibility.
    /// </summary>
    private void ActivateTab(MainTab tab)
    {
        _activeTab = tab;

        SetTabActiveState(RecipePlannerTab, tab == MainTab.RecipePlanner);
        SetTabActiveState(MarketAnalysisTab, tab == MainTab.MarketAnalysis);
        SetTabActiveState(ProcurementPlannerTab, tab == MainTab.ProcurementPlanner);

        RecipePlannerContent.Visibility = tab == MainTab.RecipePlanner ? Visibility.Visible : Visibility.Collapsed;
        MarketAnalysisContent.Visibility = tab == MainTab.MarketAnalysis ? Visibility.Visible : Visibility.Collapsed;
        ProcurementPlannerContent.Visibility = tab == MainTab.ProcurementPlanner ? Visibility.Visible : Visibility.Collapsed;

        RecipePlannerLeftPanel.Visibility = tab == MainTab.RecipePlanner ? Visibility.Visible : Visibility.Collapsed;
        MarketAnalysisLeftPanel.Visibility = tab == MainTab.MarketAnalysis ? Visibility.Visible : Visibility.Collapsed;
        ProcurementPlannerLeftPanel.Visibility = tab == MainTab.ProcurementPlanner ? Visibility.Visible : Visibility.Collapsed;

        switch (tab)
        {
            case MainTab.RecipePlanner:
                MarketTotalCostText.Text = string.Empty;
                StatusLabel.Text = "Recipe Planner";
                break;
            case MainTab.MarketAnalysis:
                if (_currentPlan != null)
                {
                    PopulateProcurementPanel();
                }

                StatusLabel.Text = "Market Analysis";
                break;
            case MainTab.ProcurementPlanner:
                MarketTotalCostText.Text = string.Empty;
                if (_currentPlan != null)
                {
                    PopulateProcurementPlanSummary();
                }

                StatusLabel.Text = "Procurement Plan";
                break;
        }
    }
    
    /// <summary>
    /// Sets visual active state for a tab.
    /// Note: UI helper - could be a style trigger in XAML.
    /// MVVM: Not an ICommand candidate - internal helper method, not directly triggered by user.
    /// </summary>
    private void SetTabActive(Border tab)
    {
        tab.Background = (Brush)FindResource("Brush.Accent.Primary");
        ((TextBlock)tab.Child).Foreground = (Brush)FindResource("Brush.Text.OnAccent");
    }
    
    /// <summary>
    /// Sets visual inactive state for a tab.
    /// Note: UI helper - could be a style trigger in XAML.
    /// MVVM: Not an ICommand candidate - internal helper method, not directly triggered by user.
    /// </summary>
    private void SetTabInactive(Border tab)
    {
        tab.Background = Brushes.Transparent;
        ((TextBlock)tab.Child).Foreground = (Brush)FindResource("Brush.Accent.Primary");
    }

    private void SetTabActiveState(Border tab, bool isActive)
    {
        if (isActive)
        {
            SetTabActive(tab);
            return;
        }

        SetTabInactive(tab);
    }
    
    /// <summary>
    /// Checks if Market Analysis or Procurement Planner tab is visible.
    /// Note: UI state check - must remain in MainWindow.
    /// MVVM: Not an ICommand candidate - helper property, could be bound in XAML.
    /// </summary>
    private bool IsMarketViewVisible()
    {
        return _activeTab is MainTab.MarketAnalysis or MainTab.ProcurementPlanner;
    }
    
    /// <summary>
    /// Populates the procurement panel based on current view mode (split-pane or legacy).
    /// Delegates to view-specific methods which use ProcurementPanelBuilder for UI construction.
    /// Coordinates between _procurementBuilder state and appropriate population method.
    /// </summary>
    /// <remarks>
    /// Refactored: All direct UI construction moved to ProcurementPanelBuilder.
    /// This method now only handles view mode routing and coordination.
    /// </remarks>
    private void PopulateProcurementPanel()
    {
        _logger.LogInformation("[PopulateProcurementPanel] START - UseSplitPane={UseSplitPane}, _currentPlan={HasPlan}, _currentMarketPlans.Count={Count}",
            _procurementBuilder?.UseSplitPane ?? false, _currentPlan != null, _currentMarketPlans?.Count ?? 0);
        
        if (_procurementBuilder?.UseSplitPane == true)
        {
            ShowSplitPaneMarketView();
            PopulateProcurementPanelSplitPane();
        }
        else
        {
            ShowLegacyMarketView();
            PopulateProcurementPanelLegacy();
        }
        
        _logger.LogInformation("[PopulateProcurementPanel] END");
    }
    
    /// <summary>
    /// Shows the split-pane market view by toggling visibility of the two view containers.
    /// Split-pane view displays collapsed cards in a grid with an expandable details panel.
    /// </summary>
    private void ShowSplitPaneMarketView()
    {
        LegacyProcurementScrollViewer.Visibility = Visibility.Collapsed;
        SplitPaneMarketView.Visibility = Visibility.Visible;
    }
    
    /// <summary>
    /// Shows the legacy market view by toggling visibility of the two view containers.
    /// Legacy view displays full-width expandable cards in a vertical stack panel.
    /// </summary>
    private void ShowLegacyMarketView()
    {
        LegacyProcurementScrollViewer.Visibility = Visibility.Visible;
        SplitPaneMarketView.Visibility = Visibility.Collapsed;
    }
    
    /// <summary>
    /// Populates the split-pane procurement panel using ProcurementPanelBuilder.
    /// Routes to appropriate population method based on plan state:
    /// - No plan: Shows no-plan placeholder
    /// - Has market plans: Populates with market cards + summary
    /// - Has simple materials only: Shows refresh-needed placeholder
    /// </summary>
    private void PopulateProcurementPanelSplitPane()
    {
        _logger.LogInformation("[PopulateProcurementPanelSplitPane] START - _currentPlan={HasPlan}, _currentMarketPlans.Count={Count}, _currentMarketPlans.Any={HasPlans}",
            _currentPlan != null, _currentMarketPlans?.Count ?? 0, _currentMarketPlans?.Any() == true);
        
        _procurementBuilder?.ClearPanels();
        
        if (_currentPlan == null)
        {
            _logger.LogWarning("[PopulateProcurementPanelSplitPane] Showing NoPlan placeholder - _currentPlan is null");
            _procurementBuilder?.ShowNoPlanPlaceholderSplitPane();
            return;
        }
        
        if (_currentMarketPlans?.Any() == true)
        {
            _logger.LogInformation("[PopulateProcurementPanelSplitPane] Has market plans - calling PopulateSplitPaneWithMarketPlans");
            PopulateSplitPaneWithMarketPlans();
            PopulateProcurementPlanSummary();
            return;
        }
        
        _logger.LogWarning("[PopulateProcurementPanelSplitPane] No market plans - calling PopulateSplitPaneWithSimpleMaterials (shows 'No market data available')");
        PopulateSplitPaneWithSimpleMaterials();
    }
    
    /// <summary>
    /// Populates the legacy procurement panel using ProcurementPanelBuilder.
    /// Routes to appropriate population method based on plan state.
    /// When market plans exist, also populates the procurement plan summary panel.
    /// </summary>
    private void PopulateProcurementPanelLegacy()
    {
        _logger.LogInformation("[PopulateProcurementPanelLegacy] START - _currentPlan={HasPlan}, _currentMarketPlans.Count={Count}, _currentMarketPlans.Any={HasPlans}",
            _currentPlan != null, _currentMarketPlans?.Count ?? 0, _currentMarketPlans?.Any() == true);
        
        _procurementBuilder?.ClearPanels();
        
        if (_currentPlan == null)
        {
            _logger.LogWarning("[PopulateProcurementPanelLegacy] Showing NoPlan placeholder - _currentPlan is null");
            _procurementBuilder?.ShowNoPlanPlaceholderLegacy();
            return;
        }
        
        if (_currentMarketPlans?.Any() == true)
        {
            _logger.LogInformation("[PopulateProcurementPanelLegacy] Has market plans - calling PopulateProcurementWithMarketPlansLegacy");
            PopulateProcurementWithMarketPlansLegacy();
            PopulateProcurementPlanSummary();
            return;
        }
        
        _logger.LogWarning("[PopulateProcurementPanelLegacy] No market plans - calling PopulateProcurementWithSimpleMaterialsLegacy (shows 'No market data available')");
        PopulateProcurementWithSimpleMaterialsLegacy();
        
        _procurementBuilder?.ProcurementPlanPanel.Children.Clear();
        ProcurementPlanPanel.Children.Add(new TextBlock 
        { 
            Text = "Click 'Refresh Market Data' to generate an actionable procurement plan with world recommendations",
            Foreground = Brushes.Gray,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
    }
    
    /// <summary>
    /// Populates the procurement plan summary grouped by recommended world.
    /// Uses ProcurementPanelBuilder.CreateWorldSummaryPanel and CreateWorldGroupPanel
    /// for consistent styling. Groups items by their recommended purchase world for travel planning.
    /// </summary>
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
            _procurementBuilder?.ProcurementPlanPanel.Children.Add(new TextBlock 
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
            
            _procurementBuilder?.ProcurementPlanPanel.Children.Add(worldHeader);
            
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
                _procurementBuilder?.ProcurementPlanPanel.Children.Add(itemText);
            }
            
            _procurementBuilder?.ProcurementPlanPanel.Children.Add(new Border { Height = 12 });
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
        _procurementBuilder?.ProcurementPlanPanel.Children.Add(totalText);
    }

    
    /// <summary>
    /// Populates legacy view with market plan cards and summary panel.
    /// Uses ProcurementPanelBuilder for summary and CreateMarketCardFromTemplate for cards.
    /// Applies current sort selection before displaying plans.
    /// </summary>
    private void PopulateProcurementWithMarketPlansLegacy()
    {
        var grandTotal = _currentMarketPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
        var itemsWithOptions = _currentMarketPlans.Count(p => p.HasOptions);
        var itemsWithoutOptions = _currentMarketPlans.Count(p => !p.HasOptions);
        
        var summaryPanel = _procurementBuilder?.CreateLegacySummaryPanel(grandTotal, itemsWithOptions, itemsWithoutOptions);
        if (summaryPanel != null)
        {
            _procurementBuilder?.LegacyPanel.Children.Add(summaryPanel);
        }
        
        var sortMode = (ProcurementSortCombo?.SelectedIndex ?? 0) switch
        {
            1 => ShoppingPlanSortMode.Alphabetical,
            2 => ShoppingPlanSortMode.PriceHighToLow,
            _ => ShoppingPlanSortMode.RecommendedWorld
        };
        var sortedPlans = _shoppingOptimizationCoordinator.SortPlans(_currentMarketPlans, sortMode);
        
        foreach (var plan in sortedPlans)
        {
            var card = CreateMarketCardFromTemplate(plan);
            ProcurementPanel.Children.Add(card);
        }
    }
    
    /// <summary>
    /// Populates legacy view with simple materials when no market data is available.
    /// Uses ProcurementPanelBuilder.ShowRefreshNeededPlaceholderLegacy to prompt user
    /// to fetch market data for actionable procurement recommendations.
    /// </summary>
    private void PopulateProcurementWithSimpleMaterialsLegacy()
    {
        var materials = _currentPlan?.AggregatedMaterials;
        
        if (materials?.Any() != true)
        {
            _procurementBuilder?.LegacyPanel.Children.Add(new TextBlock 
            { 
                Text = "No materials to display",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }
        
        _procurementBuilder?.ShowRefreshNeededPlaceholderLegacy(materials.Count);
    }
    
    /// <summary>
    /// Populates split-pane view with collapsed market plan cards.
    /// Uses ProcurementPanelBuilder for panel management and CreateCollapsedCardFromTemplate
    /// (via ICardFactory) for card creation. Restores expanded state if previously selected.
    /// Applies current sort selection before displaying cards.
    /// </summary>
    private void PopulateSplitPaneWithMarketPlans()
    {
        _procurementBuilder?.SplitPaneCardsGrid.Children.Clear();
        
        var grandTotal = _currentMarketPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
        var itemsWithOptions = _currentMarketPlans.Count(p => p.HasOptions);
        
        _procurementBuilder?.UpdateSplitPaneTotal(grandTotal, itemsWithOptions);
        
        var sortMode = (ProcurementSortCombo?.SelectedIndex ?? 0) switch
        {
            1 => ShoppingPlanSortMode.Alphabetical,
            2 => ShoppingPlanSortMode.PriceHighToLow,
            _ => ShoppingPlanSortMode.RecommendedWorld
        };
        var sortedPlans = _shoppingOptimizationCoordinator.SortPlans(_currentMarketPlans, sortMode);
        
        foreach (var plan in sortedPlans)
        {
            var card = CreateCollapsedCardFromTemplate(plan);
            _procurementBuilder?.SplitPaneCardsGrid.Children.Add(card);
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
                _procurementBuilder?.SetExpandedPanelVisibility(false);
            }
        }
    }
    
    /// <summary>
    /// Populates split-pane view with simple materials placeholder when no market data exists.
    /// Uses ProcurementPanelBuilder.ShowRefreshNeededPlaceholderSplitPane to display
    /// item count and prompt for market data refresh.
    /// </summary>
    private void PopulateSplitPaneWithSimpleMaterials()
    {
        _procurementBuilder?.ClearExpandedPanel();
        _procurementBuilder?.SetExpandedPanelVisibility(false);
        
        var materials = _currentPlan?.AggregatedMaterials;
        
        if (materials?.Any() != true)
        {
            _procurementBuilder?.SplitPaneCardsGrid.Children.Add(new TextBlock 
            { 
                Text = "No materials to display",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 40, 0, 0)
            });
            return;
        }
        
        _procurementBuilder?.ShowRefreshNeededPlaceholderSplitPane(materials.Count);
    }
    
    /// <summary>
    /// Creates a collapsed market card for split-pane view using ICardFactory.
    /// Delegates card creation to _cardFactory.CreateCollapsedMarketCard for consistent
    /// styling and click handling across the application.
    /// </summary>
    /// <param name="plan">The shopping plan to display in the card.</param>
    /// <returns>A Border configured as a clickable collapsed card.</returns>
    private Border CreateCollapsedCardFromTemplate(DetailedShoppingPlan plan)
    {
        var isExpanded = _expandedSplitPanePlan?.ItemId == plan.ItemId;
        var viewModel = new MarketCardViewModel(plan);
        
        return _cardFactory.CreateCollapsedMarketCard(
            viewModel, 
            isExpanded, 
            () => OnCollapsedCardClick(plan));
    }
    
    /// <summary>
    /// Handles click on a collapsed market card in split-pane view.
    /// Note: MUST REMAIN in MainWindow - directly manipulates _expandedSplitPanePlan,
    ///       SplitPaneExpandedPanel.Visibility, and calls PopulateSplitPaneWithMarketPlans.
    ///       Pure UI state management not suitable for ViewModel.
    /// </summary>
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
    
    /// <summary>
    /// Builds the expanded details panel for a selected market plan in split-pane view.
    /// Uses ProcurementPanelBuilder for panel management. Creates ExpandedPanelViewModel
    /// with close handler that updates expansion state and refreshes the card display.
    /// </summary>
    /// <param name="plan">The shopping plan to display expanded details for.</param>
    private void BuildExpandedPanel(DetailedShoppingPlan plan)
    {
        _procurementBuilder?.ClearExpandedPanel();
        _procurementBuilder?.SetExpandedPanelVisibility(true);
        
        var viewModel = new ExpandedPanelViewModel(plan);
        viewModel.CloseRequested += () =>
        {
            _expandedSplitPanePlan = null;
            _procurementBuilder?.SetExpandedPanelVisibility(false);
            PopulateSplitPaneWithMarketPlans();
        };
        
        var contentControl = new ContentControl
        {
            Content = viewModel
        };
        
        _procurementBuilder?.AddToExpandedPanel(contentControl);
    }
    
    private async void ShowBlacklistConfirmationDialog(string worldName, int worldId)
    {
        if (!await _dialogs.ConfirmAsync(
            $"Blacklist {worldName}?\n\n" +
            "This world will be excluded from acquisition recommendations for 30 minutes. " +
            "You can still manually select this world if needed.\n\n" +
            "Use this when a world is currently travel-prohibited (at capacity).",
            "Confirm World Blacklist"))
        {
            return;
        }

        {
            _blacklistService.AddToBlacklist(worldId, worldName, "Travel prohibited - user blacklisted");
            StatusLabel.Text = $"{worldName} blacklisted for 30 minutes";
            
            if (IsMarketViewVisible())
            {
                PopulateProcurementPanel();
            }
        }
    }
    
    /// <summary>
    /// Handles procurement sort selection change.
    /// Note: Event handler - triggers panel refresh.
    /// MVVM: Not an ICommand candidate - SelectionChanged event, but could be replaced with binding.
    /// </summary>
    private void OnProcurementSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentPlan == null)
            return;
            
        if (IsMarketViewVisible() && _currentMarketPlans?.Any() == true)
        {
            PopulateProcurementPanel();
        }
    }
    
    /// <summary>
    /// Handles procurement mode (MinimizeTotalCost/MaximizeValue) change.
    /// Note: Event handler - saves setting and refreshes panel.
    /// MVVM: Not an ICommand candidate - SelectionChanged event, but could be replaced with binding.
    /// </summary>
    private void OnProcurementModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // Skip if service not ready or if this is initial load (AddedItems is empty during programmatic set)
        if (_settingsService == null || e.AddedItems.Count == 0)
            return;
            
        if (ProcurementModeCombo.SelectedIndex >= 0)
        {
            var mode = ProcurementModeCombo.SelectedIndex == 1 ? "MaximizeValue" : "MinimizeTotalCost";
            _logger.LogInformation("[OnProcurementModeChanged] User changed mode to '{Mode}', saving setting", mode);
            _settingsService.Set("planning.default_recommendation_mode", mode);
            
            if (IsMarketViewVisible() && _currentPlan != null)
            {
                PopulateProcurementPanel();
            }
        }
    }

    /// <summary>
    /// Resets the application state for a new plan.
    /// Note: UI coordination - delegates to RecipePlannerViewModel.ClearCommand.
    /// </summary>
    private void OnNewPlan(object sender, RoutedEventArgs e)
    {
        _recipeVm.ClearCommand.Execute(null);
        
        // Additional UI cleanup
        ProjectList.ItemsSource = null;
        RecipePlanPanel?.Children.Clear();
        
        BuildPlanButton.IsEnabled = false;
        BrowsePlanButton.IsEnabled = false;
        
        StatusLabel.Text = "New plan created. Add items to get started.";
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

    /// <summary>
    /// Validates quantity input to allow only digits.
    /// Note: Input validation - could be an attached behavior.
    /// MVVM: Not an ICommand candidate - PreviewTextInput event, use attached behavior instead.
    /// </summary>
    private void OnQuantityPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    /// <summary>
    /// Selects all text when quantity field gets focus.
    /// Note: UX enhancement - could be an attached behavior.
    /// MVVM: Not an ICommand candidate - GotFocus event, use attached behavior instead.
    /// </summary>
    private void OnQuantityGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    /// <summary>
    /// Handles quantity change in project items list.
    /// Note: UI event handler - updates ViewModel and refreshes display.
    /// MVVM: Not an ICommand candidate - LostFocus event, two-way binding would replace this.
    /// </summary>
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

    /// <summary>
    /// Removes an item from the project list.
    /// Note: UI event handler - delegates to RecipePlannerViewModel.RemoveProjectItemCommand.
    /// </summary>
    private void OnRemoveProjectItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            var listBoxItem = button.FindParent<ListBoxItem>();
            if (listBoxItem?.DataContext is ProjectItem projectItem)
            {
                // Use ViewModel command
                _recipeVm.RemoveProjectItemCommand.Execute(projectItem.Id);
                
                StatusLabel.Text = $"Removed {projectItem.Name} from project";
                
                BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
                BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
                
                ProjectList.ItemsSource = null;
                ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
                UpdateQuickViewCount();
            }
        }
    }

    /// <summary>
    /// Updates the quick view count display in the project panel.
    /// Note: UI-specific - updates TextBlock directly.
    /// MVVM: Not an ICommand candidate - helper method, could be a computed property binding.
    /// </summary>
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

    /// <summary>
    /// Opens the Project Items management window.
    /// Note: MUST REMAIN in MainWindow - creates ProjectItemsWindow, sets Owner=this,
    ///       handles callbacks (onItemsChanged, onAddItem), updates ProjectList.ItemsSource.
    ///       Complex window management with callbacks requires MainWindow context.
    /// </summary>
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

    
    /// <summary>
    /// Gets the currently selected data center.
    /// Note: UI value extraction - simple property access.
    /// MVVM: Not an ICommand candidate - helper property, could be bound in XAML.
    /// MVVM: Not an ICommand candidate - helper property, could be bound in XAML.
    /// </summary>
    public string? GetCurrentDataCenter()
    {
        return DcCombo.SelectedItem as string;
    }
    
    /// <summary>
    /// Gets the currently selected world.
    /// Note: UI value extraction - simple property access.
    /// MVVM: Not an ICommand candidate - helper property, could be bound in XAML.
    /// </summary>
    public string? GetCurrentWorld()
    {
        return WorldCombo.SelectedItem as string;
    }
    
    /// <summary>
    /// Prompts user to reanalyze cached market data after watch state restore.
    /// Note: UI coordination - displays informational panel.
    /// MVVM: Not an ICommand candidate - dialog owner must be the window (this).
    /// </summary>
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
            
            var infoPanel = _infoPanelBuilder.CreateCacheAvailablePanel(
                cachedCount, 
                itemIds.Count, 
                "Click 'Refresh Market Data' above to re-analyze using cached data.");
            
            ProcurementPanel.Children.Add(infoPanel);
            
            StatusLabel.Text = $"[Watch] State restored. {cachedCount} items have cached market data.";
        }
    }

    /// <summary>
    /// Restores application state from a WatchState after app restart.
    /// Note: Uses WatchStateCoordinator - refactor successful.
    /// MVVM: Not an ICommand candidate - internal coordination method, triggered by watch restore.
    /// </summary>
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
        
        _recipeVm.ProjectItems = _watchStateCoordinator.RestoreProjectItemsFromPlan(state.CurrentPlan);
        
        ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
        UpdateQuickViewCount();
        BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        
        if (_currentPlan != null)
        {
            DisplayPlanInTreeView(_currentPlan);
        }
        UpdateBuildPlanButtonText();
        PopulateShoppingList();
        ProcurementRefreshButton.IsEnabled = true;
        
        StatusLabel.Text = "[Watch] State restored from reload";
        
        await PromptToReanalyzeCachedMarketDataAsync();
    }

}
