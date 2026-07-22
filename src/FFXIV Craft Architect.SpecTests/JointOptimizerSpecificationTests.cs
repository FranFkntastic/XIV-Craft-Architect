using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class JointOptimizerSpecificationTests
{
    [Fact]
    public async Task LowestCostEndpointMatchesIndependentExhaustiveMakeBuyOracle()
    {
        int[] directCosts = [100, 80, 120];
        int[] craftCosts = [70, 95, 90];
        var roots = new List<PlanNode>();
        var evidence = new List<DetailedShoppingPlan>();
        for (var index = 0; index < directCosts.Length; index++)
        {
            var child = SpecificationFixtures.MarketNode(200 + index, $"Ingredient {index}", nodeId: $"child-{index}");
            child.SourceReason = AcquisitionSourceReason.SystemDefault;
            var root = SpecificationFixtures.MakeOrBuyNode(100 + index, $"Finished {index}", child, $"root-{index}");
            roots.Add(root);
            evidence.Add(SpecificationFixtures.Evidence(
                root.ItemId,
                root.Name,
                1,
                SpecificationFixtures.World("Aether", "Siren", 1, directCosts[index])));
            evidence.Add(SpecificationFixtures.Evidence(
                child.ItemId,
                child.Name,
                1,
                SpecificationFixtures.World("Aether", "Siren", 1, craftCosts[index])));
        }

        var oracle = ExhaustiveBinaryOracle(directCosts, craftCosts);
        var result = await SpecificationFixtures.JointService().OptimizeAsync(
            new CraftingPlan { RootItems = roots },
            evidence,
            SpecificationFixtures.Config(11),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(oracle.Cost, result.RouteDecision?.SelectedGilCost);
        Assert.Equal(oracle.Sources, result.OptimizedPlan.RootItems.Select(root => root.Source).ToArray());
    }

    [Fact]
    public async Task GlobalPremiumBoundaryAdmitsOneStopMakeBuyAlternative()
    {
        var childA = SpecificationFixtures.MarketNode(201, "Ingredient A", nodeId: "ingredient-a");
        var childB = SpecificationFixtures.MarketNode(202, "Ingredient B", nodeId: "ingredient-b");
        childA.SourceReason = AcquisitionSourceReason.SystemDefault;
        childB.SourceReason = AcquisitionSourceReason.SystemDefault;
        var root = new PlanNode
        {
            ItemId = 100,
            Name = "Finished",
            NodeId = "finished",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true,
            CanBuyFromMarket = true,
            Children = [childA, childB]
        };
        childA.Parent = root;
        childB.Parent = root;
        var evidence = new[]
        {
            SpecificationFixtures.Evidence(100, root.Name, 1, SpecificationFixtures.World("Aether", "Siren", 1, 102)),
            SpecificationFixtures.Evidence(201, childA.Name, 1, SpecificationFixtures.World("Aether", "Faerie", 1, 50)),
            SpecificationFixtures.Evidence(202, childB.Name, 1, SpecificationFixtures.World("Aether", "Gilgamesh", 1, 50))
        };

        var result = await SpecificationFixtures.JointService().OptimizeAsync(
            new CraftingPlan { RootItems = [root] },
            evidence,
            SpecificationFixtures.Config(10),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(100, result.RouteDecision?.CheapestGilCost);
        Assert.Equal(102, result.RouteDecision?.SelectedGilCost);
        Assert.Equal(AcquisitionSource.MarketBuyNq, result.OptimizedPlan.RootItems[0].Source);
    }

    [Fact]
    public async Task PartialMarketRouteCannotBecomeCompleteJointIncumbent()
    {
        var unfillable = SpecificationFixtures.MarketNode(300, "Unfillable", quantity: 5, hq: true);
        var fillable = SpecificationFixtures.MarketNode(301, "Fillable");
        var evidence = new[]
        {
            SpecificationFixtures.Evidence(300, unfillable.Name, 5, SpecificationFixtures.World("Aether", "Siren", 5, 10, hq: false)),
            SpecificationFixtures.Evidence(301, fillable.Name, 1, SpecificationFixtures.World("Aether", "Faerie", 1, 20))
        };

        var result = await SpecificationFixtures.JointService().OptimizeAsync(
            new CraftingPlan { RootItems = [unfillable, fillable] },
            evidence,
            SpecificationFixtures.Config(11),
            includeSplitPurchases: true,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(0, result.FeasiblePlanCount);
        Assert.Null(result.RouteDecision);
    }

    [Fact]
    public async Task TravelWorkLimitReturnsCompleteMarkedIncumbent()
    {
        var (plan, evidence) = BuildBinaryCorpus(3, lastItemRequiresHq: true);

        var result = await SpecificationFixtures.JointService().OptimizeAsync(
            plan,
            evidence,
            SpecificationFixtures.Config(0),
            includeSplitPurchases: true,
            new MarketAnalysisExecutionOptions
            {
                YieldEveryItems = 0,
                ProgressEveryItems = 0,
                MaxTravelRouteEvaluations = 1
            });

        var decision = Assert.IsType<MarketRouteDecision>(result.RouteDecision);
        Assert.True(decision.TravelSearchWasTruncated);
        Assert.Equal(1, decision.TravelRoutesEvaluated);
        Assert.Equal(
            [(1_000, 1, false), (1_001, 1, false), (1_002, 1, true)],
            result.ActiveProcurementItems
                .OrderBy(item => item.ItemId)
                .Select(item => (item.ItemId, item.TotalQuantity, item.RequiresHq))
                .ToArray());
        Assert.Equal(
            [(1_000, 1, 0, 100L), (1_001, 1, 0, 101L), (1_002, 1, 1, 102L)],
            result.ShoppingPlans
                .OrderBy(plan => plan.ItemId)
                .Select(plan => (
                    plan.ItemId,
                    plan.QuantityNeeded,
                    plan.HqQuantityNeeded,
                    plan.RecommendedWorld?.TotalCost ?? -1L))
                .ToArray());
        Assert.Equal(303L, decision.SelectedGilCost);
    }

    [Fact]
    public async Task TruncatedAcquisitionFrontierIsDeterministicAcrossRuns()
    {
        var (firstPlan, evidence) = BuildBinaryCorpus(13);
        var first = await SpecificationFixtures.JointService().OptimizeAsync(
            firstPlan,
            evidence,
            SpecificationFixtures.Config(11),
            includeSplitPurchases: false,
            MarketAnalysisExecutionOptions.Synchronous);
        var (secondPlan, _) = BuildBinaryCorpus(13);
        var second = await SpecificationFixtures.JointService().OptimizeAsync(
            secondPlan,
            evidence,
            SpecificationFixtures.Config(11),
            includeSplitPurchases: false,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.True(first.RouteDecision?.AcquisitionSearchWasTruncated);
        Assert.Equal(first.RouteDecision?.SelectedGilCost, second.RouteDecision?.SelectedGilCost);
        Assert.Equal(
            first.OptimizedPlan.RootItems.Select(root => root.Source),
            second.OptimizedPlan.RootItems.Select(root => root.Source));
    }

    [Fact]
    public async Task NonselectedTruncatedInnerRouteStillMarksJointResultApproximate()
    {
        var children = Enumerable.Range(0, 8)
            .Select(index => SpecificationFixtures.MarketNode(500 + index, $"Ingredient {index}", nodeId: $"child-{index}"))
            .ToList();
        var root = new PlanNode
        {
            ItemId = 499,
            Name = "Direct winner",
            NodeId = "direct-winner",
            Quantity = 1,
            Source = AcquisitionSource.Craft,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanCraft = true,
            CanBuyFromMarket = true,
            Children = children
        };
        foreach (var child in children)
        {
            child.Parent = root;
        }

        var evidence = children.Select((child, itemIndex) => SpecificationFixtures.Evidence(
            child.ItemId,
            child.Name,
            1,
            Enumerable.Range(0, 8)
                .Select(worldIndex => SpecificationFixtures.World(
                    $"DC {worldIndex / 2}",
                    $"World {worldIndex}",
                    1,
                    100 + ((itemIndex * 17 + worldIndex * 11) % 73)))
                .ToArray())).ToList();
        evidence.Add(SpecificationFixtures.Evidence(
            root.ItemId,
            root.Name,
            1,
            SpecificationFixtures.World("Aether", "Siren", 1, 500)));

        var result = await SpecificationFixtures.JointService().OptimizeAsync(
            new CraftingPlan { RootItems = [root] },
            evidence,
            SpecificationFixtures.Config(0),
            includeSplitPurchases: false,
            MarketAnalysisExecutionOptions.Synchronous);

        Assert.Equal(AcquisitionSource.MarketBuyNq, result.OptimizedPlan.RootItems[0].Source);
        Assert.True(result.RouteDecision?.RouteSearchWasTruncated);
        Assert.True(result.SearchWasTruncated);
    }

    private static (long Cost, AcquisitionSource[] Sources) ExhaustiveBinaryOracle(
        IReadOnlyList<int> directCosts,
        IReadOnlyList<int> craftCosts)
    {
        var bestCost = long.MaxValue;
        var bestMask = 0;
        for (var mask = 0; mask < 1 << directCosts.Count; mask++)
        {
            long cost = 0;
            for (var index = 0; index < directCosts.Count; index++)
            {
                cost += (mask & (1 << index)) == 0 ? craftCosts[index] : directCosts[index];
            }

            if (cost < bestCost)
            {
                bestCost = cost;
                bestMask = mask;
            }
        }

        var sources = Enumerable.Range(0, directCosts.Count)
            .Select(index => (bestMask & (1 << index)) == 0
                ? AcquisitionSource.Craft
                : AcquisitionSource.MarketBuyNq)
            .ToArray();
        return (bestCost, sources);
    }

    private static (CraftingPlan Plan, List<DetailedShoppingPlan> Evidence) BuildBinaryCorpus(
        int count,
        bool lastItemRequiresHq = false)
    {
        var roots = new List<PlanNode>();
        var evidence = new List<DetailedShoppingPlan>();
        for (var index = 0; index < count; index++)
        {
            var requiresHq = lastItemRequiresHq && index == count - 1;
            var child = SpecificationFixtures.MarketNode(
                2_000 + index,
                $"Ingredient {index}",
                hq: requiresHq,
                nodeId: $"ingredient-{index:D2}");
            child.SourceReason = AcquisitionSourceReason.SystemDefault;
            var root = SpecificationFixtures.MakeOrBuyNode(
                1_000 + index,
                $"Finished {index}",
                child,
                $"finished-{index:D2}");
            root.MustBeHq = requiresHq;
            root.CanBeHq = requiresHq;
            roots.Add(root);
            evidence.Add(SpecificationFixtures.Evidence(
                root.ItemId,
                root.Name,
                1,
                SpecificationFixtures.World("Aether", "Siren", 1, 100 + index, hq: requiresHq)));
            evidence.Add(SpecificationFixtures.Evidence(
                child.ItemId,
                child.Name,
                1,
                SpecificationFixtures.World("Primal", $"World {index}", 1, 70 + index, hq: requiresHq)));
        }

        return (new CraftingPlan { RootItems = roots }, evidence);
    }
}
