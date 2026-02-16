using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.ViewModels;

/// <summary>
/// ViewModel for the Market Analysis panel (Market Analysis tab in MainWindow).
///
/// DATA FLOW:
/// 1. Input from Recipe Planner:
///    - Receives CraftingPlan with AggregatedMaterials
///    - Separates market items, vendor items, and untradeable items
///    - Vendor items are displayed separately (no market lookup needed)
///
/// 2. Market Data Fetching (RefreshPricesAsync):
///    - Calls MarketLogisticsCoordinator.CalculateMarketLogisticsAsync()
///    - Coordinator handles:
///      a. IMarketCacheService.EnsurePopulatedAsync() - fetch/cache market data
///      b. MarketShoppingService.CalculateDetailedShoppingPlansAsync() - analyze
///    - Returns List&lt;DetailedShoppingPlan&gt; with world recommendations
///
/// 3. ViewModel Wrapping:
///    - Each DetailedShoppingPlan wrapped in ShoppingPlanViewModel
///    - Each WorldShoppingSummary wrapped in WorldOptionViewModel
///    - Added to ShoppingPlans ObservableCollection
///
/// 4. World Grouping (RegroupByWorld):
///    - Groups ShoppingPlans by RecommendedWorld.WorldName
///    - Creates ProcurementWorldViewModel for each world
///    - Vendor items grouped in special "Vendor" world
///    - Used for Procurement Planner tab display
///
/// 5. User Interactions:
///    - Change sort order (Recommended, Alphabetical, Price)
///    - Toggle search all NA DCs
///    - Change recommendation mode (Cost vs Value)
///    - Refresh prices (re-fetches market data)
///
/// UI BINDINGS:
/// - ShoppingPlans → Market cards grid (MarketCardTemplates.xaml)
/// - GroupedByWorld → Procurement world cards
/// - StatusMessage → Progress/status display
/// - IsLoading → Loading indicator
///
/// STATE MANAGEMENT:
/// - ShoppingPlans persists when switching tabs
/// - Cleared when new plan is loaded
/// - Saved with plan via CraftingPlan.SavedMarketPlans
/// </summary>
public partial class MarketAnalysisViewModel : ViewModelBase
{
    private ObservableCollection<ShoppingPlanViewModel> _shoppingPlans = new();
    private ObservableCollection<ProcurementWorldViewModel> _groupedByWorld = new();
    private ObservableCollection<ProcurementItemViewModel> _procurementItems = new();
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private MarketSortOption _currentSort = MarketSortOption.RecommendedWorld;
    private RecommendationMode _recommendationMode = RecommendationMode.MinimizeTotalCost;
    private bool _searchAllNaDcs;
    private readonly PriceCheckService _priceCheckService;
    private readonly RecipeCalculationService _recipeCalcService;
    private readonly ILogger<MarketAnalysisViewModel>? _logger;

    public MarketAnalysisViewModel(
        PriceCheckService priceCheckService,
        RecipeCalculationService recipeCalcService,
        ILogger<MarketAnalysisViewModel>? logger = null)
    {
        _priceCheckService = priceCheckService;
        _recipeCalcService = recipeCalcService;
        _logger = logger;
        _shoppingPlans.CollectionChanged += OnShoppingPlansCollectionChanged;
    }

