using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

/// <summary>
/// Unit tests for PriceCheckService cache functionality.
/// </summary>
public class PriceCheckServiceTests
{
    private readonly Mock<IGarlandService> _mockGarlandService;
    private readonly Mock<IUniversalisService> _mockUniversalisService;
    private readonly Mock<ISettingsService> _mockSettingsService;
    private readonly Mock<IMarketCacheService> _mockCacheService;
    private readonly PriceCheckService _service;

    public PriceCheckServiceTests()
    {
        // Use null logger for tests
        var logger = new NullLogger<PriceCheckService>();
        
        // Mock dependencies
        _mockGarlandService = new Mock<IGarlandService>();
        _mockUniversalisService = new Mock<IUniversalisService>();
        _mockSettingsService = new Mock<ISettingsService>();
        _mockCacheService = new Mock<IMarketCacheService>();
        
        // Default TTL setting
        _mockSettingsService
            .Setup(s => s.Get("market.cache_ttl_hours", It.IsAny<double>()))
            .Returns(3.0);
        
        _service = new PriceCheckService(
            _mockGarlandService.Object,
            _mockUniversalisService.Object,
            _mockSettingsService.Object,
            _mockCacheService.Object,
            logger);
    }

    [Fact]
    public async Task GetBestPriceAsync_CacheHitValid_ReturnsCachedMarketPrice()
    {
        // Arrange
        var itemId = 123;
        var itemName = "Test Item";
        var worldOrDc = "Aether";
        var cachedData = new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = worldOrDc,
            FetchedAt = DateTime.UtcNow.AddMinutes(-30), // Fresh (within 3h TTL)
            DCAveragePrice = 1000m,
            Worlds = new List<CachedWorldData>()
        };
        
        _mockCacheService
            .Setup(c => c.GetWithStaleAsync(itemId, worldOrDc, It.IsAny<TimeSpan>()))
            .ReturnsAsync((cachedData, false)); // Valid, not stale

