using System.Collections.ObjectModel;
using System.Windows.Input;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Infrastructure.Commands;

namespace FFXIVCraftArchitect.ViewModels;

/// <summary>
/// ViewModel for the expanded panel in split-pane market view.
/// </summary>
public class ExpandedPanelViewModel : ViewModelBase
{
    private readonly DetailedShoppingPlan _plan;
    private ObservableCollection<ExpandedWorldViewModel> _worldOptions = new();

    public ExpandedPanelViewModel(DetailedShoppingPlan plan)
    {
        _plan = plan;
        CloseCommand = new RelayCommand(OnClose);
        
        // Initialize world options with proper sorting
        RefreshWorldOptions();
    }

    /// <summary>
    /// The underlying shopping plan.
    /// </summary>
    public DetailedShoppingPlan Plan => _plan;

    /// <summary>
    /// Plan name with quantity.
    /// </summary>
    public string HeaderText => $"{_plan.Name} ×{_plan.QuantityNeeded}";

    /// <summary>
    /// DC Average price display.
    /// </summary>
    public string DCAverageText => $"Data Center Average: {_plan.DCAveragePrice:N0}g";

    /// <summary>
    /// Whether the plan has world options.
    /// </summary>
    public bool HasOptions => _plan.HasOptions;

    /// <summary>
    /// Number of world options.
    /// </summary>
    public int WorldOptionsCount => _plan.WorldOptions.Count;

    /// <summary>
    /// Options header text.
    /// </summary>
    public string OptionsHeaderText => $"All Worlds ({_plan.WorldOptions.Count} options):";

    /// <summary>
    /// Error message if any.
    /// </summary>
    public string? Error => _plan.Error;

    /// <summary>
    /// Whether there's an error.
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(_plan.Error);

    /// <summary>
    /// Whether to show the "no data" message.
    /// </summary>
    public bool ShowNoData => !_plan.HasOptions && string.IsNullOrEmpty(_plan.Error);

    /// <summary>
    /// All world options sorted by recommendation.
    /// </summary>
    public ObservableCollection<ExpandedWorldViewModel> WorldOptions
    {
        get => _worldOptions;
        private set => SetProperty(ref _worldOptions, value);
    }

    /// <summary>
    /// Command to close the expanded panel.
    /// </summary>
    public ICommand CloseCommand { get; }

    /// <summary>
    /// Event raised when the panel should be closed.
    /// </summary>
    public event Action? CloseRequested;

    private void OnClose()
    {
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Refreshes the world options collection from the underlying plan.
    /// </summary>
    private void RefreshWorldOptions()
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
            _worldOptions.Add(new ExpandedWorldViewModel(world, isRecommended));
        }
    }
}

/// <summary>
/// ViewModel for a world option within the expanded panel.
/// </summary>
public class ExpandedWorldViewModel : ViewModelBase
{
    private readonly WorldShoppingSummary _world;
    private readonly bool _isRecommended;

    public ExpandedWorldViewModel(WorldShoppingSummary world, bool isRecommended)
    {
        _world = world;
        _isRecommended = isRecommended;
    }

    /// <summary>
    /// World name.
    /// </summary>
    public string WorldName => _world.WorldName;

    /// <summary>
    /// World ID.
    /// </summary>
    public int WorldId => _world.WorldId;

    /// <summary>
    /// Whether this is the user's home world.
    /// </summary>
    public bool IsHomeWorld => _world.IsHomeWorld;

    /// <summary>
    /// Whether this world is congested.
    /// </summary>
    public bool IsCongested => _world.IsCongested;

    /// <summary>
    /// Whether travel to this world is prohibited.
    /// </summary>
    public bool IsTravelProhibited => _world.IsTravelProhibited;

    /// <summary>
    /// Whether this world is blacklisted.
    /// </summary>
    public bool IsBlacklisted => _world.IsBlacklisted;

    /// <summary>
    /// Whether this world is the recommended option.
    /// </summary>
    public bool IsRecommended => _isRecommended;

    /// <summary>
    /// Background color for this world option.
    /// </summary>
    public string BackgroundColor
    {
        get
        {
            if (_world.IsCongested)
                return "#3d2d2d";  // Muted reddish for congested
            if (_world.IsHomeWorld)
                return "#3d3520";  // Gold-tinted for home world
            if (_isRecommended)
                return "#2d4a3e";  // Greenish for recommended
            return "#2d2d2d";      // Default gray
        }
    }

    /// <summary>
    /// Border brush color (null means no border).
    /// </summary>
    public string? BorderBrushColor
    {
        get
        {
            if (_world.IsHomeWorld)
                return "#ffd700";  // Gold border for home world
            if (_world.IsCongested)
                return "#cd5c5c";  // IndianRed for congested
            if (_isRecommended)
                return "#d4a73a";  // Gold for recommended
            return null;
        }
    }

    /// <summary>
    /// Border thickness.
    /// </summary>
    public double BorderThickness => (_world.IsHomeWorld || _world.IsCongested || _isRecommended) ? 1 : 0;

    /// <summary>
    /// Opacity for the world option (fades congested non-home worlds).
    /// </summary>
    public double Opacity => (_world.IsCongested && !_world.IsHomeWorld) ? 0.85 : 1.0;

    /// <summary>
    /// Foreground color for the world name.
    /// </summary>
    public string WorldNameForeground
    {
        get
        {
            if (_world.IsHomeWorld)
                return "#ffd700";  // Gold
            if (_world.IsCongested)
                return "#cd5c5c";  // IndianRed
            return "#ffffff";      // White
        }
    }

