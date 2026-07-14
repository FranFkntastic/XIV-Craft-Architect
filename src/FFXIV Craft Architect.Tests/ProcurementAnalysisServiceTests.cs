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
            new MarketEvidenceReconciliationService(marketExecution.Object),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = sourcePlans,
                SourceMarketAnalyses =
                [
                    MarketAnalysis(101, "Existing Item", MarketFetchScope.SelectedDataCenter, "Aether"),
                    MarketAnalysis(202, "Missing Item", MarketFetchScope.SelectedDataCenter, "Aether")
                ],
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
    public async Task AnalyzeAsync_UsesRequestActiveProcurementItemsForEvidenceSelection()
    {
        var plan = new CraftingPlan
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 101,
                    Name = "Plan Item",
                    Quantity = 5,
                    Source = AcquisitionSource.MarketBuyNq,
                    CanBuyFromMarket = true
                }
            ]
        };
        var sourcePlans = new[]
        {
            ShoppingPlan(
                101,
                "Plan Item",
                World("Aether", "Siren", 500, 5)),
            ShoppingPlan(
                202,
                "Projected Active Item",
                World("Aether", "Faerie", 700, 5))
        };
        var marketExecution = new Mock<IMarketAnalysisExecutionService>(MockBehavior.Strict);
        var service = new ProcurementRouteExecutionService(
            new MarketEvidenceReconciliationService(marketExecution.Object),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                ActiveProcurementItems =
                [
                    new MaterialAggregate
                    {
                        ItemId = 202,
                        Name = "Projected Active Item",
                        TotalQuantity = 5
                    }
                ],
                SourceShoppingPlans = sourcePlans,
                SourceMarketAnalyses =
                [
                    MarketAnalysis(202, "Projected Active Item", MarketFetchScope.SelectedDataCenter, "Aether")
                ],
                Scope = MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                ProcurementConfig = new MarketAnalysisConfig { TravelTolerance = 0 }
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        Assert.Empty(result.MissingItems);
        Assert.Equal([202], result.EvidencePlans.Select(plan => plan.ItemId));
        Assert.Equal([202], result.ShoppingPlans.Select(plan => plan.ItemId));
    }

    [Fact]
    public async Task AnalyzeAsync_EquivalentExplicitActiveItems_MatchesLegacyPlanFallback()
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
        var activeProcurementItems = new RecipeDemandProjectionService()
            .Build(plan, snapshot: null)
            .ToActiveProcurementMaterialAggregates();

        var fallbackResult = await CreateService().AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = sourcePlans,
                SourceMarketAnalyses =
                [
                    MarketAnalysis(101, "Existing Item", MarketFetchScope.SelectedDataCenter, "Aether"),
                    MarketAnalysis(202, "Missing Item", MarketFetchScope.SelectedDataCenter, "Aether")
                ],
                Scope = MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                ProcurementConfig = new MarketAnalysisConfig { TravelTolerance = 0 }
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);
        var explicitResult = await CreateService().AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                ActiveProcurementItems = activeProcurementItems,
                SourceShoppingPlans = sourcePlans,
                SourceMarketAnalyses =
                [
                    MarketAnalysis(101, "Existing Item", MarketFetchScope.SelectedDataCenter, "Aether"),
                    MarketAnalysis(202, "Missing Item", MarketFetchScope.SelectedDataCenter, "Aether")
                ],
                Scope = MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                ProcurementConfig = new MarketAnalysisConfig { TravelTolerance = 0 }
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(
            fallbackResult.EvidencePlans.Select(ToRouteSummary),
            explicitResult.EvidencePlans.Select(ToRouteSummary));
        Assert.Equal(
            fallbackResult.ShoppingPlans.Select(ToRouteSummary),
            explicitResult.ShoppingPlans.Select(ToRouteSummary));
        Assert.Equal(
            fallbackResult.MissingItems.Select(item => item.ItemId),
            explicitResult.MissingItems.Select(item => item.ItemId));
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
            new MarketEvidenceReconciliationService(marketExecution.Object),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = [reusablePlan],
                SourceMarketAnalyses =
                [
                    MarketAnalysis(101, "Existing Item", MarketFetchScope.SelectedDataCenter, "Aether")
                ],
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
    public async Task AnalyzeAsync_RefreshesStructurallyCompletePlanWhenEvidenceHasExpired()
    {
        var plan = CreatePlan();
        var stalePlan = ShoppingPlan(
            101,
            "Existing Item",
            World("Aether", "Siren", 500, 5));
        var currentPlan = ShoppingPlan(
            202,
            "Missing Item",
            World("Aether", "Faerie", 700, 5));
        var replacementPlan = ShoppingPlan(
            101,
            "Existing Item",
            World("Aether", "Adamantoise", 300, 5));
        var marketExecution = new Mock<IMarketAnalysisExecutionService>();
        marketExecution.Setup(service => service.ExecuteAsync(
                It.IsAny<MarketAnalysisExecutionRequest>(),
                It.IsAny<IProgress<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<MarketAnalysisExecutionOptions?>()))
            .ReturnsAsync(new MarketAnalysisExecutionResult(
                CreateEmptyEvidence(),
                [MarketAnalysis(101, "Existing Item", MarketFetchScope.SelectedDataCenter, "Aether")],
                [replacementPlan]));
        var service = new ProcurementRouteExecutionService(
            new MarketEvidenceReconciliationService(marketExecution.Object),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = [stalePlan, currentPlan],
                SourceMarketAnalyses =
                [
                    MarketAnalysis(
                        101,
                        "Existing Item",
                        MarketFetchScope.SelectedDataCenter,
                        DateTime.UtcNow - TimeSpan.FromHours(13),
                        "Aether"),
                    MarketAnalysis(202, "Missing Item", MarketFetchScope.SelectedDataCenter, "Aether")
                ],
                Scope = MarketFetchScope.SelectedDataCenter,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                ProcurementConfig = new MarketAnalysisConfig { TravelTolerance = 0 }
            },
            executionOptions: MarketAnalysisExecutionOptions.Synchronous);

        marketExecution.Verify(service => service.ExecuteAsync(
            It.Is<MarketAnalysisExecutionRequest>(request =>
                request.Items.Count == 1 && request.Items[0].ItemId == 101),
            It.IsAny<IProgress<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<MarketAnalysisExecutionOptions?>()));
        Assert.Equal("Adamantoise", result.EvidencePlans.Single(item => item.ItemId == 101).RecommendedWorld?.WorldName);
        var reconciliation = Assert.Single(result.ReconciliationItems!, item => item.ItemId == 101);
        Assert.Equal(MarketEvidenceReconciliationReason.RecommendationExpired, reconciliation.Reason);
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
            new MarketEvidenceReconciliationService(Mock.Of<IMarketAnalysisExecutionService>()),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = sourcePlans,
                SourceMarketAnalyses =
                [
                    MarketAnalysis(101, "Existing Item", MarketFetchScope.EntireRegion, "Aether", "Primal"),
                    MarketAnalysis(202, "Missing Item", MarketFetchScope.EntireRegion, "Aether", "Primal")
                ],
                Scope = MarketFetchScope.EntireRegion,
                SelectedDataCenter = "Aether",
                SelectedRegion = "North America",
                Lens = MarketAcquisitionLens.MinimumUpfrontCost,
                ProcurementConfig = new MarketAnalysisConfig { TravelTolerance = 11 },
                ReconciliationPolicy = new MarketEvidenceReconciliationPolicy { RequireCompleteScope = false }
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
            new MarketEvidenceReconciliationService(marketExecution.Object),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = [partialRegionPlan],
                SourceMarketAnalyses =
                [
                    MarketAnalysis(101, "Existing Item", MarketFetchScope.EntireRegion, "Aether", "Primal")
                ],
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
            new MarketEvidenceReconciliationService(Mock.Of<IMarketAnalysisExecutionService>()),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));

        var result = await service.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = sourcePlans,
                SourceMarketAnalyses =
                [
                    MarketAnalysis(101, "Existing Item", MarketFetchScope.SelectedDataCenter, "Aether"),
                    MarketAnalysis(202, "Missing Item", MarketFetchScope.SelectedDataCenter, "Aether")
                ],
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

    private static ProcurementRouteExecutionService CreateService()
    {
        return new ProcurementRouteExecutionService(
            new MarketEvidenceReconciliationService(Mock.Of<IMarketAnalysisExecutionService>()),
            new MarketShoppingService(Mock.Of<IMarketCacheService>()));
    }

    private static string ToRouteSummary(DetailedShoppingPlan plan)
    {
        var worlds = string.Join(
            "|",
            plan.WorldOptions.Select(world =>
                $"{world.DataCenter}:{world.WorldName}:{world.TotalQuantityPurchased}:{world.TotalCost}"));

        return string.Join(
            ";",
            plan.ItemId,
            plan.QuantityNeeded,
            plan.RecommendedWorld?.DataCenter,
            plan.RecommendedWorld?.WorldName,
            plan.RecommendedWorld?.TotalCost,
            worlds);
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

    private static MarketItemAnalysis MarketAnalysis(
        int itemId,
        string name,
        MarketFetchScope scope,
        params string[] dataCenters)
    {
        return MarketAnalysis(itemId, name, scope, DateTime.UtcNow, dataCenters);
    }

    private static MarketItemAnalysis MarketAnalysis(
        int itemId,
        string name,
        MarketFetchScope scope,
        DateTime evidenceTimestampUtc,
        params string[] dataCenters)
    {
        var loadedAtUtc = DateTime.UtcNow;
        return new MarketItemAnalysis
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = 5,
            Scope = scope,
            LoadedAtUtc = loadedAtUtc,
            RequestedDataCenters = dataCenters,
            PresentDataCenters = dataCenters,
            Worlds = dataCenters
                .Select(dataCenter => new WorldMarketAnalysis
                {
                    DataCenter = dataCenter,
                    WorldName = dataCenter == "Aether" ? "Siren" : "Leviathan",
                    MarketUploadedAtUtc = evidenceTimestampUtc,
                    DataQualityBucket = MarketDataQualityBucket.Current
                })
                .ToList()
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
