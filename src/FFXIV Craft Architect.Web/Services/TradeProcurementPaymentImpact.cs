using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>Attributes frontier cash to graph subtrees, never adding craft summaries to purchases.</summary>
public static class TradeProcurementPaymentImpact
{
    public readonly record struct Impact(decimal? Cash, decimal? TermsUnitCost = null, string? UnavailableReason = null);

    internal static Impact FromTerms(
        IReadOnlyList<CompanyCommissionMaterialTerm>? materials, int itemId, bool hq, long quantity)
    {
        var matches = materials?.Where(line => line.ItemId == itemId && line.RequiresHq == hq &&
            line.Responsibility == CommissionMaterialResponsibility.Crafter).Take(2).ToArray();
        if (matches is not [var line] || line.Quantity != quantity)
        {
            return new(null, UnavailableReason: PlanMismatch);
        }
        return line.UnitCost > 0
            ? new(line.UnitCost * quantity, line.UnitCost)
            : new(null, UnavailableReason: "Not itemized in terms");
    }

    private const string PlanMismatch = "Plan differs from terms";

    public static IReadOnlyDictionary<(int ItemId, bool RequiresHq), Impact> CalculateAll(
        WorkerTradeProjection snapshot,
        IReadOnlyDictionary<(int ItemId, bool RequiresHq), CommissionMaterialResponsibility> responsibilities,
        bool requireRecordedCash = false,
        IReadOnlyList<CompanyCommissionMaterialTerm>? materials = null)
    {
        var impacts = snapshot.AcquisitionRows
            .Select(row => (row.ItemId, row.MustBeHq)).Distinct()
            .ToDictionary(key => key, _ => default(Impact));
        var occurrences = snapshot.PlanOccurrences;
        if (occurrences == null || occurrences.Count == 0 ||
            occurrences.Any(node => string.IsNullOrWhiteSpace(node.NodeId)) ||
            occurrences.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != occurrences.Count)
        {
            return impacts;
        }

        var nodes = occurrences.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var paths = new Dictionary<string, List<WorkerPlanOccurrence>>(StringComparer.Ordinal);
        foreach (var node in occurrences)
        {
            var path = new List<WorkerPlanOccurrence>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = node;
            while (true)
            {
                if (!visited.Add(current.NodeId))
                {
                    return impacts;
                }

                path.Add(current);
                if (current.ParentNodeId == null)
                {
                    break;
                }

                if (!nodes.TryGetValue(current.ParentNodeId, out current!))
                {
                    return impacts;
                }
            }
            paths[node.NodeId] = path;
        }

        var targetsByItem = occurrences.GroupBy(node => node.ItemId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var row in snapshot.AcquisitionRows)
        {
            if (targetsByItem.ContainsKey(row.ItemId))
            {
                impacts[(row.ItemId, row.MustBeHq)] = new(0m);
            }
        }
        var frontierGroups = occurrences.Where(node => node.IsActiveProcurement && !node.IsSuppressed && node.Quantity > 0)
            .GroupBy(node => (node.ItemId, node.MustBeHq))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var materialLines = snapshot.MaterialLines.GroupBy(line => (line.ItemId, line.RequiresHq))
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var (key, peers) in frontierGroups)
        {
            var uncovered = peers.Where(peer => !paths[peer.NodeId].Any(node =>
                node.Source == AcquisitionSource.OnHand ||
                responsibilities.GetValueOrDefault((node.ItemId, node.MustBeHq)) == CommissionMaterialResponsibility.Provided)).ToArray();
            if (uncovered.Length == 0)
            {
                continue;
            }
            var source = uncovered[0].Source;
            var quantity = peers.Sum(node => (long)node.Quantity);
            decimal? unit = null;
            Impact recorded = default;
            if (uncovered.All(node => node.Source == source) &&
                source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq or AcquisitionSource.VendorBuy)
            {
                if (requireRecordedCash)
                {
                    recorded = FromTerms(materials, key.ItemId, key.MustBeHq, quantity);
                    unit = recorded.TermsUnitCost;
                }
                else if (materialLines.TryGetValue(key, out var lines) && lines.Length == 1 &&
                    lines[0].Quantity == quantity && TradeOrderWorkflow.IsResolvedMaterialEvidence(
                        lines[0].UnitCost, lines[0].UnitCost * quantity, lines[0].EvidenceSource))
                {
                    unit = lines[0].UnitCost;
                }
            }
            foreach (var frontier in uncovered)
            {
                foreach (var ancestor in paths[frontier.NodeId]
                    .Select(node => (node.ItemId, node.MustBeHq)).Distinct())
                {
                    if (!impacts.TryGetValue(ancestor, out var current))
                    {
                        continue;
                    }

                    impacts[ancestor] = new(current.Cash + unit * frontier.Quantity,
                        ancestor == key ? recorded.TermsUnitCost : current.TermsUnitCost,
                        recorded.UnavailableReason == PlanMismatch ? PlanMismatch : current.UnavailableReason ?? recorded.UnavailableReason);
                }
            }
        }
        // A single source/quality label cannot represent conflicting aggregate occurrences.
        foreach (var row in snapshot.AcquisitionRows)
        {
            if (targetsByItem.TryGetValue(row.ItemId, out var targets) &&
                targets.Any(node => !node.IsSuppressed &&
                    (node.MustBeHq != row.MustBeHq || node.Source != row.Source)))
            {
                impacts[(row.ItemId, row.MustBeHq)] = default;
            }
        }
        return impacts;
    }
}
