using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketShoppingServiceDataCenterTests
{
    [Fact]
    public async Task CalculateShoppingPlansWithSplitsAsync_SetsDataCenterOnWorldSummariesAndSplitPurchases()
    {
        var cache = new Mock<IMarketCacheService>();
        cache
            .Setup(c => c.GetAsync(123, "Aether", null))
            .ReturnsAsync(new CachedMarketData
            {
                ItemId = 123,
                DataCenter = "Aether",
                DCAveragePrice = 20,
                Worlds =
                {
                    new CachedWorldData
                    {
                        WorldName = "Siren",
                        Listings =
                        {
                            new CachedListing { Quantity = 6, PricePerUnit = 10, RetainerName = "Siren Retainer" }
                        }
                    },
                    new CachedWorldData
                    {
                        WorldName = "Gilgamesh",
                        Listings =
                        {
                            new CachedListing { Quantity = 4, PricePerUnit = 20, RetainerName = "Gilgamesh Retainer" }
                        }
                    }
                }
            });

        var service = new MarketShoppingService(cache.Object);
        var config = new MarketAnalysisConfig { EnableSplitWorld = true };
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Route Item", TotalQuantity = 10 }
        };

        var plans = await service.CalculateShoppingPlansWithSplitsAsync(materials, "Aether", config: config);

        var plan = Assert.Single(plans);
        Assert.All(plan.WorldOptions, world => Assert.Equal("Aether", world.DataCenter));
        Assert.NotNull(plan.RecommendedSplit);
        Assert.All(plan.RecommendedSplit!, split => Assert.Equal("Aether", split.DataCenter));
    }

    [Fact]
    public async Task CalculateDetailedShoppingPlansAsync_SelectedListingDetailsTrackRemainingNeededQuantity()
    {
        var cache = new Mock<IMarketCacheService>();
        cache
            .Setup(c => c.GetAsync(123, "Aether", null))
            .ReturnsAsync(new CachedMarketData
            {
                ItemId = 123,
                DataCenter = "Aether",
                DCAveragePrice = 110,
                Worlds =
                {
                    new CachedWorldData
                    {
                        WorldName = "Siren",
                        Listings =
                        {
                            new CachedListing { Quantity = 5, PricePerUnit = 100, RetainerName = "Retainer A" },
                            new CachedListing { Quantity = 5, PricePerUnit = 110, RetainerName = "Retainer B" }
                        }
                    }
                }
            });

        var service = new MarketShoppingService(cache.Object);
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Stack Detail Item", TotalQuantity = 7 }
        };

        var plans = await service.CalculateDetailedShoppingPlansAsync(materials, "Aether");

        var plan = Assert.Single(plans);
        var world = Assert.Single(plan.WorldOptions);
        Assert.Equal(1050, world.TotalCost);
        Assert.Collection(
            world.Listings.Where(listing => !listing.IsAdditionalOption),
            first =>
            {
                Assert.Equal(5, first.Quantity);
                Assert.Equal(5, first.NeededFromStack);
                Assert.Equal(0, first.ExcessQuantity);
            },
            second =>
            {
                Assert.Equal(5, second.Quantity);
                Assert.Equal(2, second.NeededFromStack);
                Assert.Equal(3, second.ExcessQuantity);
            });
    }

    [Fact]
    public async Task CalculateDetailedShoppingPlansMultiDCAsync_StructuredBlacklistExcludesOnlyMatchingDataCenterWorld()
    {
        var cache = new Mock<IMarketCacheService>();
        SetupCachedWorld(cache, "Aether", "Siren", 10);
        SetupCachedWorld(cache, "Primal", "Siren", 20);
        cache
            .Setup(c => c.GetAsync(123, "Crystal", null))
            .ReturnsAsync((CachedMarketData?)null);
        cache
            .Setup(c => c.GetAsync(123, "Dynamis", null))
            .ReturnsAsync((CachedMarketData?)null);

        var service = new MarketShoppingService(cache.Object);
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 123, Name = "Structured Blacklist Item", TotalQuantity = 1 }
        };
        var blacklistedWorlds = new HashSet<MarketWorldKey>
        {
            new("Aether", "Siren")
        };

        var plans = await service.CalculateDetailedShoppingPlansMultiDCAsync(
            materials,
            blacklistedMarketWorlds: blacklistedWorlds);

        var plan = Assert.Single(plans);
        var siren = Assert.Single(plan.WorldOptions, option => option.WorldName == "Siren");
        Assert.Equal("Primal", siren.DataCenter);
    }

    private static void SetupCachedWorld(Mock<IMarketCacheService> cache, string dataCenter, string worldName, long pricePerUnit)
    {
        cache
            .Setup(c => c.GetAsync(123, dataCenter, null))
            .ReturnsAsync(new CachedMarketData
            {
                ItemId = 123,
                DataCenter = dataCenter,
                DCAveragePrice = pricePerUnit,
                Worlds =
                {
                    new CachedWorldData
                    {
                        WorldName = worldName,
                        Listings =
                        {
                            new CachedListing
                            {
                                Quantity = 1,
                                PricePerUnit = pricePerUnit,
                                RetainerName = $"{dataCenter} Retainer"
                            }
                        }
                    }
                }
            });
    }
}
