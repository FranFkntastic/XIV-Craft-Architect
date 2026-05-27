using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketPurchaseCandidateTests
{
    [Fact]
    public void GeneratePurchaseCandidates_FullSingleWorldStock_ReturnsSingleWorldCandidateWithPreservedListings()
    {
        var listing = Listing(5, 100, "Cactuar Retainer");
        var world = World("Aether", "Cactuar", 500, 100, listing);
        var plan = Plan(quantityNeeded: 5, world);

        var candidates = GenerateCandidates(plan);

        var candidate = Assert.Single(candidates);
        Assert.Equal(123, candidate.ItemId);
        Assert.Equal("Candidate Test Item", candidate.ItemName);
        Assert.Equal(500, candidate.GilCost);
        Assert.True(candidate.IsFullyFulfilled);
        Assert.Equal(5, candidate.QuantityFulfilled);
        Assert.Same(world, candidate.SingleWorld);
        Assert.Null(candidate.Split);

        var worldKey = Assert.Single(candidate.Worlds);
        Assert.Equal(new MarketWorldKey("Aether", "Cactuar"), worldKey);
        Assert.Same(listing, Assert.Single(candidate.SingleWorld!.Listings));
    }

    [Fact]
    public void GeneratePurchaseCandidates_SplitRequiredStock_ReturnsSplitCandidateWithQuantitiesCostsAndDataCenters()
    {
        var aetherWorld = World("Aether", "Siren", 300, 100, Listing(3, 100, "Siren Retainer"));
        var primalWorld = World("Primal", "Leviathan", 800, 200, Listing(4, 200, "Leviathan Retainer"));
        var plan = Plan(quantityNeeded: 5, aetherWorld, primalWorld);

        var candidates = GenerateCandidates(plan);

        var candidate = Assert.Single(candidates);
        Assert.True(candidate.IsFullyFulfilled);
        Assert.Equal(5, candidate.QuantityFulfilled);
        Assert.Equal(1100, candidate.GilCost);
        Assert.Null(candidate.SingleWorld);

        Assert.NotNull(candidate.Split);
        var split = candidate.Split;
        Assert.Collection(
            split,
            siren =>
            {
                Assert.Equal("Aether", siren.DataCenter);
                Assert.Equal("Siren", siren.WorldName);
                Assert.Equal(3, siren.QuantityToBuy);
                Assert.Equal(300, siren.TotalCost);
                Assert.Equal(TravelContextConstants.Primary, siren.TravelContext);
            },
            leviathan =>
            {
                Assert.Equal("Primal", leviathan.DataCenter);
                Assert.Equal("Leviathan", leviathan.WorldName);
                Assert.Equal(2, leviathan.QuantityToBuy);
                Assert.Equal(800, leviathan.TotalCost);
                Assert.Equal(200, leviathan.PricePerUnit);
                Assert.Equal(400, leviathan.EffectivePricePerNeededUnit);
                Assert.Equal(TravelContextConstants.Supplemental, leviathan.TravelContext);
                var listing = Assert.Single(leviathan.Listings);
                Assert.Equal(2, listing.NeededFromStack);
                Assert.Equal(2, listing.ExcessQuantity);
            });

        Assert.Equal(
            [new MarketWorldKey("Aether", "Siren"), new MarketWorldKey("Primal", "Leviathan")],
            candidate.Worlds);
    }

    [Fact]
    public void CalculateSplitPurchase_PricesSelectedStacksByFullListingCost()
    {
        var aetherWorld = World("Aether", "Siren", 300, 100, Listing(3, 100, "Siren Retainer"));
        var primalWorld = World("Primal", "Leviathan", 800, 200, Listing(4, 200, "Leviathan Retainer"));
        var plan = Plan(quantityNeeded: 5, aetherWorld, primalWorld);
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        service.CalculateSplitPurchase(plan, new MarketAnalysisConfig());

        Assert.NotNull(plan.RecommendedSplit);
        Assert.Collection(
            plan.RecommendedSplit!,
            siren =>
            {
                Assert.Equal("Siren", siren.WorldName);
                Assert.Equal(3, siren.QuantityToBuy);
                Assert.Equal(300, siren.TotalCost);
            },
            leviathan =>
            {
                Assert.Equal("Leviathan", leviathan.WorldName);
                Assert.Equal(2, leviathan.QuantityToBuy);
                Assert.Equal(800, leviathan.TotalCost);
                Assert.Equal(200, leviathan.PricePerUnit);
                Assert.Equal(400, leviathan.EffectivePricePerNeededUnit);
                var listing = Assert.Single(leviathan.Listings);
                Assert.Equal(4, listing.Quantity);
                Assert.Equal(2, listing.NeededFromStack);
                Assert.Equal(2, listing.ExcessQuantity);
                Assert.Equal(800, listing.Quantity * listing.PricePerUnit);
            });
        Assert.Equal(1100, plan.SplitTotalCost);
    }

    [Fact]
    public void GeneratePurchaseCandidates_SufficientStockBeyondDefaultSplitBound_ReturnsFulfilledSplitCandidate()
    {
        var siren = World("Aether", "Siren", 200, 100, Listing(2, 100, "Siren Retainer"));
        var balmung = World("Crystal", "Balmung", 220, 110, Listing(2, 110, "Balmung Retainer"));
        var leviathan = World("Primal", "Leviathan", 240, 120, Listing(2, 120, "Leviathan Retainer"));
        var shiva = World("Light", "Shiva", 260, 130, Listing(2, 130, "Shiva Retainer"));
        var plan = Plan(quantityNeeded: 8, siren, balmung, leviathan, shiva);

        var candidates = GenerateCandidates(plan);

        var candidate = Assert.Single(candidates);
        Assert.True(candidate.IsFullyFulfilled);
        Assert.False(candidate.HasInsufficientStock);
        Assert.Equal(8, candidate.QuantityFulfilled);
        Assert.Equal(920, candidate.GilCost);
        Assert.Equal(4, candidate.Split!.Count);
        Assert.Equal(
            [
                new MarketWorldKey("Aether", "Siren"),
                new MarketWorldKey("Crystal", "Balmung"),
                new MarketWorldKey("Primal", "Leviathan"),
                new MarketWorldKey("Light", "Shiva")
            ],
            candidate.Worlds);
    }

    [Fact]
    public void GeneratePurchaseCandidates_MultipleSplitRoutesAvailable_ReturnsBoundedDistinctRouteAlternatives()
    {
        var siren = World("Aether", "Siren", 300, 100, Listing(3, 100, "Siren Retainer"));
        var gilgamesh = World("Aether", "Gilgamesh", 315, 105, Listing(3, 105, "Gilgamesh Retainer"));
        var balmung = World("Crystal", "Balmung", 330, 110, Listing(3, 110, "Balmung Retainer"));
        var leviathan = World("Primal", "Leviathan", 180, 90, Listing(2, 90, "Leviathan Retainer"));
        var plan = Plan(quantityNeeded: 5, siren, gilgamesh, balmung, leviathan);

        var candidates = GenerateCandidates(plan);

        var splitCandidates = candidates.Where(c => c.IsSplitPurchase).ToList();
        Assert.InRange(splitCandidates.Count, 3, 8);
        Assert.All(splitCandidates, candidate =>
        {
            Assert.True(candidate.IsFullyFulfilled);
            Assert.False(candidate.HasInsufficientStock);
            Assert.Equal(5, candidate.QuantityFulfilled);
            Assert.Equal(5, candidate.Split!.Sum(split => split.QuantityToBuy));
        });

        var routeKeys = splitCandidates.Select(RouteKey).ToList();
        Assert.Equal(routeKeys.Distinct().Count(), routeKeys.Count);
        Assert.Contains("AETHER:GILGAMESH|AETHER:SIREN", routeKeys);
        Assert.Contains("AETHER:SIREN|CRYSTAL:BALMUNG", routeKeys);
        Assert.Contains("AETHER:SIREN|PRIMAL:LEVIATHAN", routeKeys);
    }

    [Fact]
    public void GeneratePurchaseCandidates_RouteStateIncludesReuseWorldBeyondDefaultBound()
    {
        var worlds = Enumerable.Range(1, 10)
            .Select(i => World("Aether", $"World{i:00}", i * 100, i * 100, Listing(1, i * 100, $"Retainer {i:00}")))
            .ToArray();
        var plan = Plan(quantityNeeded: 2, worlds);
        var currentRoute = new MarketRouteState([new MarketWorldKey("Aether", "World10")]);

        var candidates = GenerateCandidates(plan, currentRoute);

        var splitCandidates = candidates.Where(c => c.IsSplitPurchase).ToList();
        Assert.InRange(splitCandidates.Count, 1, 8);
        Assert.Contains(splitCandidates, candidate => RouteKey(candidate) == "AETHER:WORLD01|AETHER:WORLD10");
    }

    [Fact]
    public void GeneratePurchaseCandidates_DuplicateRouteKeysRetainsCheapestFulfilledSplit()
    {
        var alpha = World(
            "Aether",
            "Alpha",
            1500,
            50,
            Listing(5, 100, "Alpha Cheap"),
            Listing(1, 1000, "Alpha Expensive"));
        var bravo = World("Aether", "Bravo", 600, 100, Listing(6, 100, "Bravo Retainer"));
        var plan = Plan(quantityNeeded: 10, alpha, bravo);

        var candidates = GenerateCandidates(plan);

        var candidate = Assert.Single(candidates, c => RouteKey(c) == "AETHER:ALPHA|AETHER:BRAVO");
        Assert.True(candidate.IsFullyFulfilled);
        Assert.Equal(1100, candidate.GilCost);
        Assert.Equal(1100, candidate.Split!.Sum(s => s.TotalCost));
    }

    [Fact]
    public void GeneratePurchaseCandidates_InsufficientTotalStock_ReturnsNoCandidate()
    {
        var siren = World("Aether", "Siren", 300, 100, Listing(3, 100, "Siren Retainer"));
        var balmung = World("Crystal", "Balmung", 300, 150, Listing(2, 150, "Balmung Retainer"));
        var plan = Plan(quantityNeeded: 10, siren, balmung);

        var candidates = GenerateCandidates(plan);

        Assert.Empty(candidates);
    }

    [Fact]
    public void CalculateSplitPurchase_InsufficientTotalStock_DoesNotPublishRecommendedSplit()
    {
        var siren = World("Aether", "Siren", 300, 100, Listing(3, 100, "Siren Retainer"));
        var balmung = World("Crystal", "Balmung", 300, 150, Listing(2, 150, "Balmung Retainer"));
        var plan = Plan(quantityNeeded: 10, siren, balmung);
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        service.CalculateSplitPurchase(plan, new MarketAnalysisConfig());

        Assert.Null(plan.RecommendedSplit);
    }

    private static List<MarketPurchaseCandidate> GenerateCandidates(
        DetailedShoppingPlan plan,
        MarketRouteState? currentRoute = null)
    {
        var cache = new Mock<IMarketCacheService>();
        var service = new MarketShoppingService(cache.Object);

        return service.GeneratePurchaseCandidates(plan, currentRoute);
    }

    private static string RouteKey(MarketPurchaseCandidate candidate)
    {
        return string.Join(
            "|",
            candidate.Worlds
                .OrderBy(world => world.DataCenter)
                .ThenBy(world => world.WorldName)
                .Select(world => $"{world.DataCenter}:{world.WorldName}"));
    }

    private static DetailedShoppingPlan Plan(int quantityNeeded, params WorldShoppingSummary[] worlds)
    {
        foreach (var world in worlds)
        {
            world.HasSufficientStock = world.TotalQuantityPurchased >= quantityNeeded;
        }

        return new DetailedShoppingPlan
        {
            ItemId = 123,
            Name = "Candidate Test Item",
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
}
