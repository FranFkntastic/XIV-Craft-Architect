using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Coordinators;

namespace FFXIV_Craft_Architect.ViewModels;

/// <summary>
/// ViewModel for an expandable market card displaying shopping plan information.
/// </summary>
public class MarketCardViewModel : ViewModelBase
{
    private static readonly PurchaseSummaryService _summaryService = new();
    
    private bool _isExpanded = true;
    private bool _isSelected;
    private ObservableCollection<WorldOptionViewModel> _worldOptions = new();
    private ObservableCollection<SplitWorldCardViewModel> _splitWorlds = new();
    private readonly DetailedShoppingPlan _plan;
    private readonly IMarketLogisticsCoordinator? _coordinator;
    private readonly PurchaseSummary _summary;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarketCardViewModel"/> class.
    /// </summary>
    /// <param name="plan">The shopping plan to display.</param>
    /// <param name="coordinator">Optional coordinator for selection management. If provided, selection will call SelectItem().</param>
    public MarketCardViewModel(DetailedShoppingPlan plan, IMarketLogisticsCoordinator? coordinator = null)
    {
        _plan = plan;
        _coordinator = coordinator;
        _summary = _summaryService.CreateSummary(plan);
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        SelectCommand = new RelayCommand(OnSelect);
        
        RefreshWorldOptions();
    }
    
    private void OnSelect()
    {
        if (_coordinator != null)
        {
            _coordinator.SelectItem(_plan.ItemId);
        }
    }

    /// <summary>
    /// The underlying shopping plan.
    /// </summary>
    public DetailedShoppingPlan Plan => _plan;
    
    /// <summary>
    /// Centralized purchase summary with actual quantities.
    /// </summary>
    public PurchaseSummary Summary => _summary;

    /// <summary>
    /// Item ID.
    /// </summary>
    public int ItemId => _plan.ItemId;

    /// <summary>
    /// Item name.
    /// </summary>
    public string Name => _plan.Name;

    /// <summary>
    /// Quantity needed for crafting (idealized).
    /// </summary>
    public int QuantityNeeded => _plan.QuantityNeeded;
    
    /// <summary>
    /// Actual quantity to purchase (from listings).
    /// </summary>
    public int QuantityToPurchase => _summary.QuantityToPurchase;
    
    /// <summary>
    /// Excess items due to full stacks.
    /// </summary>
    public int ExcessQuantity => _summary.ExcessQuantity;
    
    /// <summary>
    /// Whether there are excess items.
    /// </summary>
    public bool HasExcess => _summary.HasExcess;

    /// <summary>
    /// DC average price.
    /// </summary>
    public decimal DCAveragePrice => _plan.DCAveragePrice;

    /// <summary>
    /// Whether the card is expanded.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Whether the card is selected in split-pane mode.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Error message if any.
    /// </summary>
    public string? Error => _plan.Error;

    /// <summary>
    /// Whether the plan has any world options.
    /// </summary>
    public bool HasOptions => _plan.HasOptions;

    /// <summary>
    /// Number of world options.
    /// </summary>
    public int WorldOptionsCount => _plan.WorldOptions.Count;

    /// <summary>
    /// The recommended world for this item.
    /// </summary>
    public WorldOptionViewModel? RecommendedWorld => 
        _plan.RecommendedWorld != null 
            ? new WorldOptionViewModel(_plan.RecommendedWorld, isRecommended: true) 
            : null;

    /// <summary>
    /// All world options sorted by recommendation.
    /// </summary>
    public ObservableCollection<WorldOptionViewModel> WorldOptions
    {
        get => _worldOptions;
        private set => SetProperty(ref _worldOptions, value);
    }

    /// <summary>
    /// Split-world recommendations with structured data-center display.
    /// </summary>
    public ObservableCollection<SplitWorldCardViewModel> SplitWorlds
    {
        get => _splitWorlds;
        private set => SetProperty(ref _splitWorlds, value);
    }

    /// <summary>
    /// Command to toggle expand/collapse state.
    /// </summary>
    public ICommand ToggleExpandCommand { get; }

    /// <summary>
    /// Command to select this card in split-pane mode.
    /// </summary>
    public ICommand SelectCommand { get; }

    /// <summary>
    /// Whether this item requires a multi-world split purchase.
    /// </summary>
    public bool RequiresSplitPurchase => HasSplitRecommendation(_plan);

    public bool IsVendorOnly => string.Equals(
        _plan.RecommendedWorld?.WorldName,
        MarketShoppingConstants.VendorWorldName,
        StringComparison.OrdinalIgnoreCase);

    public string RecommendedSourceName => IsVendorOnly
        ? _plan.RecommendedWorld?.VendorName ?? MarketShoppingConstants.VendorWorldName
        : _plan.RecommendedWorld?.WorldName ?? string.Empty;

    /// <summary>
    /// Refreshes the world options collection from the underlying plan.
    /// </summary>
    public void RefreshWorldOptions()
    {
        var sortedWorlds = _plan.WorldOptions
            .OrderByDescending(w => w.IsHomeWorld)
            .ThenBy(w => w.IsBlacklisted && !w.IsHomeWorld)
            .ThenBy(w => w.IsCongested && !w.IsHomeWorld)
            .ThenBy(w => w.ValueScore)
            .ThenBy(w => w.TotalCost)
            .ToList();

        _worldOptions.Clear();
        foreach (var world in sortedWorlds)
        {
            var isRecommended = world == _plan.RecommendedWorld;
            _worldOptions.Add(new WorldOptionViewModel(world, isRecommended));
        }

        _splitWorlds.Clear();
        if (_plan.RecommendedSplit?.Any() == true)
        {
            foreach (var split in _plan.RecommendedSplit)
            {
                _splitWorlds.Add(new SplitWorldCardViewModel(split, _plan.QuantityNeeded));
            }
        }
    }

    private static bool HasSplitRecommendation(DetailedShoppingPlan plan)
    {
        return plan.RecommendedSplit?.Any() == true &&
            (plan.RequiresSplitPurchase || plan.RecommendedWorld == null);
    }
}
