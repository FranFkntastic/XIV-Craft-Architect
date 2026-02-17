using System.Collections.ObjectModel;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.ViewModels;

/// <summary>
/// ViewModel for a world option within a market card.
/// </summary>
public class WorldOptionViewModel : ViewModelBase
{
    private readonly WorldShoppingSummary _world;
    private readonly bool _isRecommended;
    private ObservableCollection<ListingViewModel> _listings = new();

    public WorldOptionViewModel(WorldShoppingSummary world, bool isRecommended)
    {
        _world = world;
        _isRecommended = isRecommended;
        
        // Initialize listings
        foreach (var listing in world.Listings)
        {
            _listings.Add(new ListingViewModel(listing));
        }
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
    /// Total cost for this world option.
    /// </summary>
    public long TotalCost => _world.TotalCost;

    /// <summary>
    /// Formatted cost display.
    /// </summary>
    public string CostDisplay => _world.CostDisplay;

    /// <summary>
    /// Average price per unit.
    /// </summary>
    public decimal AveragePricePerUnit => _world.AveragePricePerUnit;

    /// <summary>
    /// Formatted price per unit display.
    /// </summary>
    public string PricePerUnitDisplay => _world.PricePerUnitDisplay;

    /// <summary>
    /// Number of listings used.
    /// </summary>
    public int ListingsUsed => _world.ListingsUsed;

    /// <summary>
    /// Whether all listings are under DC average.
    /// </summary>
    public bool IsFullyUnderAverage => _world.IsFullyUnderAverage;

    /// <summary>
    /// Total quantity that would be purchased.
    /// </summary>
    public int TotalQuantityPurchased => _world.TotalQuantityPurchased;

    /// <summary>
    /// Excess quantity (extra items beyond what's needed).
    /// </summary>
    public int ExcessQuantity => _world.ExcessQuantity;

    /// <summary>
    /// Whether there is excess quantity.
    /// </summary>
    public bool HasExcess => _world.HasExcess;

    /// <summary>
    /// Value score (lower is better).
    /// </summary>
    public decimal ValueScore => _world.ValueScore;

    /// <summary>
    /// Whether this world has competitively priced listings.
    /// </summary>
    public bool IsCompetitive => _world.IsCompetitive;

    /// <summary>
    /// World classification.
    /// </summary>
    public WorldClassification Classification => _world.Classification;

    /// <summary>
    /// Whether this world is congested.
    /// </summary>
    public bool IsCongested => _world.IsCongested;

    /// <summary>
    /// Whether this is the user's home world.
    /// </summary>
    public bool IsHomeWorld => _world.IsHomeWorld;

    /// <summary>
    /// Whether this world is blacklisted.
    /// </summary>
    public bool IsBlacklisted => _world.IsBlacklisted;

    /// <summary>
    /// Whether travel to this world is prohibited.
    /// </summary>
    public bool IsTravelProhibited => _world.IsTravelProhibited;

    /// <summary>
    /// Congested warning message.
    /// </summary>
    public string? CongestedWarning => _world.CongestedWarning;

    /// <summary>
    /// Whether this world has any accessibility issues.
    /// </summary>
    public bool HasAccessibilityIssues => _world.HasAccessibilityIssues;

    /// <summary>
    /// Best single listing for value comparison.
    /// </summary>
    public ShoppingListingEntry? BestSingleListing => _world.BestSingleListing;

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
    /// Listings for this world.
    /// </summary>
    public ObservableCollection<ListingViewModel> Listings
    {
        get => _listings;
        set => SetProperty(ref _listings, value);
    }
}