    private void OnShoppingPlansCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ShoppingPlans));
        OnPropertyChanged(nameof(TotalCost));
        OnPropertyChanged(nameof(HasData));
        RegroupByWorld();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shoppingPlans.CollectionChanged -= OnShoppingPlansCollectionChanged;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Shopping plans for each material needed.
    /// </summary>
    public ObservableCollection<ShoppingPlanViewModel> ShoppingPlans
    {
        get => _shoppingPlans;
        set
        {
            _shoppingPlans = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalCost));
            OnPropertyChanged(nameof(HasData));
            RegroupByWorld();
        }
    }

    /// <summary>
    /// Shopping plans grouped by recommended world for efficient procurement.
    /// </summary>
    public ObservableCollection<ProcurementWorldViewModel> GroupedByWorld
    {
        get => _groupedByWorld;
        private set
        {
            _groupedByWorld = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Flat list of procurement items (for simple view without market data).
    /// </summary>
    public ObservableCollection<ProcurementItemViewModel> ProcurementItems
    {
        get => _procurementItems;
        set
        {
            _procurementItems = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Total cost across all shopping plans.
    /// </summary>
    public long TotalCost => _shoppingPlans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);

    /// <summary>
    /// Whether any shopping data is available.
    /// </summary>
    public bool HasData => _shoppingPlans.Any();

    /// <summary>
    /// Number of items with viable market options.
    /// </summary>
    public int ItemsWithOptions => _shoppingPlans.Count(p => p.HasOptions);

    /// <summary>
    /// Number of items without market options.
    /// </summary>
    public int ItemsWithoutOptions => _shoppingPlans.Count(p => !p.HasOptions);

    /// <summary>
    /// Current status message.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether a loading operation is in progress.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            _isLoading = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Current sort option for shopping plans.
    /// </summary>
    public MarketSortOption CurrentSort
    {
        get => _currentSort;
        set
        {
            _currentSort = value;
            OnPropertyChanged();
            ApplySort();
        }
    }

    /// <summary>
    /// Current recommendation mode.
    /// </summary>
    public RecommendationMode RecommendationMode
    {
        get => _recommendationMode;
        set
        {
            _recommendationMode = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether to search all NA data centers.
    /// </summary>
    public bool SearchAllNaDcs
    {
        get => _searchAllNaDcs;
        set
        {
            _searchAllNaDcs = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Event raised when shopping plans change.
    /// </summary>
    public event EventHandler? PlansChanged;

    /// <summary>
    /// Sets the shopping plans from service results.
    /// Must be called on UI thread or will be marshaled to UI thread.
    /// </summary>
    public void SetShoppingPlans(List<DetailedShoppingPlan> plans)
    {
        _logger?.LogInformation("[SetShoppingPlans] START - Received {Count} plans, Thread={ThreadId}", 
            plans.Count, Environment.CurrentManagedThreadId);
        
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _logger?.LogInformation("[SetShoppingPlans] Marshaling to UI thread...");
            dispatcher.Invoke(() => SetShoppingPlans(plans));
            return;
        }

        _logger?.LogInformation("[SetShoppingPlans] Clearing existing plans and adding {Count} new plans...", plans.Count);
        _shoppingPlans.Clear();
        foreach (var plan in plans)
        {
            _shoppingPlans.Add(new ShoppingPlanViewModel(plan));
        }
        _logger?.LogInformation("[SetShoppingPlans] Added {Count} plans, calling ApplySort...", _shoppingPlans.Count);
        ApplySort();
        _logger?.LogInformation("[SetShoppingPlans] Firing PlansChanged event...");
        PlansChanged?.Invoke(this, EventArgs.Empty);
        _logger?.LogInformation("[SetShoppingPlans] END - _shoppingPlans.Count={Count}, HasData={HasData}", 
            _shoppingPlans.Count, HasData);
    }

    /// <summary>
    /// Sets procurement items from aggregated materials (when no market data).
    /// </summary>
    public void SetProcurementItems(List<MaterialAggregate> materials)
    {
        _procurementItems.Clear();
        foreach (var material in materials.OrderBy(m => m.Name))
        {
            _procurementItems.Add(new ProcurementItemViewModel
            {
                ItemId = material.ItemId,
                Name = material.Name,
                Quantity = material.TotalQuantity,
                RequiresHq = material.RequiresHq
            });
        }
    }

    /// <summary>
    /// Clears all market data.
    /// </summary>
    [RelayCommand]
    public void Clear()
    {
        _shoppingPlans.Clear();
        _groupedByWorld.Clear();
        _procurementItems.Clear();
    }

    /// <summary>
    /// Applies the current sort to shopping plans.
    /// </summary>
    public void ApplySort()
    {
        var sorted = _currentSort switch
        {
            MarketSortOption.RecommendedWorld => _shoppingPlans.OrderBy(p => p.RecommendedWorld?.WorldName ?? "ZZZ"),
            MarketSortOption.Alphabetical => _shoppingPlans.OrderBy(p => p.Name),
            MarketSortOption.PriceHighToLow => _shoppingPlans.OrderByDescending(p => p.RecommendedWorld?.TotalCost ?? 0),
            _ => _shoppingPlans.OrderBy(p => p.Name)
        };

        // Re-add in sorted order
        var list = sorted.ToList();
        _shoppingPlans.Clear();
        foreach (var item in list)
        {
            _shoppingPlans.Add(item);
        }
    }

    /// <summary>
    /// Gets the shopping plans for watch state saving.
    /// </summary>
    public List<DetailedShoppingPlan> GetPlansForWatch()
    {
        return _shoppingPlans.Select(vm => vm.Plan).ToList();
    }

    private void RegroupByWorld()
    {
        var groups = _shoppingPlans
            .Where(p => p.RecommendedWorld != null)
            .GroupBy(p => p.RecommendedWorld!.WorldName)
            .OrderBy(g => g.Key)
            .Select(g => new ProcurementWorldViewModel
            {
                WorldName = g.Key,
                Items = new ObservableCollection<ShoppingPlanViewModel>(g),
                TotalCost = g.Sum(i => i.RecommendedWorld?.TotalCost ?? 0),
                IsHomeWorld = g.First().RecommendedWorld?.IsHomeWorld ?? false
            })
            .ToList();

        GroupedByWorld = new ObservableCollection<ProcurementWorldViewModel>(groups);
    }

}

/// <summary>
/// ViewModel wrapper for a DetailedShoppingPlan.
/// </summary>
public partial class ShoppingPlanViewModel : ObservableObject
{
    private readonly DetailedShoppingPlan _plan;

    public ShoppingPlanViewModel(DetailedShoppingPlan plan)
    {
        _plan = plan;
    }

    public DetailedShoppingPlan Plan => _plan;

    public int ItemId => _plan.ItemId;
    public string Name => _plan.Name;
    public int QuantityNeeded => _plan.QuantityNeeded;
    public decimal DCAveragePrice => _plan.DCAveragePrice;
    public WorldShoppingSummary? RecommendedWorld => _plan.RecommendedWorld;
    public List<WorldShoppingSummary> WorldOptions => _plan.WorldOptions;
    public bool HasOptions => _plan.HasOptions;
    public bool HasHqData => _plan.HasHqData;
    public decimal? HQAveragePrice => _plan.HQAveragePrice;
    public string? Error => _plan.Error;
}

/// <summary>
/// ViewModel for procurement items grouped by world.
/// </summary>
public partial class ProcurementWorldViewModel : ObservableObject
{
    [ObservableProperty]
    private string _worldName = string.Empty;

    [ObservableProperty]
    private long _totalCost;

    [ObservableProperty]
    private bool _isHomeWorld;

    [ObservableProperty]
    private ObservableCollection<ShoppingPlanViewModel> _items = new();
}

/// <summary>
/// ViewModel for simple procurement items (without market data).
/// </summary>
public partial class ProcurementItemViewModel : ObservableObject
{
    [ObservableProperty]
    private int _itemId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _quantity;

    [ObservableProperty]
    private bool _requiresHq;
}

/// <summary>
/// Sort options for market analysis.
/// </summary>
public enum MarketSortOption
{
    RecommendedWorld,
    Alphabetical,
    PriceHighToLow
}
