using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public static class AcquisitionEvaluationSnapshotBuilder
{
    public static AcquisitionEvaluationSnapshot Build(
        CraftingPlan? plan,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<CoreMarketDataUnavailableItem> unavailableMarketItems,
        AcquisitionFilter filter,
        RecipeDemandProjection demandProjection)
    {
        var costContext = AcquisitionPlanningService.CreateCostContext(shoppingPlans);
        var marketCandidates = demandProjection.ToMarketAnalysisMaterialAggregates().ToList();
        var activeProcurementItems = demandProjection.ToActiveProcurementMaterialAggregates()
            .Where(item => item.TotalQuantity > 0)
            .ToList();
        var unavailableMarketItemIds = unavailableMarketItems
            .Select(item => item.ItemId)
            .ToHashSet();
        var rows = BuildRows(plan, demandProjection, costContext, unavailableMarketItemIds);
        var visibleRows = ApplyFilter(rows, filter).ToList();

#if DEBUG
        var parityReport = CompareWithLegacyTraversal(plan, shoppingPlans, unavailableMarketItems, demandProjection);
        if (!parityReport.Matches)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AcquisitionEvaluationParity] {parityReport.Mismatches.Count} mismatch(es): " +
                string.Join("; ", parityReport.Mismatches.Select(mismatch => mismatch.ToString())));
        }
