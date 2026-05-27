using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.ViewModels;

public class ExpandedSplitWorldViewModel : ViewModelBase
{
    private bool _isExpanded;
    private readonly List<ExpandedListingViewModel> _displayListings;
    private readonly WorldShoppingSummary? _worldData;

    public ExpandedSplitWorldViewModel(
        SplitWorldPurchase split,
        WorldShoppingSummary? worldData,
        int totalQuantityNeeded)
    {
        Split = split;
        _worldData = worldData;
        TotalQuantityNeeded = totalQuantityNeeded;
        _displayListings = BuildRelevantListings(split, worldData)
            .Select(l => new ExpandedListingViewModel(l))
            .ToList();

        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public SplitWorldPurchase Split { get; }
    public int TotalQuantityNeeded { get; }

    public string WorldName => Split.WorldName;
    public int QuantityToBuy => Split.QuantityToBuy;
    public long TotalCost => Split.TotalCost;
    public decimal PricePerUnit => EffectivePricePerNeededUnit;
    public decimal ListingPricePerUnit => Split.PricePerUnit;
    public decimal EffectivePricePerNeededUnit => Split.EffectivePricePerNeededUnit;
    public string TravelContext => Split.TravelContext;
    public int ExcessAvailable => Split.ExcessAvailable;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public ICommand ToggleExpandCommand { get; }

    public bool IsHomeWorld => _worldData?.IsHomeWorld == true;
    public bool IsCongested => _worldData?.IsCongested == true;
    public bool IsTravelProhibited => _worldData?.IsTravelProhibited == true;

    public bool ShowHomeBadge => IsHomeWorld;
    public bool ShowCongestedBadge => IsCongested && !IsHomeWorld;
    public bool ShowTravelProhibitedBadge => IsTravelProhibited && !IsHomeWorld;

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

    public string QuantityDisplay => $"x{QuantityToBuy} of {TotalQuantityNeeded}";
    public string CostText => $"{TotalCost:N0}g total  -  ~{EffectivePricePerNeededUnit:N0}g/needed ea";

    public bool HasExcess => ExcessAvailable > 0;
    public string ExcessText => $"{ExcessAvailable} excess due to full stacks";

    public List<ExpandedListingViewModel> DisplayListings => _displayListings;
    public bool HasListings => _displayListings.Count > 0;

    private static List<ShoppingListingEntry> BuildRelevantListings(
        SplitWorldPurchase split,
        WorldShoppingSummary? worldData)
    {
        var splitListings = split.Listings
            .Where(l => !l.IsAdditionalOption)
            .OrderBy(l => l.PricePerUnit)
            .ToList();

        if (splitListings.Count > 0)
        {
            return splitListings;
        }

        if (worldData == null)
        {
            return new List<ShoppingListingEntry>();
        }

        var relevant = new List<ShoppingListingEntry>();
        var remaining = split.QuantityToBuy;

        foreach (var listing in worldData.Listings.Where(l => !l.IsAdditionalOption).OrderBy(l => l.PricePerUnit))
        {
            if (remaining <= 0)
            {
                break;
            }

            var neededFromStack = Math.Min(remaining, listing.Quantity);
            if (neededFromStack <= 0)
            {
                continue;
            }

            relevant.Add(new ShoppingListingEntry
            {
                Quantity = listing.Quantity,
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsUnderAverage = listing.IsUnderAverage,
                IsHq = listing.IsHq,
                IsAdditionalOption = false,
                NeededFromStack = neededFromStack,
                ExcessQuantity = Math.Max(0, listing.Quantity - neededFromStack)
            });

            remaining -= neededFromStack;
        }

        return relevant;
    }
}
