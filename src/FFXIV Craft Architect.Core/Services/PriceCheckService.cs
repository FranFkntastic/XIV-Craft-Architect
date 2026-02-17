using System.Collections.Concurrent;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Service for checking item prices from multiple sources:
/// - SQLite cache (persistent, survives restarts)
/// - Garland Tools (vendor prices)
/// - Universalis (market board prices)
/// Automatically picks the cheapest option, prioritizing vendors when equal.
/// </summary>
public class PriceCheckService
{
    private readonly IGarlandService _garlandService;
    private readonly IUniversalisService _universalisService;
    private readonly SettingsService _settingsService;
    private readonly IMarketCacheService _marketCache;
    private readonly ILogger<PriceCheckService> _logger;
    
    // In-memory cache for hot paths (backed by SQLite)
    private readonly ConcurrentDictionary<int, PriceInfo> _memoryCache = new();

    public PriceCheckService(
        IGarlandService garlandService,
        IUniversalisService universalisService,
        SettingsService settingsService,
        IMarketCacheService marketCache,
        ILogger<PriceCheckService> logger)
    {
        _garlandService = garlandService;
        _universalisService = universalisService;
        _settingsService = settingsService;
        _marketCache = marketCache;
        _logger = logger;
    }

    /// <summary>
    /// Get the TTL for market cache from settings (default 3 hours).
    /// </summary>
    private TimeSpan GetCacheTtl()
    {
        var hours = _settingsService.Get("market.cache_ttl_hours", 3.0);
        return TimeSpan.FromHours(hours);
    }

