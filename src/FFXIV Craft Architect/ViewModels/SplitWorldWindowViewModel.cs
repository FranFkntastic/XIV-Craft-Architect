using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.ViewModels;

public class SplitWorldWindowViewModel : ViewModelBase
{
    private static readonly PurchaseSummaryService _summaryService = new();
    
    private readonly DetailedShoppingPlan _plan;
    private readonly PurchaseSummary _summary;

    public SplitWorldWindowViewModel(DetailedShoppingPlan plan)
    {
        _plan = plan;
        _summary = _summaryService.CreateSummary(plan);
        WorldCards = new ObservableCollection<SplitWorldCardViewModel>(BuildCards(plan));
        VendorOptions = new ObservableCollection<VendorDetailViewModel>(BuildVendorOptions(plan));

        CloseCommand = new RelayCommand(Close);
        CopyItemNameCommand = new RelayCommand(CopyItemName);
        CopyShoppingListCommand = new RelayCommand(CopyShoppingList);
    }

    public string ItemName => _plan.Name;
    public int QuantityNeeded => _plan.QuantityNeeded;
    public int QuantityToPurchase => _summary.QuantityToPurchase;
    public int ExcessQuantity => _summary.ExcessQuantity;
    public bool HasExcess => _summary.HasExcess;
    
    public string HeaderText => _summary.DisplayText;
    public decimal DCAveragePrice => _plan.DCAveragePrice;
    public bool IsVendorMode => string.Equals(
        _plan.RecommendedWorld?.WorldName,
        MarketShoppingConstants.VendorWorldName,
        StringComparison.OrdinalIgnoreCase);

    public string SubHeaderText => IsVendorMode
        ? $"Fixed Vendor Price: {(_plan.RecommendedWorld?.AveragePricePerUnit ?? _plan.DCAveragePrice):N0}g"
        : $"DC Average: {DCAveragePrice:N0}g";

    public bool IsSplitMode => _plan.RequiresSplitPurchase;
    public string RecommendationsHeaderText => IsVendorMode
        ? $"Vendor Purchase Options ({VendorOptions.Count} {Pluralize(VendorOptions.Count, "vendor")})"
        : IsSplitMode
        ? $"Split Purchase Plan ({WorldCards.Count} {Pluralize(WorldCards.Count, "world")})"
        : $"World Purchase Options ({WorldCards.Count} {Pluralize(WorldCards.Count, "option")})";

    public long DetailsTotalCost => IsSplitMode
        ? (_plan.SplitTotalCost ?? 0)
        : (_plan.RecommendedWorld?.TotalCost ?? 0);

    public ObservableCollection<SplitWorldCardViewModel> WorldCards { get; }
    public ObservableCollection<VendorDetailViewModel> VendorOptions { get; }
    public VendorDetailViewModel? PrimaryVendor => VendorOptions.FirstOrDefault();
    public bool HasPrimaryVendor => PrimaryVendor != null;
    public List<VendorDetailViewModel> AlternativeVendors => VendorOptions.Skip(1).ToList();
    public bool HasAlternativeVendors => VendorOptions.Count > 1;

    public ICommand CloseCommand { get; }
    public ICommand CopyItemNameCommand { get; }
    public ICommand CopyShoppingListCommand { get; }

    public event EventHandler? RequestClose;

