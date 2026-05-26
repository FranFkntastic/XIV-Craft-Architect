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

    public static decimal CalculateCraftCost(
        PlanNode node,
        IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        var planByItemId = shoppingPlans
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());

        return CalculateCraftCost(node, planByItemId);
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
        if (node.Source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq &&
            planByItemId.TryGetValue(node.ItemId, out var shoppingPlan) &&
            TryGetEvidenceCost(shoppingPlan, node.Quantity, out var evidenceCost))
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

    private static bool TryGetEvidenceCost(
        DetailedShoppingPlan shoppingPlan,
        int quantity,
        out decimal cost)
    {
        var totalCost = shoppingPlan.RecommendedWorld?.TotalCost ?? shoppingPlan.SplitTotalCost;
        if (totalCost == null || shoppingPlan.QuantityNeeded <= 0)
        {
            cost = 0;
            return false;
        }

        cost = quantity == shoppingPlan.QuantityNeeded
            ? totalCost.Value
            : totalCost.Value * quantity / shoppingPlan.QuantityNeeded;
        return true;
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
