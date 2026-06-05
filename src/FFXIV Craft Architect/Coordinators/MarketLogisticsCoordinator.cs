using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.ViewModels;
using Microsoft.Extensions.Logging;
using PriceInfo = FFXIV_Craft_Architect.Core.Models.PriceInfo;

namespace FFXIV_Craft_Architect.Coordinators;

/// <summary>
/// Summary data for market logistics calculations.
/// Contains aggregated cost and item information for display.
/// </summary>
/// <param name="TotalVendorCost">Total cost of vendor-purchasable items.</param>
/// <param name="TotalMarketCost">Total estimated cost of market-purchasable items.</param>
/// <param name="VendorItemCount">Number of items available from vendors.</param>
/// <param name="MarketItemCount">Number of items available from market board.</param>
/// <param name="UntradeableItemCount">Number of untradeable items.</param>
public record MarketSummaryData(
    int TotalVendorCost,
    int TotalMarketCost,
    int VendorItemCount,
    int MarketItemCount,
    int UntradeableItemCount);

/// <summary>
/// Coordinates market logistics calculations and UI state for the Market Logistics tab.
/// Separates market logistics logic from MainWindow.
/// Implements INotifyPropertyChanged for MVVM binding to selection state.
/// </summary>
public partial class MarketLogisticsCoordinator : ObservableObject, IMarketLogisticsCoordinator
{
    private readonly MarketShoppingService _marketShoppingService;
    private readonly ILogger<MarketLogisticsCoordinator> _logger;
    private IReadOnlyList<DetailedShoppingPlan> _availablePlans = [];
    
    /// <summary>
    /// Initializes a new instance of the <see cref="MarketLogisticsCoordinator"/> class.
    /// </summary>
    /// <param name="marketShoppingService">Service for calculating shopping plans.</param>
    /// <param name="logger">Logger instance.</param>
    public MarketLogisticsCoordinator(
        MarketShoppingService marketShoppingService,
        ILogger<MarketLogisticsCoordinator> logger)
    {
        _marketShoppingService = marketShoppingService;
        _logger = logger;
    }
    
    #region Selection State (MVVM Binding)
    
    [ObservableProperty]
    private ExpandedPanelViewModel? _selectedExpandedPanel;
    
    [ObservableProperty]
    private int? _selectedItemId;
    
    /// <inheritdoc />
    public void SetAvailablePlans(IReadOnlyList<DetailedShoppingPlan> plans)
    {
        _availablePlans = plans ?? [];
        
        if (SelectedItemId.HasValue)
        {
            var stillExists = _availablePlans.Any(p => p.ItemId == SelectedItemId.Value);
            if (!stillExists)
            {
                ClearSelection();
            }
        }
    }
    
    /// <inheritdoc />
    public void SelectItem(int itemId)
    {
        var plan = _availablePlans.FirstOrDefault(p => p.ItemId == itemId);
        if (plan == null)
        {
            _logger.LogWarning("[SelectItem] Item {ItemId} not found in available plans", itemId);
            return;
        }
        
        if (SelectedItemId == itemId)
        {
            ClearSelection();
            return;
        }
        
        SelectedItemId = itemId;
        SelectedExpandedPanel = new ExpandedPanelViewModel(plan, this);
        
        _logger.LogDebug("[SelectItem] Selected item {ItemId} - {Name}", itemId, plan.Name);
    }
    
    /// <inheritdoc />
    public void ClearSelection()
    {
        SelectedItemId = null;
        SelectedExpandedPanel = null;
        
        _logger.LogDebug("[ClearSelection] Selection cleared");
    }

