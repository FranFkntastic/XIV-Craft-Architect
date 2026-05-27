using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketFetchScopeTests
{
    [Fact]
    public void GetDataCenters_SelectedDataCenterScope_ReturnsOnlySelectedDataCenter()
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            MarketFetchScope.SelectedDataCenter,
            selectedDataCenter: "Aether",
            selectedRegion: "North America");

        Assert.Equal(["Aether"], dataCenters);
    }

    [Fact]
    public void GetDataCenters_EntireRegionScope_ReturnsRegionDataCenters()
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            MarketFetchScope.EntireRegion,
            selectedDataCenter: "Aether",
            selectedRegion: "Europe");

        Assert.Equal(["Chaos", "Light"], dataCenters);
    }

    [Fact]
    public void GetDataCenters_UnknownRegion_FallsBackToSelectedDataCenter()
    {
        var dataCenters = MarketFetchScopeResolver.GetDataCenters(
            MarketFetchScope.EntireRegion,
            selectedDataCenter: "Aether",
            selectedRegion: "Unknown");

        Assert.Equal(["Aether"], dataCenters);
    }

    [Fact]
    public async Task LoadResponsesAsync_EntireRegion_EnsuresAllRegionRequestsAndReturnsCheapestCachedResponse()
    {
        var cache = new Mock<IMarketCacheService>();
        List<(int itemId, string dataCenter)>? ensuredRequests = null;
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<List<(int itemId, string dataCenter)>, TimeSpan?, IProgress<string>?, CancellationToken>(
                (requests, _, _, _) => ensuredRequests = requests)
            .ReturnsAsync(0);
        cache.Setup(c => c.GetAsync(123, "Aether", It.IsAny<TimeSpan?>()))
            .ReturnsAsync(CreateCachedData(123, "Aether", 200));
        cache.Setup(c => c.GetAsync(123, "Primal", It.IsAny<TimeSpan?>()))
            .ReturnsAsync(CreateCachedData(123, "Primal", 100));
        cache.Setup(c => c.GetAsync(123, "Crystal", It.IsAny<TimeSpan?>()))
            .ReturnsAsync(CreateCachedData(123, "Crystal", 150));
        cache.Setup(c => c.GetAsync(123, "Dynamis", It.IsAny<TimeSpan?>()))
            .ReturnsAsync((CachedMarketData?)null);

        var responses = await MarketScopedPriceLoader.LoadResponsesAsync(
            cache.Object,
            [123],
            MarketFetchScope.EntireRegion,
            selectedDataCenter: "Aether",
            selectedRegion: "North America");

        Assert.Equal(
            [(123, "Aether"), (123, "Primal"), (123, "Crystal"), (123, "Dynamis")],
            ensuredRequests);
        var response = Assert.Single(responses);
        Assert.Equal(123, response.Key);
        Assert.Equal("Primal", response.Value.DataCenterName);
        Assert.Equal(100, response.Value.AveragePrice);
    }

    [Fact]
    public async Task LoadResponsesAsync_CachedAverageMissing_UsesCheapestListingPrice()
    {
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        cache.Setup(c => c.GetAsync(456, "Aether", It.IsAny<TimeSpan?>()))
            .ReturnsAsync(CreateCachedData(456, "Aether", averagePrice: 0, listingPrice: 375));

        var responses = await MarketScopedPriceLoader.LoadResponsesAsync(
            cache.Object,
            [456],
            MarketFetchScope.SelectedDataCenter,
            selectedDataCenter: "Aether",
            selectedRegion: "North America");

        var response = Assert.Single(responses);
        Assert.Equal(456, response.Key);
        Assert.Equal(375, response.Value.AveragePrice);
    }

    private static CachedMarketData CreateCachedData(
        int itemId,
        string dataCenter,
        decimal averagePrice,
        long? listingPrice = null)
    {
        return new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            DCAveragePrice = averagePrice,
            FetchedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Worlds =
            [
                new CachedWorldData
                {
                    WorldName = $"{dataCenter} World",
                    Listings =
                    [
                        new CachedListing
                        {
                            PricePerUnit = listingPrice ?? (long)averagePrice,
                            Quantity = 99,
                            IsHq = false,
                            RetainerName = "Retainer"
                        }
                    ]
                }
            ]
        };
    }
}
