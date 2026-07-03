using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class CoreAcquisitionEvaluationSnapshotBuilder
{
    public static CoreAcquisitionEvaluationSnapshot Build(
        CraftingPlan? plan,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlySet<int> unavailableMarketItemIds,
        CoreAcquisitionFilter filter,
        RecipeDemandProjection demandProjection)
    {
        var costContext = AcquisitionPlanningService.CreateCostContext(shoppingPlans);
        var marketCandidates = demandProjection.ToMarketAnalysisMaterialAggregates().ToList();
        var activeProcurementItems = demandProjection.ToActiveProcurementMaterialAggregates()
            .Where(item => item.TotalQuantity > 0)
            .ToList();
        var rows = BuildRows(plan, demandProjection, costContext, unavailableMarketItemIds);
        var visibleRows = ApplyFilter(rows, filter).ToList();

        return new CoreAcquisitionEvaluationSnapshot(
            rows,
            visibleRows,
            marketCandidates,
            activeProcurementItems,
            costContext);
    }

    public static CoreAcquisitionEvaluationParityReport CompareWithLegacyTraversal(
        CraftingPlan? plan,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlySet<int> unavailableMarketItemIds,
        RecipeDemandProjection demandProjection)
    {
        var costContext = AcquisitionPlanningService.CreateCostContext(shoppingPlans);
        var expectedRows = BuildLegacyRows(plan, costContext, unavailableMarketItemIds);
        var actualRows = BuildRows(plan, demandProjection, costContext, unavailableMarketItemIds);
        var mismatches = new List<CoreAcquisitionEvaluationParityMismatch>();

        CompareRows(expectedRows, actualRows, mismatches);
        CompareMaterialAggregates(
            CoreAcquisitionEvaluationParityView.MarketAnalysisCandidate,
            AcquisitionPlanningService.GetMarketAnalysisCandidates(plan),
            demandProjection.ToMarketAnalysisMaterialAggregates(),
            mismatches);
        CompareMaterialAggregates(
            CoreAcquisitionEvaluationParityView.ActiveProcurementItem,
            AcquisitionPlanningService.GetActiveProcurementItems(plan).Where(item => item.TotalQuantity > 0).ToList(),
            demandProjection.ToActiveProcurementMaterialAggregates().Where(item => item.TotalQuantity > 0).ToList(),
            mismatches);

        return new CoreAcquisitionEvaluationParityReport(mismatches);
    }

    public static IEnumerable<CoreDecisionRow> ApplyFilter(
        IEnumerable<CoreDecisionRow> rows,
        CoreAcquisitionFilter filter)
    {
        return filter switch
        {
            CoreAcquisitionFilter.Active => rows.Where(row => row.IsActiveProcurement),
            CoreAcquisitionFilter.Market => rows.Where(row => row.IsMarketCandidate),
            CoreAcquisitionFilter.Suppressed => rows.Where(row => row.HasSuppressedOccurrences),
            _ => rows
        };
    }

    private static List<CoreDecisionRow> BuildRows(
        CraftingPlan? plan,
        RecipeDemandProjection demandProjection,
        AcquisitionCostContext costContext,
        IReadOnlySet<int> unavailableMarketItemIds)
    {
        if (plan == null)
        {
            return [];
        }

        var nodesByNodeId = BuildNodeIndex(plan);
        var activeNodeIds = demandProjection.ActiveProcurementDemand
            .Select(row => row.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        var marketCandidateItemIds = demandProjection.MarketAnalysisCandidates
            .Select(row => row.ItemId)
            .ToHashSet();
        var rowsByItemId = new Dictionary<int, CoreDecisionAggregate>();
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
                aggregate = new CoreDecisionAggregate(
                    node,
                    CoreDecisionRowReadState.FromDemandRow(demandRow),
                    marketCandidateItemIds.Contains(demandRow.ItemId),
                    isSuppressed);
                rowsByItemId[demandRow.ItemId] = aggregate;
            }
            else
            {
                aggregate.PreferPrimary(
                    node,
                    CoreDecisionRowReadState.FromDemandRow(demandRow),
                    isSuppressed);
            }

            aggregate.Occurrences.Add(new CoreDecisionOccurrence(
                node,
                demandRow.ParentNodeId ?? demandRow.NodeId,
                demandRow.ParentItemName ?? "Project item",
                demandRow.Quantity,
                demandRow.ParentOutputQuantity,
                isSuppressed,
                demandRow.SuppressedByItemName,
                isActiveProcurement));
        }

        return rowsByItemId.Values
            .Select(aggregate => ToDecisionRow(aggregate, costContext, unavailableMarketItemIds))
            .OrderBy(row => row.Node.Name)
            .ToList();
    }

    private static List<CoreDecisionRow> BuildLegacyRows(
        CraftingPlan? plan,
        AcquisitionCostContext costContext,
        IReadOnlySet<int> unavailableMarketItemIds)
    {
        if (plan == null)
        {
            return [];
        }

        var activeNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in plan.RootItems)
        {
            CollectLegacyActiveProcurement(root, activeNodeIds);
        }

        var marketCandidateItemIds = AcquisitionPlanningService.GetMarketAnalysisCandidates(plan)
            .Select(item => item.ItemId)
            .ToHashSet();
        var rowsByItemId = new Dictionary<int, CoreDecisionAggregate>();
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
        Dictionary<int, CoreDecisionAggregate> rowsByItemId,
        IReadOnlySet<string> activeNodeIds,
        IReadOnlySet<int> marketCandidateItemIds,
        CoreSuppressingDecisionAncestor? suppressingAncestor)
    {
        if (!rowsByItemId.TryGetValue(node.ItemId, out var aggregate))
        {
            aggregate = new CoreDecisionAggregate(
                node,
                CoreDecisionRowReadState.FromNode(node),
                marketCandidateItemIds.Contains(node.ItemId),
                isSuppressed: suppressingAncestor != null);
            rowsByItemId[node.ItemId] = aggregate;
        }
        else
        {
            aggregate.PreferPrimary(
                node,
                CoreDecisionRowReadState.FromNode(node),
                isSuppressed: suppressingAncestor != null);
        }

        var isSuppressed = suppressingAncestor != null;
        aggregate.Occurrences.Add(new CoreDecisionOccurrence(
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
            childSuppressingAncestor = new CoreSuppressingDecisionAncestor(node.NodeId, node.ItemId, node.Name);
        }

        foreach (var child in node.Children)
        {
            CollectLegacyRows(child, rowsByItemId, activeNodeIds, marketCandidateItemIds, childSuppressingAncestor);
        }
    }

    private static bool IsLegacySuppressingDirectSource(PlanNode node)
    {
        return node.Source is AcquisitionSource.MarketBuyNq or
            AcquisitionSource.MarketBuyHq or
            AcquisitionSource.VendorBuy or
            AcquisitionSource.UnknownSource;
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

    private static CoreDecisionRow ToDecisionRow(
        CoreDecisionAggregate aggregate,
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
        var readState = SelectCheapestRepresentativeReadState(
            aggregate,
            effectiveOccurrences,
            totalQuantity,
            costContext);

        return new CoreDecisionRow(
            node,
            readState.NodeId,
            readState.ItemId,
            readState.ItemName,
            readState.IconId,
            readState.Source,
            readState.SourceReason,
            readState.MustBeHq,
            readState.HasChildren,
            readState.CanCraft,
            readState.CanBeHq,
            readState.Yield,
            readState.CanBuyFromMarket,
            readState.CanBuyFromVendor,
            readState.UnitPrice,
            readState.HqUnitPrice,
            readState.VendorUnitPrice,
            readState.VendorOptions,
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
                readState.Source == AcquisitionSource.MarketBuyHq),
            GetEstimatedCost(readState, effectiveOccurrences, totalQuantity, costContext));
    }

    private static CoreDecisionRowReadState SelectCheapestRepresentativeReadState(
        CoreDecisionAggregate aggregate,
        IReadOnlyList<CoreDecisionOccurrence> effectiveOccurrences,
        int quantity,
        AcquisitionCostContext costContext)
    {
        var readState = aggregate.ReadState;
        var candidates = GetRepresentativeSourceCandidates(readState)
            .Select(source => (
                Source: source,
                HasCost: TryGetRepresentativeCost(readState, effectiveOccurrences, quantity, costContext, source, out var cost),
                Cost: cost))
            .Where(candidate => candidate.HasCost)
            .OrderBy(candidate => candidate.Cost)
            .ThenBy(candidate => GetRepresentativeSourceTieBreak(candidate.Source))
            .ToList();

        return candidates.Count == 0
            ? readState
            : readState with { Source = candidates[0].Source };
    }

    private static IEnumerable<AcquisitionSource> GetRepresentativeSourceCandidates(CoreDecisionRowReadState readState)
    {
        if (readState.HasChildren && readState.CanCraft)
        {
            yield return AcquisitionSource.Craft;
        }

        if (readState.CanBuyFromMarket && !readState.MustBeHq)
        {
            yield return AcquisitionSource.MarketBuyNq;
        }

        if (readState.CanBuyFromMarket && readState.CanBeHq)
        {
            yield return AcquisitionSource.MarketBuyHq;
        }

        if (readState.CanBuyFromVendor)
        {
            yield return AcquisitionSource.VendorBuy;
        }
    }

    private static bool TryGetRepresentativeCost(
        CoreDecisionRowReadState readState,
        IReadOnlyList<CoreDecisionOccurrence> effectiveOccurrences,
        int quantity,
        AcquisitionCostContext costContext,
        AcquisitionSource source,
        out decimal cost)
    {
        if (source != AcquisitionSource.Craft)
        {
            return CoreAcquisitionEvaluationCostCalculator.TryGetCost(readState, quantity, source, costContext, out cost);
        }

        cost = 0;
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

        return cost > 0;
    }

    private static int GetRepresentativeSourceTieBreak(AcquisitionSource source)
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

    private static string GetEstimatedCost(
        CoreDecisionRowReadState readState,
        IReadOnlyList<CoreDecisionOccurrence> effectiveOccurrences,
        int quantity,
        AcquisitionCostContext costContext)
    {
        if (readState.Source == AcquisitionSource.Craft)
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

        return CoreAcquisitionEvaluationCostCalculator.TryGetCost(
            readState,
            quantity,
            readState.Source,
            costContext,
            out var directCost)
                ? $"{directCost:N0}g"
                : "-";
    }

    private static IReadOnlyList<CoreDecisionOccurrence> GetEffectiveOccurrences(
        IReadOnlyList<CoreDecisionOccurrence> occurrences)
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
            return unavailableMarketItemIds.Contains(itemId) ? "Needs data" : "Not analyzed";
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

    private static string GetUsedInText(IReadOnlyList<CoreDecisionOccurrence> occurrences)
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
        IReadOnlyList<CoreDecisionRow> expected,
        IReadOnlyList<CoreDecisionRow> actual,
        List<CoreAcquisitionEvaluationParityMismatch> mismatches)
    {
        var expectedByItemId = expected.ToDictionary(row => row.Node.ItemId);
        var actualByItemId = actual.ToDictionary(row => row.Node.ItemId);

        foreach (var missing in expected.Where(row => !actualByItemId.ContainsKey(row.Node.ItemId)))
        {
            mismatches.Add(CreateMismatch(
                CoreAcquisitionEvaluationParityView.Row,
                CoreAcquisitionEvaluationParityField.MissingItem,
                missing.Node.ItemId,
                missing.Node.Name,
                "present",
                "missing"));
        }

        foreach (var extra in actual.Where(row => !expectedByItemId.ContainsKey(row.Node.ItemId)))
        {
            mismatches.Add(CreateMismatch(
                CoreAcquisitionEvaluationParityView.Row,
                CoreAcquisitionEvaluationParityField.ExtraItem,
                extra.Node.ItemId,
                extra.Node.Name,
                "missing",
                "present"));
        }

        foreach (var expectedRow in expected.Where(row => actualByItemId.ContainsKey(row.Node.ItemId)))
        {
            var actualRow = actualByItemId[expectedRow.Node.ItemId];
            CompareValue(CoreAcquisitionEvaluationParityView.Row, CoreAcquisitionEvaluationParityField.NodeId, expectedRow, expectedRow.NodeId, actualRow.NodeId, mismatches);
            CompareValue(CoreAcquisitionEvaluationParityView.Row, CoreAcquisitionEvaluationParityField.ItemName, expectedRow, expectedRow.ItemName, actualRow.ItemName, mismatches);
            CompareValue(CoreAcquisitionEvaluationParityView.Row, CoreAcquisitionEvaluationParityField.Source, expectedRow, expectedRow.Source, actualRow.Source, mismatches);
            CompareValue(CoreAcquisitionEvaluationParityView.Row, CoreAcquisitionEvaluationParityField.TotalQuantity, expectedRow, expectedRow.TotalQuantity, actualRow.TotalQuantity, mismatches);
            CompareValue(CoreAcquisitionEvaluationParityView.Row, CoreAcquisitionEvaluationParityField.ActiveQuantity, expectedRow, expectedRow.ActiveQuantity, actualRow.ActiveQuantity, mismatches);
            CompareValue(CoreAcquisitionEvaluationParityView.Row, CoreAcquisitionEvaluationParityField.MarketEvidence, expectedRow, expectedRow.MarketEvidence, actualRow.MarketEvidence, mismatches);
            CompareValue(CoreAcquisitionEvaluationParityView.Row, CoreAcquisitionEvaluationParityField.EstimatedCost, expectedRow, expectedRow.EstimatedCost, actualRow.EstimatedCost, mismatches);
        }
    }

    private static void CompareMaterialAggregates(
        CoreAcquisitionEvaluationParityView view,
        IReadOnlyList<MaterialAggregate> expected,
        IReadOnlyList<MaterialAggregate> actual,
        List<CoreAcquisitionEvaluationParityMismatch> mismatches)
    {
        var expectedByItemId = expected.ToDictionary(item => item.ItemId);
        var actualByItemId = actual.ToDictionary(item => item.ItemId);

        foreach (var missing in expected.Where(item => !actualByItemId.ContainsKey(item.ItemId)))
        {
            mismatches.Add(CreateMismatch(view, CoreAcquisitionEvaluationParityField.MissingItem, missing.ItemId, missing.Name, "present", "missing"));
        }

        foreach (var extra in actual.Where(item => !expectedByItemId.ContainsKey(item.ItemId)))
        {
            mismatches.Add(CreateMismatch(view, CoreAcquisitionEvaluationParityField.ExtraItem, extra.ItemId, extra.Name, "missing", "present"));
        }

        foreach (var expectedItem in expected.Where(item => actualByItemId.ContainsKey(item.ItemId)))
        {
            var actualItem = actualByItemId[expectedItem.ItemId];
            CompareValue(view, CoreAcquisitionEvaluationParityField.TotalQuantity, expectedItem, expectedItem.TotalQuantity, actualItem.TotalQuantity, mismatches);
        }
    }

    private static void CompareValue<T>(
        CoreAcquisitionEvaluationParityView view,
        CoreAcquisitionEvaluationParityField field,
        CoreDecisionRow row,
        T expected,
        T actual,
        List<CoreAcquisitionEvaluationParityMismatch> mismatches)
    {
        CompareValue(view, field, row.Node.ItemId, row.Node.Name, expected, actual, mismatches);
    }

    private static void CompareValue<T>(
        CoreAcquisitionEvaluationParityView view,
        CoreAcquisitionEvaluationParityField field,
        MaterialAggregate item,
        T expected,
        T actual,
        List<CoreAcquisitionEvaluationParityMismatch> mismatches)
    {
        CompareValue(view, field, item.ItemId, item.Name, expected, actual, mismatches);
    }

    private static void CompareValue<T>(
        CoreAcquisitionEvaluationParityView view,
        CoreAcquisitionEvaluationParityField field,
        int itemId,
        string itemName,
        T expected,
        T actual,
        List<CoreAcquisitionEvaluationParityMismatch> mismatches)
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

    private static CoreAcquisitionEvaluationParityMismatch CreateMismatch(
        CoreAcquisitionEvaluationParityView view,
        CoreAcquisitionEvaluationParityField field,
        int itemId,
        string itemName,
        string expected,
        string actual)
    {
        return new CoreAcquisitionEvaluationParityMismatch(view, field, itemId, itemName, expected, actual);
    }
}