    /// <inheritdoc />
    public void OpenDetailsWindow(DetailedShoppingPlan plan)
    {
        var viewModel = new SplitWorldWindowViewModel(plan);
        var window = new Views.SplitWorldRecommendationWindow(viewModel)
        {
            Owner = null,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        viewModel.RequestClose += (_, _) => window.Close();
        window.Show();

        _logger.LogDebug("[OpenDetailsWindow] Opened details window for {Name} (split={Split})", plan.Name, plan.RequiresSplitPurchase);
    }

    /// <inheritdoc />
    public void OpenSplitWorldWindow(DetailedShoppingPlan plan)
    {
        OpenDetailsWindow(plan);
    }
    
    #endregion

    /// <summary>
    /// Result of a market logistics calculation.
    /// </summary>
    /// <param name="Success">Whether the operation succeeded.</param>
    /// <param name="Message">Status or error message.</param>
    /// <param name="ShoppingPlans">Calculated shopping plans for market items.</param>
    /// <param name="CraftVsBuyAnalyses">Analysis comparing craft vs buy options.</param>
    /// <param name="HasMarketData">Whether market data is available.</param>
    /// <param name="EnableRefreshButton">Whether the refresh button should be enabled.</param>
    /// <param name="EnableViewStatusButton">Whether the view status button should be enabled.</param>
    /// <param name="LoadingCard">Optional loading UI element.</param>
    public record MarketLogisticsResult(
        bool Success,
        string Message,
        List<DetailedShoppingPlan> ShoppingPlans,
        List<CraftVsBuyAnalysis>? CraftVsBuyAnalyses,
        bool HasMarketData,
        bool EnableRefreshButton,
        bool EnableViewStatusButton,
        Border? LoadingCard);

    /// <summary>
    /// Creates a loading state UI element for market data fetching.
    /// </summary>
    /// <param name="dataCenter">The data center being queried.</param>
    /// <param name="itemCount">Number of items being fetched.</param>
    /// <param name="searchAllNA">Whether searching all NA data centers.</param>
    /// <returns>A Border element displaying the loading message.</returns>
    public Border CreateLoadingState(string dataCenter, int itemCount, bool searchAllNA)
    {
        var location = searchAllNA ? "all NA DCs" : dataCenter;
        var message = $"Fetching detailed listings for {itemCount} items from {location}...";

        var border = new Border
        {
            Background = ResolveBrush("Brush.Surface.Card.Placeholder", Brushes.DimGray),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var stackPanel = new StackPanel();
        
        var header = new TextBlock
        {
            Text = "Market Board Items",
            Foreground = ResolveBrush("TextPrimaryBrush", Brushes.White),
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        
        var content = new TextBlock
        {
            Text = message,
            Foreground = ResolveBrush("LightGrayBrush", Brushes.LightGray),
            TextWrapping = TextWrapping.Wrap
        };
        
        stackPanel.Children.Add(header);
        stackPanel.Children.Add(content);
        border.Child = stackPanel;

        return border;
    }

    /// <summary>
    /// Calculates market logistics for the given plan with live market data.
    /// </summary>
    /// <param name="plan">The crafting plan.</param>
    /// <param name="prices">Dictionary of price information for items.</param>
    /// <param name="dataCenter">The data center to query.</param>
    /// <param name="searchAllNA">Whether to search all NA data centers.</param>
    /// <param name="mode">Recommendation mode for shopping plans.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing shopping plans and analysis.</returns>
    public async Task<MarketLogisticsResult> CalculateMarketLogisticsAsync(
        CraftingPlan plan,
        Dictionary<int, PriceInfo> prices,
        string dataCenter,
        bool searchAllNA,
        RecommendationMode mode,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (plan == null)
        {
            return new MarketLogisticsResult(
                false,
                "No plan available",
                new List<DetailedShoppingPlan>(),
                null,
                false,
                false,
                false,
                null);
        }

        var (vendorItems, marketItems, untradeableItems) = CategorizeMaterials(plan, prices);

        try
        {
            List<DetailedShoppingPlan> shoppingPlans = new();

            // Create shopping plans for market items
            if (marketItems.Any())
            {
                if (searchAllNA)
                {
                    var marketPlans = await _marketShoppingService.CalculateDetailedShoppingPlansMultiDCAsync(
                        marketItems, progress, ct, mode);
                    shoppingPlans.AddRange(marketPlans);
                }
                else
                {
                    var marketPlans = await _marketShoppingService.CalculateDetailedShoppingPlansAsync(
                        marketItems, dataCenter, progress, ct, mode);
                    shoppingPlans.AddRange(marketPlans);
                }
            }
            
            // Create shopping plans for vendor items
            if (vendorItems.Any())
            {
                var vendorPlans = CreateVendorShoppingPlans(vendorItems, prices);
                shoppingPlans.AddRange(vendorPlans);
            }

            // Apply hard-lock vendor overrides for items explicitly marked as VendorBuy in the plan tree
            // This ensures user's vendor dropdown selection is respected
            _marketShoppingService.ApplyVendorPurchaseOverrides(plan, shoppingPlans);

            var craftVsBuyAnalyses = _marketShoppingService.AnalyzeCraftVsBuy(plan, prices);

            return new MarketLogisticsResult(
                true,
                $"Market analysis complete. {shoppingPlans.Count} items analyzed.",
                shoppingPlans,
                craftVsBuyAnalyses,
                true,
                true,
                true,
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CalculateMarketLogistics] Failed to calculate market logistics");
            return new MarketLogisticsResult(
                false,
                $"Error fetching listings: {ex.Message}",
                new List<DetailedShoppingPlan>(),
                null,
                false,
                true,
                false,
                null);
        }
    }

    /// <summary>
    /// Calculates market logistics using cached/saved data without making API calls.
    /// </summary>
    /// <param name="plan">The crafting plan.</param>
    /// <param name="prices">Dictionary of price information for items.</param>
    /// <param name="savedPlans">Previously saved shopping plans, if any.</param>
    /// <returns>Result containing shopping plans and analysis from cached data.</returns>
    public MarketLogisticsResult CalculateCachedLogistics(
        CraftingPlan plan,
        Dictionary<int, PriceInfo> prices,
        List<DetailedShoppingPlan>? savedPlans)
    {
        if (plan == null)
        {
            return new MarketLogisticsResult(
                false,
                "No plan available",
                new List<DetailedShoppingPlan>(),
                null,
                false,
                false,
                false,
                null);
        }

        var (vendorItems, marketItems, untradeableItems) = CategorizeMaterials(plan, prices);

        try
        {
            List<CraftVsBuyAnalysis>? craftVsBuyAnalyses = null;

            if (marketItems.Any())
            {
                craftVsBuyAnalyses = _marketShoppingService.AnalyzeCraftVsBuy(plan, prices);
            }

            // Use saved plans if available, otherwise empty list
            var shoppingPlans = savedPlans ?? new List<DetailedShoppingPlan>();
            
            // Add vendor shopping plans (these are always calculated fresh from price data)
            if (vendorItems.Any())
            {
                var vendorPlans = CreateVendorShoppingPlans(vendorItems, prices);
                shoppingPlans = shoppingPlans.Concat(vendorPlans).ToList();
            }

            // Apply hard-lock vendor overrides for items explicitly marked as VendorBuy in the plan tree
            // This ensures user's vendor dropdown selection is respected
            _marketShoppingService.ApplyVendorPurchaseOverrides(plan, shoppingPlans);

            return new MarketLogisticsResult(
                true,
                $"Market analysis rebuilt from {prices.Count} cached prices.",
                shoppingPlans,
                craftVsBuyAnalyses,
                true,
                true,
                true,
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CalculateCachedLogistics] Failed to calculate cached logistics");
            return new MarketLogisticsResult(
                false,
                $"Failed to rebuild from cache: {ex.Message}",
                new List<DetailedShoppingPlan>(),
                null,
                false,
                true,
                false,
                null);
        }
    }

    /// <summary>
    /// Categorizes materials by their price source (vendor, market, or untradeable).
    /// Checks both PriceSource (from price data) AND AcquisitionSource (from user selection in plan tree).
    /// Items marked as VendorBuy in the plan tree are always treated as vendor items.
    /// </summary>
    /// <param name="plan">The crafting plan containing materials.</param>
    /// <param name="prices">Dictionary of price information.</param>
    /// <returns>Tuple of vendor items, market items, and untradeable items.</returns>
    private (List<MaterialAggregate> VendorItems, List<MaterialAggregate> MarketItems, List<MaterialAggregate> UntradeableItems)
        CategorizeMaterials(CraftingPlan plan, Dictionary<int, PriceInfo> prices)
    {
        var vendorItems = new List<MaterialAggregate>();
        var marketItems = new List<MaterialAggregate>();
        var untradeableItems = new List<MaterialAggregate>();

        // Collect items explicitly marked as VendorBuy in the plan tree
        var vendorBuyItemIds = new HashSet<int>();
        CollectVendorBuyItemIds(plan.RootItems, vendorBuyItemIds);
        
        _logger.LogDebug("[CategorizeMaterials] Found {Count} items marked as VendorBuy in plan tree", 
            vendorBuyItemIds.Count);

        foreach (var material in AcquisitionPlanningService.GetActiveProcurementItems(plan))
        {
            // Check if user explicitly selected VendorBuy in recipe tree
            if (vendorBuyItemIds.Contains(material.ItemId))
            {
                _logger.LogDebug("[CategorizeMaterials] {Name} marked as vendor (VendorBuy in plan)", material.Name);
                vendorItems.Add(material);
                continue;
            }
            
            if (prices.TryGetValue(material.ItemId, out var priceInfo))
            {
                switch (priceInfo.Source)
                {
                    case PriceSource.Vendor:
                        _logger.LogDebug("[CategorizeMaterials] {Name} marked as vendor (PriceSource.Vendor)", material.Name);
                        vendorItems.Add(material);
                        break;
                    case PriceSource.Untradeable:
                        untradeableItems.Add(material);
                        break;
                    case PriceSource.Market:
                    default:
                        marketItems.Add(material);
                        break;
                }
            }
            else
            {
                // Default to market if no price info available
                marketItems.Add(material);
            }
        }

        _logger.LogInformation("[CategorizeMaterials] Categorized: {VendorCount} vendor, {MarketCount} market, {UntradeableCount} untradeable",
            vendorItems.Count, marketItems.Count, untradeableItems.Count);

        return (vendorItems, marketItems, untradeableItems);
    }

    /// <summary>
    /// Recursively collects item IDs that are marked as VendorBuy in the plan tree.
    /// </summary>
    private static void CollectVendorBuyItemIds(List<PlanNode> nodes, HashSet<int> itemIds)
    {
        foreach (var node in nodes)
        {
            if (node.Source == AcquisitionSource.VendorBuy)
            {
                itemIds.Add(node.ItemId);
            }
            
            if (node.Children.Count > 0)
            {
                CollectVendorBuyItemIds(node.Children, itemIds);
            }
        }
    }

    /// <summary>
    /// Creates DetailedShoppingPlan objects for vendor-purchasable items.
    /// Vendor items are treated as having a single "Vendor" world option with unlimited stock.
    /// </summary>
    /// <param name="vendorItems">Items available from vendors.</param>
    /// <param name="prices">Price information dictionary.</param>
    /// <returns>List of shopping plans for vendor items.</returns>
    private List<DetailedShoppingPlan> CreateVendorShoppingPlans(
        List<MaterialAggregate> vendorItems, 
        Dictionary<int, PriceInfo> prices)
    {
        var plans = new List<DetailedShoppingPlan>();
        
        foreach (var item in vendorItems)
        {
            if (!prices.TryGetValue(item.ItemId, out var priceInfo))
                continue;
            
            var unitPrice = priceInfo.UnitPrice;
            var totalCost = (long)(unitPrice * item.TotalQuantity);
            
            // Create the vendor "world" summary
            var vendorWorldSummary = new WorldShoppingSummary
            {
                WorldName = MarketShoppingConstants.VendorWorldName,
                WorldId = 0, // Vendor has no world ID
                TotalCost = totalCost,
                AveragePricePerUnit = unitPrice,
                ListingsUsed = 1,
                TotalQuantityPurchased = item.TotalQuantity,
                HasSufficientStock = true, // Vendors always have unlimited stock
                IsHomeWorld = false,
                IsTravelProhibited = false,
                IsBlacklisted = false,
                Classification = WorldClassification.Standard,
                // Vendor listings are a single virtual listing
                Listings = new List<ShoppingListingEntry>
                {
                    new ShoppingListingEntry
                    {
                        Quantity = item.TotalQuantity,
                        PricePerUnit = (long)unitPrice,
                        RetainerName = MarketShoppingConstants.VendorWorldName,
                        IsUnderAverage = true, // Vendor prices are always the best
                        IsHq = false,
                        NeededFromStack = item.TotalQuantity,
                        ExcessQuantity = 0
                    }
                }
            };
            
            var plan = new DetailedShoppingPlan
            {
                ItemId = item.ItemId,
                Name = item.Name,
                QuantityNeeded = item.TotalQuantity,
                DCAveragePrice = unitPrice, // Vendor price is the "average"
                Vendors = priceInfo.Vendors?.ToList() ?? new List<VendorInfo>(),
                RecommendedWorld = vendorWorldSummary,
                WorldOptions = new List<WorldShoppingSummary> { vendorWorldSummary },
                Error = null
            };
            
            plans.Add(plan);
            
            _logger.LogDebug("[CreateVendorShoppingPlans] Created plan for {ItemName} - Vendor price: {Price}g x{Quantity} = {Total}g",
                item.Name, unitPrice, item.TotalQuantity, totalCost);
        }

        _logger.LogInformation("[CreateVendorShoppingPlans] Created {Count} vendor shopping plans", plans.Count);
        return plans;
    }

    private static Brush ResolveBrush(string resourceKey, Brush fallback)
    {
        return Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
    }

    /// <summary>
    /// Calculates summary data for market logistics display.
    /// This is business logic - calculating costs based on the plan and prices.
    /// </summary>
    /// <param name="plan">The crafting plan containing materials.</param>
    /// <param name="prices">Dictionary of price information for items.</param>
    /// <returns>Summary data containing total costs and item counts.</returns>
    public MarketSummaryData CalculateSummaryData(CraftingPlan plan, Dictionary<int, PriceInfo> prices)
    {
        if (plan == null || prices == null || prices.Count == 0)
        {
            return new MarketSummaryData(0, 0, 0, 0, 0);
        }

        var (vendorItems, marketItems, untradeableItems) = CategorizeMaterials(plan, prices);

        int totalVendorCost = 0;
        int totalMarketCost = 0;

        foreach (var item in vendorItems)
        {
            if (prices.TryGetValue(item.ItemId, out var priceInfo) && priceInfo.UnitPrice > 0)
            {
                totalVendorCost += (int)(priceInfo.UnitPrice * item.TotalQuantity);
            }
        }

        foreach (var item in marketItems)
        {
            if (prices.TryGetValue(item.ItemId, out var priceInfo) && priceInfo.UnitPrice > 0)
            {
                totalMarketCost += (int)(priceInfo.UnitPrice * item.TotalQuantity);
            }
        }

        return new MarketSummaryData(
            totalVendorCost,
            totalMarketCost,
            vendorItems.Count,
            marketItems.Count,
            untradeableItems.Count);
    }

}