    /// <summary>
    /// Get the best price for an item (vendors + cached market data).
    /// Vendor prices are fetched fresh from Garland (cheap API).
    /// Market prices come from IMarketCacheService (pre-populated by caller).
    /// </summary>
    public async Task<PriceInfo> GetBestPriceAsync(int itemId, string itemName, string worldOrDc, CancellationToken ct = default)
    {
        var ttl = GetCacheTtl();
        
        // Check memory cache first (fastest)
        if (_memoryCache.TryGetValue(itemId, out var memCached) && 
            DateTime.UtcNow - memCached.LastUpdated < ttl)
        {
            _logger.LogDebug("[PriceCheck] Memory cache hit for {ItemName}", itemName);
            return memCached;
        }
        
        // Fetch vendor price from Garland (cheap, individual call is OK)
        var garlandItem = await _garlandService.GetItemAsync(itemId, ct);
        var vendorPrice = GetVendorPrice(garlandItem);
        
        // Check if item is untradeable
        if (garlandItem?.Tradeable == false)
        {
            var untradeableInfo = new PriceInfo
            {
                ItemId = itemId,
                ItemName = itemName,
                UnitPrice = 0,
                Source = PriceSource.Untradeable,
                SourceDetails = "Untradeable",
                LastUpdated = DateTime.UtcNow
            };
            _memoryCache[itemId] = untradeableInfo;
            return untradeableInfo;
        }
        
        // Get market price from cache (caller should have pre-populated)
        var (cachedData, isStale) = await _marketCache.GetWithStaleAsync(itemId, worldOrDc, ttl);
        var marketPrice = cachedData?.DCAveragePrice ?? 0;
        
        if (cachedData != null)
        {
            var age = DateTime.UtcNow - cachedData.FetchedAt;
            _logger.LogDebug("[PriceCheck] Cache data for {ItemName}@{DC}: Age={Age:F1}min, TTL={TTL:F1}h, IsStale={IsStale}", 
                itemName, worldOrDc, age.TotalMinutes, ttl.TotalHours, isStale);
        }
        
        // Determine best price (vendor prioritized if equal)
        PriceInfo priceInfo;
        if (vendorPrice > 0 && (marketPrice <= 0 || vendorPrice <= marketPrice))
        {
            // Get gil vendors only (filter out tomestone/other currency vendors)
            var garlandVendors = garlandItem?.Vendors
                ?.Where(v => string.Equals(v.Currency, "gil", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? new List<GarlandVendor>();
            
            // Convert GarlandVendor to VendorInfo
            var vendorInfos = garlandVendors.Select(v => new VendorInfo
            {
                Name = v.Name,
                Location = v.Location,
                Price = v.Price,
                Currency = v.Currency?.ToLowerInvariant() ?? "gil"
            }).ToList();
            
            priceInfo = new PriceInfo
            {
                ItemId = itemId,
                ItemName = itemName,
                UnitPrice = vendorPrice,
                Source = PriceSource.Vendor,
                SourceDetails = vendorInfos.FirstOrDefault() != null 
                    ? $"Vendor: {vendorInfos.First().Name}" 
                    : "Vendor",
                Vendors = vendorInfos,
                LastUpdated = DateTime.UtcNow
            };
        }
        else if (marketPrice > 0)
        {
            priceInfo = new PriceInfo
            {
                ItemId = itemId,
                ItemName = itemName,
                UnitPrice = marketPrice,
                Source = PriceSource.Market,
                SourceDetails = isStale 
                    ? $"Market ({worldOrDc}) - stale" 
                    : $"Market ({worldOrDc})",
                LastUpdated = cachedData?.FetchedAt ?? DateTime.UtcNow
            };
        }
        else
        {
            priceInfo = new PriceInfo
            {
                ItemId = itemId,
                ItemName = itemName,
                UnitPrice = 0,
                Source = PriceSource.Unknown,
                SourceDetails = "No price data",
                LastUpdated = DateTime.UtcNow
            };
        }
        
        _memoryCache[itemId] = priceInfo;
        return priceInfo;
    }
    
    /// <summary>
    /// Force a fresh fetch from APIs, bypassing cache. Saves result to cache.
    /// </summary>
    public async Task<PriceInfo> ForceRefreshAsync(int itemId, string itemName, string worldOrDc, CancellationToken ct = default)
    {
        _logger.LogInformation("[PriceCheck] Force refresh for {ItemName}", itemName);
        return await FetchAndCachePriceAsync(itemId, itemName, worldOrDc, ct);
    }
    
    /// <summary>
    /// Get price even if stale. Returns null only if no data exists.
    /// </summary>
    public async Task<PriceInfo?> GetPriceAsync(int itemId, string itemName, string worldOrDc, bool allowStale, CancellationToken ct = default)
    {
        var ttl = GetCacheTtl();
        var (cachedData, isStale) = await _marketCache.GetWithStaleAsync(itemId, worldOrDc, ttl);
        
        if (cachedData == null)
        {
            // No data at all - fetch fresh
            return await FetchAndCachePriceAsync(itemId, itemName, worldOrDc, ct);
        }
        
        if (isStale && !allowStale)
        {
            // Stale and not allowed - fetch fresh
            return await FetchAndCachePriceAsync(itemId, itemName, worldOrDc, ct);
        }
        
        return ConvertCachedDataToPriceInfo(cachedData, itemName);
    }

    /// <summary>
    /// Fetch price from APIs and save to cache.
    /// </summary>
    private async Task<PriceInfo> FetchAndCachePriceAsync(int itemId, string itemName, string worldOrDc, CancellationToken ct)
    {
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
                _memoryCache[itemId] = priceInfo;
                return priceInfo;
            }

            // Check vendor prices first
            var vendorPrice = GetVendorPrice(garlandItem);
            
            // Fetch market prices from Universalis
            var marketData = await _universalisService.GetMarketDataAsync(worldOrDc, itemId, ct: ct);
            var marketPriceNq = GetAverageMarketPrice(marketData, hqOnly: false);
            var marketPriceHq = GetAverageMarketPrice(marketData, hqOnly: true);

            // Pick the best NQ price (vendors prioritized if equal)
            if (vendorPrice > 0 && (marketPriceNq <= 0 || vendorPrice <= marketPriceNq))
            {
                // Get gil vendors only (filter out tomestone/other currency vendors)
                var garlandVendors = garlandItem?.Vendors
                    ?.Where(v => string.Equals(v.Currency, "gil", StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? new List<GarlandVendor>();
                
                // Convert GarlandVendor to VendorInfo
                priceInfo.Vendors = garlandVendors.Select(v => new VendorInfo
                {
                    Name = v.Name,
                    Location = v.Location,
                    Price = v.Price,
                    Currency = v.Currency?.ToLowerInvariant() ?? "gil"
                }).ToList();
                
                priceInfo.UnitPrice = vendorPrice;
                priceInfo.Source = PriceSource.Vendor;
                var vendor = priceInfo.Vendors.FirstOrDefault();
                priceInfo.SourceDetails = vendor != null 
                    ? $"Vendor: {vendor.Name} ({vendor.Location})"
                    : "Vendor";
            }
            else if (marketPriceNq > 0)
            {
                priceInfo.UnitPrice = marketPriceNq;
                priceInfo.Source = PriceSource.Market;
                priceInfo.SourceDetails = $"Market ({worldOrDc})";
            }
            else
            {
                priceInfo.UnitPrice = 0;
                priceInfo.Source = PriceSource.Unknown;
                priceInfo.SourceDetails = "No price data";
            }
            
            // Save to memory cache
            _memoryCache[itemId] = priceInfo;
            
            // Save to SQLite cache (fire and forget - don't block)
            if (marketData != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var cachedData = new CachedMarketData
                        {
                            ItemId = itemId,
                            DataCenter = worldOrDc,
                            FetchedAt = DateTime.UtcNow,
                            DCAveragePrice = (decimal)marketData.AveragePrice,
                            HQAveragePrice = marketPriceHq > 0 ? marketPriceHq : null,
                            Worlds = marketData.Listings?.Select(l => new CachedWorldData
                            {
                                WorldName = l.WorldName ?? worldOrDc,
                                Listings = new List<CachedListing>
                                {
                                    new CachedListing
                                    {
                                        Quantity = l.Quantity,
                                        PricePerUnit = l.PricePerUnit,
                                        IsHq = l.IsHq,
                                        RetainerName = l.RetainerName ?? ""
                                    }
                                }
                            }).ToList() ?? new List<CachedWorldData>()
                        };
                        
                        await _marketCache.SetAsync(itemId, worldOrDc, cachedData);
                        _logger.LogDebug("[PriceCheck] Saved {ItemName} to SQLite cache", itemName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[PriceCheck] Failed to save {ItemName} to cache", itemName);
                    }
                });
            }
            
            return priceInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PriceCheck] Failed to fetch price for {ItemName}", itemName);
            priceInfo.UnitPrice = 0;
            priceInfo.Source = PriceSource.Unknown;
            priceInfo.SourceDetails = $"Error: {ex.Message}";
            return priceInfo;
        }
    }
    
    private PriceInfo ConvertCachedDataToPriceInfo(CachedMarketData cached, string itemName)
    {
        // Determine best price from cached data
        var bestPrice = cached.DCAveragePrice;
        var source = PriceSource.Market;
        var details = $"Market ({cached.DataCenter}) - cached";
        
        // Check for vendor price (we don't cache vendor prices in SQLite, so this is market-only)
        // Vendor prices are fetched fresh each time as they're more reliable
        
        return new PriceInfo
        {
            ItemId = cached.ItemId,
            ItemName = itemName,
            UnitPrice = bestPrice,
            Source = source,
            SourceDetails = details,
            LastUpdated = cached.FetchedAt
        };
    }

    // ... rest of existing methods (GetBestPricesBulkAsync, GetVendorPrice, etc.) ...
    
    /// <summary>
    /// Get best prices for multiple items in bulk with progress reporting.
    /// </summary>
    public async Task<Dictionary<int, PriceInfo>> GetBestPricesBulkAsync(
        List<(int itemId, string name)> items, 
        string worldOrDc,
        CancellationToken ct = default,
        IProgress<(int completed, int total, string currentItem, PriceFetchStage stage, string message)>? progress = null,
        bool forceRefresh = false)
    {
        var results = new Dictionary<int, PriceInfo>();
        var ttl = GetCacheTtl();

        progress?.Report((0, items.Count, "", PriceFetchStage.CheckingCache, "Checking cache..."));
        
        int processed = 0;
        foreach (var (itemId, name) in items)
        {
            try
            {
                PriceInfo priceInfo;
                
                if (forceRefresh)
                {
                    priceInfo = await ForceRefreshAsync(itemId, name, worldOrDc, ct);
                }
                else
                {
                    priceInfo = await GetBestPriceAsync(itemId, name, worldOrDc, ct);
                }
                
                results[itemId] = priceInfo;
                processed++;
                
                progress?.Report((processed, items.Count, name, PriceFetchStage.FetchingMarketData, 
                    $"Processed {processed}/{items.Count}"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PriceCheck] Failed to get price for {ItemName}", name);
                results[itemId] = new PriceInfo
                {
                    ItemId = itemId,
                    ItemName = name,
                    UnitPrice = 0,
                    Source = PriceSource.Unknown,
                    SourceDetails = $"Error: {ex.Message}"
                };
                processed++;
            }
        }
        
        progress?.Report((processed, items.Count, "", PriceFetchStage.Complete, $"Complete - {processed} items"));
        return results;
    }
    
    /// <summary>
    /// North American Data Centers for multi-DC search.
    /// </summary>
    private static readonly string[] NorthAmericanDCs = { "Aether", "Primal", "Crystal", "Dynamis" };
    
    /// <summary>
    /// Get best prices for multiple items across all NA Data Centers.
    /// Returns the best price found across all DCs for each item.
    /// </summary>
    public async Task<Dictionary<int, PriceInfo>> GetBestPricesMultiDCAsync(
        List<(int itemId, string name)> items,
        CancellationToken ct = default,
        IProgress<(int completed, int total, string currentItem, PriceFetchStage stage, string message)>? progress = null,
        bool forceRefresh = false)
    {
        var results = new Dictionary<int, PriceInfo>();
        var ttl = GetCacheTtl();
        
        _logger.LogInformation("[PriceCheck] Starting multi-DC price fetch for {Count} items across {DCCount} NA DCs", 
            items.Count, NorthAmericanDCs.Length);

        progress?.Report((0, items.Count, "", PriceFetchStage.CheckingCache, "Checking cache across all NA DCs..."));
        
        int processed = 0;
        foreach (var (itemId, name) in items)
        {
            try
            {
                PriceInfo? bestPriceInfo = null;
                var checkedDCs = new List<string>();
                
                // Check each NA DC and find the best price
                foreach (var dc in NorthAmericanDCs)
                {
                    ct.ThrowIfCancellationRequested();
                    
                    PriceInfo dcPriceInfo;
                    if (forceRefresh)
                    {
                        dcPriceInfo = await ForceRefreshAsync(itemId, name, dc, ct);
                    }
                    else
                    {
                        dcPriceInfo = await GetBestPriceAsync(itemId, name, dc, ct);
                    }
                    
                    checkedDCs.Add(dc);
                    
                    // Track the best price across DCs
                    if (bestPriceInfo == null || dcPriceInfo.UnitPrice < bestPriceInfo.UnitPrice && dcPriceInfo.UnitPrice > 0)
                    {
                        bestPriceInfo = dcPriceInfo;
                        // Update source details to include DC info
                        if (dcPriceInfo.Source == PriceSource.Market && !string.IsNullOrEmpty(dcPriceInfo.SourceDetails))
                        {
                            bestPriceInfo.SourceDetails = $"{dc}: {dcPriceInfo.SourceDetails}";
                        }
                    }
                }
                
                // If we found a valid price, use it; otherwise return the last checked result
                results[itemId] = bestPriceInfo ?? new PriceInfo
                {
                    ItemId = itemId,
                    ItemName = name,
                    UnitPrice = 0,
                    Source = PriceSource.Unknown,
                    SourceDetails = "No price found on any NA DC"
                };
                
                processed++;
                progress?.Report((processed, items.Count, name, PriceFetchStage.FetchingMarketData, 
                    $"Processed {processed}/{items.Count} (checked {string.Join(", ", checkedDCs)})"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PriceCheck] Failed to get price for {ItemName} across DCs", name);
                results[itemId] = new PriceInfo
                {
                    ItemId = itemId,
                    ItemName = name,
                    UnitPrice = 0,
                    Source = PriceSource.Unknown,
                    SourceDetails = $"Error: {ex.Message}"
                };
                processed++;
            }
        }
        
        _logger.LogInformation("[PriceCheck] Multi-DC price fetch complete for {Count} items", processed);
        progress?.Report((processed, items.Count, "", PriceFetchStage.Complete, $"Complete - {processed} items across all NA DCs"));
        return results;
    }

    /// <summary>
    /// Extract vendor price from Garland item data.
    /// </summary>
    private static long GetVendorPrice(GarlandItem? item)
    {
        if (item?.Vendors == null || item.Vendors.Count == 0)
            return 0;
            
        // Get cheapest vendor price
        return item.Vendors.Min(v => v.Price);
    }

    /// <summary>
    /// Calculate average market price from Universalis data.
    /// </summary>
    private static long GetAverageMarketPrice(UniversalisResponse? data, bool hqOnly)
    {
        if (data?.Listings == null || data.Listings.Count == 0)
            return 0;

        var relevantListings = data.Listings
            .Where(l => !hqOnly || l.IsHq)
            .ToList();

        if (relevantListings.Count == 0)
            return 0;

        // Weighted average by quantity
        var totalQuantity = relevantListings.Sum(l => l.Quantity);
        if (totalQuantity == 0)
            return 0;

        var weightedSum = relevantListings.Sum(l => l.PricePerUnit * l.Quantity);
        return weightedSum / totalQuantity;
    }
}

/// <summary>
/// Stages of price fetching for progress reporting.
/// </summary>
public enum PriceFetchStage
{
    CheckingCache,
    FetchingGarlandData,
    FetchingMarketData,
    ProcessingResults,
    Complete
}