#endif

        return new AcquisitionEvaluationSnapshot(
            rows,
            visibleRows,
            marketCandidates,
            activeProcurementItems,
            costContext);
    }

    public static AcquisitionEvaluationParityReport CompareWithLegacyTraversal(
        CraftingPlan? plan,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<CoreMarketDataUnavailableItem> unavailableMarketItems,
        RecipeDemandProjection demandProjection)
    {
        var costContext = AcquisitionPlanningService.CreateCostContext(shoppingPlans);
        var unavailableMarketItemIds = unavailableMarketItems
            .Select(item => item.ItemId)
            .ToHashSet();
        var expectedRows = BuildLegacyRows(plan, costContext, unavailableMarketItemIds);
        var actualRows = BuildRows(plan, demandProjection, costContext, unavailableMarketItemIds);
        var mismatches = new List<AcquisitionEvaluationParityMismatch>();

        CompareRows(expectedRows, actualRows, mismatches);
        CompareMaterialAggregates(
            AcquisitionEvaluationParityView.MarketAnalysisCandidate,
            AcquisitionPlanningService.GetMarketAnalysisCandidates(plan),
            demandProjection.ToMarketAnalysisMaterialAggregates(),
            mismatches);
        CompareMaterialAggregates(
            AcquisitionEvaluationParityView.ActiveProcurementItem,
            AcquisitionPlanningService.GetActiveProcurementItems(plan).Where(item => item.TotalQuantity > 0).ToList(),
            demandProjection.ToActiveProcurementMaterialAggregates().Where(item => item.TotalQuantity > 0).ToList(),
            mismatches);

        return new AcquisitionEvaluationParityReport(mismatches);
    }

    private static List<DecisionRow> BuildRows(
        CraftingPlan? plan,
        RecipeDemandProjection demandProjection,
        AcquisitionCostContext costContext,
        IReadOnlySet<int> unavailableMarketItemIds)
    {
        if (plan == null)
        {
            return new List<DecisionRow>();
        }

        var nodesByNodeId = BuildNodeIndex(plan);
        var activeNodeIds = demandProjection.ActiveProcurementDemand
            .Select(row => row.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        var marketCandidateItemIds = demandProjection.MarketAnalysisCandidates
            .Select(row => row.ItemId)
            .ToHashSet();
        var rowsByItemId = new Dictionary<int, DecisionAggregate>();
        foreach (var demandRow in demandProjection.AllPlanDemand)
        {
            if (!nodesByNodeId.TryGetValue(demandRow.NodeId, out var node))
            {
                continue;
            }

            var isSuppressed = demandRow.SuppressedByItemId.HasValue;
            var isActiveProcurement = !isSuppressed && activeNodeIds.Contains(demandRow.NodeId);

            if (!rowsByItemId.TryGetValue(demandRow.ItemId, out var aggregate))
            {
                aggregate = new DecisionAggregate(
                    node,
                    DecisionRowReadState.FromDemandRow(demandRow),
                    marketCandidateItemIds.Contains(demandRow.ItemId),
                    isSuppressed);
                rowsByItemId[demandRow.ItemId] = aggregate;
            }
            else
            {
                aggregate.PreferPrimary(
                    node,
                    DecisionRowReadState.FromDemandRow(demandRow),
                    isSuppressed);
            }

            aggregate.Occurrences.Add(new DecisionOccurrence(
                node,
                demandRow.ParentNodeId ?? demandRow.NodeId,
                demandRow.ParentItemName ?? "Project item",
                demandRow.Quantity,
                demandRow.ParentOutputQuantity,
                isSuppressed,
                demandRow.SuppressedByItemName,
                isActiveProcurement));
        }

        if (rowsByItemId.Count == 0)
        {
            return new List<DecisionRow>();
        }

        return rowsByItemId.Values
            .Select(aggregate => ToDecisionRow(aggregate, costContext, unavailableMarketItemIds))
            .OrderBy(row => row.Node.Name)
            .ToList();
    }

    private static List<DecisionRow> BuildLegacyRows(
        CraftingPlan? plan,
        AcquisitionCostContext costContext,
        IReadOnlySet<int> unavailableMarketItemIds)
    {
        if (plan == null)
        {
            return new List<DecisionRow>();
        }

        var activeNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in plan.RootItems)
        {
            CollectLegacyActiveProcurement(root, activeNodeIds);
        }

        var marketCandidateItemIds = AcquisitionPlanningService.GetMarketAnalysisCandidates(plan)
            .Select(item => item.ItemId)
            .ToHashSet();
        var rowsByItemId = new Dictionary<int, DecisionAggregate>();
        foreach (var root in plan.RootItems)
        {
            CollectLegacyRows(
                root,
                rowsByItemId,
                activeNodeIds,
                marketCandidateItemIds,
                suppressingAncestor: null);
        }

        return rowsByItemId.Values
            .Select(aggregate => ToDecisionRow(aggregate, costContext, unavailableMarketItemIds))
            .OrderBy(row => row.Node.Name)
            .ToList();
    }

    private static void CollectLegacyActiveProcurement(PlanNode node, HashSet<string> activeNodeIds)
    {
        if (node.Source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq or AcquisitionSource.VendorBuy)
        {
            activeNodeIds.Add(node.NodeId);
            return;
        }

        if (node.Children.Count == 0)
        {
            activeNodeIds.Add(node.NodeId);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectLegacyActiveProcurement(child, activeNodeIds);
        }
    }

    private static void CollectLegacyRows(
        PlanNode node,
        Dictionary<int, DecisionAggregate> rowsByItemId,
        IReadOnlySet<string> activeNodeIds,
        IReadOnlySet<int> marketCandidateItemIds,
        SuppressingDecisionAncestor? suppressingAncestor)
    {
        if (!rowsByItemId.TryGetValue(node.ItemId, out var aggregate))
        {
            aggregate = new DecisionAggregate(
                node,
                DecisionRowReadState.FromNode(node),
                marketCandidateItemIds.Contains(node.ItemId),
                isSuppressed: suppressingAncestor != null);
            rowsByItemId[node.ItemId] = aggregate;
        }
        else
        {
            aggregate.PreferPrimary(
                node,
                DecisionRowReadState.FromNode(node),
                isSuppressed: suppressingAncestor != null);
        }

        var isSuppressed = suppressingAncestor != null;
        aggregate.Occurrences.Add(new DecisionOccurrence(
            node,
            node.Parent?.NodeId ?? node.NodeId,
            node.Parent?.Name ?? "Project item",
            node.Quantity,
            node.Quantity,
            isSuppressed,
            suppressingAncestor?.ItemName,
            !isSuppressed && activeNodeIds.Contains(node.NodeId)));

        var childSuppressingAncestor = suppressingAncestor;
        if (suppressingAncestor == null && IsLegacySuppressingDirectSource(node))
        {
            childSuppressingAncestor = new SuppressingDecisionAncestor(node.NodeId, node.ItemId, node.Name);
        }

        foreach (var child in node.Children)
        {
            CollectLegacyRows(child, rowsByItemId, activeNodeIds, marketCandidateItemIds, childSuppressingAncestor);
        }
    }

    private static bool IsLegacySuppressingDirectSource(PlanNode node)
    {
        return node.Source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq or AcquisitionSource.VendorBuy or AcquisitionSource.UnknownSource;
    }

    private static Dictionary<string, PlanNode> BuildNodeIndex(CraftingPlan plan)
    {
        var nodesByNodeId = new Dictionary<string, PlanNode>();
        foreach (var root in plan.RootItems)
        {
            CollectNode(root, nodesByNodeId);
        }

        return nodesByNodeId;
    }

    private static void CollectNode(PlanNode node, Dictionary<string, PlanNode> nodesByNodeId)
    {
        nodesByNodeId[node.NodeId] = node;

        foreach (var child in node.Children)
        {
            CollectNode(child, nodesByNodeId);
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
        var effectiveOccurrences = GetEffectiveOccurrences(aggregate.Occurrences);
        var totalQuantity = effectiveOccurrences.Sum(occurrence => occurrence.Quantity);
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
            aggregate.ReadState.NodeId,
            aggregate.ReadState.ItemId,
            aggregate.ReadState.ItemName,
            aggregate.ReadState.IconId,
            aggregate.ReadState.Source,
            aggregate.ReadState.SourceReason,
            aggregate.ReadState.MustBeHq,
            aggregate.ReadState.HasChildren,
            aggregate.ReadState.CanCraft,
            aggregate.ReadState.CanBeHq,
            aggregate.ReadState.Yield,
            aggregate.ReadState.CanBuyFromMarket,
            aggregate.ReadState.CanBuyFromVendor,
            aggregate.ReadState.UnitPrice,
            aggregate.ReadState.HqUnitPrice,
            aggregate.ReadState.VendorUnitPrice,
            aggregate.ReadState.VendorOptions,
            totalQuantity,
            activeQuantity,
            GetUsedInText(effectiveOccurrences),
            aggregate.Occurrences.Any(occurrence => occurrence.IsSuppressed),
            aggregate.Occurrences.All(occurrence => occurrence.IsSuppressed),
            suppressedBy,
            aggregate.Occurrences.Any(occurrence => occurrence.IsActiveProcurement),
            aggregate.Occurrences.Any(occurrence => !occurrence.IsSuppressed),
            aggregate.IsMarketCandidate,
            GetMarketEvidence(
                node.ItemId,
                marketPlan,
                unavailableMarketItemIds,
                aggregate.ReadState.Source == AcquisitionSource.MarketBuyHq),
            GetEstimatedCost(aggregate, effectiveOccurrences, totalQuantity, costContext));
    }

    private static string GetEstimatedCost(
        DecisionAggregate aggregate,
        IReadOnlyList<DecisionOccurrence> effectiveOccurrences,
        int quantity,
        AcquisitionCostContext costContext)
    {
        if (aggregate.ReadState.Source == AcquisitionSource.Craft)
        {
            decimal cost = 0;
            foreach (var occurrence in effectiveOccurrences)
            {
                if (AcquisitionPlanningService.TryGetAcquisitionCost(
                    occurrence.Node,
                    AcquisitionSource.Craft,
                    costContext,
                    occurrence.Quantity,
                    out var occurrenceCost))
                {
                    cost += occurrenceCost;
                }
            }

            return cost > 0 ? $"{cost:N0}g" : "-";
        }

        return AcquisitionEvaluationCostCalculator.TryGetCost(
            aggregate.ReadState,
            quantity,
            aggregate.ReadState.Source,
            costContext,
            out var directCost)
                ? $"{directCost:N0}g"
                : "-";
    }

    private static IReadOnlyList<DecisionOccurrence> GetEffectiveOccurrences(IReadOnlyList<DecisionOccurrence> occurrences)
    {
        var activeOccurrences = occurrences
            .Where(occurrence => !occurrence.IsSuppressed)
            .ToList();

        return activeOccurrences.Count > 0
            ? activeOccurrences
            : occurrences;
    }

    private static string GetMarketEvidence(
        int itemId,
        DetailedShoppingPlan? marketPlan,
        IReadOnlySet<int> unavailableMarketItemIds,
        bool hqOnly)
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

        if (MarketPurchaseCostProjectionService.IsUnsupportedProjectedCost(marketPlan, hqOnly: hqOnly))
        {
            return "Projected only";
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
            .GroupBy(occurrence => occurrence.ParentNodeId, StringComparer.Ordinal)
            .Select(group => group.First())
            .GroupBy(occurrence => occurrence.ParentItemName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.Key,
                Quantity = group.Sum(occurrence => occurrence.ParentOutputQuantity)
            })
            .OrderBy(usage => usage.Name == "Project item" ? 0 : 1)
            .ThenBy(usage => usage.Name)
            .ToList();

        return string.Join(", ", usages.Select(usage => $"{usage.Name} x{usage.Quantity}"));
    }

    private static void CompareRows(
        IReadOnlyList<DecisionRow> expected,
        IReadOnlyList<DecisionRow> actual,
        List<AcquisitionEvaluationParityMismatch> mismatches)
    {
        var expectedByItemId = expected.ToDictionary(row => row.Node.ItemId);
        var actualByItemId = actual.ToDictionary(row => row.Node.ItemId);

        foreach (var missing in expected.Where(row => !actualByItemId.ContainsKey(row.Node.ItemId)))
        {
            mismatches.Add(CreateMismatch(
                AcquisitionEvaluationParityView.Row,
                AcquisitionEvaluationParityField.MissingItem,
                missing.Node.ItemId,
                missing.Node.Name,
                "present",
                "missing"));
        }

        foreach (var extra in actual.Where(row => !expectedByItemId.ContainsKey(row.Node.ItemId)))
        {
            mismatches.Add(CreateMismatch(
                AcquisitionEvaluationParityView.Row,
                AcquisitionEvaluationParityField.ExtraItem,
                extra.Node.ItemId,
                extra.Node.Name,
                "missing",
                "present"));
        }

        foreach (var expectedRow in expected.Where(row => actualByItemId.ContainsKey(row.Node.ItemId)))
        {
            var actualRow = actualByItemId[expectedRow.Node.ItemId];
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.NodeId, expectedRow, expectedRow.NodeId, actualRow.NodeId, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.ItemName, expectedRow, expectedRow.ItemName, actualRow.ItemName, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.IconId, expectedRow, expectedRow.IconId, actualRow.IconId, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.Source, expectedRow, expectedRow.Source, actualRow.Source, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.SourceReason, expectedRow, expectedRow.SourceReason, actualRow.SourceReason, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.CanCraft, expectedRow, expectedRow.CanCraft, actualRow.CanCraft, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.CanBeHq, expectedRow, expectedRow.CanBeHq, actualRow.CanBeHq, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.Yield, expectedRow, expectedRow.Yield, actualRow.Yield, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.CanBuyFromMarket, expectedRow, expectedRow.CanBuyFromMarket, actualRow.CanBuyFromMarket, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.CanBuyFromVendor, expectedRow, expectedRow.CanBuyFromVendor, actualRow.CanBuyFromVendor, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.UnitPrice, expectedRow, expectedRow.UnitPrice, actualRow.UnitPrice, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.HqUnitPrice, expectedRow, expectedRow.HqUnitPrice, actualRow.HqUnitPrice, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.VendorUnitPrice, expectedRow, expectedRow.VendorUnitPrice, actualRow.VendorUnitPrice, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.TotalQuantity, expectedRow, expectedRow.TotalQuantity, actualRow.TotalQuantity, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.ActiveQuantity, expectedRow, expectedRow.ActiveQuantity, actualRow.ActiveQuantity, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.HasSuppressedOccurrences, expectedRow, expectedRow.HasSuppressedOccurrences, actualRow.HasSuppressedOccurrences, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.IsFullySuppressed, expectedRow, expectedRow.IsFullySuppressed, actualRow.IsFullySuppressed, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.SuppressedBy, expectedRow, string.Join("|", expectedRow.SuppressedBy), string.Join("|", actualRow.SuppressedBy), mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.IsActiveProcurement, expectedRow, expectedRow.IsActiveProcurement, actualRow.IsActiveProcurement, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.HasEditableOccurrences, expectedRow, expectedRow.HasEditableOccurrences, actualRow.HasEditableOccurrences, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.IsMarketCandidate, expectedRow, expectedRow.IsMarketCandidate, actualRow.IsMarketCandidate, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.MarketEvidence, expectedRow, expectedRow.MarketEvidence, actualRow.MarketEvidence, mismatches);
            CompareValue(AcquisitionEvaluationParityView.Row, AcquisitionEvaluationParityField.EstimatedCost, expectedRow, expectedRow.EstimatedCost, actualRow.EstimatedCost, mismatches);
        }
    }

    private static void CompareMaterialAggregates(
        AcquisitionEvaluationParityView view,
        IReadOnlyList<MaterialAggregate> expected,
        IReadOnlyList<MaterialAggregate> actual,
        List<AcquisitionEvaluationParityMismatch> mismatches)
    {
        var expectedByItemId = expected.ToDictionary(item => item.ItemId);
        var actualByItemId = actual.ToDictionary(item => item.ItemId);

        foreach (var missing in expected.Where(item => !actualByItemId.ContainsKey(item.ItemId)))
        {
            mismatches.Add(CreateMismatch(view, AcquisitionEvaluationParityField.MissingItem, missing.ItemId, missing.Name, "present", "missing"));
        }

        foreach (var extra in actual.Where(item => !expectedByItemId.ContainsKey(item.ItemId)))
        {
            mismatches.Add(CreateMismatch(view, AcquisitionEvaluationParityField.ExtraItem, extra.ItemId, extra.Name, "missing", "present"));
        }

        foreach (var expectedItem in expected.Where(item => actualByItemId.ContainsKey(item.ItemId)))
        {
            var actualItem = actualByItemId[expectedItem.ItemId];
            CompareValue(view, AcquisitionEvaluationParityField.IconId, expectedItem, expectedItem.IconId, actualItem.IconId, mismatches);
            CompareValue(view, AcquisitionEvaluationParityField.UnitPrice, expectedItem, expectedItem.UnitPrice, actualItem.UnitPrice, mismatches);
            CompareValue(view, AcquisitionEvaluationParityField.TotalQuantity, expectedItem, expectedItem.TotalQuantity, actualItem.TotalQuantity, mismatches);
            CompareValue(view, AcquisitionEvaluationParityField.RequiresHq, expectedItem, expectedItem.RequiresHq, actualItem.RequiresHq, mismatches);
            CompareValue(view, AcquisitionEvaluationParityField.SourceCount, expectedItem, expectedItem.Sources.Count, actualItem.Sources.Count, mismatches);

            var sourceCount = Math.Min(expectedItem.Sources.Count, actualItem.Sources.Count);
            for (var index = 0; index < sourceCount; index++)
            {
                var expectedSource = expectedItem.Sources[index];
                var actualSource = actualItem.Sources[index];
                CompareValue(view, AcquisitionEvaluationParityField.SourceParent, expectedItem, expectedSource.ParentItemName, actualSource.ParentItemName, mismatches);
                CompareValue(view, AcquisitionEvaluationParityField.SourceQuantity, expectedItem, expectedSource.Quantity, actualSource.Quantity, mismatches);
                CompareValue(view, AcquisitionEvaluationParityField.SourceCraftedFlag, expectedItem, expectedSource.IsCrafted, actualSource.IsCrafted, mismatches);
            }
        }
    }

    private static void CompareValue<T>(
        AcquisitionEvaluationParityView view,
        AcquisitionEvaluationParityField field,
        DecisionRow row,
        T expected,
        T actual,
        List<AcquisitionEvaluationParityMismatch> mismatches)
    {
        CompareValue(view, field, row.Node.ItemId, row.Node.Name, expected, actual, mismatches);
    }

    private static void CompareValue<T>(
        AcquisitionEvaluationParityView view,
        AcquisitionEvaluationParityField field,
        MaterialAggregate item,
        T expected,
        T actual,
        List<AcquisitionEvaluationParityMismatch> mismatches)
    {
        CompareValue(view, field, item.ItemId, item.Name, expected, actual, mismatches);
    }

    private static void CompareValue<T>(
        AcquisitionEvaluationParityView view,
        AcquisitionEvaluationParityField field,
        int itemId,
        string itemName,
        T expected,
        T actual,
        List<AcquisitionEvaluationParityMismatch> mismatches)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            return;
        }

        mismatches.Add(CreateMismatch(
            view,
            field,
            itemId,
            itemName,
            expected?.ToString() ?? string.Empty,
            actual?.ToString() ?? string.Empty));
    }

    private static AcquisitionEvaluationParityMismatch CreateMismatch(
        AcquisitionEvaluationParityView view,
        AcquisitionEvaluationParityField field,
        int itemId,
        string itemName,
        string expected,
        string actual)
    {
        return new AcquisitionEvaluationParityMismatch(view, field, itemId, itemName, expected, actual);
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
    public DecisionAggregate(
        PlanNode primaryNode,
        DecisionRowReadState readState,
        bool isMarketCandidate,
        bool isSuppressed)
    {
        PrimaryNode = primaryNode;
        ReadState = readState;
        IsMarketCandidate = isMarketCandidate;
        PrimaryIsSuppressed = isSuppressed;
    }

    public PlanNode PrimaryNode { get; private set; }
    public DecisionRowReadState ReadState { get; private set; }
    public bool IsMarketCandidate { get; }
    public bool PrimaryIsSuppressed { get; private set; }
    public List<DecisionOccurrence> Occurrences { get; } = new();

    public void PreferPrimary(PlanNode node, DecisionRowReadState readState, bool isSuppressed)
    {
        if (!PrimaryIsSuppressed || isSuppressed)
        {
            return;
        }

        PrimaryNode = node;
        ReadState = readState;
        PrimaryIsSuppressed = false;
    }
}

