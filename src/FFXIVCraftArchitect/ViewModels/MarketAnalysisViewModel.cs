using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.ViewModels;

/// <summary>
/// ViewModel for the Market Analysis panel.
/// Manages shopping plans, recommendations, and procurement grouping.
/// </summary>
public class MarketAnalysisViewModel : ViewModelBase
{
    private ObservableCollection<ShoppingPlanViewModel> _shoppingPlans = new();
    private ObservableCollection<ProcurementWorldViewModel> _groupedByWorld = new();
    private ObservableCollection<ProcurementItemViewModel> _procurementItems = new();
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private MarketSortOption _currentSort = MarketSortOption.RecommendedWorld;
    private RecommendationMode _recommendationMode = RecommendationMode.MinimizeTotalCost;
    private bool _searchAllNaDcs;

    public MarketAnalysisViewModel()
    {
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
    /// </summary>
    public void SetShoppingPlans(List<DetailedShoppingPlan> plans)
    {
        _shoppingPlans.Clear();
        foreach (var plan in plans)
        {
            _shoppingPlans.Add(new ShoppingPlanViewModel(plan));
        }
        ApplySort();
        PlansChanged?.Invoke(this, EventArgs.Empty);
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
public class ShoppingPlanViewModel : INotifyPropertyChanged
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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// ViewModel for procurement items grouped by world.
/// </summary>
public class ProcurementWorldViewModel : INotifyPropertyChanged
{
    private string _worldName = string.Empty;
    private long _totalCost;
    private bool _isHomeWorld;
    private ObservableCollection<ShoppingPlanViewModel> _items = new();

    public string WorldName
    {
        get => _worldName;
        set { _worldName = value; OnPropertyChanged(); }
    }

    public long TotalCost
    {
        get => _totalCost;
        set { _totalCost = value; OnPropertyChanged(); }
    }

    public bool IsHomeWorld
    {
        get => _isHomeWorld;
        set { _isHomeWorld = value; OnPropertyChanged(); }
    }

    public ObservableCollection<ShoppingPlanViewModel> Items
    {
        get => _items;
        set { _items = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// ViewModel for simple procurement items (without market data).
/// </summary>
public class ProcurementItemViewModel : INotifyPropertyChanged
{
    private int _itemId;
    private string _name = string.Empty;
    private int _quantity;
    private bool _requiresHq;

    public int ItemId
    {
        get => _itemId;
        set { _itemId = value; OnPropertyChanged(); }
    }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public int Quantity
    {
        get => _quantity;
        set { _quantity = value; OnPropertyChanged(); }
    }

    public bool RequiresHq
    {
        get => _requiresHq;
        set { _requiresHq = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
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
