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
        var fetchedAtUtc = DateTime.UtcNow;
        var cache = CreateCache(
            fetchedCount: 0,
            CreateCobaltRivetsData(fetchedAtUtc),
            CreateIngredientData(fetchedAtUtc));
        var service = CreateService(cache.Object, CreateBenchmarkPlan());

        var result = await service.RecalculateManagedCobaltRivetsAsync(CreateRequest());

        Assert.Equal(TradeLaborBenchmarkCalibrationStatus.ReusedFreshEvidence, result.Status);
        Assert.NotNull(result.LaborStandard);
        Assert.True(result.LaborStandard.IsManagedCobaltRivets);
        Assert.False(result.LaborStandard.BenchmarkRequiresHq);
        Assert.Equal(20_000m, result.LaborStandard.BenchmarkLaborPayout);
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
        var fetchedAtUtc = DateTime.UtcNow;
        var cache = CreateCache(
            fetchedCount: 1,
            CreateCobaltRivetsData(fetchedAtUtc),
            CreateIngredientData(fetchedAtUtc));
        var service = CreateService(cache.Object, CreateBenchmarkPlan());

        var result = await service.RecalculateManagedCobaltRivetsAsync(CreateRequest());

        Assert.Equal(TradeLaborBenchmarkCalibrationStatus.RefreshedEvidence, result.Status);
        Assert.NotNull(result.LaborStandard);
        Assert.True(result.LaborStandard.IsManagedCobaltRivets);
        Assert.Contains("refreshed", result.Message, StringComparison.OrdinalIgnoreCase);
        cache.Verify(c => c.EnsurePopulatedAsync(
                It.Is<List<(int itemId, string dataCenter)>>(requests =>
                    requests.Count == 1 &&
                    requests[0].itemId == 42 &&
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
        var service = CreateService(cache.Object, CreateBenchmarkPlan());

        var result = await service.RecalculateManagedCobaltRivetsAsync(CreateRequest());

        Assert.Equal(TradeLaborBenchmarkCalibrationStatus.RefreshFailed, result.Status);
        Assert.Null(result.LaborStandard);
        Assert.Contains("Universalis unavailable", result.Message);
    }

    [Fact]
    public async Task RecalculateManagedCobaltRivetsAsync_ReturnsMissingEvidenceWhenNoMarketDataIsAvailable()
    {
        var cache = CreateCache(fetchedCount: 1);
        var service = CreateService(cache.Object, CreateBenchmarkPlan());

        var result = await service.RecalculateManagedCobaltRivetsAsync(CreateRequest());

        Assert.Equal(TradeLaborBenchmarkCalibrationStatus.MissingEvidence, result.Status);
        Assert.Null(result.LaborStandard);
        Assert.Contains("Cobalt Rivets", result.Message);
    }

    [Fact]
    public async Task RecalculateManagedCobaltRivetsAsync_UsesCraftedBenchmarkCostInsteadOfFinishedRivetsBuyCost()
    {
        var fetchedAtUtc = DateTime.UtcNow;
        var cache = CreateCache(
            fetchedCount: 0,
            CreateCobaltRivetsData(fetchedAtUtc),
            CreateIngredientData(fetchedAtUtc));
        var service = CreateService(cache.Object, CreateBenchmarkPlan());

        var result = await service.RecalculateManagedCobaltRivetsAsync(CreateRequest());

        Assert.Equal(TradeLaborBenchmarkCalibrationStatus.ReusedFreshEvidence, result.Status);
        Assert.NotNull(result.LaborStandard);
        Assert.Equal(20_000m, result.LaborStandard.BenchmarkLaborPayout);
        cache.Verify(c => c.EnsurePopulatedAsync(
                It.Is<List<(int itemId, string dataCenter)>>(requests =>
                    requests.Count == 1 &&
                    requests[0].itemId == 42 &&
                    requests[0].dataCenter == "Aether"),
                TimeSpan.FromHours(1),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BuildManagedCobaltRivetsPlanPreviewAsync_ReturnsCraftTreeAndActiveProcurementLeaves()
    {
        var service = CreateService(Mock.Of<IMarketCacheService>(), CreateBenchmarkPlan());

        var preview = await service.BuildManagedCobaltRivetsPlanPreviewAsync("Aether");

        Assert.Equal("Cobalt Rivets benchmark craft plan", preview.Title);
        Assert.Equal("Aether", preview.DataCenter);
        Assert.Equal(2, preview.Items.Count);
        Assert.Collection(
            preview.Items,
            root =>
            {
                Assert.Equal("Cobalt Rivets", root.Name);
                Assert.Equal(999, root.Quantity);
                Assert.Equal(0, root.Depth);
                Assert.Equal(AcquisitionSource.Craft, root.Source);
                Assert.False(root.IsActiveProcurement);
            },
            ingredient =>
            {
                Assert.Equal("Benchmark Ingredient", ingredient.Name);
                Assert.Equal(200, ingredient.Quantity);
                Assert.Equal(1, ingredient.Depth);
                Assert.Equal(AcquisitionSource.MarketBuyNq, ingredient.Source);
                Assert.True(ingredient.IsActiveProcurement);
            });
    }

    private static TradeLaborBenchmarkCalibrationWorkflowService CreateService(
        IMarketCacheService cache,
        CraftingPlan benchmarkPlan)
    {
        return new TradeLaborBenchmarkCalibrationWorkflowService(
            cache,
            new MarketShoppingService(Mock.Of<IMarketCacheService>()),
            new TradeLaborStandardCalibrationService(),
            new FakeTradeLaborBenchmarkPlanBuilder(benchmarkPlan));
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

    private static Mock<IMarketCacheService> CreateCache(int fetchedCount, params CachedMarketData?[] data)
    {
        var entries = (data ?? [])
            .Where(entry => entry != null)
            .Cast<CachedMarketData>()
            .ToDictionary(entry => (entry.ItemId, entry.DataCenter));
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
            .ReturnsAsync((IReadOnlyCollection<(int itemId, string dataCenter)> requests, TimeSpan? _) =>
                requests
                    .Where(request => entries.ContainsKey(request))
                    .ToDictionary(request => request, request => entries[request]));
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
                            PricePerUnit = 1_000,
                            IsHq = false,
                            RetainerName = "Bench"
                        }
                    ]
                }
            ]
        };
    }

    private static CachedMarketData CreateIngredientData(DateTime fetchedAtUtc)
    {
        return new CachedMarketData
        {
            ItemId = 42,
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
                            Quantity = 200,
                            PricePerUnit = 500,
                            IsHq = false,
                            RetainerName = "Ingredient"
                        }
                    ]
                }
            ]
        };
    }

    private static CraftingPlan CreateBenchmarkPlan()
    {
        var root = new PlanNode
        {
            ItemId = TradeLaborStandardCalibrationService.CobaltRivetsItemId,
            Name = TradeLaborStandardCalibrationService.CobaltRivetsItemName,
            Quantity = TradeLaborStandardCalibrationService.CobaltRivetsBenchmarkQuantity,
            Source = AcquisitionSource.Craft,
            CanCraft = true,
            Yield = 1
        };
        var ingredient = new PlanNode
        {
            ItemId = 42,
            Name = "Benchmark Ingredient",
            Quantity = 200,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true,
            Parent = root
        };
        root.Children.Add(ingredient);

        return new CraftingPlan
        {
            RootItems = [root]
        };
    }

    private sealed class FakeTradeLaborBenchmarkPlanBuilder : ITradeLaborBenchmarkPlanBuilder
    {
        private readonly CraftingPlan _plan;

        public FakeTradeLaborBenchmarkPlanBuilder(CraftingPlan plan)
        {
            _plan = plan;
        }

        public Task<CraftingPlan> BuildManagedCobaltRivetsPlanAsync(
            string dataCenter,
            CancellationToken ct = default)
        {
            return Task.FromResult(_plan);
        }
    }
}
