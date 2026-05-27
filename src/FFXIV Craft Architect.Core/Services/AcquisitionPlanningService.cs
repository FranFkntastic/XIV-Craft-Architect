using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Builds the distinct material views used by market analysis and procurement.
/// </summary>
public static class AcquisitionPlanningService
{
    public static List<MaterialAggregate> GetMarketAnalysisCandidates(CraftingPlan? plan)
    {
        if (plan == null)
        {
            return new List<MaterialAggregate>();
        }

        var aggregates = new Dictionary<int, MaterialAggregate>();
        foreach (var root in plan.RootItems)
        {
            CollectMarketCandidate(root, aggregates);
        }

        return aggregates.Values.OrderBy(item => item.Name).ToList();
    }

    public static List<MaterialAggregate> GetActiveProcurementItems(CraftingPlan? plan)
    {
        return plan?.AggregatedMaterials ?? new List<MaterialAggregate>();
    }

    public static List<DetailedShoppingPlan> FilterShoppingPlansForActiveProcurement(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        if (plan == null)
        {
            return new List<DetailedShoppingPlan>();
        }

        var activeItemIds = GetActiveProcurementItems(plan)
            .Select(item => item.ItemId)
            .ToHashSet();

        return shoppingPlans
            .Where(shoppingPlan => activeItemIds.Contains(shoppingPlan.ItemId))
            .ToList();
    }

    public static ProcurementEvidenceSummary GetProcurementEvidenceSummary(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        if (plan == null)
        {
            return new ProcurementEvidenceSummary(0, 0, 0, 0, 0);
        }

        var activeItems = GetActiveProcurementItems(plan)
            .Where(item => item.TotalQuantity > 0)
            .ToList();
        var activeItemIds = activeItems
            .Select(item => item.ItemId)
            .ToHashSet();
        var planByItemId = shoppingPlans
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        var activeWithEvidence = activeItemIds.Count(itemId =>
            planByItemId.TryGetValue(itemId, out var shoppingPlan) && HasUsableEvidence(shoppingPlan));
        var candidateItemIds = GetMarketAnalysisCandidates(plan)
            .Select(item => item.ItemId)
            .ToHashSet();
        var suppressedCandidateCount = candidateItemIds.Count(itemId => !activeItemIds.Contains(itemId));

        return new ProcurementEvidenceSummary(
            activeItems.Count,
            planByItemId.Count,
            activeWithEvidence,
            activeItems.Count - activeWithEvidence,
            suppressedCandidateCount);
    }

    public static bool HasCompleteProcurementEvidence(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        var summary = GetProcurementEvidenceSummary(plan, shoppingPlans);
        return summary.HasCompleteActiveEvidence;
    }

    public static decimal CalculateCraftCost(
        PlanNode node,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        var planByItemId = shoppingPlans
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());

