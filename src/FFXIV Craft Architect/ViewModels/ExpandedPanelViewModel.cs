using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Coordinators;

namespace FFXIV_Craft_Architect.ViewModels;

/// <summary>
/// ViewModel for the expanded panel in split-pane market view.
/// </summary>
public class ExpandedPanelViewModel : ViewModelBase
{
    private static readonly PurchaseSummaryService _summaryService = new();
    
    private readonly DetailedShoppingPlan _plan;
    private readonly PurchaseSummary _summary;
    private readonly IMarketLogisticsCoordinator? _coordinator;
    private ObservableCollection<ExpandedWorldViewModel> _worldOptions = new();
    private ObservableCollection<ExpandedSplitWorldViewModel> _splitWorlds = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ExpandedPanelViewModel"/> class.
    /// </summary>
    /// <param name="plan">The shopping plan to display.</param>
    /// <param name="coordinator">Optional coordinator for selection management. If provided, close button will call ClearSelection().</param>
    public ExpandedPanelViewModel(DetailedShoppingPlan plan, IMarketLogisticsCoordinator? coordinator = null)
    {
        _plan = plan;
        _summary = _summaryService.CreateSummary(plan);
        _coordinator = coordinator;
        CloseCommand = new RelayCommand(OnClose);
        OpenDetailsCommand = new RelayCommand(OnOpenDetails);
        
        RefreshWorldOptions();
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
    /// Plan name with actual purchase quantity.
    /// </summary>
    public string HeaderText => _summary.DisplayText;
    
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
    /// DC Average price display.
    /// </summary>
    public string DCAverageText => IsVendorMode
        ? $"Fixed Vendor Price: {(_plan.RecommendedWorld?.AveragePricePerUnit ?? _plan.DCAveragePrice):N0}g"
        : $"Data Center Average: {_plan.DCAveragePrice:N0}g";

    public bool IsVendorMode => _plan.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName;

    public string VendorPricingText
    {
        get
        {
            var unitPrice = _plan.RecommendedWorld?.AveragePricePerUnit ?? _plan.DCAveragePrice;
            var total = _plan.RecommendedWorld?.TotalCost ?? 0;
            return $"{unitPrice:N0}g each  •  {total:N0}g total";
        }
    }

    /// <summary>
    /// Whether the plan has world options.
    /// </summary>
    public bool HasOptions => _plan.HasOptions;

    /// <summary>
    /// Number of world options.
    /// </summary>
    public int WorldOptionsCount => _plan.WorldOptions.Count;

    /// <summary>
    /// Whether this item is using a split-world recommendation.
    /// </summary>
    public bool RequiresSplitPurchase => _plan.RequiresSplitPurchase;

    /// <summary>
    /// Split-world recommendations for this item.
    /// </summary>
    public ObservableCollection<ExpandedSplitWorldViewModel> SplitWorlds
    {
        get => _splitWorlds;
        private set => SetProperty(ref _splitWorlds, value);
    }

    /// <summary>
    /// True when split-world recommendations should be shown.
    /// </summary>
    public bool HasSplitWorldOptions => RequiresSplitPurchase && _splitWorlds.Count > 0;

    /// <summary>
    /// True when the standard all-world comparison should be shown.
    /// </summary>
    public bool ShowSingleWorldOptions => HasOptions && !RequiresSplitPurchase && !IsVendorMode;

    /// <summary>
    /// Options header text.
    /// </summary>
    public string OptionsHeaderText => RequiresSplitPurchase
        ? $"Split Purchase Worlds ({_splitWorlds.Count} {Pluralize(_splitWorlds.Count, "world")}):"
        : $"World Purchase Options ({_plan.WorldOptions.Count} {Pluralize(_plan.WorldOptions.Count, "option")}):";

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
    /// Vendor information for items purchased from vendors.
    /// </summary>
    public List<VendorInfo> Vendors => _plan.Vendors;

    public VendorInfo? PrimaryVendor => GetOrderedVendors().FirstOrDefault();

    public List<VendorInfo> AlternativeVendors => GetOrderedVendors().Skip(1).ToList();

    public bool HasAlternativeVendors => AlternativeVendors.Count > 0;

    /// <summary>
    /// Whether this item has vendor information.
    /// </summary>
    public bool HasVendors => _plan.Vendors?.Any() == true;

    /// <summary>
    /// Whether this is a vendor-only item (no market world options).
    /// </summary>
    public bool IsVendorOnly => _plan.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName;

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
    /// Command to open the full details window.
    /// </summary>
    public ICommand OpenDetailsCommand { get; }

    private void OnClose()
    {
        _coordinator?.ClearSelection();
    }

    private void OnOpenDetails()
    {
        _coordinator?.OpenDetailsWindow(_plan);
    }

    /// <summary>
    /// Refreshes the world options collection from the underlying plan.
    /// </summary>
    private void RefreshWorldOptions()
    {
        _splitWorlds.Clear();
        var sortedWorlds = _plan.WorldOptions
            .OrderByDescending(w => w.IsHomeWorld)
            .ThenBy(w => w.IsBlacklisted && !w.IsHomeWorld)
            .ThenBy(w => w.IsCongested && !w.IsHomeWorld)
            .ThenBy(w => w.ValueScore)
            .ThenBy(w => w.TotalCost)
            .ToList();

        _worldOptions.Clear();

        if (RequiresSplitPurchase && _plan.RecommendedSplit?.Any() == true)
        {
            foreach (var split in _plan.RecommendedSplit)
            {
                var worldData = sortedWorlds.FirstOrDefault(w => string.Equals(w.WorldName, split.WorldName, StringComparison.OrdinalIgnoreCase));
                _splitWorlds.Add(new ExpandedSplitWorldViewModel(split, worldData, _plan.QuantityNeeded));
            }
        }
        else
        {
            foreach (var world in sortedWorlds)
            {
                var isRecommended = world == _plan.RecommendedWorld;
                _worldOptions.Add(new ExpandedWorldViewModel(world, isRecommended));
            }
        }

        OnPropertyChanged(nameof(HasSplitWorldOptions));
        OnPropertyChanged(nameof(ShowSingleWorldOptions));
        OnPropertyChanged(nameof(OptionsHeaderText));
    }

    private List<VendorInfo> GetOrderedVendors()
    {
        if (_plan.Vendors?.Any() != true)
        {
            return new List<VendorInfo>();
        }

        var gilVendors = _plan.Vendors.Where(v => v.IsGilVendor).ToList();
        var candidates = gilVendors.Count > 0 ? gilVendors : _plan.Vendors;

        var preferredName = _plan.RecommendedWorld?.VendorName;
        var primary = candidates.FirstOrDefault(v =>
            !string.IsNullOrWhiteSpace(preferredName) &&
            string.Equals(v.DisplayName, preferredName, StringComparison.OrdinalIgnoreCase));

        primary ??= candidates
            .OrderBy(v => v.Price)
            .ThenBy(v => v.Name)
            .First();

        return new[] { primary }
            .Concat(candidates.Where(v => !ReferenceEquals(v, primary)).OrderBy(v => v.Price).ThenBy(v => v.Name))
            .ToList();
    }

    private static string Pluralize(int count, string singular)
    {
        return count == 1 ? singular : $"{singular}s";
    }
}

/// <summary>
/// ViewModel for a world option within the expanded panel.
/// </summary>
public class ExpandedWorldViewModel : ViewModelBase
{
    private readonly WorldShoppingSummary _world;
    private readonly bool _isRecommended;
    private bool _isExpanded;

