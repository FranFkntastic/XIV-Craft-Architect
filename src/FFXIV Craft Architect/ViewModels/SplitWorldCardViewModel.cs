using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.ViewModels;

public class SplitWorldCardViewModel : ViewModelBase
{
    private readonly SplitWorldPurchase _split;
    private readonly int _totalQuantity;
    private bool _isExpanded;

    public SplitWorldCardViewModel(SplitWorldPurchase split, int totalQuantity)
    {
        _split = split;
        _totalQuantity = totalQuantity;
        _isExpanded = false;
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string WorldName => _split.WorldName;
    public int QuantityToBuy => _split.QuantityToBuy;
    public int TotalQuantityNeeded => _totalQuantity;
    public decimal PricePerUnit => EffectivePricePerNeededUnit;
    public decimal ListingPricePerUnit => _split.PricePerUnit;
    public decimal EffectivePricePerNeededUnit => _split.EffectivePricePerNeededUnit;
    public long TotalCost => _split.TotalCost;
    public bool IsPartial => _split.IsPartial;
    public string TravelContext => _split.TravelContext;
    public int ExcessAvailable => _split.ExcessAvailable;

    public string QuantityDisplay => $"×{QuantityToBuy} of {TotalQuantityNeeded}";
    public string CostDisplay => $"{TotalCost:N0}g";
    public string PriceDisplay => EffectivePriceDisplay;
    public string ListingPriceDisplay => $"@{ListingPricePerUnit:N0}g/ea listing";
    public string EffectivePriceDisplay => $"~{EffectivePricePerNeededUnit:N0}g/needed ea";
    public string? ExcessDisplay => ExcessAvailable > 0 ? $"+{ExcessAvailable} excess" : null;
    public bool HasExcess => ExcessAvailable > 0;

    public string ContextBadge
    {
        get => TravelContext switch
        {
            TravelContextConstants.Primary => "PRIMARY",
            TravelContextConstants.Consolidated => "CONSOLIDATED",
            _ => "SUPPLEMENTAL"
        };
    }

    public bool IsPrimary => TravelContext == TravelContextConstants.Primary;
    public bool IsSupplemental => TravelContext == TravelContextConstants.Supplemental;
    public bool IsConsolidated => TravelContext == TravelContextConstants.Consolidated;

    public List<ShoppingListingEntry> Listings => _split.Listings ?? new List<ShoppingListingEntry>();
    public bool HasListings => Listings.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public ICommand ToggleExpandCommand { get; }
}
