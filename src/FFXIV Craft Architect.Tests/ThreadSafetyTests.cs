using System.Collections.Concurrent;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

/// <summary>
/// Unit tests for thread-safety fixes in P1.2.
/// Validates ConcurrentDictionary usage, async semaphore patterns, and caching strategies.
/// </summary>
public class ThreadSafetyTests
{
    #region PriceCheckService - Concurrency Tests

    [Fact]
    public async Task PriceCheckService_GetBestPriceAsync_ConcurrentAccess_DoesNotThrow()
    {
        // Arrange
        var garland = new Mock<IGarlandService>();
        var universalis = new Mock<IUniversalisService>();
        var settings = new Mock<ISettingsService>();
        var marketCache = new Mock<IMarketCacheService>();

        settings.Setup(s => s.Get("market.cache_ttl_hours", It.IsAny<double>())).Returns(3.0);

        var itemId = 1;
        var itemName = "Test Item";
        var worldOrDc = "Aether";

        garland
            .Setup(g => g.GetItemAsync(itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GarlandItem
            {
                Id = itemId,
                Name = itemName,
                TradeableRaw = true,
                VendorsRaw = new List<object>()
            });

        marketCache
            .Setup(c => c.GetWithStaleAsync(itemId, worldOrDc, It.IsAny<TimeSpan>()))
            .ReturnsAsync((new CachedMarketData
            {
                ItemId = itemId,
                DataCenter = worldOrDc,
                FetchedAt = DateTime.UtcNow,
                DCAveragePrice = 1234m,
                Worlds = new List<CachedWorldData>()
            }, false));

        var service = new PriceCheckService(
            garland.Object,
            universalis.Object,
            settings.Object,
            marketCache.Object,
            new NullLogger<PriceCheckService>());

        var tasks = new List<Task>();
        var exceptions = new ConcurrentBag<Exception>();
        var results = new ConcurrentBag<PriceInfo>();
        
        // Act - Simulate concurrent access
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await service.GetBestPriceAsync(itemId, itemName, worldOrDc);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }
        
        await Task.WhenAll(tasks);
        
        // Assert
        Assert.Empty(exceptions);
        Assert.Equal(100, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(itemId, r.ItemId);
            Assert.Equal(1234m, r.UnitPrice);
            Assert.Equal(PriceSource.Market, r.Source);
        });
    }

    #endregion

    #region ItemCacheService - Async Method Tests

    [Fact]
    public async Task ItemCacheService_GetItemNameAsync_UsesWaitAsync()
    {
        // Arrange
        var service = new ItemCacheService(new NullLogger<ItemCacheService>());
        service.StoreItem(1, "Test Item", 100);
        
        // Act
        var result = await service.GetItemNameAsync(1);
        
        // Assert
        Assert.Equal("Test Item", result);
    }

    [Fact]
    public async Task ItemCacheService_GetItemNameAsync_CancellationToken_RespectsCancellation()
    {
        // Arrange
        var service = new ItemCacheService(new NullLogger<ItemCacheService>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        // Act & Assert - Should throw OperationCanceledException (or derived TaskCanceledException) when cancelled
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.GetItemNameAsync(1, cts.Token);
        });
    }

    [Fact]
    public async Task ItemCacheService_GetIconIdAsync_ReturnsCorrectValue()
    {
        // Arrange
        var service = new ItemCacheService(new NullLogger<ItemCacheService>());
        service.StoreItem(1, "Test Item", 100);
        
        // Act
        var result = await service.GetIconIdAsync(1);
        
        // Assert
        Assert.Equal(100, result);
    }

    [Fact]
    public async Task ItemCacheService_GetItemAsync_ReturnsNameAndIcon()
    {
        // Arrange
        var service = new ItemCacheService(new NullLogger<ItemCacheService>());
        service.StoreItem(1, "Test Item", 100);
        
        // Act
        var (name, iconId) = await service.GetItemAsync(1);
        
        // Assert
        Assert.Equal("Test Item", name);
        Assert.Equal(100, iconId);
    }

    [Fact]
    public async Task ItemCacheService_StoreItemAsync_StoresCorrectly()
    {
        // Arrange
        var service = new ItemCacheService(new NullLogger<ItemCacheService>());
        
        // Act
        await service.StoreItemAsync(1, "Async Item", 200);
        var result = service.GetItemName(1);
        
        // Assert
        Assert.Equal("Async Item", result);
    }

    [Fact]
    public async Task ItemCacheService_StoreItemsAsync_StoresMultipleItems()
    {
        // Arrange
        var service = new ItemCacheService(new NullLogger<ItemCacheService>());
        var items = new[]
        {
            (Id: 1, Name: "Item 1", IconId: 100),
            (Id: 2, Name: "Item 2", IconId: 200),
            (Id: 3, Name: "Item 3", IconId: 300)
        };
        
        // Act
        await service.StoreItemsAsync(items);
        
        // Assert
        Assert.Equal("Item 1", service.GetItemName(1));
        Assert.Equal("Item 2", service.GetItemName(2));
        Assert.Equal("Item 3", service.GetItemName(3));
    }

    [Fact]
    public async Task ItemCacheService_ContainsAsync_ReturnsCorrectResult()
    {
        // Arrange
        var service = new ItemCacheService(new NullLogger<ItemCacheService>());
        service.StoreItem(1, "Test Item", 100);
        
        // Act & Assert
        Assert.True(await service.ContainsAsync(1));
        Assert.False(await service.ContainsAsync(999));
    }

    [Fact]
    public async Task ItemCacheService_GetStatsAsync_ReturnsStats()
    {
        // Arrange
        var service = new ItemCacheService(new NullLogger<ItemCacheService>());
        service.StoreItem(1, "Item 1", 100);
        service.StoreItem(2, "Item 2", 200);
        
        // Act
        var (count, oldest) = await service.GetStatsAsync();
        
        // Assert
        Assert.Equal(2, count);
        Assert.NotNull(oldest);
    }

    [Fact]
    public async Task ItemCacheService_ConcurrentAsyncAccess_DoesNotThrow()
    {
        // Arrange
        var service = new ItemCacheService(new NullLogger<ItemCacheService>());
        var exceptions = new ConcurrentBag<Exception>();
        var tasks = new List<Task>();
        
        // Act - Multiple concurrent async operations
        for (int i = 0; i < 50; i++)
        {
            var itemId = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await service.StoreItemAsync(itemId, $"Item {itemId}", itemId * 10);
                    await service.GetItemNameAsync(itemId);
                    await service.GetIconIdAsync(itemId);
                    await service.ContainsAsync(itemId);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }
        
        await Task.WhenAll(tasks);
        
        // Assert
        Assert.Empty(exceptions);
    }

    [Fact]
    public void ItemCacheService_SyncMethods_StillWork()
    {
        // Verify backward compatibility - sync methods should still work
        // Arrange
        var service = new ItemCacheService(new NullLogger<ItemCacheService>());
        
        // Act & Assert
        service.StoreItem(1, "Sync Item", 100);
        Assert.Equal("Sync Item", service.GetItemName(1));
        Assert.Equal(100, service.GetIconId(1));
        Assert.True(service.Contains(1));
        
        var (count, _) = service.GetStats();
        Assert.Equal(1, count);
    }

    #endregion

}
