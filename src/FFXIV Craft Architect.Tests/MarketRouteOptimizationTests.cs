using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public class MarketRouteOptimizationTests
{
    [Fact]
    public void OptimizeProcurementRoute_IntermediateToleranceUsesPercentagePremiumBudget()
    {
        var plans = new[]
        {
            Plan(1, "Anchor", 1,
                World("Aether", "Siren", 1_000, 1_000, Listing(1, 1_000, "Siren Anchor"))),
            Plan(2, "Flexible", 1,
                World("Aether", "Siren", 1_000, 1_000, Listing(1, 1_000, "Siren Flexible")),
                World("Aether", "Gilgamesh", 700, 700, Listing(1, 700, "Gilgamesh Flexible")))
        };

        var toleranceSix = Optimize(plans, travelTolerance: 6, includeSplitPurchases: false);
        var toleranceSeven = Optimize(plans, travelTolerance: 7, includeSplitPurchases: false);

        Assert.All(toleranceSix, plan => Assert.Equal("Siren", plan.RecommendedWorld?.WorldName));
        Assert.Equal("Gilgamesh", toleranceSeven[1].RecommendedWorld?.WorldName);
    }

    [Fact]
    public void OptimizeProcurementRoute_SingleItemEqualShapeAlwaysChoosesLowerCost()
    {
        var plan = Plan(1, "Equal Shape", 1,
            World("Aether", "Siren", 500, 500, Listing(1, 500, "Siren")),
            World("Primal", "Leviathan", 100, 100, Listing(1, 100, "Leviathan")));

        foreach (var tolerance in Enumerable.Range(0, 12))
        {
            var optimized = Optimize([plan], tolerance, includeSplitPurchases: false);
            Assert.Equal("Leviathan", optimized[0].RecommendedWorld?.WorldName);
        }
    }

    [Fact]
    public async Task OptimizeProcurementRoute_HomeOriginOnlyChangesTravelWhenEnabled()
    {
        var plan = Plan(1, "Origin", 1,
            World("Aether", "Siren", 120, 120, Listing(1, 120, "Siren")),
            World("Primal", "Leviathan", 100, 100, Listing(1, 100, "Leviathan")));
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        var withoutOrigin = await service.OptimizeProcurementRouteWithDecisionAsync(
            [plan],
            new MarketAnalysisConfig { TravelTolerance = 0 });
        var withOrigin = await service.OptimizeProcurementRouteWithDecisionAsync(
            [plan],
            new MarketAnalysisConfig
            {
                TravelTolerance = 0,
                StartFromHomeDataCenter = true,
                HomeDataCenter = "Aether"
            });

        Assert.Equal("Leviathan", withoutOrigin.ShoppingPlans[0].RecommendedWorld?.WorldName);
        Assert.Equal("Siren", withOrigin.ShoppingPlans[0].RecommendedWorld?.WorldName);
        Assert.False(withoutOrigin.Decision?.StartsFromHomeDataCenter);
        Assert.True(withOrigin.Decision?.StartsFromHomeDataCenter);
    }

    [Fact]
    public async Task OptimizeProcurementRoute_DecisionExplainsSelectedVersusCheapestRoute()
    {
        var plans = new[]
        {
            Plan(1, "Anchor", 1,
                World("Aether", "Siren", 1_000, 1_000, Listing(1, 1_000, "Siren Anchor"))),
            Plan(2, "Flexible", 1,
                World("Aether", "Siren", 1_000, 1_000, Listing(1, 1_000, "Siren Flexible")),
                World("Aether", "Gilgamesh", 700, 700, Listing(1, 700, "Gilgamesh Flexible")))
        };
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        var result = await service.OptimizeProcurementRouteWithDecisionAsync(
            plans,
            new MarketAnalysisConfig { TravelTolerance = 6 });

        var decision = Assert.IsType<MarketRouteDecision>(result.Decision);
        Assert.Equal(1_700, decision.CheapestGilCost);
        Assert.Equal(2_000, decision.SelectedGilCost);
        Assert.Equal(300, decision.PremiumGil);
        Assert.Equal(1, decision.WorldStopsAvoided);
    }

    [Fact]
    public async Task OptimizeProcurementRoute_FullRouteRetainsAccumulatedEvidencePenalty()
    {
        var lowQuality = World("Aether", "Faerie", 100, 100, Listing(1, 100, "Stale listing"));
        lowQuality.MarketDataQualityScore = 5;
        lowQuality.MarketDataQualityBucket = MarketDataQualityBucket.VeryOld;
        var current = World("Aether", "Siren", 200, 200, Listing(1, 200, "Current listing"));
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        var result = await service.OptimizeProcurementRouteWithDecisionAsync(
            [Plan(1, "Evidence", 1, lowQuality, current)],
            new MarketAnalysisConfig { TravelTolerance = 11 },
            includeSplitPurchases: false);

        Assert.Equal("Siren", result.ShoppingPlans[0].RecommendedWorld?.WorldName);
        Assert.Equal(200, result.Decision?.SelectedGilCost);
        Assert.Equal(200, result.Decision?.CheapestGilCost);
        Assert.Equal(0, result.Decision?.SelectedEvidencePenalty);
    }

    [Fact]
    public async Task OptimizeProcurementRoute_OldEvidenceRemainsEligibleAndGilCeilingUsesCashCost()
    {
        var oldCheap = World("Aether", "Faerie", 100, 100, Listing(1, 100, "Old listing"));
        oldCheap.MarketDataQualityScore = 25;
        oldCheap.MarketDataQualityBucket = MarketDataQualityBucket.Old;
        var current = World("Aether", "Siren", 150, 150, Listing(1, 150, "Current listing"));
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        var result = await service.OptimizeProcurementRouteWithDecisionAsync(
            [Plan(1, "Evidence", 1, oldCheap, current)],
            new MarketAnalysisConfig { TravelTolerance = 11 },
            includeSplitPurchases: false);

        Assert.Equal("Faerie", result.ShoppingPlans[0].RecommendedWorld?.WorldName);
        Assert.Equal(100, result.Decision?.CheapestGilCost);
        Assert.Equal(100, result.Decision?.SelectedGilCost);
        Assert.True(result.Decision?.SelectedEvidencePenalty > 0);
    }

    [Fact]
    public async Task OptimizeProcurementRoute_UntrustedOnlyEvidenceRequiresRefresh()
    {
        var ancient = World("Aether", "Faerie", 100, 100, Listing(1, 100, "Ancient listing"));
        ancient.MarketDataQualityBucket = MarketDataQualityBucket.Ancient;
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        var result = await service.OptimizeProcurementRouteWithDecisionAsync(
            [Plan(1, "Evidence", 1, ancient)],
            new MarketAnalysisConfig { TravelTolerance = 11 },
            includeSplitPurchases: false);

        Assert.Null(result.Decision);
        Assert.Contains("12 hours old or older", result.ShoppingPlans[0].Error);
    }

    [Fact]
    public async Task OptimizeProcurementRoute_DecisionExposesEveryRepresentativeSliderRoute()
    {
        var plans = new[]
        {
            Plan(1, "Anchor", 1,
                World("Aether", "Siren", 1_000, 1_000, Listing(1, 1_000, "Siren Anchor"))),
            Plan(2, "Flexible", 1,
                World("Aether", "Siren", 1_000, 1_000, Listing(1, 1_000, "Siren Flexible")),
                World("Aether", "Gilgamesh", 700, 700, Listing(1, 700, "Gilgamesh Flexible")))
        };
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        var result = await service.OptimizeProcurementRouteWithDecisionAsync(
            plans,
            new MarketAnalysisConfig { TravelTolerance = 6 });

        var decision = Assert.IsType<MarketRouteDecision>(result.Decision);
        Assert.Equal(2, decision.RepresentativeRoutes.Count);
        Assert.Equal(Enumerable.Range(0, 12), decision.RepresentativeRoutes.SelectMany(route =>
            Enumerable.Range(route.MinimumTolerance, route.MaximumTolerance - route.MinimumTolerance + 1)));
        Assert.Single(decision.RepresentativeRoutes, route =>
            decision.TravelTolerance >= route.MinimumTolerance &&
            decision.TravelTolerance <= route.MaximumTolerance);
    }

    [Fact]
    public async Task OptimizeProcurementRoute_DecisionExposesItemConsolidationPremium()
    {
        var plans = new[]
        {
            Plan(1, "Anchor", 1,
                World("Aether", "Siren", 1_000, 1_000, Listing(1, 1_000, "Anchor"))),
            Plan(2, "Premium item", 1,
                World("Aether", "Siren", 500, 500, Listing(1, 500, "Consolidated")),
                World("Aether", "Faerie", 100, 100, Listing(1, 100, "Cheapest")))
        };
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        var result = await service.OptimizeProcurementRouteWithDecisionAsync(
            plans,
            new MarketAnalysisConfig { TravelTolerance = 0 });

        var decision = Assert.IsType<MarketRouteDecision>(result.Decision);
        var item = Assert.Single(decision.ItemPremiums, item => item.ItemId == 2);
        Assert.Equal(100, item.CheapestEligibleGilCost);
        Assert.Equal(500, item.SelectedGilCost);
        Assert.Equal(400, item.ConsolidationPremiumGil);
    }

    [Fact]
    public async Task OptimizeProcurementRoute_CoverageSelectionMatchesDecisionAndWorldCards()
    {
        var plan = Plan(1, "Cash-out alignment", 1,
            World("Aether", "Faerie", 500, 500, Listing(1, 500, "Faerie listing")),
            World("Dynamis", "Cuchulainn", 100, 100, Listing(10, 100, "Cuchulainn stack")));
        plan.CoverageSet = MarketCoverageBuilder.Build(plan);
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        var result = await service.OptimizeProcurementRouteWithDecisionAsync(
            [plan],
            new MarketAnalysisConfig { TravelTolerance = 0 },
            includeSplitPurchases: false);

        var optimizedPlan = Assert.Single(result.ShoppingPlans);
        var decision = Assert.IsType<MarketRouteDecision>(result.Decision);
        Assert.Equal(500, decision.SelectedGilCost);
        Assert.Equal(decision.SelectedGilCost, PurchaseRecommendationCost.GetRecommendedCost(optimizedPlan));

        var card = Assert.Single(ProcurementWorldCardBuilder.BuildWorldCards(result.ShoppingPlans, "Aether"));
        Assert.Equal("Faerie", card.WorldName);
        Assert.Equal(decision.SelectedGilCost, card.TotalCost);
    }

    [Fact]
    public async Task OptimizeProcurementRoute_WideSearchIsBoundedAndMarkedApproximate()
    {
        var plans = Enumerable.Range(0, 8)
            .Select(itemIndex => Plan(
                10_000 + itemIndex,
                $"Bounded item {itemIndex}",
                1,
                Enumerable.Range(0, 8)
                    .Select(worldIndex =>
                    {
                        var price = 100L + ((itemIndex * 17 + worldIndex * 11) % 73);
                        return World(
                            $"DC {worldIndex / 2}",
                            $"World {worldIndex}",
                            price,
                            price,
                            Listing(1, price, $"Listing {itemIndex}-{worldIndex}"));
                    })
                    .ToArray()))
            .ToList();
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);
        var config = new MarketAnalysisConfig
        {
            TravelTolerance = 0,
            TravelPriority = MarketTravelPriority.WorldVisitsFirst
        };

        var first = await service.OptimizeProcurementRouteWithDecisionAsync(
            plans,
            config,
            includeSplitPurchases: false);
        var second = await service.OptimizeProcurementRouteWithDecisionAsync(
            plans,
            config,
            includeSplitPurchases: false);

        var firstDecision = Assert.IsType<MarketRouteDecision>(first.Decision);
        var secondDecision = Assert.IsType<MarketRouteDecision>(second.Decision);
        Assert.True(first.IsComplete);
        Assert.True(firstDecision.RouteSearchWasTruncated);
        Assert.Equal(firstDecision.SelectedGilCost, secondDecision.SelectedGilCost);
        Assert.Equal(firstDecision.SelectedWorldStops, secondDecision.SelectedWorldStops);
        Assert.Equal(
            first.ShoppingPlans.Select(plan => plan.RecommendedWorld?.WorldName),
            second.ShoppingPlans.Select(plan => plan.RecommendedWorld?.WorldName));
    }

    [Fact]
    public void OptimizeProcurementRoute_MovingTowardCostNeverIncreasesSelectedCashCost()
    {
        var plans = new[]
        {
            Plan(1, "Anchor", 1,
                World("Aether", "Siren", 1_000, 1_000, Listing(1, 1_000, "Siren Anchor"))),
            Plan(2, "Flexible", 1,
                World("Aether", "Siren", 1_000, 1_000, Listing(1, 1_000, "Siren Flexible")),
                World("Aether", "Gilgamesh", 700, 700, Listing(1, 700, "Gilgamesh Flexible")))
        };
        var costs = Enumerable.Range(0, 12)
            .Select(tolerance => Optimize(plans, tolerance, false).Sum(PurchaseRecommendationCost.GetRecommendedCost))
            .ToList();

        Assert.All(costs.Zip(costs.Skip(1)), pair => Assert.True(pair.First >= pair.Second));
    }

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
        Assert.Null(plan.RecommendedSplit);
        var coverage = Assert.IsType<MarketCoverageOption>(
            PurchaseRecommendationCost.GetDefaultCoverageOption(plan));
        Assert.Collection(
            coverage.Worlds,
            siren =>
            {
                Assert.Equal("Aether", siren.DataCenter);
                Assert.Equal("Siren", siren.WorldName);
                Assert.Equal(5, siren.QuantityToPurchase);
            },
            leviathan =>
            {
                Assert.Equal("Primal", leviathan.DataCenter);
                Assert.Equal("Leviathan", leviathan.WorldName);
                Assert.Equal(5, leviathan.QuantityToPurchase);
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
    public void OptimizeProcurementRoute_SplitPolicyMateriallyChangesCostAndRouteShape()
    {
        var plan = Plan(1, "Split Policy Item", 10,
            World("Aether", "Siren", 500, 100, Listing(5, 100, "Siren Split")),
            World("Primal", "Leviathan", 600, 120, Listing(5, 120, "Leviathan Split")),
            World("Crystal", "Balmung", 10_000, 1_000, Listing(10, 1_000, "Balmung Single")));

        var splitEnabled = Assert.Single(Optimize([plan], travelTolerance: 11, includeSplitPurchases: true));
        var splitDisabled = Assert.Single(Optimize([plan], travelTolerance: 11, includeSplitPurchases: false));

        Assert.Equal(1_100, PurchaseRecommendationCost.GetRecommendedCost(splitEnabled));
        Assert.Equal(10_000, PurchaseRecommendationCost.GetRecommendedCost(splitDisabled));
        Assert.Equal(2, splitEnabled.RecommendedSplit?.Count);
        Assert.Equal("Balmung", splitDisabled.RecommendedWorld?.WorldName);
    }

    [Fact]
    public void OptimizeProcurementRoute_SplitDisabled_CanRemoveTheOnlyCompleteRecommendation()
    {
        var plan = Plan(1, "Coverage Required Item", 10,
            World("Aether", "Siren", 500, 100, Listing(5, 100, "Siren Split")),
            World("Primal", "Leviathan", 600, 120, Listing(5, 120, "Leviathan Split")));

        var splitEnabled = Assert.Single(Optimize([plan], travelTolerance: 11, includeSplitPurchases: true));
        var splitDisabled = Assert.Single(Optimize([plan], travelTolerance: 11, includeSplitPurchases: false));

        Assert.Equal(1_100, PurchaseRecommendationCost.GetRecommendedCost(splitEnabled));
        Assert.Equal(2, splitEnabled.RecommendedSplit?.Count);
        Assert.Null(splitDisabled.RecommendedWorld);
        Assert.Null(splitDisabled.RecommendedSplit);
        Assert.Equal(0, PurchaseRecommendationCost.GetRecommendedCost(splitDisabled));
    }

    [Fact]
    public void OptimizeProcurementRoute_SplitEnabled_StillRespectsTravelTolerancePremiumCeiling()
    {
        var plan = Plan(1, "Tolerance Split Item", 10,
            World("Aether", "Siren", 500, 100, Listing(5, 100, "Siren Split")),
            World("Primal", "Leviathan", 600, 120, Listing(5, 120, "Leviathan Split")),
            World("Crystal", "Balmung", 1_500, 150, Listing(10, 150, "Balmung Single")));

        var fiftyPercentCeiling = Assert.Single(Optimize([plan], travelTolerance: 3, includeSplitPurchases: true));
        var thirtyFivePercentCeiling = Assert.Single(Optimize([plan], travelTolerance: 4, includeSplitPurchases: true));

        Assert.Equal("Balmung", fiftyPercentCeiling.RecommendedWorld?.WorldName);
        Assert.Equal(1_500, PurchaseRecommendationCost.GetRecommendedCost(fiftyPercentCeiling));
        Assert.Equal(2, thirtyFivePercentCeiling.RecommendedSplit?.Count);
        Assert.Equal(1_100, PurchaseRecommendationCost.GetRecommendedCost(thirtyFivePercentCeiling));
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
        Assert.NotSame(plan, result);
        Assert.Equal(plan.ItemId, result.ItemId);
        Assert.Equal(plan.Name, result.Name);
        Assert.Equal("No market data in cache", result.Error);
        Assert.Null(result.RecommendedWorld);
        Assert.Null(result.RecommendedSplit);
    }

    [Fact]
    public async Task OptimizeProcurementRouteAsync_MatchesSynchronousRouteChoices()
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
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);
        var config = new MarketAnalysisConfig { TravelTolerance = 11 };
        var options = new MarketAnalysisExecutionOptions { YieldEveryItems = 1 };

        var sync = service.OptimizeProcurementRoute(plans, config, includeSplitPurchases: false);
        var async = await service.OptimizeProcurementRouteAsync(
            plans,
            config,
            includeSplitPurchases: false,
            executionOptions: options);

        Assert.Equal(sync.Select(plan => plan.RecommendedWorld?.WorldName), async.Select(plan => plan.RecommendedWorld?.WorldName));
        Assert.Equal(sync.Select(plan => plan.RecommendedWorld?.DataCenter), async.Select(plan => plan.RecommendedWorld?.DataCenter));
    }

    [Fact]
    public void OptimizeProcurementRoute_WithoutAsyncWrapper_CompletesSynchronously()
    {
        var plans = new[]
        {
            Plan(1, "Synchronous Route Item", 1,
                World("Aether", "Siren", 100, 100, Listing(1, 100, "Sync Route")))
        };
        var service = new MarketShoppingService(new Mock<IMarketCacheService>().Object);

        var optimized = service.OptimizeProcurementRoute(plans);

        var result = Assert.Single(optimized);
        Assert.Equal("Siren", result.RecommendedWorld?.WorldName);
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
