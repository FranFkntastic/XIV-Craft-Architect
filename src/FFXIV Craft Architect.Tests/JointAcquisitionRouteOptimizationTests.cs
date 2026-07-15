using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public sealed class JointAcquisitionRouteOptimizationTests
{
    [Fact]
    public async Task LowestCostCraftsWhileFewestStopsBuysFinishedItem()
    {
        var plan = CreateMakeOrBuyPlan();
        var evidence = new[]
        {
            Evidence(100, "Finished", 1, ("Aether", "Siren", 100L)),
            Evidence(201, "Ingredient A", 1, ("Aether", "Faerie", 40L)),
            Evidence(202, "Ingredient B", 1, ("Aether", "Gilgamesh", 45L))
        };
        var service = CreateService();

        var cheapest = await service.OptimizeAsync(
            plan,
            evidence,
            Config(11),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);
        var leastTravel = await service.OptimizeAsync(
            plan,
            evidence,
            Config(0),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(AcquisitionSource.Craft, cheapest.OptimizedPlan.RootItems[0].Source);
        Assert.Equal(85, cheapest.RouteDecision?.SelectedGilCost);
        Assert.Equal(2, cheapest.RouteDecision?.SelectedWorldStops);
        Assert.Equal(AcquisitionSource.MarketBuyNq, leastTravel.OptimizedPlan.RootItems[0].Source);
        Assert.Equal(100, leastTravel.RouteDecision?.SelectedGilCost);
        Assert.Equal(1, leastTravel.RouteDecision?.SelectedWorldStops);
        Assert.Equal(85, leastTravel.RouteDecision?.CheapestGilCost);
        Assert.Contains(leastTravel.RouteDecision!.RepresentativeRoutes,
            route => route.GilCost == 85 && route.WorldStops == 2);
        Assert.Contains(leastTravel.RouteDecision.RepresentativeRoutes,
            route => route.GilCost == 100 && route.WorldStops == 1);
    }

    [Fact]
    public async Task RepresentativeRoutesIncludeConsolidatedRouteDiscoveredDuringTolerancePass()
    {
        var first = Leaf(301, "First material");
        first.SourceReason = AcquisitionSourceReason.UserSelected;
        var second = Leaf(302, "Second material");
        second.SourceReason = AcquisitionSourceReason.UserSelected;
        var plan = new CraftingPlan { RootItems = [first, second] };
        var evidence = new[]
        {
            Evidence(301, first.Name, 1,
                ("Primal", "Excalibur", 230L),
                ("Aether", "Mateus", 300L)),
            Evidence(302, second.Name, 1,
                ("Crystal", "Goblin", 240L),
                ("Aether", "Mateus", 300L))
        };

        var result = await CreateService().OptimizeAsync(
            plan,
            evidence,
            Config(tolerance: 4),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        var decision = Assert.IsType<MarketRouteDecision>(result.RouteDecision);
        Assert.Equal(600, decision.SelectedGilCost);
        Assert.Equal(1, decision.SelectedWorldStops);
        Assert.Equal(0, decision.SelectedDataCenterTransfers);
        Assert.Contains(decision.RepresentativeRoutes,
            route => route.GilCost == 470 && route.WorldStops == 2 && route.DataCenterTransfers == 1);
        Assert.Contains(decision.RepresentativeRoutes,
            route => route.GilCost == 600 && route.WorldStops == 1 && route.DataCenterTransfers == 0);
        Assert.Contains(decision.RepresentativeRoutes,
            route => route.MinimumTolerance <= decision.TravelTolerance &&
                     route.MaximumTolerance >= decision.TravelTolerance &&
                     route.GilCost == decision.SelectedGilCost &&
                     route.WorldStops == decision.SelectedWorldStops &&
                     route.DataCenterTransfers == decision.SelectedDataCenterTransfers);
    }

    [Fact]
    public async Task UserSelectedSourceIsAHardConstraint()
    {
        var plan = CreateMakeOrBuyPlan();
        plan.RootItems[0].Source = AcquisitionSource.MarketBuyNq;
        plan.RootItems[0].SourceReason = AcquisitionSourceReason.UserSelected;
        var service = CreateService();

        var result = await service.OptimizeAsync(
            plan,
            [
                Evidence(100, "Finished", 1, ("Aether", "Siren", 100L)),
                Evidence(201, "Ingredient A", 1, ("Aether", "Faerie", 1L)),
                Evidence(202, "Ingredient B", 1, ("Aether", "Gilgamesh", 1L))
            ],
            Config(11),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(AcquisitionSource.MarketBuyNq, result.OptimizedPlan.RootItems[0].Source);
        Assert.Equal(AcquisitionSourceReason.UserSelected, result.OptimizedPlan.RootItems[0].SourceReason);
        Assert.Equal(100, result.RouteDecision?.SelectedGilCost);
    }

    [Fact]
    public async Task VendorGilParticipatesInTheGlobalCostAndProducesAProcurementCard()
    {
        var root = new PlanNode
        {
            ItemId = 300,
            Name = "Vendor material",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanBuyFromMarket = true,
            CanBuyFromVendor = true,
            VendorPrice = 20,
            VendorOptions =
            [
                new VendorInfo { Name = "Supplier", Location = "Limsa Lominsa", Price = 20, Currency = "Gil" }
            ]
        };
        var service = CreateService();

        var result = await service.OptimizeAsync(
            new CraftingPlan { RootItems = [root] },
            [Evidence(300, root.Name, 3, ("Aether", "Siren", 25L))],
            Config(11),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(AcquisitionSource.VendorBuy, result.OptimizedPlan.RootItems[0].Source);
        Assert.Equal(60, result.RouteDecision?.SelectedGilCost);
        Assert.Equal(60, result.RouteDecision?.FixedAcquisitionGilCost);
        var plan = Assert.Single(result.ShoppingPlans);
        Assert.Equal(MarketShoppingConstants.VendorWorldName, plan.RecommendedWorld?.WorldName);
    }

    [Fact]
    public async Task DuplicateIngredientsAreAggregatedBeforeWholeStackPricing()
    {
        var sharedA = Leaf(201, "Shared", quantity: 2);
        var sharedB = Leaf(201, "Shared", quantity: 3);
        var plan = new CraftingPlan
        {
            RootItems =
            [
                CraftOnly(101, "Root A", sharedA),
                CraftOnly(102, "Root B", sharedB)
            ]
        };
        var service = CreateService();

        var result = await service.OptimizeAsync(
            plan,
            [EvidenceWithStack(201, "Shared", quantityNeeded: 5, "Aether", "Siren", stackQuantity: 10, unitPrice: 7)],
            Config(11),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        var sharedPlan = Assert.Single(result.ShoppingPlans);
        Assert.Equal(5, sharedPlan.QuantityNeeded);
        Assert.Equal(70, result.RouteDecision?.SelectedGilCost);
    }

    [Fact]
    public async Task MixedQualityDemandBuysOnlyTheRequiredHqQuantityAcrossWorlds()
    {
        var nq = new PlanNode
        {
            ItemId = 400,
            Name = "Shared material",
            Quantity = 3,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanBuyFromMarket = true
        };
        var hq = new PlanNode
        {
            ItemId = 400,
            Name = "Shared material",
            Quantity = 2,
            Source = AcquisitionSource.MarketBuyHq,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanBuyFromMarket = true,
            CanBeHq = true,
            MustBeHq = true
        };
        var evidence = new DetailedShoppingPlan
        {
            ItemId = 400,
            Name = "Shared material",
            QuantityNeeded = 5,
            WorldOptions =
            [
                WorldWithListing("Aether", "Siren", 3, 10, isHq: false),
                WorldWithListing("Aether", "Faerie", 2, 100, isHq: true)
            ]
        };

        var result = await CreateService().OptimizeAsync(
            new CraftingPlan { RootItems = [nq, hq] },
            [evidence],
            Config(11),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(5, Assert.Single(result.ShoppingPlans).QuantityNeeded);
        Assert.Equal(2, result.ShoppingPlans[0].HqQuantityNeeded);
        Assert.Equal(230, result.RouteDecision?.SelectedGilCost);
        Assert.Equal(2, result.RouteDecision?.SelectedWorldStops);
    }

    [Fact]
    public async Task PartiallyRoutedVariantIsNotAFeasibleAcquisitionPlan()
    {
        var hqOnly = new PlanNode
        {
            ItemId = 500,
            Name = "Unfillable HQ purchase",
            Quantity = 4_995,
            Source = AcquisitionSource.MarketBuyHq,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanBuyFromMarket = true,
            CanBeHq = true,
            MustBeHq = true
        };
        var ordinary = Leaf(501, "Fillable purchase");
        var evidence = new[]
        {
            new DetailedShoppingPlan
            {
                ItemId = 500,
                Name = hqOnly.Name,
                QuantityNeeded = hqOnly.Quantity,
                WorldOptions = [WorldWithListing("Aether", "Siren", 6_000, 100, isHq: false)]
            },
            Evidence(501, ordinary.Name, 1, ("Aether", "Faerie", 10L))
        };

        var result = await CreateService().OptimizeAsync(
            new CraftingPlan { RootItems = [hqOnly, ordinary] },
            evidence,
            Config(11),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(0, result.FeasiblePlanCount);
        Assert.Null(result.RouteDecision);
    }

    [Theory]
    [InlineData(11, AcquisitionSource.Craft)]
    [InlineData(10, AcquisitionSource.Craft)]
    [InlineData(6, AcquisitionSource.MarketBuyNq)]
    [InlineData(0, AcquisitionSource.MarketBuyNq)]
    public async Task PremiumCurveIsAppliedToTheGlobalAcquisitionBaseline(
        int tolerance,
        AcquisitionSource expectedSource)
    {
        var service = CreateService();
        var result = await service.OptimizeAsync(
            CreateMakeOrBuyPlan(),
            [
                Evidence(100, "Finished", 1, ("Aether", "Siren", 100L)),
                Evidence(201, "Ingredient A", 1, ("Aether", "Faerie", 40L)),
                Evidence(202, "Ingredient B", 1, ("Aether", "Gilgamesh", 45L))
            ],
            Config(tolerance),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(expectedSource, result.OptimizedPlan.RootItems[0].Source);
        Assert.True(result.RouteDecision!.SelectedGilCost >= result.RouteDecision.CheapestGilCost);
        Assert.True(result.RouteDecision.MaximumPremiumRate == null ||
            result.RouteDecision.SelectedGilCost <=
            result.RouteDecision.CheapestGilCost * (1m + result.RouteDecision.MaximumPremiumRate.Value));
    }

    [Fact]
    public async Task LowestCostEndpointNeverExceedsLegacyEvaluatorAcrossDeterministicCorpus()
    {
        var random = new Random(0xCA2026);
        var marketShopping = new MarketShoppingService(Mock.Of<IMarketCacheService>());
        var joint = new JointAcquisitionRouteOptimizationService(marketShopping);

        for (var sample = 0; sample < 40; sample++)
        {
            var directCost = random.Next(25, 250);
            var firstCost = random.Next(10, 140);
            var secondCost = random.Next(10, 140);
            var evidence = new[]
            {
                Evidence(100, "Finished", 1, ("Aether", "Siren", (long)directCost)),
                Evidence(201, "Ingredient A", 1, ("Aether", sample % 2 == 0 ? "Faerie" : "Siren", (long)firstCost)),
                Evidence(202, "Ingredient B", 1, ("Aether", sample % 3 == 0 ? "Gilgamesh" : "Siren", (long)secondCost))
            };
            var legacyPlan = CreateMakeOrBuyPlan();
            AcquisitionPlanningService.ReconcileAcquisitionDecisions(legacyPlan, evidence);
            var activeIds = AcquisitionPlanningService.GetActiveProcurementItems(legacyPlan)
                .Select(item => item.ItemId)
                .ToHashSet();
            var legacyRoute = await marketShopping.OptimizeProcurementRouteWithDecisionAsync(
                evidence.Where(plan => activeIds.Contains(plan.ItemId)),
                Config(11),
                includeSplitPurchases: true,
                MarketAnalysisExecutionOptions.Synchronous);
            var legacyCost = legacyRoute.Decision?.SelectedGilCost ?? 0;

            var optimized = await joint.OptimizeAsync(
                CreateMakeOrBuyPlan(),
                evidence,
                Config(11),
                includeSplitPurchases: true,
                MarketAnalysisExecutionOptions.Synchronous);

            Assert.True(
                optimized.RouteDecision!.SelectedGilCost <= legacyCost,
                $"Sample {sample}: joint {optimized.RouteDecision.SelectedGilCost:N0}g exceeded legacy {legacyCost:N0}g.");
        }
    }

    [Fact]
    public async Task MediumRouteSearchIsBoundedAndDeterministic()
    {
        var roots = new List<PlanNode>();
        var evidence = new List<DetailedShoppingPlan>();
        for (var index = 0; index < 14; index++)
        {
            var rootId = 1_000 + index;
            var childId = 2_000 + index;
            var child = Leaf(childId, $"Ingredient {index}");
            var root = new PlanNode
            {
                ItemId = rootId,
                Name = $"Finished {index}",
                Quantity = 1,
                Source = AcquisitionSource.Craft,
                SourceReason = AcquisitionSourceReason.SystemDefault,
                CanCraft = true,
                CanBuyFromMarket = true,
                Children = [child]
            };
            child.Parent = root;
            roots.Add(root);
            evidence.Add(Evidence(rootId, root.Name, 1, ("Aether", "Siren", 100L + index)));
            evidence.Add(Evidence(childId, child.Name, 1, ("Aether", index % 2 == 0 ? "Faerie" : "Gilgamesh", 80L + index)));
        }

        var service = CreateService();
        var first = await service.OptimizeAsync(
            new CraftingPlan { RootItems = roots },
            evidence,
            Config(11),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);
        var second = await service.OptimizeAsync(
            new CraftingPlan { RootItems = roots },
            evidence,
            Config(11),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.InRange(first.FrontierPlanCount, 1, 4_097);
        Assert.True(first.SearchWasTruncated);
        Assert.True(first.RouteDecision?.AcquisitionSearchWasTruncated);
        Assert.Equal(first.RouteDecision?.SelectedGilCost, second.RouteDecision?.SelectedGilCost);
        Assert.Equal(first.OptimizedPlan.RootItems.Select(root => root.Source),
            second.OptimizedPlan.RootItems.Select(root => root.Source));
    }

    private static JointAcquisitionRouteOptimizationService CreateService() =>
        new(new MarketShoppingService(Mock.Of<IMarketCacheService>()));

    private static MarketAnalysisConfig Config(int tolerance) => new()
    {
        TravelTolerance = tolerance,
        TravelPriority = MarketTravelPriority.WorldVisitsFirst,
        MaxWorldsPerItem = 8
    };

    private static CraftingPlan CreateMakeOrBuyPlan()
    {
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Finished",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true,
            CanBuyFromMarket = true,
            Children = [Leaf(201, "Ingredient A"), Leaf(202, "Ingredient B")]
        };
        foreach (var child in root.Children)
        {
            child.Parent = root;
        }

        return new CraftingPlan { RootItems = [root] };
    }

    private static PlanNode CraftOnly(int itemId, string name, PlanNode child)
    {
        var node = new PlanNode
        {
            ItemId = itemId,
            Name = name,
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.UserSelected,
            CanCraft = true,
            CanBuyFromMarket = false,
            Children = [child]
        };
        child.Parent = node;
        return node;
    }

    private static PlanNode Leaf(int itemId, string name, int quantity = 1) => new()
    {
        ItemId = itemId,
        Name = name,
        Quantity = quantity,
        Source = AcquisitionSource.MarketBuyNq,
        SourceReason = AcquisitionSourceReason.SystemDefault,
        CanCraft = false,
        CanBuyFromMarket = true
    };

    private static DetailedShoppingPlan Evidence(
        int itemId,
        string name,
        int quantity,
        params (string DataCenter, string World, long UnitPrice)[] worlds) => new()
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = quantity,
            WorldOptions = worlds.Select(world => World(world.DataCenter, world.World, quantity, world.UnitPrice)).ToList()
        };

    private static DetailedShoppingPlan EvidenceWithStack(
        int itemId,
        string name,
        int quantityNeeded,
        string dataCenter,
        string world,
        int stackQuantity,
        long unitPrice) => new()
        {
            ItemId = itemId,
            Name = name,
            QuantityNeeded = quantityNeeded,
            WorldOptions = [World(dataCenter, world, stackQuantity, unitPrice)]
        };

    private static WorldShoppingSummary World(
        string dataCenter,
        string world,
        int stackQuantity,
        long unitPrice) => new()
        {
            DataCenter = dataCenter,
            WorldName = world,
            TotalCost = stackQuantity * unitPrice,
            TotalQuantityPurchased = stackQuantity,
            HasSufficientStock = true,
            MarketDataQualityBucket = MarketDataQualityBucket.Current,
            MarketDataQualityScore = 100,
            Listings =
        [
            new ShoppingListingEntry
            {
                Quantity = stackQuantity,
                NeededFromStack = stackQuantity,
                PricePerUnit = unitPrice
            }
        ]
        };

    private static WorldShoppingSummary WorldWithListing(
        string dataCenter,
        string world,
        int quantity,
        long unitPrice,
        bool isHq) => new()
        {
            DataCenter = dataCenter,
            WorldName = world,
            TotalCost = quantity * unitPrice,
            TotalQuantityPurchased = quantity,
            HasSufficientStock = true,
            MarketDataQualityBucket = MarketDataQualityBucket.Current,
            MarketDataQualityScore = 100,
            Listings =
            [
                new ShoppingListingEntry
                {
                    Quantity = quantity,
                    NeededFromStack = quantity,
                    PricePerUnit = unitPrice,
                    IsHq = isHq
                }
            ]
        };
}
