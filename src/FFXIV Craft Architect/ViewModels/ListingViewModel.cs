using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.ViewModels;

/// <summary>
/// ViewModel for an individual market listing.
/// </summary>
public class ListingViewModel : ViewModelBase
{
    private readonly ShoppingListingEntry _listing;

    public ListingViewModel(ShoppingListingEntry listing)
    {
        _listing = listing;
    }

    /// <summary>
    /// Quantity available in this listing.
    /// </summary>
    public int Quantity => _listing.Quantity;

    /// <summary>
    /// Price per unit.
    /// </summary>
    public long PricePerUnit => _listing.PricePerUnit;

    /// <summary>
    /// Retainer name.
    /// </summary>
    public string RetainerName => _listing.RetainerName;

    /// <summary>
    /// Whether this listing is under DC average price.
    /// </summary>
    public bool IsUnderAverage => _listing.IsUnderAverage;

    /// <summary>
    /// Whether this is an HQ listing.
    /// </summary>
    public bool IsHq => _listing.IsHq;

    /// <summary>
    /// How many items we need from this stack.
    /// </summary>
    public int NeededFromStack => _listing.NeededFromStack;

    /// <summary>
    /// How many extra items we'll have.
    /// </summary>
    public int ExcessQuantity => _listing.ExcessQuantity;

    /// <summary>
    /// Whether this is an additional option (not needed for quantity).
    /// </summary>
    public bool IsAdditionalOption => _listing.IsAdditionalOption;

    /// <summary>
    /// Formatted subtotal display.
    /// </summary>
    public string SubtotalDisplay => _listing.SubtotalDisplay;

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
    /// Foreground color for the price text.
    /// </summary>
    public string PriceForeground => _listing.IsUnderAverage ? "#90ee90" : "#ffffff";  // LightGreen : White

    /// <summary>
    /// Font weight for the price text.
    /// </summary>
    public string PriceFontWeight => _listing.IsHq ? "Bold" : "Normal";

    /// <summary>
    /// Foreground color for the subtotal text.
    /// </summary>
    public string SubtotalForeground => _listing.IsAdditionalOption ? "#696969" : "#808080";  // DarkGray : Gray

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
    /// Tooltip for quantity display.
    /// </summary>
    public string? QuantityTooltip
    {
        get
        {
            if (_listing.IsAdditionalOption)
                return "Additional option - not needed for quantity, but good value";
            if (_listing.ExcessQuantity > 0)
                return $"Must buy full stack of {_listing.Quantity}, only need {_listing.NeededFromStack}";
            return null;
        }
    }
}
