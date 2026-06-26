using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class TradeLaborBenchmarkCalibrationWorkflowServiceTests
{
    [Fact]
    public async Task RecalculateManagedCobaltRivetsAsync_ReusesFreshEvidence()
    {
        var cache = CreateCache(fetchedCount: 0, CreateCobaltRivetsData(DateTime.UtcNow));
        var service = CreateService(cache.Object);

        var result = await service.RecalculateManagedCobaltRivetsAsync(CreateRequest());

        Assert.Equal(TradeLaborBenchmarkCalibrationStatus.ReusedFreshEvidence, result.Status);
        Assert.NotNull(result.LaborStandard);
        Assert.True(result.LaborStandard.IsManagedCobaltRivets);
        Assert.Equal(100_000m, result.LaborStandard.BenchmarkLaborPayout);
        Assert.Contains("reused", result.Message, StringComparison.OrdinalIgnoreCase);
        cache.Verify(c => c.RefreshRequestedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RecalculateManagedCobaltRivetsAsync_RefreshesMissingOrStaleEvidence()
    {
        var cache = CreateCache(fetchedCount: 1, CreateCobaltRivetsData(DateTime.UtcNow));
        var service = CreateService(cache.Object);

        var result = await service.RecalculateManagedCobaltRivetsAsync(CreateRequest());

        Assert.Equal(TradeLaborBenchmarkCalibrationStatus.RefreshedEvidence, result.Status);
        Assert.NotNull(result.LaborStandard);
        Assert.True(result.LaborStandard.IsManagedCobaltRivets);
        Assert.Contains("refreshed", result.Message, StringComparison.OrdinalIgnoreCase);
        cache.Verify(c => c.EnsurePopulatedAsync(
                It.Is<List<(int itemId, string dataCenter)>>(requests =>
                    requests.Count == 1 &&
                    requests[0].itemId == TradeLaborStandardCalibrationService.CobaltRivetsItemId &&
                    requests[0].dataCenter == "Aether"),
                TimeSpan.FromHours(1),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RecalculateManagedCobaltRivetsAsync_ReturnsFailureWithoutLaborStandardWhenRefreshFails()
    {
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Universalis unavailable"));
        var service = CreateService(cache.Object);

        var result = await service.RecalculateManagedCobaltRivetsAsync(CreateRequest());

        Assert.Equal(TradeLaborBenchmarkCalibrationStatus.RefreshFailed, result.Status);
        Assert.Null(result.LaborStandard);
        Assert.Contains("Universalis unavailable", result.Message);
    }

    [Fact]
    public async Task RecalculateManagedCobaltRivetsAsync_ReturnsMissingEvidenceWhenNoMarketDataIsAvailable()
    {
        var cache = CreateCache(fetchedCount: 1, data: null);
        var service = CreateService(cache.Object);

        var result = await service.RecalculateManagedCobaltRivetsAsync(CreateRequest());

        Assert.Equal(TradeLaborBenchmarkCalibrationStatus.MissingEvidence, result.Status);
        Assert.Null(result.LaborStandard);
        Assert.Contains("Cobalt Rivets", result.Message);
    }

    private static TradeLaborBenchmarkCalibrationWorkflowService CreateService(IMarketCacheService cache)
    {
        return new TradeLaborBenchmarkCalibrationWorkflowService(
            cache,
            new MarketShoppingService(Mock.Of<IMarketCacheService>()),
            new TradeLaborStandardCalibrationService());
    }

    private static TradeLaborBenchmarkCalibrationRequest CreateRequest()
    {
        return new TradeLaborBenchmarkCalibrationRequest(
            SelectedDataCenter: "Aether",
            SelectedRegion: "North America",
            LegacyCommissionPercent: 20m,
            BenchmarkSynthCount: 200,
            FreshnessWindow: TimeSpan.FromHours(1),
            CalibratedAtUtc: new DateTime(2026, 6, 25, 22, 0, 0, DateTimeKind.Utc));
    }

    private static Mock<IMarketCacheService> CreateCache(int fetchedCount, CachedMarketData? data)
    {
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(fetchedCount);
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(data == null
                ? new Dictionary<(int itemId, string dataCenter), CachedMarketData>()
                : new Dictionary<(int itemId, string dataCenter), CachedMarketData>
                {
                    [(data.ItemId, data.DataCenter)] = data
                });
        return cache;
    }

    private static CachedMarketData CreateCobaltRivetsData(DateTime fetchedAtUtc)
    {
        return new CachedMarketData
        {
            ItemId = TradeLaborStandardCalibrationService.CobaltRivetsItemId,
            DataCenter = "Aether",
            DCAveragePrice = 500m,
            HQAveragePrice = 500m,
            FetchedAt = fetchedAtUtc,
            Worlds =
            [
                new CachedWorldData
                {
                    WorldName = "Gilgamesh",
                    Listings =
                    [
                        new CachedListing
                        {
                            Quantity = TradeLaborStandardCalibrationService.CobaltRivetsBenchmarkQuantity,
                            PricePerUnit = 500,
                            IsHq = true,
                            RetainerName = "Bench"
                        }
                    ]
                }
            ]
        };
    }
}
