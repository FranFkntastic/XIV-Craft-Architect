using FFXIV_Craft_Architect.Core.Models;

using AcquisitionFrontierBuildResult = FFXIV_Craft_Architect.Core.Services.JointAcquisitionRouteOptimizationService.AcquisitionFrontierBuildResult;
using AcquisitionVariant = FFXIV_Craft_Architect.Core.Services.JointAcquisitionRouteOptimizationService.AcquisitionVariant;

namespace FFXIV_Craft_Architect.Core.Services;

internal static class AcquisitionVariantFrontierBuilder
{
    internal const int MaxRetainedFrontierPlans = 1_024;
    internal const int MaxCombinationEvaluationsPerMerge = MaxRetainedFrontierPlans * 2;
    private const int ProgressReportInterval = 1_024;
    private const int MaxDominanceCandidates = 256;

    public static AcquisitionFrontierBuildResult Build(
        CraftingPlan plan,
        IReadOnlyDictionary<int, long> lowerBoundUnitCosts,
        CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var searchContext = new FrontierSearchContext(progress);
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

        var frontier = Prune(combined, lowerBoundUnitCosts, searchContext);
        var current = BuildCurrentPlanVariant(plan, ct);
        if (frontier.All(candidate => !string.Equals(
                candidate.DecisionKey,
                current.DecisionKey,
                StringComparison.Ordinal)))
        {
            frontier.Add(current);
        }

        return new AcquisitionFrontierBuildResult(
            frontier,
            searchContext.WasTruncated,
            searchContext.CombinationEvaluations);
    }

    internal static long EstimateMaximumWorkUnits(
        CraftingPlan plan,
        int maxTravelRouteEvaluations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var combinedCount = 1L;
        var combinationEvaluations = 0L;
        foreach (var root in plan.RootItems)
        {
            var rootEstimate = EstimateNode(root);
            combinationEvaluations = checked(
                combinationEvaluations + rootEstimate.CombinationEvaluations);
            var product = checked(combinedCount * rootEstimate.VariantCount);
            combinationEvaluations = checked(
                combinationEvaluations + Math.Min(product, MaxCombinationEvaluationsPerMerge));
            combinedCount = Math.Min(product, MaxRetainedFrontierPlans);
        }

        return checked(combinationEvaluations + combinedCount + 1L + maxTravelRouteEvaluations);
    }