    /// <summary>
    /// Whether to show the home world badge.
    /// </summary>
    public bool ShowHomeBadge => _world.IsHomeWorld;

    /// <summary>
    /// Whether to show the congested badge.
    /// </summary>
    public bool ShowCongestedBadge => _world.IsCongested && !_world.IsHomeWorld;

    /// <summary>
    /// Whether to show the travel prohibited badge.
    /// </summary>
    public bool ShowTravelProhibitedBadge => _world.IsTravelProhibited && !_world.IsHomeWorld;

    /// <summary>
    /// Whether to show the blacklisted badge.
    /// </summary>
    public bool ShowBlacklistedBadge => _world.IsBlacklisted && !_world.IsHomeWorld;

    /// <summary>
    /// Whether to show the blacklist button.
    /// </summary>
    public bool ShowBlacklistButton => 
        (_world.IsTravelProhibited || (_world.IsCongested && !_world.IsHomeWorld)) && _world.WorldId > 0;

    /// <summary>
    /// Cost display text.
    /// </summary>
    public string CostDisplay => _world.CostDisplay;

    /// <summary>
    /// Whether there is excess quantity.
    /// </summary>
    public bool HasExcess => _world.HasExcess;

    /// <summary>
    /// Excess quantity.
    /// </summary>
    public int ExcessQuantity => _world.ExcessQuantity;

    /// <summary>
    /// Price per unit display.
    /// </summary>
    public string PricePerUnitDisplay => _world.PricePerUnitDisplay;

    /// <summary>
    /// Whether all listings are under DC average.
    /// </summary>
    public bool IsFullyUnderAverage => _world.IsFullyUnderAverage;

    /// <summary>
    /// Cost text with excess info if applicable.
    /// </summary>
    public string CostText
    {
        get
        {
            if (_world.HasExcess)
                return $"{_world.CostDisplay} total  •  {_world.ExcessQuantity} excess";
            return $"{_world.CostDisplay} total  •  ~{_world.PricePerUnitDisplay}/ea";
        }
    }

    /// <summary>
    /// Tooltip for cost text.
    /// </summary>
    public string? CostTooltip => _world.HasExcess ? "FFXIV requires buying full stacks. You'll have excess items." : null;

    /// <summary>
    /// Listings for this world (limited to 5).
    /// </summary>
    public List<ExpandedListingViewModel> DisplayListings => 
        _world.Listings.Take(5).Select(l => new ExpandedListingViewModel(l)).ToList();

    /// <summary>
    /// Whether there are more listings than shown.
    /// </summary>
    public bool HasMoreListings => _world.Listings.Count > 5;

    /// <summary>
    /// Text for additional listings.
    /// </summary>
    public string MoreListingsText => $"... and {_world.Listings.Count - 5} more listings";
}

/// <summary>
/// ViewModel for an individual listing in the expanded panel.
/// </summary>
public class ExpandedListingViewModel
{
    private readonly ShoppingListingEntry _listing;

    public ExpandedListingViewModel(ShoppingListingEntry listing)
    {
        _listing = listing;
    }

    /// <summary>
    /// Quantity display text.
    /// </summary>
    public string QuantityDisplay
    {
        get
        {
            if (_listing.IsAdditionalOption)
                return $"x{_listing.Quantity} extra";
            if (_listing.ExcessQuantity > 0)
                return $"x{_listing.Quantity} (need {_listing.NeededFromStack})";
            return $"x{_listing.Quantity}";
        }
    }

    /// <summary>
    /// Foreground color for the quantity text.
    /// </summary>
    public string QuantityForeground
    {
        get
        {
            if (_listing.IsAdditionalOption)
                return "#696969";  // DarkGray
            if (_listing.ExcessQuantity > 0)
                return "#ffa500";  // Orange
            return "#d3d3d3";      // LightGray
        }
    }

    /// <summary>
    /// Whether the quantity should be italic.
    /// </summary>
    public bool IsQuantityItalic => _listing.IsAdditionalOption;

    /// <summary>
    /// Price per unit.
    /// </summary>
    public long PricePerUnit => _listing.PricePerUnit;

    /// <summary>
    /// Foreground color for the price text.
    /// </summary>
    public string PriceForeground => _listing.IsUnderAverage ? "#90ee90" : "#ffffff";  // LightGreen : White

    /// <summary>
    /// Font weight for the price text.
    /// </summary>
    public string PriceFontWeight => _listing.IsHq ? "Bold" : "Normal";

    /// <summary>
    /// Formatted subtotal display.
    /// </summary>
    public string SubtotalDisplay => _listing.SubtotalDisplay;

    /// <summary>
    /// Foreground color for the subtotal text.
    /// </summary>
    public string SubtotalForeground => _listing.IsAdditionalOption ? "#696969" : "#808080";  // DarkGray : Gray

    /// <summary>
    /// Retainer display text (includes HQ indicator if applicable).
    /// </summary>
    public string RetainerDisplay
    {
        get
        {
            if (_listing.IsHq)
                return $"HQ {_listing.RetainerName}";
            return _listing.RetainerName;
        }
    }

    /// <summary>
    /// Foreground color for the retainer text.
    /// </summary>
    public string RetainerForeground
    {
        get
        {
            if (_listing.IsAdditionalOption)
                return "#696969";  // DarkGray
            if (_listing.IsHq)
                return "#ffd700";  // Gold
            return "#808080";      // Gray
        }
    }
}
