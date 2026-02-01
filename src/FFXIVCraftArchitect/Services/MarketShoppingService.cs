using FFXIVCraftArchitect.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Service for calculating optimal market board shopping plans.
/// Groups listings by world and applies intelligent filtering.
/// </summary>
public class MarketShoppingService
{
    private readonly UniversalisService _universalisService;
    private readonly ILogger<MarketShoppingService> _logger;

    public MarketShoppingService(UniversalisService universalisService, ILogger<MarketShoppingService> logger)
    {
        _universalisService = universalisService;
        _logger = logger;
    }

    /// <summary>
    /// Calculate detailed shopping plans for market board items.
    /// </summary>
    public async Task<List<DetailedShoppingPlan>> CalculateDetailedShoppingPlansAsync(
        List<MaterialAggregate> marketItems,
        string dataCenter,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var plans = new List<DetailedShoppingPlan>();

        foreach (var item in marketItems)
        {
            progress?.Report($"Analyzing {item.Name}...");
            
            try
            {
                // Fetch detailed listings from the entire DC
                var marketData = await _universalisService.GetMarketDataAsync(dataCenter, item.ItemId, ct);
                
                var plan = CalculateItemShoppingPlan(
                    item.Name,
                    item.ItemId,
                    item.TotalQuantity,
                    marketData);
                
                plans.Add(plan);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to calculate shopping plan for {ItemName}", item.Name);
                plans.Add(new DetailedShoppingPlan
                {
                    ItemId = item.ItemId,
                    Name = item.Name,
                    QuantityNeeded = item.TotalQuantity,
                    Error = ex.Message
                });
            }
        }

        return plans;
    }

    /// <summary>
    /// Calculate optimal shopping plan for a single item with world-grouped listings.
    /// </summary>
    private DetailedShoppingPlan CalculateItemShoppingPlan(
        string itemName,
        int itemId,
        int quantityNeeded,
        UniversalisResponse marketData)
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = itemName,
            QuantityNeeded = quantityNeeded,
            DCAveragePrice = (decimal)marketData.AveragePrice
        };

        if (marketData.Listings.Count == 0)
        {
            plan.Error = "No market listings found";
            return plan;
        }

        // Group listings by world
        var listingsByWorld = marketData.Listings
            .GroupBy(l => l.WorldName)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.PricePerUnit).ToList());

        // Calculate per-world summaries
        foreach (var (worldName, listings) in listingsByWorld)
        {
            var worldSummary = CalculateWorldSummary(worldName, listings, quantityNeeded, plan.DCAveragePrice);
            if (worldSummary != null)
            {
                plan.WorldOptions.Add(worldSummary);
            }
        }

        // Sort world options by total cost
        plan.WorldOptions = plan.WorldOptions
            .OrderBy(w => w.TotalCost)
            .ThenBy(w => w.WorldName)
            .ToList();

        // Set recommended option (cheapest viable)
        plan.RecommendedWorld = plan.WorldOptions.FirstOrDefault();

        return plan;
    }

    /// <summary>
    /// Calculate a summary for purchasing from a specific world.
    /// Applies the filtering logic for small listings.
    /// </summary>
    private WorldShoppingSummary? CalculateWorldSummary(
        string worldName,
        List<MarketListing> listings,
        int quantityNeeded,
        decimal dcAveragePrice)
    {
        var summary = new WorldShoppingSummary
        {
            WorldName = worldName,
            Listings = new List<ShoppingListingEntry>()
        };

        var remaining = quantityNeeded;
        long totalCost = 0;
        int listingsUsed = 0;

        // First pass: identify which listings we can use
        foreach (var listing in listings)
        {
            if (remaining <= 0)
                break;

            var isUnderAverage = listing.PricePerUnit <= dcAveragePrice;
            var meetsMinimumQty = listing.Quantity >= quantityNeeded * 0.20;

            // Include if:
            // 1. Has >= 20% of needed quantity, OR
            // 2. Is under DC average price (can combine multiple)
            var shouldInclude = meetsMinimumQty || isUnderAverage;

            if (!shouldInclude)
                continue;

            var toBuy = Math.Min(listing.Quantity, remaining);
            totalCost += toBuy * listing.PricePerUnit;
            remaining -= toBuy;
            listingsUsed++;

            summary.Listings.Add(new ShoppingListingEntry
            {
                Quantity = toBuy,
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsUnderAverage = isUnderAverage
            });
        }

        // Only return summary if we can fulfill the entire quantity
        if (remaining > 0)
            return null;

        summary.TotalCost = totalCost;
        summary.AveragePricePerUnit = (decimal)totalCost / quantityNeeded;
        summary.ListingsUsed = listingsUsed;
        summary.IsFullyUnderAverage = summary.Listings.All(l => l.IsUnderAverage);

        return summary;
    }
}

/// <summary>
/// Detailed shopping plan for a single item with world-specific options.
/// </summary>
public class DetailedShoppingPlan
{
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int QuantityNeeded { get; set; }
    public decimal DCAveragePrice { get; set; }
    public List<WorldShoppingSummary> WorldOptions { get; set; } = new();
    public WorldShoppingSummary? RecommendedWorld { get; set; }
    public string? Error { get; set; }

    public bool HasOptions => WorldOptions.Count > 0;
}

/// <summary>
/// Shopping summary for a specific world.
/// </summary>
public class WorldShoppingSummary
{
    public string WorldName { get; set; } = string.Empty;
    public long TotalCost { get; set; }
    public decimal AveragePricePerUnit { get; set; }
    public int ListingsUsed { get; set; }
    public List<ShoppingListingEntry> Listings { get; set; } = new();
    public bool IsFullyUnderAverage { get; set; }

    public string CostDisplay => $"{TotalCost:N0}g";
    public string PricePerUnitDisplay => $"{AveragePricePerUnit:N0}g";
}

/// <summary>
/// Individual listing entry in a shopping plan.
/// </summary>
public class ShoppingListingEntry
{
    public int Quantity { get; set; }
    public long PricePerUnit { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public bool IsUnderAverage { get; set; }

    public string SubtotalDisplay => $"{(Quantity * PricePerUnit):N0}g";
}
