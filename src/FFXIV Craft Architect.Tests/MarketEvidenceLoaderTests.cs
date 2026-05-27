using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketEvidenceLoaderTests
{
    [Fact]
    public async Task LoadAsync_SelectedDataCenter_EnsuresAndLoadsRequestedPairs()
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
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(101, "Aether")] = CreateCachedData(101, "Aether"),
                [(202, "Aether")] = CreateCachedData(202, "Aether")
            });

        var evidence = await MarketEvidenceLoader.LoadAsync(
            cache.Object,
            [101, 202, 101],
            MarketFetchScope.SelectedDataCenter,
            selectedDataCenter: "Aether",
            selectedRegion: "North America");

        Assert.Equal([(101, "Aether"), (202, "Aether")], ensuredRequests);
        Assert.Equal(MarketFetchScope.SelectedDataCenter, evidence.Scope);
        Assert.Equal(["Aether"], evidence.DataCenters);
        Assert.False(evidence.IsPartial);
        Assert.Equal(2, evidence.Entries.Count);
        Assert.Same(evidence.Entries[(101, "Aether")], evidence.GetEntriesForItem(101).Single());
    }

    [Fact]
    public async Task LoadAsync_EntireRegion_TracksMissingPairsAsPartialEvidence()
    {
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(303, "Aether")] = CreateCachedData(303, "Aether"),
                [(303, "Primal")] = CreateCachedData(303, "Primal")
            });

        var evidence = await MarketEvidenceLoader.LoadAsync(
            cache.Object,
            [303],
            MarketFetchScope.EntireRegion,
            selectedDataCenter: "Aether",
            selectedRegion: "North America");

        Assert.True(evidence.IsPartial);
        Assert.Equal(
            [(303, "Crystal"), (303, "Dynamis")],
            evidence.MissingRequests);
    }

    [Fact]
    public async Task LoadAsync_ForceRefresh_UsesNormalFreshnessForPostFetchRead()
    {
        var cache = new Mock<IMarketCacheService>();
        TimeSpan? ensureMaxAge = TimeSpan.FromMinutes(5);
        TimeSpan? readMaxAge = TimeSpan.FromMinutes(5);
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<List<(int itemId, string dataCenter)>, TimeSpan?, IProgress<string>?, CancellationToken>(
                (_, maxAge, _, _) => ensureMaxAge = maxAge)
            .ReturnsAsync(1);
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .Callback<IReadOnlyCollection<(int itemId, string dataCenter)>, TimeSpan?>(
                (_, maxAge) => readMaxAge = maxAge)
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(404, "Aether")] = CreateCachedData(404, "Aether")
            });

        var evidence = await MarketEvidenceLoader.LoadAsync(
            cache.Object,
            [404],
            MarketFetchScope.SelectedDataCenter,
            selectedDataCenter: "Aether",
            selectedRegion: "North America",
            maxAge: TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, ensureMaxAge);
        Assert.Null(readMaxAge);
        Assert.False(evidence.IsPartial);
    }

    [Fact]
    public async Task LoadAsync_ForceRefresh_DoesNotReturnOldCachedDataWhenRefreshMisses()
    {
        var cache = new Mock<IMarketCacheService>();
        cache.Setup(c => c.EnsurePopulatedAsync(
                It.IsAny<List<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        cache.Setup(c => c.GetManyAsync(
                It.IsAny<IReadOnlyCollection<(int itemId, string dataCenter)>>(),
                It.IsAny<TimeSpan?>()))
            .ReturnsAsync(new Dictionary<(int itemId, string dataCenter), CachedMarketData>
            {
                [(505, "Aether")] = CreateCachedData(505, "Aether", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds())
            });

        var evidence = await MarketEvidenceLoader.LoadAsync(
            cache.Object,
            [505],
            MarketFetchScope.SelectedDataCenter,
            selectedDataCenter: "Aether",
            selectedRegion: "North America",
            maxAge: TimeSpan.Zero);

        Assert.Empty(evidence.Entries);
        Assert.True(evidence.IsPartial);
        Assert.Equal([(505, "Aether")], evidence.MissingRequests);
    }

    private static CachedMarketData CreateCachedData(int itemId, string dataCenter, long? fetchedAtUnix = null)
    {
        return new CachedMarketData
        {
            ItemId = itemId,
            DataCenter = dataCenter,
            DCAveragePrice = 100,
            FetchedAtUnix = fetchedAtUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Worlds =
            [
                new CachedWorldData
                {
                    WorldName = $"{dataCenter} World",
                    Listings =
                    [
                        new CachedListing
                        {
                            Quantity = 10,
                            PricePerUnit = 100,
                            RetainerName = "Retainer"
                        }
                    ]
                }
            ]
        };
    }
}
