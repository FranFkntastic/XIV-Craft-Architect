using FFXIV_Craft_Architect.Core.Models;

using AcquisitionFrontierBuildResult = FFXIV_Craft_Architect.Core.Services.JointAcquisitionRouteOptimizationService.AcquisitionFrontierBuildResult;
using AcquisitionVariant = FFXIV_Craft_Architect.Core.Services.JointAcquisitionRouteOptimizationService.AcquisitionVariant;

namespace FFXIV_Craft_Architect.Core.Services;

internal static class AcquisitionVariantFrontierBuilder
{
    private const int MaxFrontierPlans = 4_096;

    public static AcquisitionFrontierBuildResult Build(
        CraftingPlan plan,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts,
        CancellationToken ct)
    {
        var searchContext = new FrontierSearchContext();
        var combined = new List<AcquisitionVariant> { AcquisitionVariant.Empty };
        foreach (var root in plan.RootItems)
        {
            ct.ThrowIfCancellationRequested();
            combined = Combine(
                combined,
                BuildNodeVariants(root, lowerBoundUnitCosts, searchContext, ct),
                lowerBoundUnitCosts,
                searchContext,
                ct);
        }

        var current = BuildCurrentPlanVariant(plan);
        combined.Add(current);
        var frontier = Prune(combined, lowerBoundUnitCosts, searchContext);
        if (frontier.All(candidate => !string.Equals(
                candidate.EconomicKey,
                current.EconomicKey,
                StringComparison.Ordinal)))
        {
            frontier.Add(current);
        }

        return new AcquisitionFrontierBuildResult(frontier, searchContext.WasTruncated);
    }

    private static List<AcquisitionVariant> BuildNodeVariants(
        PlanNode node,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts,
        FrontierSearchContext searchContext,
        CancellationToken ct)
    {
        var variants = new List<AcquisitionVariant>();
        foreach (var source in GetAllowedSources(node))
        {
            ct.ThrowIfCancellationRequested();
            switch (source)
            {
                case AcquisitionSource.Craft:
                    {
                        var crafted = new List<AcquisitionVariant> { AcquisitionVariant.Empty };
                        foreach (var child in node.Children)
                        {
                            crafted = Combine(
                                crafted,
                                BuildNodeVariants(child, lowerBoundUnitCosts, searchContext, ct),
                                lowerBoundUnitCosts,
                                searchContext,
                                ct);
                        }

                        variants.AddRange(crafted.Select(value => value.WithDecision(node.NodeId, source)));
                        break;
                    }
                case AcquisitionSource.MarketBuyNq:
                case AcquisitionSource.MarketBuyHq:
                    variants.Add(AcquisitionVariant.Empty
                        .WithMarketDemand(node, source == AcquisitionSource.MarketBuyHq)
                        .WithDecision(node.NodeId, source));
                    break;
                case AcquisitionSource.VendorBuy:
                    variants.Add(AcquisitionVariant.Empty
                        .WithFixedCost(GetVendorCost(node))
                        .WithDecision(node.NodeId, source));
                    break;
                case AcquisitionSource.VendorSpecialCurrency:
                case AcquisitionSource.UnknownSource:
                    variants.Add(AcquisitionVariant.Empty.WithDecision(node.NodeId, source));
                    break;
            }
        }

        return Prune(variants, lowerBoundUnitCosts, searchContext);
    }

    private static IReadOnlyList<AcquisitionSource> GetAllowedSources(PlanNode node)
    {
        if (node.SourceReason == AcquisitionSourceReason.UserSelected)
        {
            return [node.Source];
        }

        var sources = new List<AcquisitionSource>();
        if (node.CanCraft && node.Children.Count > 0)
        {
            sources.Add(AcquisitionSource.Craft);
        }

        if (node.CanBuyFromMarket)
        {
            if (!node.MustBeHq)
            {
                sources.Add(AcquisitionSource.MarketBuyNq);
            }

            if (node.MustBeHq || node.CanBeHq)
            {
                sources.Add(AcquisitionSource.MarketBuyHq);
            }
        }

        if (node.CanBuyFromVendor && !node.MustBeHq && GetVendorCost(node) > 0)
        {
            sources.Add(AcquisitionSource.VendorBuy);
        }

        if (sources.Count == 0)
        {
            sources.Add(node.Source is AcquisitionSource.UnknownSource or AcquisitionSource.VendorSpecialCurrency
                ? node.Source
                : AcquisitionSource.UnknownSource);
        }

        return sources;
    }

    private static long GetVendorCost(PlanNode node)
    {
        var unitPrice = node.SelectedVendor?.Price ?? node.VendorPrice;
        if (unitPrice <= 0 || node.Quantity <= 0)
        {
            return 0;
        }

        return ToLong(decimal.Ceiling(unitPrice * node.Quantity));
    }

    private static List<AcquisitionVariant> Combine(
        IReadOnlyList<AcquisitionVariant> left,
        IReadOnlyList<AcquisitionVariant> right,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts,
        FrontierSearchContext searchContext,
        CancellationToken ct)
    {
        var product = (long)left.Count * right.Count;
        var combined = new List<AcquisitionVariant>((int)Math.Min(product, MaxFrontierPlans * 2L));
        foreach (var leftValue in left)
        {
            foreach (var rightValue in right)
            {
                ct.ThrowIfCancellationRequested();
                combined.Add(leftValue.Combine(rightValue));
                if (combined.Count >= MaxFrontierPlans * 2)
                {
                    combined = Prune(combined, lowerBoundUnitCosts, searchContext);
                }
            }
        }

        return Prune(combined, lowerBoundUnitCosts, searchContext);
    }

