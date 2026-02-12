using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Services;
using FFXIVCraftArchitect.Services.Interfaces;
using Microsoft.Extensions.Logging;
using PriceInfo = FFXIVCraftArchitect.Core.Models.PriceInfo;

namespace FFXIVCraftArchitect.Coordinators;

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
/// </summary>
public class MarketLogisticsCoordinator
{
    private readonly IMarketShoppingService _marketShoppingService;
    private readonly ICardFactory _cardFactory;
    private readonly ILogger<MarketLogisticsCoordinator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarketLogisticsCoordinator"/> class.
    /// </summary>
    /// <param name="marketShoppingService">Service for calculating shopping plans.</param>
    /// <param name="cardFactory">Factory for creating UI card elements.</param>
    /// <param name="logger">Logger instance.</param>
    public MarketLogisticsCoordinator(
        IMarketShoppingService marketShoppingService,
        ICardFactory cardFactory,
        ILogger<MarketLogisticsCoordinator> logger)
    {
        _marketShoppingService = marketShoppingService;
        _cardFactory = cardFactory;
        _logger = logger;
    }

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
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3d3e2d")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var stackPanel = new StackPanel();
        
        var header = new TextBlock
        {
            Text = "Market Board Items",
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        
        var content = new TextBlock
        {
            Text = message,
            Foreground = Brushes.LightGray,
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

        foreach (var material in plan.AggregatedMaterials)
        {
            if (prices.TryGetValue(material.ItemId, out var priceInfo))
            {
                switch (priceInfo.Source)
                {
                    case PriceSource.Vendor:
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

        return (vendorItems, marketItems, untradeableItems);
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
                WorldName = "Vendor",
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
                        RetainerName = "Vendor",
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
                Vendors = priceInfo.Vendors?.ToList() ?? new List<GarlandVendor>(),
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

    /// <summary>
    /// Creates a placeholder card for the "no data" state.
    /// Delegates to ICardFactory for actual UI creation.
    /// </summary>
    /// <returns>A configured Border element representing the placeholder.</returns>
    public Border CreatePlaceholderCard()
    {
        return _cardFactory.CreatePlaceholder(
            "Market Board Items",
            "Click 'Fetch Market Data' to get current market board prices and shopping recommendations.");
    }

    /// <summary>
    /// Creates an error card for displaying error messages.
    /// Delegates to ICardFactory for actual UI creation.
    /// </summary>
    /// <param name="errorMessage">The error message to display.</param>
    /// <returns>A configured Border element representing the error card.</returns>
    public Border CreateErrorCard(string errorMessage)
    {
        return _cardFactory.CreateErrorCard("Error", errorMessage);
    }
}
