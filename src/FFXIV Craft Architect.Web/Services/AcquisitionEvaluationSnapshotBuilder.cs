using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public static class AcquisitionEvaluationSnapshotBuilder
{
    public static AcquisitionEvaluationSnapshot Build(
        CraftingPlan? plan,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<MarketDataUnavailableItem> unavailableMarketItems,
        AcquisitionFilter filter)
    {
        var costContext = AcquisitionPlanningService.CreateCostContext(shoppingPlans);
        var marketCandidates = AcquisitionPlanningService.GetMarketAnalysisCandidates(plan);
        var activeProcurementItems = AcquisitionPlanningService.GetActiveProcurementItems(plan)
            .Where(item => item.TotalQuantity > 0)
            .ToList();
        var unavailableMarketItemIds = unavailableMarketItems
            .Select(item => item.ItemId)
            .ToHashSet();
        var rows = BuildRows(plan, costContext, unavailableMarketItemIds);
        var visibleRows = ApplyFilter(rows, filter).ToList();

        return new AcquisitionEvaluationSnapshot(
            rows,
            visibleRows,
            marketCandidates,
            activeProcurementItems,
            costContext);
    }

    private static List<DecisionRow> BuildRows(
        CraftingPlan? plan,
        AcquisitionCostContext costContext,
        IReadOnlySet<int> unavailableMarketItemIds)
    {
        if (plan == null)
        {
            return new List<DecisionRow>();
        }

        var rowsByItemId = new Dictionary<int, DecisionAggregate>();
        foreach (var root in plan.RootItems)
        {
            AddDecisionRows(root, suppressedBy: null, rowsByItemId);
        }

        return rowsByItemId.Values
            .Select(aggregate => ToDecisionRow(aggregate, costContext, unavailableMarketItemIds))
            .OrderBy(row => row.Node.Name)
            .ToList();
    }

    private static void AddDecisionRows(PlanNode node, string? suppressedBy, Dictionary<int, DecisionAggregate> rowsByItemId)
    {
        var isSuppressed = !string.IsNullOrEmpty(suppressedBy);
        var isDirectSource = node.Source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq or AcquisitionSource.VendorBuy or AcquisitionSource.UnknownSource;
        var isActiveProcurement = !isSuppressed && (isDirectSource || !node.Children.Any());

        if (!rowsByItemId.TryGetValue(node.ItemId, out var aggregate))
        {
            aggregate = new DecisionAggregate(node);
            rowsByItemId[node.ItemId] = aggregate;
        }

        aggregate.Occurrences.Add(new DecisionOccurrence(
            node,
            node.Parent?.Name ?? "Project item",
            node.Quantity,
            isSuppressed,
            suppressedBy,
            isActiveProcurement));

        var childSuppression = isSuppressed
            ? suppressedBy
            : isDirectSource
                ? node.Name
                : null;

        foreach (var child in node.Children)
        {
            AddDecisionRows(child, childSuppression, rowsByItemId);
        }
    }

    public static IEnumerable<DecisionRow> ApplyFilter(IEnumerable<DecisionRow> rows, AcquisitionFilter filter)
    {
        return filter switch
        {
            AcquisitionFilter.Active => rows.Where(row => row.IsActiveProcurement),
            AcquisitionFilter.Market => rows.Where(row => row.IsMarketCandidate),
            AcquisitionFilter.Suppressed => rows.Where(row => row.HasSuppressedOccurrences),
            _ => rows
        };
    }

    private static DecisionRow ToDecisionRow(
        DecisionAggregate aggregate,
        AcquisitionCostContext costContext,
        IReadOnlySet<int> unavailableMarketItemIds)
    {
        var node = aggregate.PrimaryNode;
        var activeQuantity = aggregate.Occurrences
            .Where(occurrence => occurrence.IsActiveProcurement)
            .Sum(occurrence => occurrence.Quantity);
        var totalQuantity = aggregate.Occurrences.Sum(occurrence => occurrence.Quantity);
        var suppressedBy = aggregate.Occurrences
            .Where(occurrence => occurrence.IsSuppressed)
            .Select(occurrence => occurrence.SuppressedBy)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        costContext.TryGetShoppingPlan(node.ItemId, out var marketPlan);

        return new DecisionRow(
            node,
            totalQuantity,
            activeQuantity,
            GetUsedInText(aggregate.Occurrences),
            aggregate.Occurrences.Any(occurrence => occurrence.IsSuppressed),
            aggregate.Occurrences.All(occurrence => occurrence.IsSuppressed),
            suppressedBy,
            aggregate.Occurrences.Any(occurrence => occurrence.IsActiveProcurement),
            aggregate.Occurrences.Any(occurrence => !occurrence.IsSuppressed),
            node.CanBuyFromMarket,
            GetMarketEvidence(node.ItemId, marketPlan, unavailableMarketItemIds),
            GetEstimatedCost(aggregate.Occurrences, costContext));
    }

    private static string GetEstimatedCost(IReadOnlyList<DecisionOccurrence> occurrences, AcquisitionCostContext costContext)
    {
        var primaryNode = occurrences.FirstOrDefault()?.Node;
        if (primaryNode == null)
        {
            return "-";
        }

        return AcquisitionPlanningService.TryGetAcquisitionCost(primaryNode, primaryNode.Source, costContext, out var cost)
            ? $"{cost:N0}g"
            : "-";
    }

    private static string GetMarketEvidence(
        int itemId,
        DetailedShoppingPlan? marketPlan,
        IReadOnlySet<int> unavailableMarketItemIds)
    {
        if (marketPlan == null)
        {
            if (unavailableMarketItemIds.Contains(itemId))
            {
                return "Needs data";
            }

            return "Not analyzed";
        }

        if (!string.IsNullOrEmpty(marketPlan.Error))
        {
            return "Needs data";
        }

        if (marketPlan.RecommendedWorld != null)
        {
            return $"{marketPlan.RecommendedWorld.WorldName} - {marketPlan.RecommendedWorld.TotalQuantityPurchased}/{marketPlan.QuantityNeeded}";
        }

        if (marketPlan.RecommendedSplit?.Any() == true)
        {
            return $"{marketPlan.RecommendedSplit.Count} world split";
        }

        return "Analyzed";
    }

    private static string GetUsedInText(IReadOnlyList<DecisionOccurrence> occurrences)
    {
        var usages = occurrences
            .GroupBy(occurrence => occurrence.ParentItemName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.Key,
                Quantity = group.Sum(occurrence => occurrence.Quantity)
            })
            .OrderBy(usage => usage.Name == "Project item" ? 0 : 1)
            .ThenBy(usage => usage.Name)
            .ToList();

        return string.Join(", ", usages.Select(usage => $"{usage.Name} x{usage.Quantity}"));
    }
}