        var garlandItem = new GarlandItem
        {
            Id = itemId,
            Name = itemName,
            TradeableRaw = true,
            VendorsRaw = new List<object>()
        };
        _mockGarlandService
            .Setup(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(garlandItem);
        
        // Act
        var result = await _service.GetBestPriceAsync(itemId, itemName, worldOrDc);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(itemId, result.ItemId);
        Assert.Equal(itemName, result.ItemName);
        Assert.Equal(1000m, result.UnitPrice);
        Assert.Equal(PriceSource.Market, result.Source);

        // Garland is consulted for vendor data; Universalis is not called in cache-read path.
        _mockGarlandService.Verify(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()), Times.Once);
        _mockUniversalisService.Verify(u => u.GetMarketDataAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBestPriceAsync_CacheMiss_ReturnsUnknownWhenNoVendorPrice()
    {
        // Arrange
        var itemId = 456;
        var itemName = "Test Item 2";
        var worldOrDc = "Primal";
        
        // No cache data
        _mockCacheService
            .Setup(c => c.GetWithStaleAsync(itemId, worldOrDc, It.IsAny<TimeSpan>()))
            .ReturnsAsync(((CachedMarketData?, bool))(null, false));
        
        // Mock Garland response (tradeable item with no vendor)
        var garlandItem = new GarlandItem
        {
            Id = itemId,
            Name = itemName,
            TradeableRaw = true,
            VendorsRaw = new List<object>()
        };
        _mockGarlandService
            .Setup(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(garlandItem);
        
        // Act
        var result = await _service.GetBestPriceAsync(itemId, itemName, worldOrDc);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(itemId, result.ItemId);
        Assert.Equal(0m, result.UnitPrice);
        Assert.Equal(PriceSource.Unknown, result.Source);

        _mockGarlandService.Verify(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()), Times.Once);
        _mockUniversalisService.Verify(u => u.GetMarketDataAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBestPriceAsync_CacheStale_ReturnsStaleCachedPrice()
    {
        // Arrange
        var itemId = 789;
        var itemName = "Stale Item";
        var worldOrDc = "Crystal";
        var staleData = new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = worldOrDc,
            FetchedAt = DateTime.UtcNow.AddHours(-4), // Stale (beyond 3h TTL)
            DCAveragePrice = 2000m,
            Worlds = new List<CachedWorldData>()
        };
        
        _mockCacheService
            .Setup(c => c.GetWithStaleAsync(itemId, worldOrDc, It.IsAny<TimeSpan>()))
            .ReturnsAsync((staleData, true)); // Stale data
        
        // Mock Garland response
        var garlandItem = new GarlandItem
        {
            Id = itemId,
            Name = itemName,
            TradeableRaw = true,
            VendorsRaw = new List<object>()
        };
        _mockGarlandService
            .Setup(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(garlandItem);
        
        // Act
        var result = await _service.GetBestPriceAsync(itemId, itemName, worldOrDc);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(2000m, result.UnitPrice);
        Assert.Equal(PriceSource.Market, result.Source);
        Assert.Contains("stale", result.SourceDetails, StringComparison.OrdinalIgnoreCase);

        _mockGarlandService.Verify(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()), Times.Once);
        _mockUniversalisService.Verify(u => u.GetMarketDataAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPriceAsync_StaleAndNotAllowed_FetchesFreshMarketData()
    {
        // Arrange
        var itemId = 790;
        var itemName = "Fresh Fetch Item";
        var worldOrDc = "Crystal";
        var staleData = new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = worldOrDc,
            FetchedAt = DateTime.UtcNow.AddHours(-4),
            DCAveragePrice = 2000m,
            Worlds = new List<CachedWorldData>()
        };

        _mockCacheService
            .Setup(c => c.GetWithStaleAsync(itemId, worldOrDc, It.IsAny<TimeSpan>()))
            .ReturnsAsync((staleData, true));

        var garlandItem = new GarlandItem
        {
            Id = itemId,
            Name = itemName,
            TradeableRaw = true,
            VendorsRaw = new List<object>()
        };
        _mockGarlandService
            .Setup(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(garlandItem);

        var marketData = new UniversalisResponse
        {
            ItemId = itemId,
            Listings = new List<MarketListing>
            {
                new MarketListing { PricePerUnit = 1500, Quantity = 1, WorldName = "Balmung", IsHq = false }
            },
            AveragePrice = 1500
        };
        _mockUniversalisService
            .Setup(u => u.GetMarketDataAsync(worldOrDc, itemId, It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(marketData);

        // Act
        var result = await _service.GetPriceAsync(itemId, itemName, worldOrDc, allowStale: false);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1500m, result.UnitPrice);
        Assert.Equal(PriceSource.Market, result.Source);
        _mockUniversalisService.Verify(u => u.GetMarketDataAsync(worldOrDc, itemId, It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForceRefreshAsync_BypassesCache_CallsApiAndSaves()
    {
        // Arrange
        var itemId = 999;
        var itemName = "Force Refresh Item";
        var worldOrDc = "Aether";
        
        // Set up valid cache data
        var cachedData = new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = worldOrDc,
            FetchedAt = DateTime.UtcNow.AddMinutes(-5), // Fresh
            DCAveragePrice = 3000m,
            Worlds = new List<CachedWorldData>()
        };
        
        _mockCacheService
            .Setup(c => c.GetWithStaleAsync(itemId, worldOrDc, It.IsAny<TimeSpan>()))
            .ReturnsAsync((cachedData, false));
        
        // Mock API responses
        var garlandItem = new GarlandItem
        {
            Id = itemId,
            Name = itemName,
            TradeableRaw = true,
            VendorsRaw = new List<object>()
        };
        _mockGarlandService
            .Setup(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(garlandItem);
        
        var marketData = new UniversalisResponse
        {
            ItemId = itemId,
            Listings = new List<MarketListing>
            {
                new MarketListing { PricePerUnit = 2500, Quantity = 1, WorldName = "Gilgamesh", IsHq = false }
            },
            AveragePrice = 2500
        };
        _mockUniversalisService
            .Setup(u => u.GetMarketDataAsync(worldOrDc, itemId, It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(marketData);
        
        // Act
        var result = await _service.ForceRefreshAsync(itemId, itemName, worldOrDc);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(2500m, result.UnitPrice); // Fresh API price, not cached
        
        // Verify API was called even though cache was valid
        _mockGarlandService.Verify(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()), Times.Once);
        _mockUniversalisService.Verify(u => u.GetMarketDataAsync(worldOrDc, itemId, It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetBestPriceAsync_UntradeableItem_SkipsCacheAndApi()
    {
        // Arrange
        var itemId = 111;
        var itemName = "Untradeable Item";
        var worldOrDc = "Aether";
        
        // No cache lookup for untradeable - but we need to mock it anyway
        _mockCacheService
            .Setup(c => c.GetWithStaleAsync(itemId, worldOrDc, It.IsAny<TimeSpan>()))
            .ReturnsAsync(((CachedMarketData?, bool))(null, false));
        
        var garlandItem = new GarlandItem
        {
            Id = itemId,
            Name = itemName,
            TradeableRaw = false // Untradeable
        };
        _mockGarlandService
            .Setup(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(garlandItem);
        
        // Act
        var result = await _service.GetBestPriceAsync(itemId, itemName, worldOrDc);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(PriceSource.Untradeable, result.Source);
        Assert.Equal(0m, result.UnitPrice);
        
        // Universalis should NOT be called for untradeable
        _mockUniversalisService.Verify(u => u.GetMarketDataAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetBestPriceAsync_VendorPriceCheaper_UsesVendorPrice()
    {
        // Arrange
        var itemId = 222;
        var itemName = "Vendor Item";
        var worldOrDc = "Aether";
        
        _mockCacheService
            .Setup(c => c.GetWithStaleAsync(itemId, worldOrDc, It.IsAny<TimeSpan>()))
            .ReturnsAsync(((CachedMarketData?, bool))(null, false));
        
        // Create vendor data as JsonElement for proper parsing
        var vendorJson = JsonSerializer.Serialize(new { name = "Test Vendor", location = "Limsa", price = 100 });
        var vendorElement = JsonSerializer.Deserialize<JsonElement>(vendorJson);
        
        var garlandItem = new GarlandItem
        {
            Id = itemId,
            Name = itemName,
            TradeableRaw = true,
            VendorsRaw = new List<object> { vendorElement }
        };
        _mockGarlandService
            .Setup(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(garlandItem);
        
        // Market price is higher than vendor
        var marketData = new UniversalisResponse
        {
            ItemId = itemId,
            Listings = new List<MarketListing>
            {
                new MarketListing { PricePerUnit = 200, Quantity = 1, WorldName = "TestWorld", IsHq = false }
            },
            AveragePrice = 200
        };
        _mockUniversalisService
            .Setup(u => u.GetMarketDataAsync(worldOrDc, itemId, It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(marketData);
        
        // Act
        var result = await _service.GetBestPriceAsync(itemId, itemName, worldOrDc);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(100m, result.UnitPrice); // Vendor price
        Assert.Equal(PriceSource.Vendor, result.Source);
    }
}
