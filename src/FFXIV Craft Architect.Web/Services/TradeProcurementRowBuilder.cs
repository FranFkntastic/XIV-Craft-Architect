using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public static class TradeProcurementRowBuilder
{
    public static IReadOnlyList<TradeOrderProcurementRow> BuildRows(
        TradeOrder order,
        TradePayrollWorkflowDraft? draft,
        string? activePlanId,
        AcquisitionEvaluationSnapshot? liveSnapshot)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!CanUseLiveProjection(order, activePlanId, liveSnapshot))
        {
            return TradeOrderWorkflow.BuildProcurementRows(order, draft);
        }

        var snapshot = liveSnapshot!;
        var responsibilities = BuildResponsibilityLookup(draft);

        return snapshot.Rows
            .Select(row => ToTradeRow(row, snapshot.CostContext, responsibilities))
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool CanUseLiveProjection(
        TradeOrder order,
        string? activePlanId,
        AcquisitionEvaluationSnapshot? liveSnapshot)
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
        DecisionRow row,
        AcquisitionCostContext costContext,
        IReadOnlyDictionary<(int ItemId, bool RequiresHq), CommissionMaterialResponsibility> responsibilities)
    {
        var totalCost = TryGetTotalCost(row, costContext, out var calculatedCost)
            ? calculatedCost
            : 0m;
        var quantity = Math.Max(row.TotalQuantity, 0);
        var unitCost = quantity > 0 && totalCost > 0
            ? totalCost / quantity
            : 0m;
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
            row.MarketEvidence,
            GetEvidenceStatus(row, totalCost),
            row.EstimatedCost,
            warnings.Count > 0 ? warnings[0] : string.Empty,
            warnings,
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

    private static bool TryGetTotalCost(DecisionRow row, AcquisitionCostContext costContext, out decimal totalCost)
    {
        return AcquisitionEvaluationCostCalculator.TryGetCost(row, row.Source, costContext, out totalCost);
    }

    private static CommissionMaterialResponsibility GetResponsibility(
        DecisionRow row,
        IReadOnlyDictionary<(int ItemId, bool RequiresHq), CommissionMaterialResponsibility> responsibilities)
    {
        return responsibilities.TryGetValue((row.ItemId, row.MustBeHq), out var responsibility)
            ? responsibility
            : CommissionMaterialResponsibility.Crafter;
    }

    private static string GetEvidenceStatus(DecisionRow row, decimal totalCost)
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

    private static IReadOnlyList<string> GetWarnings(DecisionRow row)
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
