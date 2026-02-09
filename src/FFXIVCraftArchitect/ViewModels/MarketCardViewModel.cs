using System.Collections.ObjectModel;
using System.Windows.Input;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Infrastructure.Commands;

namespace FFXIVCraftArchitect.ViewModels;

/// <summary>
/// ViewModel for an expandable market card displaying shopping plan information.
/// </summary>
public class MarketCardViewModel : ViewModelBase
{
    private bool _isExpanded = true;
    private ObservableCollection<WorldOptionViewModel> _worldOptions = new();
    private readonly DetailedShoppingPlan _plan;

    public MarketCardViewModel(DetailedShoppingPlan plan)
    {
        _plan = plan;
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        
        // Initialize world options
        RefreshWorldOptions();
    }

    /// <summary>
    /// The underlying shopping plan.
    /// </summary>
    public DetailedShoppingPlan Plan => _plan;

    /// <summary>
    /// Item ID.
    /// </summary>
    public int ItemId => _plan.ItemId;

    /// <summary>
    /// Item name.
    /// </summary>
    public string Name => _plan.Name;

    /// <summary>
    /// Quantity needed.
    /// </summary>
    public int QuantityNeeded => _plan.QuantityNeeded;

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
    /// Command to toggle expand/collapse state.
    /// </summary>
    public ICommand ToggleExpandCommand { get; }

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
    }
}