public sealed record DecisionRowReadState(
    string NodeId,
    int ItemId,
    string ItemName,
    int IconId,
    AcquisitionSource Source,
    AcquisitionSourceReason SourceReason,
    bool MustBeHq,
    bool HasChildren,
    bool CanCraft,
    bool CanBeHq,
    int Yield,
    bool CanBuyFromMarket,
    bool CanBuyFromVendor,
    decimal UnitPrice,
    decimal HqUnitPrice,
    decimal VendorUnitPrice,
    IReadOnlyList<RecipeDemandVendorOption> VendorOptions)
{
    public static DecisionRowReadState FromDemandRow(RecipeDemandRow row)
    {
        return new DecisionRowReadState(
            row.NodeId,
            row.ItemId,
            row.ItemName,
            row.IconId,
            row.Source,
            row.SourceReason,
            row.MustBeHq,
            row.HasChildren,
            row.CanCraft,
            row.CanBeHq,
            row.Yield,
            row.CanBuyFromMarket,
            row.CanBuyFromVendor,
            row.UnitPrice,
            row.HqUnitPrice,
            row.VendorUnitPrice,
            row.VendorOptions);
    }

    public static DecisionRowReadState FromNode(PlanNode node)
    {
        return new DecisionRowReadState(
            node.NodeId,
            node.ItemId,
            node.Name,
            node.IconId,
            node.Source,
            node.SourceReason,
            node.MustBeHq,
            node.Children.Count > 0,
            node.CanCraft,
            node.CanBeHq,
            node.Yield,
            node.CanBuyFromMarket,
            node.CanBuyFromVendor,
            node.MarketPrice,
            node.HqMarketPrice,
            node.VendorPrice,
            node.VendorOptions.Select(vendor => new RecipeDemandVendorOption(
                vendor.Name,
                vendor.Location,
                vendor.Price,
                vendor.Currency)).ToList());
    }

    public static DecisionRowReadState FromDecisionRow(DecisionRow row)
    {
        return new DecisionRowReadState(
            row.NodeId,
            row.ItemId,
            row.ItemName,
            row.IconId,
            row.Source,
            row.SourceReason,
            row.MustBeHq,
            row.HasChildren,
            row.CanCraft,
            row.CanBeHq,
            row.Yield,
            row.CanBuyFromMarket,
            row.CanBuyFromVendor,
            row.UnitPrice,
            row.HqUnitPrice,
            row.VendorUnitPrice,
            row.VendorOptions);
    }
}