    private static FrontierEstimate EstimateNode(PlanNode node)
    {
        var variantCount = 0L;
        var combinationEvaluations = 0L;
        foreach (var source in GetAllowedSources(node))
        {
            if (source != AcquisitionSource.Craft)
            {
                variantCount++;
                continue;
            }

            var craftedCount = 1L;
            foreach (var child in node.Children)
            {
                var childEstimate = EstimateNode(child);
                combinationEvaluations = checked(
                    combinationEvaluations + childEstimate.CombinationEvaluations);
                var product = checked(craftedCount * childEstimate.VariantCount);
                combinationEvaluations = checked(
                    combinationEvaluations + Math.Min(product, MaxCombinationEvaluationsPerMerge));
                craftedCount = Math.Min(product, MaxRetainedFrontierPlans);
            }
            variantCount = checked(variantCount + craftedCount);
        }

        return new FrontierEstimate(
            Math.Min(variantCount, MaxRetainedFrontierPlans),
            combinationEvaluations);
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
        if (product == 0)
        {
            return [];
        }

        var evaluationLimit = (int)Math.Min(product, MaxCombinationEvaluationsPerMerge);
        if (product > evaluationLimit)
        {
            searchContext.WasTruncated = true;
        }

        var combined = new List<AcquisitionVariant>(evaluationLimit);
        // Diagonal traversal gives every retained left variant equal access to the
        // bounded product instead of exhausting the first rows of the Cartesian set.
        for (var diagonal = 0; combined.Count < evaluationLimit; diagonal++)
        {
            for (var leftIndex = 0; leftIndex < left.Count && combined.Count < evaluationLimit; leftIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var rightIndex = (leftIndex + diagonal) % right.Count;
                combined.Add(left[leftIndex].Combine(right[rightIndex]));
                searchContext.RecordCombinationEvaluation();
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

        if (distinct.Count > MaxRetainedFrontierPlans)
        {
            searchContext.WasTruncated = true;
            var cheapest = distinct
                .OrderBy(value => EstimateLowerBound(value, lowerBoundUnitCosts))
                .ThenBy(value => value.DecisionKey, StringComparer.Ordinal)
                .Take(768);
            var leastComplex = distinct
                .OrderBy(value => value.MarketDemand.Count)
                .ThenBy(value => EstimateLowerBound(value, lowerBoundUnitCosts))
                .ThenBy(value => value.DecisionKey, StringComparer.Ordinal)
                .Take(128);
            var smallestDemand = distinct
                .OrderBy(value => value.MarketDemand.Values.Sum(demand => (long)demand.Quantity))
                .ThenBy(value => EstimateLowerBound(value, lowerBoundUnitCosts))
                .ThenBy(value => value.DecisionKey, StringComparer.Ordinal)
                .Take(128);
            var selected = cheapest
                .Concat(leastComplex)
                .Concat(smallestDemand)
                .DistinctBy(value => value.EconomicKey, StringComparer.Ordinal)
                .ToList();
            if (selected.Count < MaxRetainedFrontierPlans)
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

                    if (selected.Count == MaxRetainedFrontierPlans)
                    {
                        break;
                    }
                }
            }

            distinct = selected.Take(MaxRetainedFrontierPlans).ToList();
        }

        if (distinct.Count > MaxDominanceCandidates)
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

    private static AcquisitionVariant BuildCurrentPlanVariant(CraftingPlan plan, CancellationToken ct) =>
        BuildRealizedPlanVariant(plan, decisions: null, ct)!;

    internal static AcquisitionVariant? BuildRealizedPlanVariant(
        CraftingPlan plan,
        IReadOnlyDictionary<string, AcquisitionSource>? decisions,
        CancellationToken ct)
    {
        var marketDemand = new Dictionary<int, JointAcquisitionRouteOptimizationService.MarketDemand>();
        var realizedDecisions = new Dictionary<string, AcquisitionSource>(StringComparer.Ordinal);
        var fixedGilCost = 0L;
        foreach (var root in plan.RootItems)
        {
            ct.ThrowIfCancellationRequested();
            if (!CollectRealizedNode(
                    root,
                    decisions,
                    marketDemand,
                    realizedDecisions,
                    ref fixedGilCost,
                    ct))
            {
                return null;
            }
        }

        return new AcquisitionVariant(marketDemand, fixedGilCost, realizedDecisions);
    }

    private static bool CollectRealizedNode(
        PlanNode node,
        IReadOnlyDictionary<string, AcquisitionSource>? decisions,
        Dictionary<int, JointAcquisitionRouteOptimizationService.MarketDemand> marketDemand,
        Dictionary<string, AcquisitionSource> realizedDecisions,
        ref long fixedGilCost,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var source = decisions?.GetValueOrDefault(node.NodeId) ?? node.Source;
        if (node.SourceReason == AcquisitionSourceReason.UserSelected && source != node.Source)
        {
            return false;
        }
        realizedDecisions[node.NodeId] = source;
        switch (source)
        {
            case AcquisitionSource.MarketBuyNq:
            case AcquisitionSource.MarketBuyHq:
                marketDemand[node.ItemId] = marketDemand
                    .GetValueOrDefault(node.ItemId)
                    .Add(node, source == AcquisitionSource.MarketBuyHq);
                return true;
            case AcquisitionSource.VendorBuy:
                fixedGilCost = Add(fixedGilCost, GetVendorCost(node));
                return true;
            case not AcquisitionSource.Craft:
                return true;
        }

        foreach (var child in node.Children)
        {
            if (!CollectRealizedNode(
                    child,
                    decisions,
                    marketDemand,
                    realizedDecisions,
                    ref fixedGilCost,
                    ct))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Dominates(AcquisitionVariant left, AcquisitionVariant right)
    {
        if (left.FixedGilCost > right.FixedGilCost)
        {
            return false;
        }

        var strictlyBetter = left.FixedGilCost < right.FixedGilCost;
        foreach (var (itemId, leftDemand) in left.MarketDemand)
        {
            var rightDemand = right.MarketDemand.GetValueOrDefault(itemId);
            if (leftDemand.Quantity > rightDemand.Quantity || leftDemand.HqQuantity > rightDemand.HqQuantity)
            {
                return false;
            }

            strictlyBetter |= leftDemand.Quantity < rightDemand.Quantity || leftDemand.HqQuantity < rightDemand.HqQuantity;
        }

        foreach (var (itemId, rightDemand) in right.MarketDemand)
        {
            if (left.MarketDemand.ContainsKey(itemId))
            {
                continue;
            }

            strictlyBetter |= rightDemand.Quantity > 0 || rightDemand.HqQuantity > 0;
        }

        return strictlyBetter;
    }

    private static long Add(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(int quantity, long price) =>
        quantity > 0 && price > long.MaxValue / quantity ? long.MaxValue : quantity * price;

    private static long ToLong(decimal value) => value >= long.MaxValue ? long.MaxValue : (long)value;

    private sealed class FrontierSearchContext(IProgress<string>? progress)
    {
        public bool WasTruncated { get; set; }

        public long CombinationEvaluations { get; private set; }

        public void RecordCombinationEvaluation()
        {
            CombinationEvaluations++;
            if (CombinationEvaluations % ProgressReportInterval == 0)
            {
                progress?.Report(
                    $"[stage] building acquisition frontier ({CombinationEvaluations:N0} combinations evaluated)...");
            }
        }
    }

    private readonly record struct FrontierEstimate(
        long VariantCount,
        long CombinationEvaluations);
}
