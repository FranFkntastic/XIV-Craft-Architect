using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public static class TradeProcurementRowBuilder
{
    public static IReadOnlyList<TradeOrderProcurementRow> BuildRows(
        TradeOrder order,
        TradePayrollWorkflowDraft? draft,
        string? activePlanId,
        WorkerTradeProjection? liveSnapshot)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!CanUseLiveProjection(order, activePlanId, liveSnapshot))
        {
            return TradeOrderWorkflow.BuildProcurementRows(order, draft);
        }

        var snapshot = liveSnapshot!;
        var responsibilities = BuildResponsibilityLookup(draft);
        var lines = snapshot.MaterialLines
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.First());

        return snapshot.AcquisitionRows
            .Select(row => ToTradeRow(row, lines, responsibilities))
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool CanUseLiveProjection(
        TradeOrder order,
        string? activePlanId,
        WorkerTradeProjection? liveSnapshot)
    {
        return liveSnapshot != null &&
            !string.IsNullOrWhiteSpace(order.CraftPlanId) &&
            string.Equals(order.CraftPlanId, activePlanId, StringComparison.Ordinal);
    }

    private static Dictionary<(int ItemId, bool RequiresHq), CommissionMaterialResponsibility> BuildResponsibilityLookup(
        TradePayrollWorkflowDraft? draft)
    {
        return (draft?.Responsibilities ?? Array.Empty<TradePayrollResponsibilityLine>())
            .GroupBy(line => (line.ItemId, line.RequiresHq))
            .ToDictionary(group => group.Key, group => group.Last().Responsibility);
    }

    private static TradeOrderProcurementRow ToTradeRow(
        WorkerAcquisitionRowProjection row,
        IReadOnlyDictionary<int, CommissionPayrollInputLine> lines,
        IReadOnlyDictionary<(int ItemId, bool RequiresHq), CommissionMaterialResponsibility> responsibilities)
    {
        lines.TryGetValue(row.ItemId, out var line);
        var quantity = Math.Max(row.TotalQuantity, 0);
        var unitCost = line?.UnitCost ?? row.UnitPrice;
        var totalCost = unitCost * (line?.Quantity ?? quantity);
        var warnings = GetWarnings(row);
        return new TradeOrderProcurementRow(
            $"{row.ItemId}:{row.MustBeHq}",
            row.ItemId,
            row.ItemName,
            row.TotalQuantity,
            row.MustBeHq,
            RecipePlanDisplayHelpers.GetSourceDisplayName(row.Source),
            unitCost,
            totalCost,
            GetResponsibility(row, responsibilities),
            line?.EvidenceSource ?? row.MarketEvidence,
            GetEvidenceStatus(row, totalCost),
            line?.UnitCostExplanation ?? row.EstimatedCost,
            warnings.Count > 0 ? warnings[0] : string.Empty,
            warnings.Concat(line?.Warnings ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            IsLiveAcquisitionRow: true,
            IsActiveProcurement: row.IsActiveProcurement,
            HasSuppressedOccurrences: row.HasSuppressedOccurrences,
            IsFullySuppressed: row.IsFullySuppressed,
            SuppressedBy: row.SuppressedBy,
            ActiveQuantity: row.ActiveQuantity,
            UsedIn: row.UsedIn,
            HasEditableOccurrences: row.HasEditableOccurrences,
            Source: row.Source);
    }

    private static CommissionMaterialResponsibility GetResponsibility(
        WorkerAcquisitionRowProjection row,
        IReadOnlyDictionary<(int ItemId, bool RequiresHq), CommissionMaterialResponsibility> responsibilities)
    {
        return responsibilities.TryGetValue((row.ItemId, row.MustBeHq), out var responsibility)
            ? responsibility
            : CommissionMaterialResponsibility.Crafter;
    }

    private static string GetEvidenceStatus(WorkerAcquisitionRowProjection row, decimal totalCost)
    {
        if (row.IsFullySuppressed)
        {
            return "Suppressed";
        }

        if (row.IsActiveProcurement && totalCost > 0)
        {
            return "Priced";
        }

        if (row.IsActiveProcurement)
        {
            return "Unpriced";
        }

        return "Inactive";
    }

    private static IReadOnlyList<string> GetWarnings(WorkerAcquisitionRowProjection row)
    {
        if (row.IsFullySuppressed && row.SuppressedBy.Count > 0)
        {
            return [$"Skipped by {string.Join(", ", row.SuppressedBy)}"];
        }

        if (row.HasSuppressedOccurrences && row.SuppressedBy.Count > 0)
        {
            return [$"Some demand skipped by {string.Join(", ", row.SuppressedBy)}"];
        }

        return Array.Empty<string>();
    }
}