public sealed record CoreAcquisitionEvaluationSnapshot(
    List<CoreDecisionRow> Rows,
    List<CoreDecisionRow> VisibleRows,
    List<MaterialAggregate> MarketAnalysisCandidates,
    List<MaterialAggregate> ActiveProcurementItems,
    AcquisitionCostContext CostContext);

internal sealed class CoreDecisionAggregate
{
    public CoreDecisionAggregate(
        PlanNode primaryNode,
        CoreDecisionRowReadState readState,
        bool isMarketCandidate,
        bool isSuppressed)
    {
        PrimaryNode = primaryNode;
        ReadState = readState;
        IsMarketCandidate = isMarketCandidate;
        PrimaryIsSuppressed = isSuppressed;
    }

    public PlanNode PrimaryNode { get; private set; }
    public CoreDecisionRowReadState ReadState { get; private set; }
    public bool IsMarketCandidate { get; }
    public bool PrimaryIsSuppressed { get; private set; }
    public List<CoreDecisionOccurrence> Occurrences { get; } = [];

    public void PreferPrimary(PlanNode node, CoreDecisionRowReadState readState, bool isSuppressed)
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

public sealed record CoreDecisionRowReadState(
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
    public static CoreDecisionRowReadState FromDemandRow(RecipeDemandRow row)
    {
        return new CoreDecisionRowReadState(
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

    public static CoreDecisionRowReadState FromNode(PlanNode node)
    {
        return new CoreDecisionRowReadState(
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

    public static CoreDecisionRowReadState FromDecisionRow(CoreDecisionRow row)
    {
        return new CoreDecisionRowReadState(
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

public sealed record CoreDecisionOccurrence(
    PlanNode Node,
    string ParentNodeId,
    string ParentItemName,
    int Quantity,
    int ParentOutputQuantity,
    bool IsSuppressed,
    string? SuppressedBy,
    bool IsActiveProcurement);

public sealed record CoreDecisionRow(
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

public static class CoreAcquisitionEvaluationCostCalculator
{
    public static bool TryGetCost(
        CoreDecisionRow row,
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
                row.TotalQuantity,
                out cost);
        }

        return TryGetCost(
            CoreDecisionRowReadState.FromDecisionRow(row),
            row.TotalQuantity,
            source,
            costContext,
            out cost);
    }

    internal static bool TryGetCost(
        CoreDecisionRowReadState readState,
        int quantity,
        AcquisitionSource source,
        AcquisitionCostContext costContext,
        out decimal cost)
    {
        cost = source switch
        {
            AcquisitionSource.MarketBuyNq when readState.CanBuyFromMarket && !readState.MustBeHq => GetMarketBuyCost(readState, quantity, costContext, hqOnly: false),
            AcquisitionSource.MarketBuyHq when readState.CanBuyFromMarket && readState.CanBeHq => GetMarketBuyCost(readState, quantity, costContext, hqOnly: true),
            AcquisitionSource.VendorBuy when readState.CanBuyFromVendor => readState.VendorUnitPrice * quantity,
            _ => 0
        };

        return cost > 0;
    }

    private static decimal GetMarketBuyCost(
        CoreDecisionRowReadState readState,
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

public enum CoreAcquisitionFilter
{
    All,
    Active,
    Market,
    Suppressed
}

public enum CoreAcquisitionEvaluationParityView
{
    Row,
    MarketAnalysisCandidate,
    ActiveProcurementItem
}

public enum CoreAcquisitionEvaluationParityField
{
    MissingItem,
    ExtraItem,
    NodeId,
    ItemName,
    Source,
    TotalQuantity,
    ActiveQuantity,
    MarketEvidence,
    EstimatedCost
}

public sealed record CoreAcquisitionEvaluationParityReport(
    IReadOnlyList<CoreAcquisitionEvaluationParityMismatch> Mismatches)
{
    public bool Matches => Mismatches.Count == 0;
}

public sealed record CoreAcquisitionEvaluationParityMismatch(
    CoreAcquisitionEvaluationParityView View,
    CoreAcquisitionEvaluationParityField Field,
    int ItemId,
    string ItemName,
    string Expected,
    string Actual);

internal sealed record CoreSuppressingDecisionAncestor(string NodeId, int ItemId, string ItemName);