public sealed record AcquisitionEvaluationSnapshot(
    List<DecisionRow> Rows,
    List<DecisionRow> VisibleRows,
    List<MaterialAggregate> MarketAnalysisCandidates,
    List<MaterialAggregate> ActiveProcurementItems,
    AcquisitionCostContext CostContext);

internal sealed class DecisionAggregate
{
    public DecisionAggregate(PlanNode primaryNode)
    {
        PrimaryNode = primaryNode;
    }

    public PlanNode PrimaryNode { get; }
    public List<DecisionOccurrence> Occurrences { get; } = new();
}

public sealed record DecisionOccurrence(
    PlanNode Node,
    string ParentItemName,
    int Quantity,
    bool IsSuppressed,
    string? SuppressedBy,
    bool IsActiveProcurement);

public sealed record DecisionRow(
    PlanNode Node,
    int TotalQuantity,
    int ActiveQuantity,
    string UsedIn,
    bool HasSuppressedOccurrences,
    bool IsFullySuppressed,
    IReadOnlyList<string> SuppressedBy,
    bool IsActiveProcurement,
    bool HasEditableOccurrences,
    bool IsMarketCandidate,
    string MarketEvidence,
    string EstimatedCost);

public enum AcquisitionFilter
{
    All,
    Active,
    Market,
    Suppressed
}
