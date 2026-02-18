using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Service for calculating optimal market board shopping plans from cached market data.
/// 
/// DATA FLOW:
/// 1. Input: List of MaterialAggregate from CraftingPlan.AggregateMaterials
/// 2. Filter: Separates vendor items, market items, and untradeable items
/// 3. For each market item:
///    - Reads from IMarketCacheService (caller must populate first)
///    - Groups listings by world
///    - Calculates ValueScore for each world (see below)
///    - Recommends best world (lowest ValueScore)
///    - Optionally calculates multi-world splits
/// 4. Output: List of DetailedShoppingPlan with recommendations
/// 
/// VALUESCORE ALGORITHM:
/// ValueScore is the primary metric for world ranking. Lower is better.
/// - Base: Total cost for needed quantity from that world
/// - Fraud filter: Excludes listings above (ModePrice × Multiplier) default 2.5x
/// - Congestion penalty: Adds 20% to congested worlds (except home)
/// - Travel penalty: Adds 15% to non-home worlds (user preference)
/// - Stock penalty: World must have sufficient quantity or score is MaxValue
/// 
/// MODE PRICE CALCULATION (anti-fraud):
/// Uses a two-pass algorithm to find the "typical" price:
/// 1. Find median of cheapest 50% of listings (fraud-resistant baseline)
/// 2. Calculate mode (most common price) from listings within 10x of baseline
/// This prevents fraudulent high-price listings from skewing the mode.
/// 
/// MULTI-WORLD SPLITS:
/// When EnableSplitWorld is true, the algorithm can recommend buying portions
/// of the needed quantity from different worlds. Uses greedy allocation
/// based on ValueScore with single-world contingency (prefers single world
/// if within 5% of optimal split cost).
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
    /// 
    /// ALGORITHM PER ITEM:
    /// 1. Read cached market data for item from IMarketCacheService
    /// 2. Group listings by world name
    /// 3. For each world:
    ///    - Calculate total cost for needed quantity
    ///    - Calculate mode price (anti-fraud baseline)
    ///    - Filter out listings above (ModePrice × MaxPriceMultiplier)
    ///    - Calculate ValueScore (see class documentation)
    ///    - Check world status (congested, travel prohibited)
    ///    - Apply filters (exclude congested, respect blacklist)
    /// 4. Sort worlds by ValueScore (ascending)
    /// 5. Set recommended world to first viable option
    /// 6. Return DetailedShoppingPlan with all options and recommendation
    /// 
    /// PREREQUISITE:
    /// Callers MUST populate the cache first via IMarketCacheService.EnsurePopulatedAsync
    /// before calling this method. This service reads from cache only.
    /// </summary>
    /// <param name="marketItems">Materials to analyze (from CraftingPlan.AggregatedMaterials)</param>
    /// <param name="dataCenter">Data center to analyze</param>
    /// <param name="progress">Progress reporter for UI feedback (optional)</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="mode">Recommendation mode (cost vs value optimization)</param>
    /// <param name="config">Analysis configuration (fraud detection, split world, etc.)</param>
    /// <param name="blacklistedWorlds">Worlds to exclude from recommendations. Home worlds bypass this filter.</param>
    /// <returns>List of shopping plans, one per market item</returns>
    public async Task<List<DetailedShoppingPlan>> CalculateDetailedShoppingPlansAsync(
        List<MaterialAggregate> marketItems,
        string dataCenter,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost,
        MarketAnalysisConfig? config = null,
        HashSet<string>? blacklistedWorlds = null)
    {
        config ??= new MarketAnalysisConfig();  // Use defaults
        var plans = new List<DetailedShoppingPlan>();
        blacklistedWorlds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
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
                    mode,
                    config,
                    blacklistedWorlds);
                
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
    /// Single-pass algorithm: ValueScore is the single source of truth for all decisions.
    /// </summary>
    /// <param name="blacklistedWorlds">Worlds to exclude from recommendations. Home worlds bypass this filter.</param>
    public async Task<List<DetailedShoppingPlan>> CalculateShoppingPlansWithSplitsAsync(
        List<MaterialAggregate> marketItems,
        string dataCenter,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisConfig? config = null,
        HashSet<string>? blacklistedWorlds = null)
    {
        config ??= new MarketAnalysisConfig();
        blacklistedWorlds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Pass 1: Calculate basic plans for all items
        progress?.Report("Calculating base recommendations...");
        var plans = await CalculateDetailedShoppingPlansAsync(marketItems, dataCenter, progress, ct, 
            RecommendationMode.MinimizeTotalCost, config, blacklistedWorlds);
        
        // Build world route map from tier 1 recommendations
        var worldsInRoute = new HashSet<string>();
        foreach (var plan in plans.Where(p => p.RecommendedWorld != null))
        {
            worldsInRoute.Add(plan.RecommendedWorld!.WorldName);
        }
        
        _logger?.LogInformation("[CalculateShoppingPlansWithSplits] Initial route contains {Count} worlds: {Worlds}", 
            worldsInRoute.Count, string.Join(", ", worldsInRoute));
        
        // Pass 2: Calculate splits for items that need them
        if (config.EnableSplitWorld)
        {
            progress?.Report("Optimizing multi-world purchases...");
            var itemsNeedingSplit = plans.Where(p => 
                p.RecommendedWorld == null || 
                p.RecommendedWorld.TotalQuantityPurchased < p.QuantityNeeded).ToList();
            
            foreach (var plan in itemsNeedingSplit)
            {
                CalculateSplitPurchase(plan, config);
                
                // Add split worlds to route for subsequent items
                if (plan.RecommendedSplit != null)
                {
                    foreach (var split in plan.RecommendedSplit)
                    {
                        worldsInRoute.Add(split.WorldName);
                    }
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
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost,
        MarketAnalysisConfig? config = null,
        HashSet<string>? blacklistedWorlds = null)
    {
        blacklistedWorlds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
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
            var worldSummary = CalculateWorldSummary(worldName, listings, quantityNeeded, plan.DCAveragePrice, homeWorld, config);
            if (worldSummary != null)
            {
                // Skip congested worlds (except home world) if setting is enabled
                if (excludeCongested && worldSummary.IsCongested && !worldSummary.IsHomeWorld)
                {
                    _logger?.LogDebug("[MarketShopping] Excluding {World} - congested world", worldName);
                    continue;
                }
                
                // Skip blacklisted worlds (except home world)
                if (blacklistedWorlds.Contains(worldName) && !worldSummary.IsHomeWorld)
                {
                    _logger?.LogDebug("[MarketShopping] Excluding {World} - user blacklisted", worldName);
                    worldSummary.IsBlacklisted = true;
                    continue;
                }
                
                plan.WorldOptions.Add(worldSummary);
            }
        }

        // Calculate ValueScore for each world (single-world mode)
        foreach (var world in plan.WorldOptions)
        {
            world.ValueScore = CalculateValueScore(world, quantityNeeded, splitEnabled: false);
        }

        // Simple sort by ValueScore (lower is better)
        plan.WorldOptions = plan.WorldOptions
            .OrderBy(w => w.ValueScore)
            .ThenBy(w => w.WorldName)
            .ToList();

        // Set recommended option to first viable world (ValueScore < MaxValue)
        var bestWorld = plan.WorldOptions.FirstOrDefault(w => w.ValueScore < decimal.MaxValue);
        if (bestWorld != null)
        {
            plan.RecommendedWorld = bestWorld;
        }

        return plan;
    }

    /// <summary>
    /// Calculate the mode price - the price with the highest available quantity.
    /// Uses a two-pass approach: first find a reasonable baseline, then calculate mode
    /// from listings within 10x of that baseline to avoid fraud skewing the mode.
    /// </summary>
    private long CalculateModePrice(List<MarketListing> listings)
    {
        if (listings.Count == 0) return 0;
        
        // Pass 1: Get a fraud-resistant baseline using median of cheapest 50%
        var sortedByPrice = listings.OrderBy(l => l.PricePerUnit).ToList();
        var halfCount = Math.Max(1, sortedByPrice.Count / 2);
        var cheapestHalf = sortedByPrice.Take(halfCount);
        var baselinePrice = cheapestHalf.Any() 
            ? cheapestHalf.Average(l => (decimal)l.PricePerUnit) 
            : sortedByPrice.First().PricePerUnit;
        
        // Pass 2: Calculate mode only from listings within 10x of baseline
        // This prevents fraudulent listings from skewing the mode
        var reasonableListings = listings
            .Where(l => l.PricePerUnit <= baselinePrice * 10)
            .ToList();
        
        if (reasonableListings.Count == 0)
            reasonableListings = sortedByPrice.Take(3).ToList(); // Fallback to cheapest 3
        
        return reasonableListings
            .GroupBy(l => l.PricePerUnit)
            .Select(g => new { Price = g.Key, Quantity = g.Sum(l => l.Quantity) })
            .OrderByDescending(x => x.Quantity)
            .ThenBy(x => x.Price)
            .FirstOrDefault()?.Price ?? 0;
    }

    /// <summary>
    /// Calculate ValueScore - the single metric for world ranking.
    /// 
    /// Split mode: ValueScore = ModePrice / StockRatio
    /// - Worlds with more stock relative to need get better scores
    /// - Worlds with lower mode prices get better scores
    /// 
    /// Single-world mode: ValueScore = TotalCost
    /// - Returns MaxValue (infinity) if world can't fulfill full quantity
    /// - Lower total cost is better
    /// </summary>
    private decimal CalculateValueScore(
        WorldShoppingSummary world, 
        int quantityNeeded, 
        bool splitEnabled)
    {
        if (splitEnabled)
        {
            // Split mode: ValueScore = ModePrice / StockRatio
            var stockRatio = Math.Min((decimal)world.TotalQuantityPurchased / quantityNeeded, 1.0m);
            if (stockRatio <= 0) return decimal.MaxValue;
            
            var modePrice = world.ModePricePerUnit;
            if (modePrice <= 0) return decimal.MaxValue;
            
            return modePrice / stockRatio;
        }
        else
        {
            // Single-world mode: ValueScore = TotalCost, Infinity if can't fulfill
            if (world.TotalQuantityPurchased < quantityNeeded)
                return decimal.MaxValue;
            
            return world.TotalCost;
        }
    }

    private WorldShoppingSummary? CalculateWorldSummary(
        string worldName,
        List<MarketListing> listings,
        int quantityNeeded,
        decimal dcAveragePrice,
        string? homeWorld = null,
        MarketAnalysisConfig? config = null)
    {
        // Get world status (if available)
        var worldStatus = _worldStatusService?.GetWorldStatus(worldName);
        
        var isHomeWorld = !string.IsNullOrWhiteSpace(homeWorld) && 
                         worldName.Equals(homeWorld, StringComparison.OrdinalIgnoreCase);
        
        var summary = new WorldShoppingSummary
        {
            WorldName = worldName,
            Listings = new List<ShoppingListingEntry>(),
            ExcludedListings = new List<ShoppingListingEntry>(),
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

        // Calculate mode price for fraud detection threshold
        summary.ModePricePerUnit = CalculateModePrice(listings);
        var maxPriceMultiplier = config?.MaxPriceMultiplier ?? 2.5m; // Default 2.5x if not specified
        var maxPriceThreshold = summary.ModePricePerUnit > 0
            ? (long)(summary.ModePricePerUnit * maxPriceMultiplier)
            : long.MaxValue;
        
        _logger?.LogInformation("[FRAUD_CHECK] {WorldName}: ModePrice={ModePrice}, Multiplier={Multiplier}, Threshold={Threshold}, TotalListings={Count}",
            worldName, summary.ModePricePerUnit, maxPriceMultiplier, maxPriceThreshold, listings.Count);
        
        var remaining = quantityNeeded;
        long totalCost = 0;
        int listingsUsed = 0;
        int fraudSkipped = 0;

        // Include listings - skip fraud/gouging listings based on price threshold
        // This prevents "desperation recommendations" with extremely overpriced listings
        foreach (var listing in listings)
        {
            // Check if listing exceeds fraud threshold (soft filter - skip but continue scanning)
            if (listing.PricePerUnit > maxPriceThreshold)
            {
                _logger?.LogWarning("[FRAUD_DETECTED] {WorldName}: Excluding listing - Price={Price}, Threshold={Threshold}, Retainer={Retainer}",
                    worldName, listing.PricePerUnit, maxPriceThreshold, listing.RetainerName);
                fraudSkipped++;
                summary.ExcludedListings.Add(new ShoppingListingEntry
                {
                    Quantity = listing.Quantity,
                    PricePerUnit = listing.PricePerUnit,
                    RetainerName = listing.RetainerName,
                    IsUnderAverage = listing.PricePerUnit <= dcAveragePrice,
                    IsHq = listing.IsHq,
                    IsAdditionalOption = true  // Mark as excluded/not primary
                });
                continue;  // Skip this listing but keep scanning
            }
            
            if (remaining <= 0)
                break;

            var isUnderAverage = listing.PricePerUnit <= dcAveragePrice;
            
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

        // Never return null - always return what we can purchase from this world
        // The split calculation will handle combining multiple worlds if needed
        if (remaining > 0 && listingsUsed == 0)
        {
            _logger?.LogDebug("[CalculateWorldSummary] {World} - No usable listings for {Quantity}", 
                worldName, quantityNeeded);
        }

        summary.TotalCost = totalCost;
        summary.TotalQuantityPurchased = summary.Listings.Where(l => !l.IsAdditionalOption).Sum(l => l.Quantity);
        summary.AveragePricePerUnit = summary.TotalQuantityPurchased > 0 
            ? (decimal)totalCost / summary.TotalQuantityPurchased 
            : 0;
        summary.ModePricePerUnit = CalculateModePrice(listings);
        summary.ListingsUsed = listingsUsed;
        summary.IsFullyUnderAverage = summary.Listings.Where(l => !l.IsAdditionalOption).All(l => l.IsUnderAverage);
        summary.ExcessQuantity = summary.TotalQuantityPurchased - quantityNeeded;
        summary.HasSufficientStock = remaining <= 0;
        summary.ShortfallQuantity = remaining > 0 ? remaining : 0;

        _logger?.LogInformation("[CalculateWorldSummary] {World} - Purchased: {Purchased}/{Needed}, Cost: {Cost:N0}g, FraudSkipped: {Fraud}, Sufficient: {Sufficient}", 
            worldName, summary.TotalQuantityPurchased, quantityNeeded, totalCost, fraudSkipped, summary.HasSufficientStock);

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
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost,
        MarketAnalysisConfig? config = null)
    {
        config ??= new MarketAnalysisConfig();  // Use defaults
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
                    mode,
                    config);
                
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
                // Check for vendor price first (preferred over market price)
                if (child.Source == AcquisitionSource.VendorBuy && child.VendorPrice > 0)
                {
                    total += child.VendorPrice * child.Quantity;
                }
                else if (marketPrices.TryGetValue(child.ItemId, out var priceInfo))
                {
                    total += priceInfo.UnitPrice * child.Quantity;
                }
            }
            else
            {
                total += CalculateComponentCost(child, marketPrices);
            }
        }
        
        // Account for recipe yield: cost per item = total ingredient cost / yield
        if (node.Yield > 1)
        {
            return total / node.Yield;
        }
        
        return total;
    }

    // ========================================================================
    // Multi-World Split Purchase Calculation
    // ========================================================================

    /// <summary>
    /// Calculates a multi-world split purchase plan for items that can't be fulfilled on a single world.
    /// Uses ValueScore as the single metric for world selection.
    /// </summary>
    public void CalculateSplitPurchase(
        DetailedShoppingPlan plan, 
        MarketAnalysisConfig config)
    {
        // Calculate ValueScores in split mode
        foreach (var world in plan.WorldOptions)
        {
            world.ValueScore = CalculateValueScore(world, plan.QuantityNeeded, splitEnabled: true);
        }
        
        // Get viable worlds sorted by ValueScore
        var viableWorlds = plan.WorldOptions
            .Where(w => w.ValueScore < decimal.MaxValue && w.TotalQuantityPurchased > 0)
            .OrderBy(w => w.ValueScore)
            .ToList();
        
        if (viableWorlds.Count == 0) return;
        
        // Greedy allocation
        var split = new List<SplitWorldPurchase>();
        var remaining = plan.QuantityNeeded;
        
        foreach (var world in viableWorlds)
        {
            if (remaining <= 0) break;
            
            var toAllocate = Math.Min(remaining, world.TotalQuantityPurchased);
            if (toAllocate <= 0) continue;
            
            // Calculate actual cost from listings
            var cost = 0L;
            var remainingFromWorld = toAllocate;
            foreach (var listing in world.Listings.Where(l => !l.IsAdditionalOption).OrderBy(l => l.PricePerUnit))
            {
                if (remainingFromWorld <= 0) break;
                var fromThis = Math.Min(remainingFromWorld, listing.Quantity);
                cost += fromThis * listing.PricePerUnit;
                remainingFromWorld -= fromThis;
            }
            
            split.Add(new SplitWorldPurchase
            {
                WorldName = world.WorldName,
                QuantityToBuy = toAllocate,
                PricePerUnit = toAllocate > 0 ? cost / (decimal)toAllocate : world.AveragePricePerUnit,
                IsPartial = toAllocate < world.TotalQuantityPurchased,
                TotalCost = cost
            });
            
            remaining -= toAllocate;
        }
        
        if (split.Count == 0) return;
        
        var splitCost = split.Sum(s => s.TotalCost);
        var singleWorldCost = plan.RecommendedWorld?.TotalCost ?? long.MaxValue;
        
        // Single-world contingency: prefer single if within 5%
        if (plan.RecommendedWorld != null && 
            plan.RecommendedWorld.HasSufficientStock &&
            singleWorldCost <= splitCost * 1.05m)
        {
            // Keep single-world recommendation
            return;
        }
        
        // Use split
        plan.RecommendedSplit = split;
    }

    /// <summary>
    /// Applies hard-lock vendor overrides for items explicitly marked as VendorBuy in the crafting plan.
    ///
    /// This keeps existing market world options for comparison, but forces the recommended purchase source
    /// to a synthetic "Vendor" world using the selected vendor (or cheapest gil vendor fallback).
    /// </summary>
    public void ApplyVendorPurchaseOverrides(CraftingPlan? plan, List<DetailedShoppingPlan> plans)
    {
        if (plan == null || plans == null || plans.Count == 0)
        {
            return;
        }

        foreach (var shoppingPlan in plans)
        {
            var vendorNode = FindVendorBuyNodeByItemId(plan.RootItems, shoppingPlan.ItemId);
            if (vendorNode == null)
            {
                continue;
            }

            var gilVendors = vendorNode.VendorOptions.Where(v => v.IsGilVendor).ToList();
            if (gilVendors.Count == 0)
            {
                continue;
            }

            var selectedVendor = vendorNode.SelectedVendor;
            if (selectedVendor == null || !selectedVendor.IsGilVendor)
            {
                selectedVendor = gilVendors.OrderBy(v => v.Price).First();
            }

            var unitPrice = selectedVendor.Price;
            if (unitPrice <= 0)
            {
                continue;
            }

            var vendorWorldSummary = new WorldShoppingSummary
            {
                WorldName = "Vendor",
                WorldId = 0,
                TotalCost = (long)(unitPrice * shoppingPlan.QuantityNeeded),
                AveragePricePerUnit = unitPrice,
                ListingsUsed = 1,
                TotalQuantityPurchased = shoppingPlan.QuantityNeeded,
                HasSufficientStock = true,
                IsHomeWorld = false,
                IsTravelProhibited = false,
                IsBlacklisted = false,
                Classification = WorldClassification.Standard,
                VendorName = selectedVendor.DisplayName,
                Listings = new List<ShoppingListingEntry>
                {
                    new()
                    {
                        Quantity = shoppingPlan.QuantityNeeded,
                        PricePerUnit = (long)unitPrice,
                        RetainerName = "Vendor",
                        IsUnderAverage = true,
                        IsHq = false,
                        NeededFromStack = shoppingPlan.QuantityNeeded,
                        ExcessQuantity = 0
                    }
                }
            };

            shoppingPlan.RecommendedWorld = vendorWorldSummary;
            shoppingPlan.RecommendedSplit = null;
            shoppingPlan.Vendors = gilVendors;

            if (shoppingPlan.WorldOptions.All(w => !string.Equals(w.WorldName, "Vendor", StringComparison.OrdinalIgnoreCase)))
            {
                shoppingPlan.WorldOptions.Insert(0, vendorWorldSummary);
            }
        }
    }

    private static PlanNode? FindVendorBuyNodeByItemId(IEnumerable<PlanNode> nodes, int itemId)
    {
        foreach (var node in nodes)
        {
            if (node.ItemId == itemId && node.Source == AcquisitionSource.VendorBuy)
            {
                return node;
            }

            if (node.Children.Count == 0)
            {
                continue;
            }

            var childMatch = FindVendorBuyNodeByItemId(node.Children, itemId);
            if (childMatch != null)
            {
                return childMatch;
            }
        }

        return null;
    }

    // ========================================================================
    // Item Categorization
    // ========================================================================

    /// <summary>
    /// Categorizes materials by their price source (Vendor, Market, or Untradeable).
    /// 
    /// VENDOR ITEM HANDLING:
    /// Vendor items are identified by PriceInfo.Source == PriceSource.Vendor.
    /// These items are excluded from market analysis and shopping plan calculations
    /// because they have fixed prices and unlimited stock from NPC vendors.
    /// 
    /// SEPARATION LOGIC:
    /// 1. Vendor items: PriceSource.Vendor → VendorItems list
    ///    - No market lookup needed
    ///    - Price is fixed (from Garland data)
    ///    - Stock is unlimited
    ///    - Displayed separately in UI with vendor location
    /// 
    /// 2. Untradeable items: PriceSource.Untradeable → UntradeableItems list
    ///    - Cannot be bought on market
    ///    - Must be gathered or crafted
    ///    - Shown in UI with "Untradeable" label
    /// 
    /// 3. Market items: PriceSource.Market or no price info → MarketItems list
    ///    - Requires market analysis
    ///    - Price varies by world
    ///    - Stock limited by listings
    ///    - Full shopping plan calculation needed
    /// 
    /// WHY SEPARATE VENDOR ITEMS:
    /// - Avoids unnecessary API calls to Universalis for fixed-price items
    /// - Allows special UI treatment (vendor location display, gold background)
    /// - Ensures accurate cost calculations (vendor always cheapest)
    /// - Simplifies procurement planning (always buy from vendor)
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
/// Result of categorizing materials by their acquisition source.
/// 
/// Separates materials into three distinct categories for different handling:
/// 
/// VendorItems:
/// - Items available from NPC vendors at fixed prices
/// - Prices from Garland data (no market lookup needed)
/// - Unlimited stock assumption
/// - Displayed in "Vendor" procurement group with location info
/// - UI: Gold background, shop icon, vendor location shown
/// 
/// MarketItems:
/// - Items that must be purchased from market board
/// - Requires Universalis API lookup
/// - Price varies by world, limited stock
/// - Full shopping plan with world recommendations
/// - UI: Blue background, market board analysis shown
/// 
/// UntradeableItems:
/// - Items that cannot be traded on market
/// - Must be gathered, crafted, or obtained through other means
/// - No price information available
/// - UI: Gray background, "Untradeable" label
/// 
/// USAGE FLOW:
/// 1. RecipePlanner aggregates materials from plan tree
/// 2. PriceCheckService.GetBestPricesBulkAsync gets PriceInfo for each
/// 3. CategorizeMaterials separates by PriceInfo.Source
/// 4. VendorItems displayed separately in procurement plan
/// 5. MarketItems sent to MarketShoppingService for analysis
/// 6. UntradeableItems shown with warning
/// 
/// </summary>
public class CategorizedMaterials
{
    /// <summary>
    /// Items available from NPC vendors (fixed price, unlimited stock).
    /// These are excluded from market analysis.
    /// </summary>
    public List<MaterialAggregate> VendorItems { get; } = new();
    
    /// <summary>
    /// Items that must be purchased from market board (variable price, limited stock).
    /// These require full market analysis and shopping plan calculation.
    /// </summary>
    public List<MaterialAggregate> MarketItems { get; } = new();
    
    /// <summary>
    /// Items that cannot be traded on the market.
    /// These must be gathered, crafted, or obtained through other means.
    /// </summary>
    public List<MaterialAggregate> UntradeableItems { get; } = new();
}
