using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketShoppingServiceRouteIdentityTests
{
    [Fact]
    public async Task CalculateDetailedShoppingPlansMultiDCAsync_SameWorldNameAcrossDataCenters_RemainsDistinctWithoutDisplaySuffix()
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
            new() { ItemId = 123, Name = "Route Identity Item", TotalQuantity = 1 }
        };

        var plans = await service.CalculateDetailedShoppingPlansMultiDCAsync(materials);

        var plan = Assert.Single(plans);
        var sirenOptions = plan.WorldOptions
            .Where(option => option.WorldName == "Siren")
            .OrderBy(option => option.DataCenter)
            .ToList();

        Assert.Equal(2, sirenOptions.Count);
        Assert.Collection(
            sirenOptions,
            option =>
            {
                Assert.Equal("Aether", option.DataCenter);
                Assert.DoesNotContain("(", option.WorldName);
            },
            option =>
            {
                Assert.Equal("Primal", option.DataCenter);
                Assert.DoesNotContain("(", option.WorldName);
            });
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