public sealed record DecisionOccurrence(
    PlanNode Node,
    string ParentNodeId,
    string ParentItemName,
    int Quantity,
    int ParentOutputQuantity,
    bool IsSuppressed,
    string? SuppressedBy,
    bool IsActiveProcurement);

public sealed record DecisionRow(
    PlanNode Node,
    string NodeId,
    int ItemId,
    string ItemName,
    int IconId,
    AcquisitionSource Source,
    AcquisitionSourceReason SourceReason,
    bool MustBeHq,
    bool HasChildren,
    bool CanCraft,
    bool CanBeHq,
    int Yield,
    bool CanBuyFromMarket,
    bool CanBuyFromVendor,
    decimal UnitPrice,
    decimal HqUnitPrice,
    decimal VendorUnitPrice,
    IReadOnlyList<RecipeDemandVendorOption> VendorOptions,
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

public static class AcquisitionEvaluationCostCalculator
{
    public static bool TryGetCost(
        DecisionRow row,
        AcquisitionSource source,
        AcquisitionCostContext costContext,
        out decimal cost)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (source == AcquisitionSource.Craft)
        {
            return AcquisitionPlanningService.TryGetAcquisitionCost(
                row.Node,
                source,
                costContext,
                GetCostQuantity(row),
                out cost);
        }

        return TryGetCost(
            DecisionRowReadState.FromDecisionRow(row),
            GetCostQuantity(row),
            source,
            costContext,
            out cost);
    }

    public static int GetCostQuantity(DecisionRow row)
    {
        return row.TotalQuantity;
    }

    internal static bool TryGetCost(
        DecisionRowReadState readState,
        int quantity,
        AcquisitionSource source,
        AcquisitionCostContext costContext,
        out decimal cost)
    {
        ArgumentNullException.ThrowIfNull(readState);
        ArgumentNullException.ThrowIfNull(costContext);

        cost = source switch
        {
            AcquisitionSource.MarketBuyNq when readState.CanBuyFromMarket && !readState.MustBeHq => GetMarketBuyCost(
                readState,
                quantity,
                costContext,
                hqOnly: false),
            AcquisitionSource.MarketBuyHq when readState.CanBuyFromMarket && readState.CanBeHq => GetMarketBuyCost(
                readState,
                quantity,
                costContext,
                hqOnly: true),
            AcquisitionSource.VendorBuy when readState.CanBuyFromVendor => readState.VendorUnitPrice * quantity,
            _ => 0
        };

        return cost > 0;
    }

    private static decimal GetMarketBuyCost(
        DecisionRowReadState readState,
        int quantity,
        AcquisitionCostContext costContext,
        bool hqOnly)
    {
        if (costContext.TryGetShoppingPlan(readState.ItemId, out var shoppingPlan))
        {
            var estimate = MarketPurchaseCostProjectionService.Estimate(
                shoppingPlan,
                quantity,
                hqOnly,
                includeVendor: false);
            if (estimate.HasCost)
            {
                return estimate.Cost;
            }

            return 0;
        }

        return (hqOnly ? readState.HqUnitPrice : readState.UnitPrice) * quantity;
    }
}

