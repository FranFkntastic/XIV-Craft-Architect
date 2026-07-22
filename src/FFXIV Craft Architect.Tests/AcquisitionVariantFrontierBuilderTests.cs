using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class AcquisitionVariantFrontierBuilderTests
{
    [Fact]
    public void Build_SaturatedProductBoundsWorkAndRetainsCurrentDecisions()
    {
        var plan = CreateSaturatedPlan(10);
        var costs = EnumerateNodes(plan)
            .ToDictionary(node => node.ItemId, node => 100L + node.ItemId);

        var result = AcquisitionVariantFrontierBuilder.Build(plan, costs, CancellationToken.None);

        Assert.True(result.WasTruncated);
        Assert.InRange(
            result.Variants.Count,
            1,
            AcquisitionVariantFrontierBuilder.MaxRetainedFrontierPlans + 1);
        Assert.InRange(
            result.CombinationEvaluations,
            1,
            plan.RootItems.Count * 2L * AcquisitionVariantFrontierBuilder.MaxCombinationEvaluationsPerMerge);

        var expectedCurrentKey = string.Join(
            ';',
            plan.RootItems
                .OrderBy(node => node.NodeId, StringComparer.Ordinal)
                .Select(node => $"{node.NodeId}:{(int)AcquisitionSource.VendorBuy}"));
        Assert.Contains(result.Variants, variant => variant.DecisionKey == expectedCurrentKey);
    }

    [Fact]
    public void Build_SaturatedProductIsDeterministic()
    {
        var plan = CreateSaturatedPlan(10);
        var costs = EnumerateNodes(plan)
            .ToDictionary(node => node.ItemId, node => 100L + node.ItemId);

        var first = AcquisitionVariantFrontierBuilder.Build(plan, costs, CancellationToken.None);
        var second = AcquisitionVariantFrontierBuilder.Build(plan, costs, CancellationToken.None);

        Assert.Equal(first.CombinationEvaluations, second.CombinationEvaluations);
        Assert.Equal(
            first.Variants.Select(variant => (variant.EconomicKey, variant.DecisionKey)),
            second.Variants.Select(variant => (variant.EconomicKey, variant.DecisionKey)));
    }

    [Fact]
    public void Build_SaturatedProductReportsCombinationProgress()
    {
        var plan = CreateSaturatedPlan(10);
        var costs = EnumerateNodes(plan)
            .ToDictionary(node => node.ItemId, node => 100L + node.ItemId);
        var messages = new List<string>();

        AcquisitionVariantFrontierBuilder.Build(
            plan,
            costs,
            CancellationToken.None,
            new DelegateProgress<string>(messages.Add));

        Assert.Contains(
            messages,
            message => message.Contains("building acquisition frontier", StringComparison.Ordinal));
    }

    [Fact]
    public void EstimateMaximumWorkUnits_ChargesFixedRootsForPossibleWorkOnly()
    {
        var plan = new CraftingPlan
        {
            RootItems = Enumerable.Range(1, 122)
                .Select(index => new PlanNode
                {
                    ItemId = index,
                    Name = $"Fixed {index}",
                    Quantity = 1,
                    Source = AcquisitionSource.MarketBuyNq,
                    SourceReason = AcquisitionSourceReason.UserSelected,
                    CanBuyFromMarket = true
                })
                .ToList()
        };

        var estimate = AcquisitionVariantFrontierBuilder.EstimateMaximumWorkUnits(plan, 8);
        var actual = AcquisitionVariantFrontierBuilder.Build(
            plan,
            new Dictionary<int, long>(),
            CancellationToken.None);

        Assert.Equal(132, estimate);
        Assert.True(actual.CombinationEvaluations + actual.Variants.Count + 8 <= estimate);
    }

    [Fact]
    public void EstimateMaximumWorkUnits_BoundsSaturatedFrontier()
    {
        var plan = CreateSaturatedPlan(10);
        var costs = EnumerateNodes(plan)
            .ToDictionary(node => node.ItemId, node => 100L + node.ItemId);

        var estimate = AcquisitionVariantFrontierBuilder.EstimateMaximumWorkUnits(plan, 8);
        var actual = AcquisitionVariantFrontierBuilder.Build(plan, costs, CancellationToken.None);

        Assert.True(actual.CombinationEvaluations + actual.Variants.Count + 8 <= estimate);
    }

    private static CraftingPlan CreateSaturatedPlan(int rootCount)
    {
        var roots = Enumerable.Range(0, rootCount).Select(index =>
        {
            var child = new PlanNode
            {
                NodeId = $"child-{index:D2}",
                ItemId = 2_000 + index,
                Name = $"Ingredient {index}",
                Quantity = 1,
                Source = AcquisitionSource.MarketBuyNq,
                SourceReason = AcquisitionSourceReason.UserSelected,
                CanBuyFromMarket = true
            };
            var root = new PlanNode
            {
                NodeId = $"root-{index:D2}",
                ItemId = 1_000 + index,
                Name = $"Finished {index}",
                Quantity = 1,
                Source = AcquisitionSource.VendorBuy,
                SourceReason = AcquisitionSourceReason.SystemDefault,
                CanCraft = true,
                CanBuyFromMarket = true,
                CanBuyFromVendor = true,
                VendorPrice = 150,
                Children = [child]
            };
            child.Parent = root;
            child.ParentNodeId = root.NodeId;
            return root;
        }).ToList();
        return new CraftingPlan { RootItems = roots };
    }

    private static IEnumerable<PlanNode> EnumerateNodes(CraftingPlan plan)
    {
        foreach (var root in plan.RootItems)
        {
            yield return root;
            foreach (var child in root.Children)
            {
                yield return child;
            }
        }
    }

    private sealed class DelegateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