        return CalculateCraftCost(node, planByItemId);
    }

    public static int ApplyCheapestAcquisitionDefaults(
        CraftingPlan? plan,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        if (plan == null)
        {
            return 0;
        }

        var planByItemId = CreatePlanLookup(shoppingPlans);
        var changed = 0;
        foreach (var root in plan.RootItems)
        {
            changed += ApplyCheapestAcquisitionDefaults(root, planByItemId);
        }

        return changed;
    }

    public static AcquisitionSource? DetermineCheapestAcquisitionSource(
        PlanNode node,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        return DetermineCheapestAcquisitionSource(node, CreatePlanLookup(shoppingPlans));
    }

    public static bool TryGetAcquisitionCost(
        PlanNode node,
        AcquisitionSource source,
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        out decimal cost)
    {
        return TryGetAcquisitionCost(node, source, CreatePlanLookup(shoppingPlans), out cost);
    }

    public static bool TryGetMarketBoardPurchase(
        DetailedShoppingPlan? shoppingPlan,
        int quantity,
        out WorldShoppingSummary? world,
        out decimal cost)
    {
        world = null;
        cost = 0;

        if (shoppingPlan == null || quantity <= 0 || shoppingPlan.QuantityNeeded <= 0)
        {
            return false;
        }

        var hasSplitCost = TryGetMarketBoardSplitCost(shoppingPlan, quantity, out var splitCost);
        var recommendedWorld = shoppingPlan.RecommendedWorld;
        if (recommendedWorld != null &&
            IsMarketWorld(recommendedWorld) &&
            recommendedWorld.TotalQuantityPurchased >= quantity &&
            recommendedWorld.TotalCost > 0)
        {
            var recommendedCost = ScaleEvidenceCost(recommendedWorld.TotalCost, quantity, shoppingPlan.QuantityNeeded);
            if (hasSplitCost && splitCost < recommendedCost)
            {
                cost = splitCost;
                return true;
            }

            world = recommendedWorld;
            cost = recommendedCost;
            return true;
        }

        world = shoppingPlan.WorldOptions
            .Where(IsMarketWorld)
            .Where(option => option.TotalQuantityPurchased >= quantity && option.TotalCost > 0)
            .OrderBy(option => ScaleEvidenceCost(option.TotalCost, quantity, shoppingPlan.QuantityNeeded))
            .FirstOrDefault();

        if (world == null)
        {
            if (!hasSplitCost)
            {
                return false;
            }

            cost = splitCost;
            return true;
        }

        var worldCost = ScaleEvidenceCost(world.TotalCost, quantity, shoppingPlan.QuantityNeeded);
        if (hasSplitCost && splitCost < worldCost)
        {
            world = null;
            cost = splitCost;
            return true;
        }

        cost = worldCost;
        return true;
    }

    private static void CollectMarketCandidate(PlanNode node, Dictionary<int, MaterialAggregate> aggregates)
    {
        if (node.Quantity > 0 && node.CanBuyFromMarket)
        {
            AddToAggregation(node, aggregates);
        }

        foreach (var child in node.Children)
        {
            CollectMarketCandidate(child, aggregates);
        }
    }

    private static void AddToAggregation(PlanNode node, Dictionary<int, MaterialAggregate> aggregates)
    {
        if (!aggregates.TryGetValue(node.ItemId, out var aggregate))
        {
            aggregate = new MaterialAggregate
            {
                ItemId = node.ItemId,
                Name = node.Name,
                IconId = node.IconId,
                UnitPrice = node.MarketPrice,
                RequiresHq = node.MustBeHq
            };
            aggregates[node.ItemId] = aggregate;
        }

        aggregate.TotalQuantity += node.Quantity;
        aggregate.UnitPrice = node.MarketPrice;
        aggregate.RequiresHq = aggregate.RequiresHq || node.MustBeHq;
        aggregate.Sources.Add(new MaterialSource
        {
            ParentItemName = node.Parent?.Name ?? "Direct",
            Quantity = node.Quantity,
            IsCrafted = node.Children.Any()
        });
    }

    private static bool HasUsableEvidence(DetailedShoppingPlan shoppingPlan)
    {
        return string.IsNullOrWhiteSpace(shoppingPlan.Error) &&
            (shoppingPlan.RecommendedWorld != null ||
             shoppingPlan.RecommendedSplit?.Any() == true ||
             shoppingPlan.Vendors.Any());
    }

    private static IReadOnlyDictionary<int, DetailedShoppingPlan> CreatePlanLookup(IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        return shoppingPlans
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
    }

    private static int ApplyCheapestAcquisitionDefaults(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        var changed = 0;
        foreach (var child in node.Children)
        {
            changed += ApplyCheapestAcquisitionDefaults(child, planByItemId);
        }

        var bestSource = DetermineCheapestAcquisitionSource(node, planByItemId);
        if (bestSource.HasValue && node.Source != bestSource.Value)
        {
            node.Source = bestSource.Value;
            changed++;
        }

        node.EnsureValidAcquisitionSource();
        return changed;
    }

    private static AcquisitionSource? DetermineCheapestAcquisitionSource(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        var candidates = GetAcquisitionCostCandidates(node, planByItemId)
            .OrderBy(candidate => candidate.Cost)
            .ThenBy(candidate => GetSourceTieBreak(candidate.Source))
            .ToList();

        return candidates.Count == 0 ? null : candidates[0].Source;
    }

    private static IEnumerable<(AcquisitionSource Source, decimal Cost)> GetAcquisitionCostCandidates(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        foreach (var source in GetAvailableSources(node))
        {
            if (TryGetAcquisitionCost(node, source, planByItemId, out var cost))
            {
                yield return (source, cost);
            }
        }
    }

    private static IEnumerable<AcquisitionSource> GetAvailableSources(PlanNode node)
    {
        if (node.Children.Any() && node.CanCraft)
        {
            yield return AcquisitionSource.Craft;
        }

        if (node.CanBuyFromMarket)
        {
            if (!node.MustBeHq)
            {
                yield return AcquisitionSource.MarketBuyNq;
            }

            if (node.CanBeHq)
            {
                yield return AcquisitionSource.MarketBuyHq;
            }
        }

        if (node.CanBuyFromVendor)
        {
            yield return AcquisitionSource.VendorBuy;
        }
    }

    private static bool TryGetAcquisitionCost(
        PlanNode node,
        AcquisitionSource source,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId,
        out decimal cost)
    {
        cost = source switch
        {
            AcquisitionSource.Craft when node.Children.Any() && node.CanCraft => CalculateCraftCost(node, planByItemId),
            AcquisitionSource.MarketBuyNq when node.CanBuyFromMarket && !node.MustBeHq => GetMarketBuyCost(node, planByItemId),
            AcquisitionSource.MarketBuyHq when node.CanBuyFromMarket && node.CanBeHq => node.HqMarketPrice * node.Quantity,
            AcquisitionSource.VendorBuy when node.CanBuyFromVendor => node.VendorPrice * node.Quantity,
            _ => 0
        };

        return cost > 0;
    }

    private static decimal GetMarketBuyCost(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        return planByItemId.TryGetValue(node.ItemId, out var shoppingPlan) &&
            TryGetMarketBoardPurchase(shoppingPlan, node.Quantity, out _, out var evidenceCost)
                ? evidenceCost
                : node.MarketPrice * node.Quantity;
    }

    private static int GetSourceTieBreak(AcquisitionSource source)
    {
        return source switch
        {
            AcquisitionSource.VendorBuy => 0,
            AcquisitionSource.MarketBuyNq => 1,
            AcquisitionSource.MarketBuyHq => 2,
            AcquisitionSource.Craft => 3,
            _ => 4
        };
    }

    private static decimal CalculateCraftCost(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        if (!node.Children.Any())
        {
            return GetDirectAcquisitionCost(node, planByItemId);
        }

        decimal cost = 0;
        foreach (var child in node.Children)
        {
            cost += child.Source == AcquisitionSource.Craft
                ? CalculateCraftCost(child, planByItemId)
                : GetDirectAcquisitionCost(child, planByItemId);
        }

        return node.Yield > 1 ? cost / node.Yield : cost;
    }

    private static decimal GetDirectAcquisitionCost(
        PlanNode node,
        IReadOnlyDictionary<int, DetailedShoppingPlan> planByItemId)
    {
        if (node.Source is AcquisitionSource.MarketBuyNq &&
            planByItemId.TryGetValue(node.ItemId, out var shoppingPlan) &&
            TryGetMarketBoardPurchase(shoppingPlan, node.Quantity, out _, out var evidenceCost))
        {
            return evidenceCost;
        }

        return node.Source switch
        {
            AcquisitionSource.MarketBuyNq => node.MarketPrice * node.Quantity,
            AcquisitionSource.MarketBuyHq => node.HqMarketPrice * node.Quantity,
            AcquisitionSource.VendorBuy => node.VendorPrice * node.Quantity,
            _ => 0
        };
    }

    private static bool IsMarketWorld(WorldShoppingSummary? world)
    {
        return world != null &&
            !string.Equals(world.WorldName, MarketShoppingConstants.VendorWorldName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetMarketBoardSplitCost(DetailedShoppingPlan shoppingPlan, int quantity, out decimal cost)
    {
        cost = 0;
        if (shoppingPlan.RecommendedSplit?.Any() != true ||
            shoppingPlan.RecommendedSplit.Any(split =>
                string.Equals(split.WorldName, MarketShoppingConstants.VendorWorldName, StringComparison.OrdinalIgnoreCase)) ||
            shoppingPlan.SplitTotalCost is not { } splitTotalCost ||
            splitTotalCost <= 0)
        {
            return false;
        }

        cost = ScaleEvidenceCost(splitTotalCost, quantity, shoppingPlan.QuantityNeeded);
        return true;
    }

    private static decimal ScaleEvidenceCost(long totalCost, int quantity, int quantityNeeded)
    {
        return quantity == quantityNeeded
            ? totalCost
            : totalCost * quantity / quantityNeeded;
    }
}

public sealed record ProcurementEvidenceSummary(
    int ActiveProcurementItemCount,
    int AnalyzedCandidateCount,
    int ActiveItemsWithEvidence,
    int ActiveItemsMissingEvidence,
    int SuppressedMarketCandidateCount)
{
    public bool HasCompleteActiveEvidence => ActiveProcurementItemCount > 0 && ActiveItemsMissingEvidence == 0;
}
