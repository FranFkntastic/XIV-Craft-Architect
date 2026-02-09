using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Core.Services;

/// <summary>
/// Service for calculating optimal market board shopping plans.
/// Groups listings by world and applies intelligent filtering.
/// Reads market data from IMarketCacheService - callers must ensure cache is populated first.
/// </summary>
public class MarketShoppingService
{
    private readonly IMarketCacheService _cacheService;
    private readonly IWorldStatusService? _worldStatusService;
    private readonly SettingsService? _settingsService;
    private readonly ILogger<MarketShoppingService>? _logger;
    private Dictionary<string, int> _worldNameToIdMapping = new();

    public MarketShoppingService(
        IMarketCacheService cacheService,
        IWorldStatusService? worldStatusService = null,
        SettingsService? settingsService = null,
        ILogger<MarketShoppingService>? logger = null)
    {
        _cacheService = cacheService;
        _worldStatusService = worldStatusService;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Sets the world name to ID mapping for travel prohibition checks.
    /// </summary>
    public void SetWorldNameToIdMapping(Dictionary<string, int> mapping)
    {
        _worldNameToIdMapping = mapping;
    }

    /// <summary>
    /// Calculate detailed shopping plans for market board items.
    /// Reads from cache only - callers must call IMarketCacheService.EnsurePopulatedAsync first.
    /// </summary>
    public async Task<List<DetailedShoppingPlan>> CalculateDetailedShoppingPlansAsync(
        List<MaterialAggregate> marketItems,
        string dataCenter,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost)
    {
        var plans = new List<DetailedShoppingPlan>();
        
        // Read all items from cache (callers must ensure cache is populated)
        foreach (var item in marketItems)
        {
            progress?.Report($"Analyzing {item.Name}...");
            ct.ThrowIfCancellationRequested();
            
            try
            {
                var cached = await _cacheService.GetAsync(item.ItemId, dataCenter);
                if (cached == null)
                {
                    plans.Add(new DetailedShoppingPlan
                    {
                        ItemId = item.ItemId,
                        Name = item.Name,
                        QuantityNeeded = item.TotalQuantity,
                        Error = "No market data in cache"
                    });
                    continue;
                }
                
                var marketData = ConvertFromCachedData(cached);
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
                _logger?.LogWarning(ex, "Failed to calculate shopping plan for {ItemName}", item.Name);
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
    /// Calculate shopping plans with multi-world split recommendations.
    /// Two-pass algorithm: First pass builds tier 1 recommendations, second pass optimizes splits.
    /// </summary>
    public async Task<List<DetailedShoppingPlan>> CalculateShoppingPlansWithSplitsAsync(
        List<MaterialAggregate> marketItems,
        string dataCenter,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisConfig? config = null)
    {
        config ??= new MarketAnalysisConfig();
        
        // Pass 1: Calculate basic plans for all items
        progress?.Report("Calculating base recommendations...");
        var plans = await CalculateDetailedShoppingPlansAsync(marketItems, dataCenter, progress, ct);
        
        // Build world route map from tier 1 recommendations
        var worldsInRoute = new HashSet<string>();
        foreach (var plan in plans.Where(p => p.RecommendedWorld != null))
        {
            worldsInRoute.Add(plan.RecommendedWorld!.WorldName);
        }
        
        _logger?.LogInformation("[CalculateShoppingPlansWithSplits] Initial route contains {Count} worlds: {Worlds}", 
            worldsInRoute.Count, string.Join(", ", worldsInRoute));
        
        // Pass 2: Calculate splits for items that need them
        progress?.Report("Optimizing multi-world purchases...");
        var itemsNeedingSplit = plans.Where(p => 
            p.RecommendedWorld == null || 
            p.RecommendedWorld.TotalQuantityPurchased < p.QuantityNeeded).ToList();
        
        foreach (var plan in itemsNeedingSplit)
        {
            CalculateSplitPurchase(plan, config, worldsInRoute);
            
            // Add split worlds to route for subsequent items
            if (plan.RecommendedSplit != null)
            {
                foreach (var split in plan.RecommendedSplit)
                {
                    worldsInRoute.Add(split.WorldName);
                }
            }
        }
        
        _logger?.LogInformation("[CalculateShoppingPlansWithSplits] Final route contains {Count} worlds", worldsInRoute.Count);
        
        return plans;
    }

    private UniversalisResponse ConvertFromCachedData(CachedMarketData cached)
    {
        var listings = new List<MarketListing>();
        
        foreach (var world in cached.Worlds)
        {
            foreach (var listing in world.Listings)
            {
                listings.Add(new MarketListing
                {
                    WorldName = world.WorldName,
                    Quantity = listing.Quantity,
                    PricePerUnit = listing.PricePerUnit,
                    RetainerName = listing.RetainerName,
                    IsHq = listing.IsHq
                });
            }
        }
        
        return new UniversalisResponse
        {
            ItemId = cached.ItemId,
            Listings = listings,
            AveragePrice = (double)cached.DCAveragePrice
        };
    }

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

        // Get user's home world (if set)
        var homeWorld = _settingsService?.Get<string>("market.home_world", "");
        var hasHomeWorld = !string.IsNullOrWhiteSpace(homeWorld);

        // Check if we should exclude congested worlds
        var excludeCongested = _settingsService?.Get<bool>("market.exclude_congested_worlds", true) ?? true;
        
        // Group listings by world
        var listingsByWorld = marketData.Listings
            .GroupBy(l => l.WorldName)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.PricePerUnit).ToList());

        // Calculate per-world summaries
        foreach (var (worldName, listings) in listingsByWorld)
        {
            var worldSummary = CalculateWorldSummary(worldName, listings, quantityNeeded, plan.DCAveragePrice, homeWorld);
            if (worldSummary != null)
            {
                // Skip congested worlds (except home world) if setting is enabled
                if (excludeCongested && worldSummary.IsCongested && !worldSummary.IsHomeWorld)
                {
                    _logger?.LogDebug("[MarketShopping] Excluding {World} - congested world", worldName);
                    continue;
                }
                
                plan.WorldOptions.Add(worldSummary);
            }
        }

        // Filter out "all around worse" worlds
        plan.WorldOptions = FilterWorldOptions(plan.WorldOptions, mode, plan.DCAveragePrice);

        // Sort based on recommendation mode
        plan.WorldOptions = mode switch
        {
            RecommendationMode.BestUnitPrice => plan.WorldOptions
                .OrderByDescending(w => w.IsHomeWorld)
                .ThenBy(w => w.IsCongested && !w.IsHomeWorld)
                .ThenBy(w => w.ValueScore)
                .ThenBy(w => w.WorldName)
                .ToList(),
            RecommendationMode.MaximizeValue => plan.WorldOptions
                .OrderByDescending(w => w.IsHomeWorld)
                .ThenBy(w => w.IsCongested && !w.IsHomeWorld)
                .ThenBy(w => w.ValueScore)
                .ThenBy(w => w.TotalCost)
                .ToList(),
            _ => plan.WorldOptions
                .OrderByDescending(w => w.IsHomeWorld)
                .ThenBy(w => w.IsCongested && !w.IsHomeWorld)
                .ThenBy(w => w.TotalCost)
                .ThenBy(w => w.WorldName)
                .ToList()
        };

        // Set recommended option
        plan.RecommendedWorld = plan.WorldOptions.FirstOrDefault();

        return plan;
    }

    private List<WorldShoppingSummary> FilterWorldOptions(
        List<WorldShoppingSummary> options, 
        RecommendationMode mode,
        decimal dcAveragePrice)
    {
        if (options.Count <= 3)
            return options;
        
        var bestValueScore = options.Min(w => w.ValueScore);
        
        var filtered = options.Where(w => 
        {
            if (w.ValueScore <= bestValueScore * 1.2m)
                return true;
            
            var minTotalCost = options.Min(o => o.TotalCost);
            if (w.TotalCost <= minTotalCost * 1.5m)
                return true;
            
            if (w.IsFullyUnderAverage)
                return true;
            
            return false;
        }).ToList();
        
        if (filtered.Count < 3 && options.Count >= 3)
        {
            var remaining = options.Except(filtered);
            filtered.AddRange(remaining.Take(3 - filtered.Count));
        }
        
        return filtered;
    }

    private WorldShoppingSummary? CalculateWorldSummary(
        string worldName,
        List<MarketListing> listings,
        int quantityNeeded,
        decimal dcAveragePrice,
        string? homeWorld = null)
    {
        // Get world status (if available)
        var worldStatus = _worldStatusService?.GetWorldStatus(worldName);
        
        var isHomeWorld = !string.IsNullOrWhiteSpace(homeWorld) && 
                         worldName.Equals(homeWorld, StringComparison.OrdinalIgnoreCase);
        
        var summary = new WorldShoppingSummary
        {
            WorldName = worldName,
            Listings = new List<ShoppingListingEntry>(),
            IsHomeWorld = isHomeWorld,
            Classification = worldStatus?.Classification ?? WorldClassification.Standard
        };

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

        // For very large orders (10k+), lower the minimum stack threshold
        // to ensure we can aggregate enough listings
        var minStackThreshold = quantityNeeded > 10000 ? 1 : (int)(quantityNeeded * 0.20);
        
        foreach (var listing in listings)
        {
            if (remaining <= 0)
                break;

            var isUnderAverage = listing.PricePerUnit <= dcAveragePrice;
            // For large orders, include any reasonably priced listing
            // For small orders, require meaningful stack size
            var meetsMinimumQty = quantityNeeded > 10000 
                ? listing.Quantity >= 1  // Include any for large orders
                : listing.Quantity >= minStackThreshold;
            var shouldInclude = meetsMinimumQty || isUnderAverage;

            if (!shouldInclude)
                continue;

            var fullStackCost = listing.Quantity * listing.PricePerUnit;
            totalCost += fullStackCost;
            remaining -= listing.Quantity;
            listingsUsed++;

            summary.Listings.Add(new ShoppingListingEntry
            {
                Quantity = listing.Quantity,
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsUnderAverage = isUnderAverage,
                IsHq = listing.IsHq,
                NeededFromStack = Math.Min(listing.Quantity, quantityNeeded),
                ExcessQuantity = Math.Max(0, listing.Quantity - quantityNeeded)
            });
        }

        // Second pass: add top 2 additional listings for value comparison
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
                IsAdditionalOption = true,
                NeededFromStack = 0,
                ExcessQuantity = listing.Quantity
            });
        }

        // For large orders, don't return null if we can't fulfill full quantity
        // Let the split calculation handle combining worlds
        if (remaining > 0 && quantityNeeded <= 10000)
        {
            _logger?.LogDebug("[CalculateWorldSummary] {World} - Insufficient stock for {Quantity} (remaining: {Remaining}), returning null", 
                worldName, quantityNeeded, remaining);
            return null;
        }

        summary.TotalCost = totalCost;
        summary.TotalQuantityPurchased = summary.Listings.Where(l => !l.IsAdditionalOption).Sum(l => l.Quantity);
        summary.AveragePricePerUnit = summary.TotalQuantityPurchased > 0 
            ? (decimal)totalCost / summary.TotalQuantityPurchased 
            : 0;
        summary.ListingsUsed = listingsUsed;
        summary.IsFullyUnderAverage = summary.Listings.Where(l => !l.IsAdditionalOption).All(l => l.IsUnderAverage);
        summary.ExcessQuantity = summary.TotalQuantityPurchased - quantityNeeded;
        summary.HasSufficientStock = remaining <= 0;
        summary.ShortfallQuantity = remaining > 0 ? remaining : 0;

        _logger?.LogDebug("[CalculateWorldSummary] {World} - Purchased: {Purchased}/{Needed}, Cost: {Cost:N0}g, Sufficient: {Sufficient}", 
            worldName, summary.TotalQuantityPurchased, quantityNeeded, totalCost, summary.HasSufficientStock);

        return summary;
    }

    // North American Data Centers for cross-DC travel searches
    private static readonly string[] NorthAmericanDCs = { "Aether", "Primal", "Crystal", "Dynamis" };

    /// <summary>
    /// Calculate shopping plans searching across all NA Data Centers for potential savings.
    /// Reads from cache only - callers must ensure cache is populated first.
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
            ct.ThrowIfCancellationRequested();
            
            try
            {
                var allListings = new List<MarketListing>();
                decimal globalAverage = 0;
                int dcCount = 0;

                // Read from cache for each DC (callers must ensure cache is populated)
                foreach (var dc in NorthAmericanDCs)
                {
                    try
                    {
                        var cached = await _cacheService.GetAsync(item.ItemId, dc);
                        
                        if (cached == null)
                        {
                            _logger?.LogWarning("[MarketShopping] No cached data for {Item}@{DC}", item.Name, dc);
                            continue;
                        }
                        
                        // Convert cached data to listings
                        foreach (var world in cached.Worlds)
                        {
                            foreach (var listing in world.Listings)
                            {
                                allListings.Add(new MarketListing
                                {
                                    WorldName = $"{world.WorldName} ({dc})",
                                    Quantity = listing.Quantity,
                                    PricePerUnit = listing.PricePerUnit,
                                    RetainerName = listing.RetainerName,
                                    IsHq = listing.IsHq
                                });
                            }
                        }
                        
                        globalAverage += cached.DCAveragePrice;
                        dcCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to read cached data for {Item}@{DC}", item.Name, dc);
                    }
                }

                if (allListings.Count == 0)
                {
                    plans.Add(new DetailedShoppingPlan
                    {
                        ItemId = item.ItemId,
                        Name = item.Name,
                        QuantityNeeded = item.TotalQuantity,
                        Error = "No cached data found on any NA Data Center"
                    });
                    continue;
                }

                if (dcCount > 0)
                {
                    globalAverage /= dcCount;
                }

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
                _logger?.LogWarning(ex, "Failed to calculate multi-DC shopping plan for {ItemName}", item.Name);
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
        if (node.Children.Any())
        {
            marketPrices.TryGetValue(node.ItemId, out var priceInfo);
            
            var buyPriceNq = priceInfo?.UnitPrice * node.Quantity ?? 0;
            var buyPriceHq = priceInfo?.HqUnitPrice * node.Quantity ?? 0;
            var hasHqData = priceInfo?.HasHqData ?? false;
            
            var componentCost = CalculateComponentCost(node, marketPrices);
            
            var savingsNq = buyPriceNq - componentCost;
            var savingsPercentNq = buyPriceNq > 0 ? (savingsNq / buyPriceNq) * 100 : 0;
            
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
                IsHqRequired = node.MustBeHq,
                IsCurrentlySetToCraft = !node.IsBuy,
                RecommendationNq = savingsNq > 0 ? CraftRecommendation.Craft : CraftRecommendation.Buy,
                RecommendationHq = hasHqData && savingsHq > 0 ? CraftRecommendation.Craft : CraftRecommendation.Buy
            });
            
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
                if (marketPrices.TryGetValue(child.ItemId, out var priceInfo))
                {
                    total += priceInfo.UnitPrice * child.Quantity;
                }
            }
            else
            {
                total += CalculateComponentCost(child, marketPrices);
            }
        }
        
        return total;
    }

    // ========================================================================
    // Multi-World Split Purchase Calculation
    // ========================================================================

    /// <summary>
    /// Calculates a multi-world split purchase plan for items that can't be fulfilled on a single world.
    /// Uses a two-pass algorithm: first pass identifies tier 1 worlds, second pass optimizes splits.
    /// </summary>
    public void CalculateSplitPurchase(
        DetailedShoppingPlan plan, 
        MarketAnalysisConfig config,
        HashSet<string>? worldsInRoute = null)
    {
        worldsInRoute ??= new HashSet<string>();
        
        // Check if we already have a viable single-world option
        if (plan.RecommendedWorld != null && plan.RecommendedWorld.TotalQuantityPurchased >= plan.QuantityNeeded)
        {
            _logger?.LogDebug("[CalculateSplitPurchase] {Item} - Single world option sufficient on {World}", 
                plan.Name, plan.RecommendedWorld.WorldName);
            return;
        }
        
        _logger?.LogInformation("[CalculateSplitPurchase] {Item} - Calculating multi-world split for {Quantity} units", 
            plan.Name, plan.QuantityNeeded);
        
        // Pass 1: Get all worlds with any stock, sorted by price
        var viableWorlds = plan.WorldOptions
            .Where(w => w.TotalQuantityPurchased > 0)
            .OrderBy(w => w.AveragePricePerUnit)
            .ToList();
        
        if (viableWorlds.Count < 2)
        {
            _logger?.LogWarning("[CalculateSplitPurchase] {Item} - Not enough worlds with stock for split", plan.Name);
            return;
        }
        
        // Calculate soft limit based on tier 1 count
        var tier1Count = viableWorlds.Count(w => w.IsFullyUnderAverage || w.ValueScore <= viableWorlds.Min(v => v.ValueScore) * 1.2m);
        var maxWorlds = config.GetEffectiveMaxWorlds(tier1Count);
        
        _logger?.LogDebug("[CalculateSplitPurchase] {Item} - Tier 1 worlds: {Tier1}, Max worlds: {Max}", 
            plan.Name, tier1Count, maxWorlds);
        
        // Pass 2: Greedy allocation with travel consolidation
        var split = new List<SplitWorldPurchase>();
        var remaining = plan.QuantityNeeded;
        var worldsUsed = new HashSet<string>();
        
        // Priority 1: Worlds already in our route (consolidation)
        if (config.PreferConsolidatedWorlds)
        {
            var consolidatedWorlds = viableWorlds
                .Where(w => worldsInRoute.Contains(w.WorldName) && !worldsUsed.Contains(w.WorldName))
                .OrderBy(w => w.AveragePricePerUnit);
            
            foreach (var world in consolidatedWorlds)
            {
                if (remaining <= 0 || worldsUsed.Count >= maxWorlds) break;
                
                var allocation = AllocateFromWorld(world, remaining, isConsolidated: true);
                if (allocation.QuantityToBuy > 0)
                {
                    split.Add(allocation);
                    remaining -= allocation.QuantityToBuy;
                    worldsUsed.Add(world.WorldName);
                }
            }
        }
        
        // Priority 2: Cheapest remaining worlds
        var remainingWorlds = viableWorlds
            .Where(w => !worldsUsed.Contains(w.WorldName))
            .OrderBy(w => w.AveragePricePerUnit);
        
        foreach (var world in remainingWorlds)
        {
            if (remaining <= 0 || worldsUsed.Count >= maxWorlds) break;
            
            var allocation = AllocateFromWorld(world, remaining, isConsolidated: false);
            if (allocation.QuantityToBuy > 0)
            {
                split.Add(allocation);
                remaining -= allocation.QuantityToBuy;
                worldsUsed.Add(world.WorldName);
            }
        }
        
        // Check if split is viable and cost-effective
        if (split.Count == 0 || (remaining > 0 && worldsUsed.Count >= maxWorlds))
        {
            _logger?.LogWarning("[CalculateSplitPurchase] {Item} - Could not fulfill full quantity. Remaining: {Remaining}", 
                plan.Name, remaining);
            // Still save partial split, but mark as incomplete
        }
        
        var splitCost = split.Sum(s => s.TotalCost);
        var singleWorldCost = plan.RecommendedWorld?.TotalCost ?? long.MaxValue;
        var savingsPercent = singleWorldCost > 0 ? (singleWorldCost - splitCost) / (decimal)singleWorldCost * 100 : 0;
        
        _logger?.LogInformation("[CalculateSplitPurchase] {Item} - Split cost: {SplitCost:N0}g vs Single: {SingleCost:N0}g ({Savings:F1}% savings)", 
            plan.Name, splitCost, singleWorldCost, savingsPercent);
        
        // Only recommend split if it meets savings threshold OR single world failed
        if (plan.RecommendedWorld == null || config.MeetsSavingsThreshold(savingsPercent) || remaining <= 0)
        {
            plan.RecommendedSplit = split;
            _logger?.LogInformation("[CalculateSplitPurchase] {Item} - Recommending split across {Count} worlds", 
                plan.Name, split.Count);
        }
        else
        {
            _logger?.LogInformation("[CalculateSplitPurchase] {Item} - Split savings ({Savings:F1}%) below threshold ({Threshold:F1}%), using single world", 
                plan.Name, savingsPercent, config.MinSplitSavingsPercent);
        }
    }
    
    private SplitWorldPurchase AllocateFromWorld(WorldShoppingSummary world, int quantityNeeded, bool isConsolidated)
    {
        var availableFromListings = world.Listings
            .Where(l => !l.IsAdditionalOption)
            .Sum(l => l.Quantity);
        
        var toAllocate = Math.Min(quantityNeeded, availableFromListings);
        
        // Calculate actual cost based on listings
        var cost = 0L;
        var remaining = toAllocate;
        var usedListings = new List<ShoppingListingEntry>();
        
        foreach (var listing in world.Listings.Where(l => !l.IsAdditionalOption).OrderBy(l => l.PricePerUnit))
        {
            if (remaining <= 0) break;
            
            var fromThisListing = Math.Min(remaining, listing.Quantity);
            cost += fromThisListing * listing.PricePerUnit;
            remaining -= fromThisListing;
            
            usedListings.Add(new ShoppingListingEntry
            {
                Quantity = listing.Quantity,
                PricePerUnit = listing.PricePerUnit,
                RetainerName = listing.RetainerName,
                IsUnderAverage = listing.IsUnderAverage,
                IsHq = listing.IsHq,
                NeededFromStack = fromThisListing
            });
        }
        
        return new SplitWorldPurchase
        {
            WorldName = world.WorldName,
            QuantityToBuy = toAllocate,
            PricePerUnit = toAllocate > 0 ? cost / (decimal)toAllocate : world.AveragePricePerUnit,
            IsPartial = toAllocate < world.TotalQuantityPurchased,
            TravelContext = isConsolidated ? "Consolidated" : (usedListings.Count > 0 ? "Primary" : "Supplemental"),
            ExcessAvailable = availableFromListings - toAllocate,
            Listings = usedListings
        };
    }

    // ========================================================================
    // Item Categorization
    // ========================================================================

    /// <summary>
    /// Categorizes materials by their price source (Vendor, Market, or Untradeable).
    /// </summary>
    public CategorizedMaterials CategorizeMaterials(List<MaterialAggregate> materials, Dictionary<int, PriceInfo> prices)
    {
        var result = new CategorizedMaterials();

        foreach (var material in materials)
        {
            if (prices.TryGetValue(material.ItemId, out var priceInfo))
            {
                switch (priceInfo.Source)
                {
                    case PriceSource.Vendor:
                        result.VendorItems.Add(material);
                        break;
                    case PriceSource.Untradeable:
                        result.UntradeableItems.Add(material);
                        break;
                    case PriceSource.Market:
                    default:
                        result.MarketItems.Add(material);
                        break;
                }
            }
            else
            {
                // No price info - assume market
                result.MarketItems.Add(material);
            }
        }

        return result;
    }
}

/// <summary>
/// Result of categorizing materials by price source.
/// </summary>
public class CategorizedMaterials
{
    public List<MaterialAggregate> VendorItems { get; } = new();
    public List<MaterialAggregate> MarketItems { get; } = new();
    public List<MaterialAggregate> UntradeableItems { get; } = new();
}