    private static List<AcquisitionVariant> Prune(
        IEnumerable<AcquisitionVariant> source,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts,
        FrontierSearchContext searchContext)
    {
        var distinct = source
            .GroupBy(value => value.EconomicKey, StringComparer.Ordinal)
            .Select(group => group.OrderBy(value => value.DecisionKey, StringComparer.Ordinal).First())
            .ToList();

        if (distinct.Count > MaxFrontierPlans)
        {
            searchContext.WasTruncated = true;
            var cheapest = distinct
                .OrderBy(value => EstimateLowerBound(value, lowerBoundUnitCosts))
                .ThenBy(value => value.DecisionKey, StringComparer.Ordinal)
                .Take(3_072);
            var leastComplex = distinct
                .OrderBy(value => value.MarketDemand.Count)
                .ThenBy(value => EstimateLowerBound(value, lowerBoundUnitCosts))
                .ThenBy(value => value.DecisionKey, StringComparer.Ordinal)
                .Take(512);
            var smallestDemand = distinct
                .OrderBy(value => value.MarketDemand.Values.Sum(demand => (long)demand.Quantity))
                .ThenBy(value => EstimateLowerBound(value, lowerBoundUnitCosts))
                .ThenBy(value => value.DecisionKey, StringComparer.Ordinal)
                .Take(512);
            var selected = cheapest
                .Concat(leastComplex)
                .Concat(smallestDemand)
                .DistinctBy(value => value.EconomicKey, StringComparer.Ordinal)
                .ToList();
            if (selected.Count < MaxFrontierPlans)
            {
                var selectedKeys = selected
                    .Select(value => value.EconomicKey)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var value in distinct
                             .OrderBy(value => EstimateLowerBound(value, lowerBoundUnitCosts))
                             .ThenBy(value => value.DecisionKey, StringComparer.Ordinal))
                {
                    if (selectedKeys.Add(value.EconomicKey))
                    {
                        selected.Add(value);
                    }

                    if (selected.Count == MaxFrontierPlans)
                    {
                        break;
                    }
                }
            }

            distinct = selected.Take(MaxFrontierPlans).ToList();
        }

        if (distinct.Count > 1_024)
        {
            return distinct.OrderBy(value => value.DecisionKey, StringComparer.Ordinal).ToList();
        }

        return distinct
            .Where(candidate => !distinct.Any(other =>
                !ReferenceEquals(candidate, other) && Dominates(other, candidate)))
            .OrderBy(value => value.DecisionKey, StringComparer.Ordinal)
            .ToList();
    }

    internal static long EstimateLowerBound(
        AcquisitionVariant variant,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts)
    {
        var total = variant.FixedGilCost;
        foreach (var (itemId, demand) in variant.MarketDemand)
        {
            var unitCost = lowerBoundUnitCosts.GetValueOrDefault(itemId, long.MaxValue);
            total = Add(total, SaturatingMultiply(demand.Quantity, unitCost));
        }

        return total;
    }

    private static AcquisitionVariant BuildCurrentPlanVariant(CraftingPlan plan)
    {
        var combined = AcquisitionVariant.Empty;
        foreach (var root in plan.RootItems)
        {
            combined = combined.Combine(BuildCurrentNodeVariant(root));
        }

        return combined;
    }

    private static AcquisitionVariant BuildCurrentNodeVariant(PlanNode node)
    {
        var variant = AcquisitionVariant.Empty.WithDecision(node.NodeId, node.Source);
        return node.Source switch
        {
            AcquisitionSource.Craft => node.Children.Aggregate(
                variant,
                (current, child) => current.Combine(BuildCurrentNodeVariant(child))),
            AcquisitionSource.MarketBuyNq => variant.WithMarketDemand(node, hq: false),
            AcquisitionSource.MarketBuyHq => variant.WithMarketDemand(node, hq: true),
            AcquisitionSource.VendorBuy => variant.WithFixedCost(GetVendorCost(node)),
            _ => variant
        };
    }

    private static bool Dominates(AcquisitionVariant left, AcquisitionVariant right)
    {
        if (left.FixedGilCost > right.FixedGilCost)
        {
            return false;
        }

        var allItems = left.MarketDemand.Keys.Concat(right.MarketDemand.Keys).Distinct();
        var strictlyBetter = left.FixedGilCost < right.FixedGilCost;
        foreach (var itemId in allItems)
        {
            var leftDemand = left.MarketDemand.GetValueOrDefault(itemId);
            var rightDemand = right.MarketDemand.GetValueOrDefault(itemId);
            if (leftDemand.Quantity > rightDemand.Quantity || leftDemand.HqQuantity > rightDemand.HqQuantity)
            {
                return false;
            }

            strictlyBetter |= leftDemand.Quantity < rightDemand.Quantity || leftDemand.HqQuantity < rightDemand.HqQuantity;
        }

        return strictlyBetter;
    }

    private static long Add(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(int quantity, long price) =>
        quantity > 0 && price > long.MaxValue / quantity ? long.MaxValue : quantity * price;

    private static long ToLong(decimal value) => value >= long.MaxValue ? long.MaxValue : (long)value;

    private sealed class FrontierSearchContext
    {
        public bool WasTruncated { get; set; }
    }
}
