using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.Services;

/// <summary>
/// Price information for an item
/// </summary>
public class PriceInfo
{
    public int ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int QuantityAvailable { get; set; }
    public PriceSource Source { get; set; }
    public string SourceDetails { get; set; } = string.Empty; // Vendor name, "Market (DC Average)", etc.
    public DateTime LastUpdated { get; set; }
    
    /// <summary>
    /// HQ price if available (for endgame crafting considerations).
    /// </summary>
    public decimal? HqUnitPrice { get; set; }
    public int? HqQuantityAvailable { get; set; }
    public bool HasHqData => HqUnitPrice.HasValue;
}

/// <summary>
/// Service for checking item prices from multiple sources:
/// - Garland Tools (vendor prices)
/// - Universalis (market board prices)
/// Automatically picks the cheapest option, prioritizing vendors when equal.
/// </summary>
public class PriceCheckService
{
    private readonly GarlandService _garlandService;
    private readonly UniversalisService _universalisService;
    private readonly SettingsService _settingsService;
    private readonly ILogger<PriceCheckService> _logger;
    
    // Cache for prices to avoid repeated API calls
    private readonly Dictionary<int, PriceInfo> _priceCache = new();
    private readonly TimeSpan _cacheDuration = TimeSpan.FromHours(1);

    public PriceCheckService(
        GarlandService garlandService,
        UniversalisService universalisService,
        SettingsService settingsService,
        ILogger<PriceCheckService> logger)
    {
        _garlandService = garlandService;
        _universalisService = universalisService;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Get the best price for an item (cheapest available, vendors prioritized).
    /// </summary>
    public async Task<PriceInfo> GetBestPriceAsync(int itemId, string itemName, string worldOrDc, CancellationToken ct = default)
    {
        // Check cache first
        if (_priceCache.TryGetValue(itemId, out var cachedPrice))
        {
            if (DateTime.UtcNow - cachedPrice.LastUpdated < _cacheDuration)
            {
                _logger.LogDebug("[PriceCheck] Using cached price for {ItemName}", itemName);
                return cachedPrice;
            }
        }

        _logger.LogInformation("[PriceCheck] Fetching prices for {ItemName} ({ItemId})", itemName, itemId);

        var priceInfo = new PriceInfo
        {
            ItemId = itemId,
            ItemName = itemName,
            LastUpdated = DateTime.UtcNow
        };

        try
        {
            // Fetch item data from Garland (for vendor prices and tradeability)
            var garlandItem = await _garlandService.GetItemAsync(itemId, ct);
            
            // DEBUG: Log what we got from Garland
            _logger.LogInformation("[PriceCheck:{ItemName}] Garland: Tradeable={Tradeable}, Vendors={VendorCount}",
                itemName, garlandItem?.Tradeable, garlandItem?.Vendors?.Count ?? 0);
            
            // Check if item is tradeable
            if (garlandItem?.Tradeable == false)
            {
                priceInfo.Source = PriceSource.Untradeable;
                priceInfo.SourceDetails = "Untradeable";
                priceInfo.UnitPrice = 0;
                _priceCache[itemId] = priceInfo;
                return priceInfo;
            }

            // Check vendor prices first (usually cheapest and most reliable)
            var vendorPrice = GetVendorPrice(garlandItem);
            
            // Fetch market prices from Universalis (NQ and HQ)
            var marketData = await _universalisService.GetMarketDataAsync(worldOrDc, itemId, ct: ct);
            var marketPriceNq = GetAverageMarketPrice(marketData, hqOnly: false);
            var marketPriceHq = GetAverageMarketPrice(marketData, hqOnly: true);
            
            // DEBUG: Log market data details
            _logger.LogInformation("[PriceCheck:{ItemName}] Universalis: Listings={Listings}, AvgPrice={AvgPrice}, NQ={Nq}, HQ={Hq}",
                itemName, marketData?.Listings?.Count ?? 0, marketData?.AveragePrice, marketPriceNq, marketPriceHq);

            // Pick the best NQ price (vendors prioritized if equal)
            if (vendorPrice > 0 && (marketPriceNq <= 0 || vendorPrice <= marketPriceNq))
            {
                priceInfo.UnitPrice = vendorPrice;
                priceInfo.Source = PriceSource.Vendor;
                var vendor = garlandItem?.Vendors?.FirstOrDefault();
                priceInfo.SourceDetails = vendor != null 
                    ? $"Vendor: {vendor.Name} ({vendor.Location})"
                    : "Vendor";
                _logger.LogInformation("[PriceCheck] {ItemName}: Vendor price {Price}g (market NQ: {MarketNq}g, HQ: {MarketHq}g)",
                    itemName, vendorPrice, marketPriceNq, marketPriceHq);
            }
            else if (marketPriceNq > 0)
            {
                priceInfo.UnitPrice = marketPriceNq;
                priceInfo.Source = PriceSource.Market;
                priceInfo.SourceDetails = $"Market ({worldOrDc})";
                _logger.LogInformation("[PriceCheck] {ItemName}: Market NQ price {Price}g (vendor: {Vendor}g, HQ: {MarketHq}g)",
                    itemName, marketPriceNq, vendorPrice, marketPriceHq);
            }
            else
            {
                // No price found
                priceInfo.UnitPrice = 0;
                priceInfo.Source = PriceSource.Unknown;
                priceInfo.SourceDetails = "No price data";
                _logger.LogWarning("[PriceCheck:{ItemName}] No price: vendor={Vendor}, marketNQ={MarketNQ}, marketHQ={MarketHQ}", 
                    itemName, vendorPrice, marketPriceNq, marketPriceHq);
            }
            
            // Store HQ price if available (only if there are actual HQ listings)
            if (marketPriceHq > 0 && HasHqListings(marketData))
            {
                priceInfo.HqUnitPrice = marketPriceHq;
            }

            _priceCache[itemId] = priceInfo;
            return priceInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PriceCheck] Error fetching price for {ItemName}", itemName);
            priceInfo.Source = PriceSource.Unknown;
            priceInfo.SourceDetails = $"Error: {ex.Message}";
            return priceInfo;
        }
    }

