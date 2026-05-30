using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class ProcurementRouteExecutionServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_WhenActiveEvidenceIsComplete_DoesNotFetchMissingItems()
    {
        var plan = CreatePlan();
        var sourcePlans = new[]
        {
            ShoppingPlan(
                101,
                "Existing Item",
                World("Aether", "Siren", 500, 5)),
            ShoppingPlan(
                202,
                "Missing Item",
                World("Aether", "Faerie", 700, 5))
        };
        var marketExecution = new Mock<IMarketAnalysisExecutionService>(MockBehavior.Strict);
        var service = new ProcurementRouteExecutionService(
            marketExecution.Object,
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = sourcePlans,
                Scope = MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                ProcurementConfig = new MarketAnalysisConfig { TravelTolerance = 0 }
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        Assert.Empty(result.MissingItems);
        Assert.Empty(result.RefreshedEvidence);
        Assert.Equal([101, 202], result.EvidencePlans.Select(plan => plan.ItemId));
    }

    [Fact]
    public async Task AnalyzeAsync_FetchesOnlyMissingActiveEvidenceAndFiltersSelectedDataCenter()
    {
        var plan = CreatePlan();
        var reusablePlan = ShoppingPlan(
            101,
            "Existing Item",
            World("Aether", "Siren", 500, 5),
            World("Primal", "Leviathan", 100, 5));
        var fetchedPlan = ShoppingPlan(
            202,
            "Missing Item",
            World("Aether", "Siren", 700, 5),
            World("Primal", "Leviathan", 200, 5));
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        marketExecution.Setup(s => s.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [],
                [fetchedPlan]));
        var service = new ProcurementRouteExecutionService(
            marketExecution.Object,
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = [reusablePlan],
                Scope = MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                ProcurementConfig = new MarketAnalysisConfig { TravelTolerance = 0 }
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        marketExecution.Verify(s => s.ExecuteAsync(
            It.Is<MarketAnalysisExecutionRequest>(request =>
                request.Items.Count == 1 &&
                request.Items[0].ItemId == 202 &&
                request.Scope == MarketFetchScope.SelectedDataCenter &&
                request.SelectedDataCenter == "Aether"),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
        Assert.Equal([101, 202], result.EvidencePlans.Select(plan => plan.ItemId));
        Assert.Equal([101, 202], result.ShoppingPlans.Select(plan => plan.ItemId));
        Assert.All(result.ShoppingPlans.SelectMany(plan => plan.WorldOptions), world =>
        {
            Assert.Equal("Aether", world.DataCenter);
        });
    }

    [Fact]
    public async Task AnalyzeAsync_EntireRegionPreservesMultiDataCenterEvidence()
    {
        var plan = CreatePlan();
        var sourcePlans = new[]
        {
            ShoppingPlan(
                101,
                "Existing Item",
                World("Aether", "Siren", 500, 5),
                World("Primal", "Leviathan", 100, 5)),
            ShoppingPlan(
                202,
                "Missing Item",
                World("Aether", "Siren", 700, 5),
                World("Primal", "Leviathan", 200, 5))
        };
        var service = new ProcurementRouteExecutionService(
            Mock.Of<IMarketAnalysisExecutionService>(),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = sourcePlans,
                Scope = MarketFetchScope.EntireRegion,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                ProcurementConfig = new MarketAnalysisConfig { TravelTolerance = 11 }
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        Assert.All(result.ShoppingPlans, shoppingPlan =>
        {
            Assert.Contains(shoppingPlan.WorldOptions, world => world.DataCenter == "Aether");
            Assert.Contains(shoppingPlan.WorldOptions, world => world.DataCenter == "Primal");
        });
    }

    [Fact]
    public async Task AnalyzeAsync_EntireRegionFetchesWhenExpectedDataCenterEvidenceIsMissing()
    {
        var plan = CreatePlan();
        var partialRegionPlan = ShoppingPlan(
            101,
            "Existing Item",
            World("Aether", "Siren", 500, 5),
            World("Primal", "Leviathan", 100, 5));
        var completeRegionPlan = ShoppingPlan(
            101,
            "Existing Item",
            World("Aether", "Siren", 500, 5),
            World("Primal", "Leviathan", 100, 5),
            World("Crystal", "Balmung", 120, 5));
        var secondItemPlan = ShoppingPlan(
            202,
            "Missing Item",
            World("Aether", "Siren", 700, 5),
            World("Primal", "Leviathan", 200, 5),
            World("Crystal", "Balmung", 250, 5));
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        marketExecution.Setup(s => s.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [],
                [completeRegionPlan, secondItemPlan]));
        var service = new ProcurementRouteExecutionService(
            marketExecution.Object,
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = [partialRegionPlan],
                Scope = MarketFetchScope.EntireRegion,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                ProcurementConfig = new MarketAnalysisConfig { TravelTolerance = 11 },
                ExpectedWorldsByDataCenter = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Aether"] = ["Siren"],
                    ["Primal"] = ["Leviathan"],
                    ["Crystal"] = ["Balmung"]
                }
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        marketExecution.Verify(s => s.ExecuteAsync(
            It.Is<MarketAnalysisExecutionRequest>(request =>
                request.Items.Select(item => item.ItemId).Order().SequenceEqual(new[] { 101, 202 })),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
        Assert.Equal(["Aether", "Primal", "Crystal"], result.EvidencePlans[0].WorldOptions.Select(world => world.DataCenter));
    }

    [Fact]
    public async Task AnalyzeAsync_AppliesTemporaryWorldExclusionsBeforeRouteOptimization()
    {
        var plan = CreatePlan();
        var sourcePlans = new[]
        {
            ShoppingPlan(
                101,
                "Existing Item",
                World("Aether", "Siren", 100, 5),
                World("Aether", "Faerie", 500, 5)),
            ShoppingPlan(
                202,
                "Missing Item",
                World("Aether", "Siren", 100, 5),
                World("Aether", "Faerie", 500, 5))
        };
        var service = new ProcurementRouteExecutionService(
            Mock.Of<IMarketAnalysisExecutionService>(),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = sourcePlans,
                Scope = MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                BlacklistedWorlds = new HashSet<MarketWorldKey>
                {
                    new("Aether", "Siren")
                },
                ProcurementConfig = new MarketAnalysisConfig { TravelTolerance = 0 }
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        Assert.All(result.ShoppingPlans, shoppingPlan =>
        {
            Assert.DoesNotContain(shoppingPlan.WorldOptions, world => world.WorldName == "Siren");
            Assert.Equal("Faerie", shoppingPlan.RecommendedWorld?.WorldName);
        });
    }

    private static CraftingPlan CreatePlan()
    {
        return new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 101,
                    Name = "Existing Item",
                    Quantity = 5,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanBuyFromMarket = true
                },
                new PlanNode
                {
                    ItemId = 202,
                    Name = "Missing Item",
                    Quantity = 5,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanBuyFromMarket = true
                }
            ]
        };
    }

    private static DetailedShoppingPlan ShoppingPlan(
        int itemId,
        string name,
        params WorldShoppingSummary[] worlds)
    {
        return new DetailedShoppingPlan
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 5,
            WorldOptions = worlds.ToList(),
            RecommendedWorld = worlds.FirstOrDefault(world => world.HasSufficientStock)
        };
    }

    private static WorldShoppingSummary World(
        string dataCenter,
        string worldName,
        long totalCost,
        int quantity)
    {
        return new WorldShoppingSummary
        {
            DataCenter = dataCenter,
            WorldName = worldName,
            TotalCost = totalCost,
            AveragePricePerUnit = totalCost / (decimal)quantity,
            TotalQuantityPurchased = quantity,
            HasSufficientStock = true,
            Listings =
            [
                new ShoppingListingEntry
                {
                    Quantity = quantity,
                    NeededFromStack = quantity,
                    PricePerUnit = totalCost / quantity,
                    RetainerName = $"{worldName} Retainer"
                }
            ]
        };
    }

    private static MarketEvidenceSet CreateEmptyEvidence()
    {
        return new MarketEvidenceSet(
            new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
            [],
            MarketFetchScope.SelectedDataCenter,
            ["Aether"],
            "Aether",
            "North America",
            maxAge: null,
            fetchedCount: 0,
            loadedAtUtc: DateTime.UtcNow);
    }
}
