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
}