    private void Close()
    {
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void CopyItemName()
    {
        try
        {
            Clipboard.SetText(ItemName);
        }
        catch
        {
        }
    }

    private void CopyShoppingList()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(_summary.DisplayText);
            sb.AppendLine($"Total: {DetailsTotalCost:N0}g");
            sb.AppendLine();

            if (IsVendorMode)
            {
                if (PrimaryVendor != null)
                {
                    sb.AppendLine($"Primary Vendor: {PrimaryVendor.Name}");
                    if (!string.IsNullOrWhiteSpace(PrimaryVendor.Location))
                    {
                        sb.AppendLine($"Location: {PrimaryVendor.Location}");
                    }

                    if (!string.IsNullOrWhiteSpace(PrimaryVendor.CoordinatesDisplay))
                    {
                        sb.AppendLine($"Coordinates: {PrimaryVendor.CoordinatesDisplay}");
                    }

                    sb.AppendLine($"Price: {PrimaryVendor.PriceDisplay}");
                }

                if (HasAlternativeVendors)
                {
                    sb.AppendLine();
                    sb.AppendLine("Alternative Vendors:");
                    foreach (var vendor in AlternativeVendors)
                    {
                        sb.AppendLine($"- {vendor.Name} ({vendor.PriceDisplay})");
                    }
                }

                Clipboard.SetText(sb.ToString());
                return;
            }

            foreach (var card in WorldCards)
            {
                sb.AppendLine($"{card.WorldName}: ×{card.QuantityToBuy} @ {card.PricePerUnit:N0}g = {card.TotalCost:N0}g");
            }

            Clipboard.SetText(sb.ToString());
        }
        catch
        {
        }
    }

    private static IEnumerable<SplitWorldCardViewModel> BuildCards(DetailedShoppingPlan plan)
    {
        if (string.Equals(plan.RecommendedWorld?.WorldName, MarketShoppingConstants.VendorWorldName, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<SplitWorldCardViewModel>();
        }

        if (plan.RequiresSplitPurchase && plan.RecommendedSplit != null)
        {
            return plan.RecommendedSplit
                .Select(s => new SplitWorldCardViewModel(s, plan.QuantityNeeded));
        }

        var sortedWorlds = plan.WorldOptions
            .OrderByDescending(w => w.IsHomeWorld)
            .ThenBy(w => w.IsBlacklisted && !w.IsHomeWorld)
            .ThenBy(w => w.IsCongested && !w.IsHomeWorld)
            .ThenBy(w => w.ValueScore)
            .ThenBy(w => w.TotalCost)
            .ToList();

        return sortedWorlds.Select(world =>
        {
            var quantityToBuy = Math.Min(plan.QuantityNeeded, Math.Max(0, world.TotalQuantityPurchased));
            var isRecommended = ReferenceEquals(world, plan.RecommendedWorld);

            var pseudoSplit = new SplitWorldPurchase
            {
                WorldName = world.WorldName,
                QuantityToBuy = quantityToBuy,
                PricePerUnit = world.AveragePricePerUnit,
                TotalCost = world.TotalCost,
                IsPartial = quantityToBuy < plan.QuantityNeeded,
                TravelContext = isRecommended ? TravelContextConstants.Primary : TravelContextConstants.Supplemental,
                ExcessAvailable = world.ExcessQuantity,
                Listings = world.Listings
                    .Where(l => !l.IsAdditionalOption)
                    .OrderBy(l => l.PricePerUnit)
                    .ToList()
            };

            return new SplitWorldCardViewModel(pseudoSplit, plan.QuantityNeeded);
        });
    }

    private static IEnumerable<VendorDetailViewModel> BuildVendorOptions(DetailedShoppingPlan plan)
    {
        if (plan.Vendors == null || plan.Vendors.Count == 0)
        {
            return Array.Empty<VendorDetailViewModel>();
        }

        var vendors = plan.Vendors
            .Where(v => v.IsGilVendor)
            .DefaultIfEmpty(plan.Vendors.First())
            .DistinctBy(v => $"{v.Name}|{v.Location}|{v.Price}")
            .ToList();

        var recommendedVendorName = plan.RecommendedWorld?.VendorName;
        var primary = vendors.FirstOrDefault(v =>
            !string.IsNullOrWhiteSpace(recommendedVendorName) &&
            string.Equals(v.DisplayName, recommendedVendorName, StringComparison.OrdinalIgnoreCase));

        primary ??= vendors.OrderBy(v => v.Price).ThenBy(v => v.Name).First();

        var ordered = new List<VendorInfo> { primary };
        ordered.AddRange(vendors.Where(v => !ReferenceEquals(v, primary)).OrderBy(v => v.Price).ThenBy(v => v.Name));

        return ordered.Select(v => new VendorDetailViewModel(v, plan.QuantityNeeded));
    }

    private static string Pluralize(int count, string singular)
    {
        return count == 1 ? singular : $"{singular}s";
    }
}

public class VendorDetailViewModel
{
    private readonly VendorInfo _vendor;
    private readonly int _quantityNeeded;

    public VendorDetailViewModel(VendorInfo vendor, int quantityNeeded)
    {
        _vendor = vendor;
        _quantityNeeded = quantityNeeded;
    }

    public string Name => _vendor.Name;
    public string Location => _vendor.Location;
    public string? CoordinatesDisplay => _vendor.CoordinatesDisplay;
    public bool HasCoordinates => _vendor.HasCoordinates;
    public string PriceDisplay => _vendor.IsGilVendor
        ? $"{_vendor.Price:N0}g each"
        : $"{_vendor.Price:N0} {_vendor.Currency} each";
    public string TotalDisplay => _vendor.IsGilVendor
        ? $"{(_vendor.Price * _quantityNeeded):N0}g total"
        : $"{(_vendor.Price * _quantityNeeded):N0} {_vendor.Currency} total";
}