public enum AcquisitionFilter
{
    All,
    Active,
    Market,
    Suppressed
}

public enum AcquisitionEvaluationParityView
{
    Row,
    MarketAnalysisCandidate,
    ActiveProcurementItem
}

public enum AcquisitionEvaluationParityField
{
    MissingItem,
    ExtraItem,
    NodeId,
    ItemName,
    IconId,
    Source,
    SourceReason,
    CanCraft,
    CanBeHq,
    Yield,
    CanBuyFromMarket,
    CanBuyFromVendor,
    UnitPrice,
    HqUnitPrice,
    VendorUnitPrice,
    TotalQuantity,
    ActiveQuantity,
    UsedIn,
    HasSuppressedOccurrences,
    IsFullySuppressed,
    SuppressedBy,
    IsActiveProcurement,
    HasEditableOccurrences,
    IsMarketCandidate,
    MarketEvidence,
    EstimatedCost,
    RequiresHq,
    SourceCount,
    SourceQuantity,
    SourceParent,
    SourceCraftedFlag
}

public sealed record AcquisitionEvaluationParityReport(
    IReadOnlyList<AcquisitionEvaluationParityMismatch> Mismatches)
{
    public bool Matches => Mismatches.Count == 0;
}

public sealed record AcquisitionEvaluationParityMismatch(
    AcquisitionEvaluationParityView View,
    AcquisitionEvaluationParityField Field,
    int ItemId,
    string ItemName,
    string Expected,
    string Actual);

internal sealed record SuppressingDecisionAncestor(string NodeId, int ItemId, string ItemName);
