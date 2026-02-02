using System.Net.Http;
using System.IO;
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
        CancellationToken ct = default,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost)
    {
        var plans = new List<DetailedShoppingPlan>();

        foreach (var item in marketItems)
        {
            progress?.Report($"Analyzing {item.Name}...");
            
            try
            {
                // Fetch detailed listings from the entire DC
                var marketData = await _universalisService.GetMarketDataAsync(dataCenter, item.ItemId, ct: ct);
                
                var plan = CalculateItemShoppingPlan(
                    item.Name,
                    item.ItemId,
                    item.TotalQuantity,
                    marketData,
                    mode);
                
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
        UniversalisResponse marketData,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost)
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

        // Filter out "all around worse" worlds (those where even the best listing is bad)
        plan.WorldOptions = FilterWorldOptions(plan.WorldOptions, mode, plan.DCAveragePrice);

        // Sort based on recommendation mode
        plan.WorldOptions = mode switch
        {
            RecommendationMode.BestUnitPrice => plan.WorldOptions
                .OrderBy(w => w.ValueScore)
                .ThenBy(w => w.WorldName)
                .ToList(),
            RecommendationMode.MaximizeValue => plan.WorldOptions
                .OrderBy(w => w.ValueScore)
                .ThenBy(w => w.TotalCost)
                .ToList(),
            _ => plan.WorldOptions
                .OrderBy(w => w.TotalCost)
                .ThenBy(w => w.WorldName)
                .ToList()
        };

        // Set recommended option
        plan.RecommendedWorld = plan.WorldOptions.FirstOrDefault();

        return plan;
    }
    
    /// <summary>
    /// Filter out worlds that are "all around worse" - where even the best listing is not competitive.
    /// </summary>
    private List<WorldShoppingSummary> FilterWorldOptions(
        List<WorldShoppingSummary> options, 
        RecommendationMode mode,
        decimal dcAveragePrice)
    {
        if (options.Count <= 3)
            return options; // Keep all if few options
        
        // Find the best value score across all worlds
        var bestValueScore = options.Min(w => w.ValueScore);
        
        // Filter worlds where best listing is >20% worse than the best available
        // OR worlds with no competitive listings at all
        var filtered = options.Where(w => 
        {
            // Always keep worlds with good unit prices
            if (w.ValueScore <= bestValueScore * 1.2m)
                return true;
            
            // Keep worlds with reasonable total cost
            var minTotalCost = options.Min(o => o.TotalCost);
            if (w.TotalCost <= minTotalCost * 1.5m)
                return true;
            
            // Keep worlds with listings under DC average
            if (w.IsFullyUnderAverage)
                return true;
            
            return false;
        }).ToList();
        
        // Ensure we keep at least 3 options if available
        if (filtered.Count < 3 && options.Count >= 3)
        {
            // Add back the next best options
            var remaining = options.Except(filtered);
            filtered.AddRange(remaining.Take(3 - filtered.Count));
        }
        
        return filtered;
    }

    /// <summary>
    /// Calculate a summary for purchasing from a specific world.
    /// Applies the filtering logic for small listings.
    /// IMPORTANT: FFXIV requires buying FULL LISTINGS, not partial stacks.
    /// Shows top listings for value comparison.
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

        // Track the best single listing for value comparison
        var bestListing = listings.FirstOrDefault();
        if (bestListing != null)
        {
            summary.BestSingleListing = new ShoppingListingEntry
            {
                Quantity = bestListing.Quantity,
                PricePerUnit = bestListing.PricePerUnit,
                RetainerName = bestListing.RetainerName,
                IsUnderAverage = bestListing.PricePerUnit <= dcAveragePrice,
                IsHq = bestListing.IsHq
            };
        }

        var remaining = quantityNeeded;
        long totalCost = 0;
        int listingsUsed = 0;

        // First pass: identify which listings we can use to fulfill quantity
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
                IsHq = listing.IsHq,
                NeededFromStack = Math.Min(listing.Quantity, quantityNeeded),  // What we actually need
                ExcessQuantity = Math.Max(0, listing.Quantity - quantityNeeded)  // Excess we'll have
            });
        }

        // Second pass: add top 2 additional listings for value comparison (even if not needed for quantity)
        var additionalListings = listings
            .Where(l => !summary.Listings.Any(sl => sl.RetainerName == l.RetainerName && sl.Quantity == l.Quantity))
            .Take(2);
            
        foreach (var listing in additionalListings)
        {
            summary.Listings.Add(new ShoppingListingEntry
            {
                Quantity = listing.Quantity,
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsUnderAverage = listing.PricePerUnit <= dcAveragePrice,
                IsHq = listing.IsHq,
                IsAdditionalOption = true,  // Not needed for quantity, shown for value
                NeededFromStack = 0,
                ExcessQuantity = listing.Quantity
            });
        }

        // Only return summary if we can fulfill the entire quantity
        if (remaining > 0)
            return null;

        summary.TotalCost = totalCost;
        summary.AveragePricePerUnit = (decimal)totalCost / quantityNeeded;
        summary.ListingsUsed = listingsUsed;
        summary.IsFullyUnderAverage = summary.Listings.Where(l => !l.IsAdditionalOption).All(l => l.IsUnderAverage);
        summary.TotalQuantityPurchased = summary.Listings.Where(l => !l.IsAdditionalOption).Sum(l => l.Quantity);
        summary.ExcessQuantity = summary.TotalQuantityPurchased - quantityNeeded;

        return summary;
    }

    // North American Data Centers for cross-DC travel searches
    private static readonly string[] NorthAmericanDCs = { "Aether", "Primal", "Crystal", "Dynamis" };

    /// <summary>
    /// Calculate shopping plans searching across all NA Data Centers for potential savings.
    /// Uses sequential fetching with retry logic to avoid timeouts.
    /// </summary>
    public async Task<List<DetailedShoppingPlan>> CalculateDetailedShoppingPlansMultiDCAsync(
        List<MaterialAggregate> marketItems,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost)
    {
        var plans = new List<DetailedShoppingPlan>();

        foreach (var item in marketItems)
        {
            progress?.Report($"Analyzing {item.Name} across all NA DCs...");
            
            try
            {
                // Fetch from all NA DCs sequentially to avoid overwhelming the API
                var allListings = new List<MarketListing>();
                decimal globalAverage = 0;
                int dcCount = 0;

                foreach (var dc in NorthAmericanDCs)
                {
                    try
                    {
                        // Use retry logic for each DC
                        var marketData = await FetchWithRetryAsync(dc, item.ItemId, ct);
                        
                        if (marketData == null)
                        {
                            _logger.LogWarning("[MarketShopping] No data returned from {DC} for {Item}", dc, item.Name);
                            continue;
                        }
                        
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
                        
                        // Small delay between DCs to be nice to the API
                        await Task.Delay(100, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to fetch data from {DC} for {Item} after retries", dc, item.Name);
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
                    combinedData,
                    mode);
                
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
    /// Fetch market data with retry logic for transient failures.
    /// </summary>
    private async Task<UniversalisResponse?> FetchWithRetryAsync(string dc, int itemId, CancellationToken ct, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 second timeout per attempt
                
                return await _universalisService.GetMarketDataAsync(dc, itemId, ct: timeoutCts.Token);
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransientError(ex))
            {
                _logger.LogWarning(ex, "[MarketShopping] Attempt {Attempt} failed for {DC}/{Item}, retrying...", attempt, dc, itemId);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct); // Exponential backoff
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MarketShopping] All retries exhausted for {DC}/{Item}", dc, itemId);
                throw;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Check if an error is transient (can be retried).
    /// </summary>
    private bool IsTransientError(Exception ex)
    {
        // Timeout, gateway errors, and rate limiting are transient
        if (ex is HttpRequestException hre)
        {
            var statusCode = (int?)hre.StatusCode;
            return statusCode is 408 or 429 or 502 or 503 or 504;
        }
        
        if (ex is TaskCanceledException || ex is OperationCanceledException)
            return true;
            
        if (ex is IOException)
            return true;
            
        return false;
    }

    /// <summary>
    /// Calculate craft-vs-buy analysis for all craftable items in a plan.
    /// Compares cost of buying finished product vs crafting from components.
    /// Includes HQ/NQ analysis for endgame crafting considerations.
    /// </summary>
    public List<CraftVsBuyAnalysis> AnalyzeCraftVsBuy(CraftingPlan plan, Dictionary<int, PriceInfo> marketPrices)
    {
        var analyses = new List<CraftVsBuyAnalysis>();
        
        foreach (var rootItem in plan.RootItems)
        {
            AnalyzeNodeCraftVsBuy(rootItem, marketPrices, analyses);
        }
        
        return analyses.OrderByDescending(a => a.PotentialSavingsNq).ToList();
    }
    
    private void AnalyzeNodeCraftVsBuy(PlanNode node, Dictionary<int, PriceInfo> marketPrices, List<CraftVsBuyAnalysis> analyses)
    {
        // Only analyze craftable items (have children)
        if (node.Children.Any())
        {
            marketPrices.TryGetValue(node.ItemId, out var priceInfo);
            
            // NQ Price (default for most analysis)
            var buyPriceNq = priceInfo?.UnitPrice * node.Quantity ?? 0;
            
            // HQ Price (important for endgame)
            var buyPriceHq = priceInfo?.HqUnitPrice * node.Quantity ?? 0;
            var hasHqData = priceInfo?.HasHqData ?? false;
            
            // Calculate cost of all components
            var componentCost = CalculateComponentCost(node, marketPrices);
            
            // NQ Analysis: Positive = buy is more expensive (craft saves money)
            var savingsNq = buyPriceNq - componentCost;
            var savingsPercentNq = buyPriceNq > 0 ? (savingsNq / buyPriceNq) * 100 : 0;
            
            // HQ Analysis
            var savingsHq = hasHqData ? buyPriceHq - componentCost : 0;
            var savingsPercentHq = (hasHqData && buyPriceHq > 0) ? (savingsHq / buyPriceHq) * 100 : 0;
            
            analyses.Add(new CraftVsBuyAnalysis
            {
                ItemId = node.ItemId,
                ItemName = node.Name,
                Quantity = node.Quantity,
                BuyCostNq = buyPriceNq,
                CraftCost = componentCost,
                PotentialSavingsNq = savingsNq,
                SavingsPercentNq = savingsPercentNq,
                BuyCostHq = buyPriceHq,
                PotentialSavingsHq = savingsHq,
                SavingsPercentHq = savingsPercentHq,
                HasHqData = hasHqData,
                IsCurrentlySetToCraft = !node.IsBuy,
                RecommendationNq = savingsNq > 0 
                    ? CraftRecommendation.Craft 
                    : CraftRecommendation.Buy,
                RecommendationHq = hasHqData && savingsHq > 0
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
