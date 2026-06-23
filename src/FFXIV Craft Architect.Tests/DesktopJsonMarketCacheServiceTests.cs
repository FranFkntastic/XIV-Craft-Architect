using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Desktop.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class DesktopJsonMarketCacheServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"craft-architect-market-cache-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetAsync_PersistsEntryAndStatsAcrossServiceInstances()
    {
        var service = CreateService();
        await service.SetAsync(5107, "Aether", new CachedMarketData
        {
            ItemId = 5107,
            DataCenter = "Aether",
            FetchedAt = DateTime.UtcNow,
            DCAveragePrice = 123,
            Worlds =
            [
                new CachedWorldData
                {
                    WorldName = "Adamantoise",
                    Listings =
                    [
                        new CachedListing
                        {
                            Quantity = 10,
                            PricePerUnit = 123,
                            RetainerName = "Test Retainer"
                        }
                    ]
                }
            ]
        });

        var reloaded = CreateService();
        var cached = await reloaded.GetAsync(5107, "Aether");
        var stats = await reloaded.GetStatsAsync();

        Assert.NotNull(cached);
        Assert.Equal(123, cached.DCAveragePrice);
        Assert.Equal("Adamantoise", Assert.Single(cached.Worlds).WorldName);
        Assert.Equal(1, stats.TotalEntries);
        Assert.Equal(1, stats.ValidEntries);
        Assert.True(stats.ApproximateSizeBytes > 0);
    }

    [Fact]
    public async Task CleanupStaleAsync_RemovesOnlyExpiredEntries()
    {
        var service = CreateService();
        await service.SetAsync(1, "Aether", CreateMarketData(1, DateTime.UtcNow.AddHours(-2)));
        await service.SetAsync(2, "Aether", CreateMarketData(2, DateTime.UtcNow));

        var removed = await service.CleanupStaleAsync(TimeSpan.FromHours(1));
        var stats = await service.GetStatsAsync();

        Assert.Equal(1, removed);
        Assert.Null(await service.GetAsync(1, "Aether", TimeSpan.FromDays(1)));
        Assert.NotNull(await service.GetAsync(2, "Aether"));
        Assert.Equal(1, stats.TotalEntries);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllEntries()
    {
        var service = CreateService();
        await service.SetAsync(1, "Aether", CreateMarketData(1, DateTime.UtcNow));
        await service.SetAsync(2, "Aether", CreateMarketData(2, DateTime.UtcNow));

        var removed = await service.ClearAsync();
        var stats = await service.GetStatsAsync();

        Assert.Equal(2, removed);
        Assert.Equal(0, stats.TotalEntries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DesktopJsonMarketCacheService CreateService() =>
        new(new UniversalisService(new HttpClient()), _root);

    private static CachedMarketData CreateMarketData(int itemId, DateTime fetchedAtUtc) =>
        new()
        {
            ItemId = itemId,
            DataCenter = "Aether",
            FetchedAt = fetchedAtUtc,
            DCAveragePrice = 123
        };
}