    public ExpandedWorldViewModel(WorldShoppingSummary world, bool isRecommended)
    {
        _world = world;
        _isRecommended = isRecommended;
        _isExpanded = isRecommended;
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
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
    /// Whether listing details for this world are expanded.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Command to toggle world listing expansion.
    /// </summary>
    public ICommand ToggleExpandCommand { get; }

    /// <summary>
    /// Background color for this world option.
    /// </summary>
    public string BackgroundColor
    {
        get
        {
            if (_world.IsCongested)
                return UiColorHex.WorldCongestedBackground;
            if (_world.IsHomeWorld)
                return UiColorHex.WorldHomeBackground;
            if (_isRecommended)
                return UiColorHex.WorldRecommendedBackground;
            return UiColorHex.WorldDefaultBackground;
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
                return UiColorHex.WorldHomeBorder;
            if (_world.IsCongested)
                return UiColorHex.WorldCongestedBorder;
            if (_isRecommended)
                return UiColorHex.WorldRecommendedBorder;
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
                return UiColorHex.WorldHomeBorder;
            if (_world.IsCongested)
                return UiColorHex.WorldCongestedBorder;
            return UiColorHex.TextPrimary;
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
    /// Listings for this world.
    /// </summary>
    public List<ExpandedListingViewModel> DisplayListings =>
        _world.Listings.Select(l => new ExpandedListingViewModel(l)).ToList();

    /// <summary>
    /// Whether there are more listings than shown.
    /// </summary>
    public bool HasMoreListings => false;

    /// <summary>
    /// Text for additional listings.
    /// </summary>
    public string MoreListingsText => string.Empty;
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
                return UiColorHex.TextMutedStrong;
            if (_listing.ExcessQuantity > 0)
                return UiColorHex.Warning;
            return UiColorHex.TextSecondary;
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
    public string PriceForeground => _listing.IsUnderAverage ? UiColorHex.PriceUnderAverage : UiColorHex.TextPrimary;

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
    public string SubtotalForeground => _listing.IsAdditionalOption ? UiColorHex.TextMutedStrong : UiColorHex.TextMuted;

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
                return UiColorHex.TextMutedStrong;
            if (_listing.IsHq)
                return UiColorHex.WorldHomeBorder;
            return UiColorHex.TextMuted;
        }
    }
}
