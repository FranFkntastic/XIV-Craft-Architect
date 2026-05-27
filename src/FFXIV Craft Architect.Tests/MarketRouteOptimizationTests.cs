using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketRouteOptimizationTests
{
    [Fact]
    public void OptimizeProcurementRoute_TravelToleranceZero_ConsolidatesRouteBeforeGil()
    {
        var plans = new[]
        {
            Plan(1, "High Impact", 10,
                World("Aether", "Siren", 10_000, 10, Listing(10, 1_000, "Siren High")),
                World("Crystal", "Balmung", 100, 10, Listing(10, 10, "Balmung High"))),
            Plan(2, "Route Anchor", 10,
                World("Aether", "Siren", 10_000, 10, Listing(10, 1_000, "Siren Anchor")),
                World("Primal", "Leviathan", 1, 1, Listing(10, 1, "Leviathan Anchor")))
        };

        var optimized = Optimize(plans, travelTolerance: 0, includeSplitPurchases: false);

        Assert.All(optimized, plan => Assert.Equal("Siren", plan.RecommendedWorld?.WorldName));
        Assert.All(optimized, plan => Assert.Equal("Aether", plan.RecommendedWorld?.DataCenter));
    }

    [Fact]
    public void OptimizeProcurementRoute_TravelToleranceEleven_ChoosesRawCheapestCandidates()
    {
        var plans = new[]
        {
            Plan(1, "First Cheapest", 10,
                World("Aether", "Siren", 10_000, 10, Listing(10, 1_000, "Siren First")),
                World("Crystal", "Balmung", 100, 10, Listing(10, 10, "Balmung First"))),
            Plan(2, "Second Cheapest", 10,
                World("Aether", "Siren", 10_000, 10, Listing(10, 1_000, "Siren Second")),
                World("Primal", "Leviathan", 1, 1, Listing(10, 1, "Leviathan Second")))
        };

        var optimized = Optimize(plans, travelTolerance: 11, includeSplitPurchases: false);

        Assert.Equal("Balmung", optimized[0].RecommendedWorld?.WorldName);
        Assert.Equal("Crystal", optimized[0].RecommendedWorld?.DataCenter);
        Assert.Equal("Leviathan", optimized[1].RecommendedWorld?.WorldName);
        Assert.Equal("Primal", optimized[1].RecommendedWorld?.DataCenter);
    }

    [Fact]
    public void OptimizeProcurementRoute_SplitEnabled_CanChooseRegionWideSplitAcrossDataCenters()
    {
        var plan = Plan(1, "Split Item", 10,
            World("Aether", "Siren", 500, 50, Listing(5, 50, "Siren Split")),
            World("Primal", "Leviathan", 600, 60, Listing(5, 60, "Leviathan Split")),
            World("Crystal", "Balmung", 10_000, 1_000, Listing(10, 1_000, "Balmung Single")));

        var optimized = Optimize([plan], travelTolerance: 11, includeSplitPurchases: true);

        Assert.Null(optimized[0].RecommendedWorld);
        Assert.NotNull(optimized[0].RecommendedSplit);
        Assert.Collection(
            optimized[0].RecommendedSplit!,
            siren =>
            {
                Assert.Equal("Aether", siren.DataCenter);
                Assert.Equal("Siren", siren.WorldName);
                Assert.Equal(5, siren.QuantityToBuy);
            },
            leviathan =>
            {
                Assert.Equal("Primal", leviathan.DataCenter);
                Assert.Equal("Leviathan", leviathan.WorldName);
                Assert.Equal(5, leviathan.QuantityToBuy);
            });
    }

    [Fact]
    public async Task OptimizeProcurementRoute_SplitEnabled_CanUseMultiDcCachedEvidence()
    {
        var cache = new Mock<IMarketCacheService>();
        SetupCachedWorld(cache, "Aether", "Siren", 5, 50);
        SetupCachedWorld(cache, "Primal", "Leviathan", 5, 60);
        SetupCachedWorld(cache, "Crystal", "Balmung", 10, 1_000);
        cache
            .Setup(c => c.GetAsync(1, "Dynamis", null))
            .ReturnsAsync((CachedMarketData?)null);

        var service = new MarketShoppingService(cache.Object);
        var materials = new List<MaterialAggregate>
        {
            new() { ItemId = 1, Name = "Cached Split Item", TotalQuantity = 10 }
        };
        var config = new MarketAnalysisConfig
        {
            EnableSplitWorld = true,
            TravelTolerance = 11
        };

        var evidencePlans = await service.CalculateDetailedShoppingPlansMultiDCAsync(
            materials,
            config: config);
        var optimized = service.OptimizeProcurementRoute(
            evidencePlans,
            config,
            includeSplitPurchases: true);

        var plan = Assert.Single(optimized);
        Assert.Null(plan.RecommendedWorld);
        Assert.NotNull(plan.RecommendedSplit);
        Assert.Collection(
            plan.RecommendedSplit!,
            siren =>
            {
                Assert.Equal("Aether", siren.DataCenter);
                Assert.Equal("Siren", siren.WorldName);
                Assert.Equal(5, siren.QuantityToBuy);
            },
            leviathan =>
            {
                Assert.Equal("Primal", leviathan.DataCenter);
                Assert.Equal("Leviathan", leviathan.WorldName);
                Assert.Equal(5, leviathan.QuantityToBuy);
            });
    }

    [Fact]
    public void OptimizeProcurementRoute_SplitDisabled_DoesNotChooseSplitCandidate()
    {
        var plan = Plan(1, "Split Disabled Item", 10,
            World("Aether", "Siren", 500, 50, Listing(5, 50, "Siren Split")),
            World("Primal", "Leviathan", 600, 60, Listing(5, 60, "Leviathan Split")),
            World("Crystal", "Balmung", 10_000, 1_000, Listing(10, 1_000, "Balmung Single")));

        var optimized = Optimize([plan], travelTolerance: 11, includeSplitPurchases: false);

        Assert.Equal("Crystal", optimized[0].RecommendedWorld?.DataCenter);
        Assert.Equal("Balmung", optimized[0].RecommendedWorld?.WorldName);
        Assert.Null(optimized[0].RecommendedSplit);
    }

    [Fact]
    public void OptimizeProcurementRoute_LowImpactItemDoesNotForceBadExtraRouteStop()
    {
        var plans = new[]
        {
            Plan(1, "Important Item", 10,
                World("Aether", "Siren", 1_000, 100, Listing(10, 100, "Siren Important"))),
            Plan(2, "Low Impact Item", 1,
                World("Aether", "Siren", 100, 100, Listing(1, 100, "Siren Small")),
                World("Crystal", "Balmung", 1, 1, Listing(1, 1, "Balmung Small")))
        };

        var optimized = Optimize(plans, travelTolerance: 1, includeSplitPurchases: false);

        Assert.Equal("Siren", optimized[0].RecommendedWorld?.WorldName);
        Assert.Equal("Siren", optimized[1].RecommendedWorld?.WorldName);
    }

    [Fact]
    public void OptimizeProcurementRoute_SameWorldNamesOnDifferentDataCentersRemainDistinct()
    {
        var plans = new[]
        {
            Plan(1, "First Coeurl", 1,
                World("Crystal", "Coeurl", 1, 1, Listing(1, 1, "Crystal Coeurl")),
                World("Shadow", "Coeurl", 1_000, 1_000, Listing(1, 1_000, "Shadow Coeurl"))),
            Plan(2, "Second Coeurl", 1,
                World("Shadow", "Coeurl", 1, 1, Listing(1, 1, "Shadow Coeurl")),
                World("Crystal", "Coeurl", 1_000, 1_000, Listing(1, 1_000, "Crystal Coeurl")))
        };

        var optimized = Optimize(plans, travelTolerance: 11, includeSplitPurchases: false);

        Assert.Equal("Crystal", optimized[0].RecommendedWorld?.DataCenter);
        Assert.Equal("Coeurl", optimized[0].RecommendedWorld?.WorldName);
        Assert.Equal("Shadow", optimized[1].RecommendedWorld?.DataCenter);
        Assert.Equal("Coeurl", optimized[1].RecommendedWorld?.WorldName);
    }

    [Fact]
    public void OptimizeProcurementRoute_PlansWithNoFeasibleCandidatesRemainPresent()
    {
        var plan = new DetailedShoppingPlan
        {
            ItemId = 1,
            Name = "Missing Item",
            QuantityNeeded = 10,
            Error = "No market data in cache"
        };

        var optimized = Optimize([plan], travelTolerance: 0, includeSplitPurchases: true);

        var result = Assert.Single(optimized);
        Assert.Same(plan, result);
        Assert.Equal("No market data in cache", result.Error);
        Assert.Null(result.RecommendedWorld);
        Assert.Null(result.RecommendedSplit);
    }

    private static List<DetailedShoppingPlan> Optimize(
        IEnumerable<DetailedShoppingPlan> plans,
        int travelTolerance,
        bool includeSplitPurchases)
    {
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);
        var config = new MarketAnalysisConfig { TravelTolerance = travelTolerance };

        return service.OptimizeProcurementRoute(plans, config, includeSplitPurchases);
    }

    private static DetailedShoppingPlan Plan(
        int itemId,
        string name,
        int quantityNeeded,
        params WorldShoppingSummary[] worlds)
    {
        foreach (var world in worlds)
        {
            world.HasSufficientStock = world.TotalQuantityPurchased >= quantityNeeded;
        }

        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = quantityNeeded,
            WorldOptions = worlds.ToList()
        };
    }

    private static WorldShoppingSummary World(
        string dataCenter,
        string worldName,
        long totalCost,
        long modePricePerUnit,
        params ShoppingListingEntry[] listings)
    {
        var totalQuantity = listings.Where(l => !l.IsAdditionalOption).Sum(l => l.Quantity);

        return new WorldShoppingSummary
        {
            DataCenter = dataCenter,
            WorldName = worldName,
            TotalCost = totalCost,
            AveragePricePerUnit = totalQuantity > 0 ? totalCost / (decimal)totalQuantity : 0,
            TotalQuantityPurchased = totalQuantity,
            ModePricePerUnit = modePricePerUnit,
            Listings = listings.ToList()
        };
    }

    private static ShoppingListingEntry Listing(int quantity, long pricePerUnit, string retainerName)
    {
        return new ShoppingListingEntry
        {
            Quantity = quantity,
            PricePerUnit = pricePerUnit,
            RetainerName = retainerName,
            NeededFromStack = quantity,
            ExcessQuantity = 0
        };
    }

    private static void SetupCachedWorld(
        Mock<IMarketCacheService> cache,
        string dataCenter,
        string worldName,
        int quantity,
        long pricePerUnit)
    {
        cache
            .Setup(c => c.GetAsync(1, dataCenter, null))
            .ReturnsAsync(new CachedMarketData
            {
                ItemId = 1,
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
                                Quantity = quantity,
                                PricePerUnit = pricePerUnit,
                                RetainerName = $"{worldName} Retainer"
                            }
                        }
                    }
                }
            });
    }
}
