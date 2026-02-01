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
    /// IMPORTANT: FFXIV requires buying FULL LISTINGS, not partial stacks.
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

            // IMPORTANT: FFXIV requires buying the FULL LISTING, not just what you need
            // Calculate cost for entire stack
            var fullStackCost = listing.Quantity * listing.PricePerUnit;
            totalCost += fullStackCost;
            remaining -= listing.Quantity;
            listingsUsed++;

            summary.Listings.Add(new ShoppingListingEntry
            {
                Quantity = listing.Quantity,  // Full stack quantity
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsUnderAverage = isUnderAverage,
                NeededFromStack = Math.Min(listing.Quantity, quantityNeeded),  // What we actually need
                ExcessQuantity = Math.Max(0, listing.Quantity - quantityNeeded)  // Excess we'll have
            });
        }

        // Only return summary if we can fulfill the entire quantity
        if (remaining > 0)
            return null;

        summary.TotalCost = totalCost;
        summary.AveragePricePerUnit = (decimal)totalCost / quantityNeeded;
        summary.ListingsUsed = listingsUsed;
        summary.IsFullyUnderAverage = summary.Listings.All(l => l.IsUnderAverage);
        summary.TotalQuantityPurchased = summary.Listings.Sum(l => l.Quantity);
        summary.ExcessQuantity = summary.TotalQuantityPurchased - quantityNeeded;

        return summary;
    }

    // North American Data Centers for cross-DC travel searches
    private static readonly string[] NorthAmericanDCs = { "Aether", "Primal", "Crystal", "Dynamis" };

    /// <summary>
    /// Calculate shopping plans searching across all NA Data Centers for potential savings.
    /// </summary>
    public async Task<List<DetailedShoppingPlan>> CalculateDetailedShoppingPlansMultiDCAsync(
        List<MaterialAggregate> marketItems,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var plans = new List<DetailedShoppingPlan>();

        foreach (var item in marketItems)
        {
            progress?.Report($"Analyzing {item.Name} across all NA DCs...");
            
            try
            {
                // Fetch from all NA DCs
                var allListings = new List<MarketListing>();
                decimal globalAverage = 0;
                int dcCount = 0;

                foreach (var dc in NorthAmericanDCs)
                {
                    try
                    {
                        var marketData = await _universalisService.GetMarketDataAsync(dc, item.ItemId, ct);
                        
                        // Add DC name to each listing's world name for identification
                        foreach (var listing in marketData.Listings)
                        {
                            // If world name is empty, use DC name
                            if (string.IsNullOrEmpty(listing.WorldName))
                            {
                                listing.WorldName = $"{dc}";
                            }
                            // Prefix with DC if not already there
                            else if (!listing.WorldName.Contains("("))
                            {
                                listing.WorldName = $"{listing.WorldName} ({dc})";
                            }
                        }
                        
                        allListings.AddRange(marketData.Listings);
                        
                        if (marketData.AveragePrice > 0)
                        {
                            globalAverage += (decimal)marketData.AveragePrice;
                            dcCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch data from {DC} for {Item}", dc, item.Name);
                    }
                }

                if (allListings.Count == 0)
                {
                    plans.Add(new DetailedShoppingPlan
                    {
                        ItemId = item.ItemId,
                        Name = item.Name,
                        QuantityNeeded = item.TotalQuantity,
                        Error = "No listings found on any NA Data Center"
                    });
                    continue;
                }

                // Calculate average across all DCs
                if (dcCount > 0)
                {
                    globalAverage /= dcCount;
                }

                // Create combined market data
                var combinedData = new UniversalisResponse
                {
                    ItemId = item.ItemId,
                    Listings = allListings,
                    AveragePrice = (double)globalAverage
                };

                var plan = CalculateItemShoppingPlan(
                    item.Name,
                    item.ItemId,
                    item.TotalQuantity,
                    combinedData);
                
                plans.Add(plan);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to calculate multi-DC shopping plan for {ItemName}", item.Name);
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
    /// Calculate craft-vs-buy analysis for all craftable items in a plan.
    /// Compares cost of buying finished product vs crafting from components.
    /// </summary>
    public List<CraftVsBuyAnalysis> AnalyzeCraftVsBuy(CraftingPlan plan, Dictionary<int, PriceInfo> marketPrices)
    {
        var analyses = new List<CraftVsBuyAnalysis>();
        
        foreach (var rootItem in plan.RootItems)
        {
            AnalyzeNodeCraftVsBuy(rootItem, marketPrices, analyses);
        }
        
        return analyses.OrderByDescending(a => a.PotentialSavings).ToList();
    }
    
    private void AnalyzeNodeCraftVsBuy(PlanNode node, Dictionary<int, PriceInfo> marketPrices, List<CraftVsBuyAnalysis> analyses)
    {
        // Only analyze craftable items (have children)
        if (node.Children.Any())
        {
            // Calculate cost to buy finished product
            var buyPrice = marketPrices.TryGetValue(node.ItemId, out var priceInfo) 
                ? priceInfo.UnitPrice * node.Quantity 
                : 0;
            
            // Calculate cost of all components
            var componentCost = CalculateComponentCost(node, marketPrices);
            
            // Calculate potential savings
            var savings = buyPrice - componentCost;
            var savingsPercent = buyPrice > 0 ? (savings / buyPrice) * 100 : 0;
            
            analyses.Add(new CraftVsBuyAnalysis
            {
                ItemId = node.ItemId,
                ItemName = node.Name,
                Quantity = node.Quantity,
                BuyCost = buyPrice,
                CraftCost = componentCost,
                PotentialSavings = savings,
                SavingsPercent = savingsPercent,
                IsCurrentlySetToCraft = !node.IsBuy,
                Recommendation = savings > 0 
                    ? CraftRecommendation.Craft 
                    : CraftRecommendation.Buy
            });
            
            // Recurse into children
            foreach (var child in node.Children)
            {
                AnalyzeNodeCraftVsBuy(child, marketPrices, analyses);
            }
        }
    }
    
    private decimal CalculateComponentCost(PlanNode node, Dictionary<int, PriceInfo> marketPrices)
    {
        decimal total = 0;
        
        foreach (var child in node.Children)
        {
            if (child.IsBuy || !child.Children.Any())
            {
                // Leaf or marked as buy - use market price
                if (marketPrices.TryGetValue(child.ItemId, out var priceInfo))
                {
                    total += priceInfo.UnitPrice * child.Quantity;
                }
            }
            else
            {
                // Craft this component - recurse
                total += CalculateComponentCost(child, marketPrices);
            }
        }
        
        return total;
    }
}

/// <summary>
/// Analysis comparing cost to buy vs craft an item.
/// </summary>
public class CraftVsBuyAnalysis
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal BuyCost { get; set; }
    public decimal CraftCost { get; set; }
    public decimal PotentialSavings { get; set; }
    public decimal SavingsPercent { get; set; }
    public bool IsCurrentlySetToCraft { get; set; }
    public CraftRecommendation Recommendation { get; set; }
    
    public string Summary => $"{ItemName} x{Quantity}: Buy {BuyCost:N0}g vs Craft {CraftCost:N0}g ({PotentialSavings:+0;-0;0}g)";
    public bool IsSignificantSavings => Math.Abs(PotentialSavings) > 1000 || Math.Abs(SavingsPercent) > 10;
}

public enum CraftRecommendation
{
    Buy,    // Cheaper to buy finished product
    Craft   // Cheaper to craft from components
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
    public int TotalQuantityPurchased { get; set; }
    public int ExcessQuantity { get; set; }

    public string CostDisplay => $"{TotalCost:N0}g";
    public string PricePerUnitDisplay => $"{AveragePricePerUnit:N0}g";
    public bool HasExcess => ExcessQuantity > 0;
}

/// <summary>
/// Individual listing entry in a shopping plan.
/// </summary>
public class ShoppingListingEntry
{
    public int Quantity { get; set; }  // Full stack quantity available
    public long PricePerUnit { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public bool IsUnderAverage { get; set; }
    public int NeededFromStack { get; set; }  // How many we actually need from this stack
    public int ExcessQuantity { get; set; }  // How many extra we'll have

    public string SubtotalDisplay => $"{(Quantity * PricePerUnit):N0}g";
    public string QuantityDisplay => ExcessQuantity > 0 
        ? $"x{Quantity} (need {NeededFromStack}, +{ExcessQuantity} extra)"
        : $"x{Quantity}";
}
