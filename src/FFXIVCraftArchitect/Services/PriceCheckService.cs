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
    private readonly ILogger<PriceCheckService> _logger;
    
    // Cache for prices to avoid repeated API calls
    private readonly Dictionary<int, PriceInfo> _priceCache = new();
    private readonly TimeSpan _cacheDuration = TimeSpan.FromHours(1);

    public PriceCheckService(
        GarlandService garlandService,
        UniversalisService universalisService,
        ILogger<PriceCheckService> logger)
    {
        _garlandService = garlandService;
        _universalisService = universalisService;
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
            
            // Fetch market price from Universalis
            var marketPrice = await GetMarketPriceAsync(itemId, worldOrDc, ct);

            // Pick the best price (vendors prioritized if equal)
            if (vendorPrice > 0 && (marketPrice <= 0 || vendorPrice <= marketPrice))
            {
                priceInfo.UnitPrice = vendorPrice;
                priceInfo.Source = PriceSource.Vendor;
                var vendor = garlandItem?.Vendors?.FirstOrDefault();
                priceInfo.SourceDetails = vendor != null 
                    ? $"Vendor: {vendor.Name} ({vendor.Location})"
                    : "Vendor";
                _logger.LogInformation("[PriceCheck] {ItemName}: Vendor price {Price}g (market: {Market}g)",
                    itemName, vendorPrice, marketPrice);
            }
            else if (marketPrice > 0)
            {
                priceInfo.UnitPrice = marketPrice;
                priceInfo.Source = PriceSource.Market;
                priceInfo.SourceDetails = $"Market ({worldOrDc})";
                _logger.LogInformation("[PriceCheck] {ItemName}: Market price {Price}g (vendor: {Vendor}g)",
                    itemName, marketPrice, vendorPrice);
            }
            else
            {
                // No price found
                priceInfo.UnitPrice = 0;
                priceInfo.Source = PriceSource.Unknown;
                priceInfo.SourceDetails = "No price data";
                _logger.LogWarning("[PriceCheck] No price found for {ItemName}", itemName);
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
    /// Get prices for multiple items in bulk (more efficient).
    /// </summary>
    /// <param name="forceRefresh">If true, bypass cache and fetch fresh prices for all items.</param>
    public async Task<Dictionary<int, PriceInfo>> GetBestPricesBulkAsync(
        List<(int itemId, string name)> items, 
        string worldOrDc, 
        CancellationToken ct = default,
        IProgress<(int current, int total, string itemName)>? progress = null,
        bool forceRefresh = false)
    {
        var results = new Dictionary<int, PriceInfo>();
        var itemsToFetch = new List<(int itemId, string name)>();

        // Check cache first (unless forcing refresh)
        foreach (var (itemId, name) in items)
        {
            if (!forceRefresh && 
                _priceCache.TryGetValue(itemId, out var cached) && 
                DateTime.UtcNow - cached.LastUpdated < _cacheDuration)
            {
                results[itemId] = cached;
            }
            else
            {
                itemsToFetch.Add((itemId, name));
            }
        }

        if (itemsToFetch.Count == 0)
        {
            _logger.LogInformation("[PriceCheck] All {Count} items found in cache", items.Count);
            return results;
        }

        _logger.LogInformation("[PriceCheck] Fetching prices for {Count} items", itemsToFetch.Count);

        // Fetch all item data from Garland first
        var garlandItems = new Dictionary<int, GarlandItem?>();
        foreach (var (itemId, name) in itemsToFetch)
        {
            try
            {
                garlandItems[itemId] = await _garlandService.GetItemAsync(itemId, ct);
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

        // Fetch market prices in bulk
        Dictionary<int, UniversalisResponse> marketPrices = new();
        if (marketIds.Count > 0)
        {
            try
            {
                marketPrices = await _universalisService.GetMarketDataBulkAsync(worldOrDc, marketIds, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PriceCheck] Failed to fetch market prices");
            }
        }

        // Process results
        int current = 0;
        foreach (var (itemId, name) in itemsToFetch)
        {
            current++;
            progress?.Report((current, itemsToFetch.Count, name));

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
                continue;
            }

            // Get vendor price
            var vendorPrice = GetVendorPrice(garlandItem);

            // Get market price
            decimal marketPrice = 0;
            if (marketPrices.TryGetValue(itemId, out var marketData))
            {
                marketPrice = GetAverageMarketPrice(marketData);
            }

            // Pick best price
            if (vendorPrice > 0 && (marketPrice <= 0 || vendorPrice <= marketPrice))
            {
                priceInfo.UnitPrice = vendorPrice;
                priceInfo.Source = PriceSource.Vendor;
                var vendor = garlandItem?.Vendors?.FirstOrDefault();
                priceInfo.SourceDetails = vendor != null 
                    ? $"Vendor: {vendor.Name}"
                    : "Vendor";
            }
            else if (marketPrice > 0)
            {
                priceInfo.UnitPrice = marketPrice;
                priceInfo.Source = PriceSource.Market;
                priceInfo.SourceDetails = $"Market ({worldOrDc})";
            }
            else
            {
                priceInfo.Source = PriceSource.Unknown;
                priceInfo.SourceDetails = "No price data";
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
            var marketData = await _universalisService.GetMarketDataAsync(worldOrDc, itemId, ct);
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
    /// </summary>
    private decimal GetAverageMarketPrice(UniversalisResponse marketData)
    {
        if (marketData.Listings?.Any() == true)
        {
            // Use average of lowest 5 current listings
            var lowestListings = marketData.Listings
                .OrderBy(l => l.PricePerUnit)
                .Take(5)
                .ToList();

            if (lowestListings.Any())
            {
                var avgPrice = lowestListings.Average(l => (decimal)l.PricePerUnit);
                return avgPrice;
            }
        }

        if (marketData.AveragePrice > 0)
        {
            return (decimal)marketData.AveragePrice;
        }

        return 0;
    }
}