    /// <summary>
    /// Progress info for price fetching operations.
    /// </summary>
    public enum PriceFetchStage
    {
        CheckingCache,
        FetchingGarlandData,
        FetchingMarketData,
        ProcessingResults,
        Complete
    }

    /// <summary>
    /// Get prices for multiple items in bulk (more efficient).
    /// </summary>
    /// <param name="forceRefresh">If true, bypass cache and fetch fresh prices for all items.</param>
    public async Task<Dictionary<int, PriceInfo>> GetBestPricesBulkAsync(
        List<(int itemId, string name)> items, 
        string worldOrDc, 
        CancellationToken ct = default,
        IProgress<(int current, int total, string itemName, PriceFetchStage stage, string? message)>? progress = null,
        bool forceRefresh = false)
    {
        var results = new Dictionary<int, PriceInfo>();
        var itemsToFetch = new List<(int itemId, string name)>();

        // Report cache checking stage
        progress?.Report((0, items.Count, "", PriceFetchStage.CheckingCache, "Checking cache for existing prices..."));
        
        // Check cache first (unless forcing refresh)
        int cacheHitCount = 0;
        foreach (var (itemId, name) in items)
        {
            if (!forceRefresh && 
                _priceCache.TryGetValue(itemId, out var cached) && 
                DateTime.UtcNow - cached.LastUpdated < _cacheDuration)
            {
                results[itemId] = cached;
                cacheHitCount++;
            }
            else
            {
                itemsToFetch.Add((itemId, name));
            }
        }

        if (itemsToFetch.Count == 0)
        {
            _logger.LogInformation("[PriceCheck] All {Count} items found in cache", items.Count);
            progress?.Report((items.Count, items.Count, "", PriceFetchStage.Complete, $"All {items.Count} items found in cache"));
            return results;
        }

        _logger.LogInformation("[PriceCheck] {Cached} items from cache, fetching {ToFetch} items", cacheHitCount, itemsToFetch.Count);
        progress?.Report((cacheHitCount, items.Count, "", PriceFetchStage.CheckingCache, 
            $"{cacheHitCount} from cache, fetching {itemsToFetch.Count} from API..."));

        // Fetch all item data from Garland first
        progress?.Report((cacheHitCount, items.Count, "", PriceFetchStage.FetchingGarlandData, "Fetching item data from Garland Tools..."));
        
        var garlandItems = new Dictionary<int, GarlandItem?>();
        int garlandProcessed = 0;
        foreach (var (itemId, name) in itemsToFetch)
        {
            try
            {
                garlandItems[itemId] = await _garlandService.GetItemAsync(itemId, ct);
                garlandProcessed++;
                
                // Report every 10 items or on the last one
                if (garlandProcessed % 10 == 0 || garlandProcessed == itemsToFetch.Count)
                {
                    progress?.Report((cacheHitCount + garlandProcessed, items.Count, name, 
                        PriceFetchStage.FetchingGarlandData, $"Loading item data: {garlandProcessed}/{itemsToFetch.Count}"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PriceCheck] Failed to fetch Garland data for {ItemId}", itemId);
                garlandItems[itemId] = null;
            }
        }

        // Get all item IDs that need market prices
        var marketIds = itemsToFetch
            .Where(i => garlandItems[i.itemId]?.Tradeable != false)
            .Select(i => i.itemId)
            .ToList();
        
        var untradeableCount = itemsToFetch.Count - marketIds.Count;
        if (untradeableCount > 0)
        {
            _logger.LogInformation("[PriceCheck] {Count} untradeable items (vendor/gather only)", untradeableCount);
        }

        // Fetch market prices in bulk
        Dictionary<int, UniversalisResponse> marketPrices = new();
        bool marketFetchFailed = false;
        if (marketIds.Count > 0)
        {
            try
            {
                // Check if parallel fetching is enabled
                var useParallel = _settingsService.Get<bool>("market.parallel_api_requests", true);
                
                string fetchMode = useParallel && marketIds.Count > 100 ? "parallel" : "sequential";
                progress?.Report((cacheHitCount, items.Count, "", PriceFetchStage.FetchingMarketData, 
                    $"Fetching market prices ({fetchMode}, {marketIds.Count} items)..."));
                
                _logger.LogInformation("[PriceCheck] Using {Mode} market fetch for {Count} items", fetchMode, marketIds.Count);
                
                if (useParallel && marketIds.Count > 100)
                {
                    marketPrices = await _universalisService.GetMarketDataBulkParallelAsync(worldOrDc, marketIds, ct: ct);
                }
                else
                {
                    marketPrices = await _universalisService.GetMarketDataBulkAsync(worldOrDc, marketIds, ct);
                }
                
                _logger.LogInformation("[PriceCheck] Market fetch returned {ReturnedCount}/{RequestedCount} items", 
                    marketPrices.Count, marketIds.Count);
                
                // Log which items are missing from response
                var missingItems = marketIds.Where(id => !marketPrices.ContainsKey(id)).ToList();
                if (missingItems.Any())
                {
                    _logger.LogWarning("[PriceCheck] Missing from market response: {MissingIds}", 
                        string.Join(", ", missingItems));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PriceCheck] Failed to fetch market prices - preserving cached prices");
                marketFetchFailed = true;
                // Don't overwrite existing cache entries on failure
            }
        }

        // Process results
        int current = 0;
        progress?.Report((0, itemsToFetch.Count, "", PriceFetchStage.ProcessingResults, "Processing results..."));
        
        foreach (var (itemId, name) in itemsToFetch)
        {
            current++;
            
            // Report progress every 5 items
            if (current % 5 == 0 || current == itemsToFetch.Count)
            {
                progress?.Report((current, itemsToFetch.Count, name, PriceFetchStage.ProcessingResults, 
                    $"Processing: {name} ({current}/{itemsToFetch.Count})"));
            }

            // If market fetch failed, try to use cached price
            if (marketFetchFailed && _priceCache.TryGetValue(itemId, out var cachedPrice))
            {
                results[itemId] = cachedPrice;
                continue;
            }

            var garlandItem = garlandItems.GetValueOrDefault(itemId);
            var priceInfo = new PriceInfo
            {
                ItemId = itemId,
                ItemName = name,
                LastUpdated = DateTime.UtcNow
            };

            // Check tradeability
            if (garlandItem?.Tradeable == false)
            {
                priceInfo.Source = PriceSource.Untradeable;
                priceInfo.SourceDetails = "Untradeable";
                results[itemId] = priceInfo;
                _priceCache[itemId] = priceInfo;
                continue;
            }

            // Get vendor price
            var vendorPrice = GetVendorPrice(garlandItem);

            // Get market prices (NQ and HQ)
            decimal marketPriceNq = 0;
            decimal marketPriceHq = 0;
            if (marketPrices.TryGetValue(itemId, out var marketData))
            {
                marketPriceNq = GetAverageMarketPrice(marketData, hqOnly: false);
                marketPriceHq = GetAverageMarketPrice(marketData, hqOnly: true);
                
                _logger.LogDebug("[PriceCheck] {ItemName}: market data found, {Listings} listings, NQ={NqPrice}, HQ={HqPrice}",
                    name, marketData.Listings?.Count ?? 0, marketPriceNq, marketPriceHq);
            }
            else
            {
                _logger.LogWarning("[PriceCheck] {ItemName}: NO market data found in response!", name);
            }

            // Pick best price (prefer NQ for default, but track HQ separately)
            if (vendorPrice > 0 && (marketPriceNq <= 0 || vendorPrice <= marketPriceNq))
            {
                priceInfo.UnitPrice = vendorPrice;
                priceInfo.Source = PriceSource.Vendor;
                var vendor = garlandItem?.Vendors?.FirstOrDefault();
                priceInfo.SourceDetails = vendor != null 
                    ? $"Vendor: {vendor.Name}"
                    : "Vendor";
                _logger.LogInformation("[PriceCheck:{Name}] Result: VENDOR @ {Price}g", name, vendorPrice);
            }
            else if (marketPriceNq > 0)
            {
                priceInfo.UnitPrice = marketPriceNq;
                priceInfo.Source = PriceSource.Market;
                priceInfo.SourceDetails = $"Market ({worldOrDc})";
                _logger.LogInformation("[PriceCheck:{Name}] Result: MARKET @ {Price}g", name, marketPriceNq);
            }
            else
            {
                priceInfo.Source = PriceSource.Unknown;
                priceInfo.SourceDetails = "No price data";
                _logger.LogWarning("[PriceCheck:{Name}] Result: UNKNOWN (vendor={Vendor}, marketNQ={MarketNQ})", 
                    name, vendorPrice, marketPriceNq);
            }
            
            // Store HQ price if available (only if there are actual HQ listings)
            if (marketPriceHq > 0 && marketPrices.TryGetValue(itemId, out var marketDataForHq) && HasHqListings(marketDataForHq))
            {
                priceInfo.HqUnitPrice = marketPriceHq;
            }

            results[itemId] = priceInfo;
            _priceCache[itemId] = priceInfo;
        }

        _logger.LogInformation("[PriceCheck] Completed price fetch for {Count} items", itemsToFetch.Count);
        return results;
    }

    /// <summary>
    /// Clear the price cache to force fresh fetches.
    /// </summary>
    public void ClearCache()
    {
        _priceCache.Clear();
        _logger.LogInformation("[PriceCheck] Price cache cleared");
    }

    /// <summary>
    /// Get the cheapest vendor price from Garland data.
    /// </summary>
    private decimal GetVendorPrice(GarlandItem? item)
    {
        if (item?.Vendors == null || !item.Vendors.Any())
            return 0;

        // Filter to gil-only vendors and find cheapest
        var gilVendors = item.Vendors
            .Where(v => string.Equals(v.Currency, "gil", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!gilVendors.Any())
            return 0;

        return gilVendors.Min(v => v.Price);
    }

    /// <summary>
    /// Get market price from Universalis data.
    /// </summary>
    private async Task<decimal> GetMarketPriceAsync(int itemId, string worldOrDc, CancellationToken ct)
    {
        try
        {
            var marketData = await _universalisService.GetMarketDataAsync(worldOrDc, itemId, ct: ct);
            return GetAverageMarketPrice(marketData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PriceCheck] Failed to get market price for {ItemId}", itemId);
            return 0;
        }
    }

    /// <summary>
    /// Calculate average price from market listings.
    /// Uses average of lowest 5 listings or recent history.
    /// For HQ, returns 0 if no HQ listings exist (doesn't fall back to NQ).
    /// </summary>
    /// <param name="marketData">Market data response</param>
    /// <param name="hqOnly">If true, only consider HQ listings</param>
    private decimal GetAverageMarketPrice(UniversalisResponse marketData, bool hqOnly = false)
    {
        if (marketData.Listings?.Any() == true)
        {
            // Filter by HQ if requested
            var filteredListings = hqOnly 
                ? marketData.Listings.Where(l => l.IsHq).ToList()
                : marketData.Listings;
            
            if (!filteredListings.Any())
                return 0;
            
            // Use average of lowest 5 current listings
            var lowestListings = filteredListings
                .OrderBy(l => l.PricePerUnit)
                .Take(5)
                .ToList();

            if (lowestListings.Any())
            {
                var avgPrice = lowestListings.Average(l => (decimal)l.PricePerUnit);
                return avgPrice;
            }
        }

        // Only fall back to AveragePrice for NQ, not HQ
        if (!hqOnly && marketData.AveragePrice > 0)
        {
            return (decimal)marketData.AveragePrice;
        }

        return 0;
    }

    /// <summary>
    /// Check if an item has any HQ listings on the market.
    /// </summary>
    private bool HasHqListings(UniversalisResponse marketData)
    {
        return marketData.Listings?.Any(l => l.IsHq) == true;
    }
}
