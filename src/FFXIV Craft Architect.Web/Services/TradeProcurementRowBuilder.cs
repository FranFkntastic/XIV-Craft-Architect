using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public static class TradeProcurementRowBuilder
{
    public static bool IsRequestedOutputRow(
        TradeOrder order,
        TradeOrderProcurementRow row)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(row);
        return row.IsLiveAcquisitionRow &&
            order.SourceSnapshot?.RootItems.Any(output =>
                output.ItemId == row.ItemId &&
                output.MustBeHq == row.RequiresHq) == true;
    }

    public static bool IsPlanPrecraftRow(TradeOrderProcurementRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.IsLiveAcquisitionRow && row.HasChildren;
    }

    public static bool ShouldIncludePlanRow(TradeOrderProcurementRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !row.IsLiveAcquisitionRow ||
            row.IsActiveProcurement ||
            IsPlanPrecraftRow(row);
    }

    public static string? GetPlanRouteDescription(TradeOrderProcurementRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.IsFullySuppressed)
        {
            return row.SuppressedBy.Count == 0
                ? "Not currently required by the selected acquisition route"
                : $"Not currently required because {string.Join(", ", row.SuppressedBy)} is sourced directly";
        }

        if (!row.HasChildren || row.Source == AcquisitionSource.Craft)
        {
            return null;
        }

        if (row.Responsibility == CommissionMaterialResponsibility.Provided)
        {
            return "Required item; its ingredients are not required because the company provides it";
        }

        return row.Source == AcquisitionSource.OnHand
            ? "Required item; its ingredients are not required because existing stock supplies it"
            : "Required item; its ingredients are not required because it is acquired finished";
    }

    public static IReadOnlyList<TradeOrderProcurementRow> BuildRows(
        TradeOrder order,
        TradePayrollWorkflowDraft? draft,
        string? activePlanId,
        WorkerTradeProjection? liveSnapshot)
    {
        ArgumentNullException.ThrowIfNull(order);

        var expectsLivePlan = !string.IsNullOrWhiteSpace(order.CraftPlanId) &&
            string.Equals(order.CraftPlanId, activePlanId, StringComparison.Ordinal);
        if (!TradeProcurementSourceMutationPolicy.CanReadLivePlan(
                order.CraftPlanId,
                activePlanId,
                liveSnapshot?.HasPlan == true,
                liveSnapshot?.PlanId))
        {
            return expectsLivePlan
                ? Array.Empty<TradeOrderProcurementRow>()
                : TradeOrderWorkflow.BuildProcurementRows(order, draft);
        }

        var snapshot = liveSnapshot!;
        var responsibilities = BuildResponsibilityLookup(draft);
        var lines = snapshot.MaterialLines
            .GroupBy(ToMaterialKey)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var retainedEvidence = BuildRetainedEvidenceLookup(order, snapshot.PlanId);

        return snapshot.AcquisitionRows
            .Select(row => ToTradeRow(row, lines, retainedEvidence, responsibilities))
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
        IReadOnlyDictionary<MaterialKey, CommissionPayrollInputLine> lines,
        IReadOnlyDictionary<MaterialKey, TradeOrderMaterialSnapshot> retainedEvidence,
        IReadOnlyDictionary<(int ItemId, bool RequiresHq), CommissionMaterialResponsibility> responsibilities)
    {
        var quantity = Math.Max(row.TotalQuantity, 0);
        var key = new MaterialKey(row.ItemId, row.MustBeHq, Math.Max(row.ActiveQuantity, 0));
        lines.TryGetValue(key, out var line);
        retainedEvidence.TryGetValue(key, out var retained);
        if (line == null || !TradeOrderWorkflow.IsResolvedMaterialEvidence(
                line.UnitCost,
                line.UnitCost * line.Quantity,
                line.EvidenceSource))
        {
            line = retained == null ? line : ToPayrollLine(retained);
        }
        var totalCost = line != null
            ? line.UnitCost * line.Quantity
            : row.CalculatedTotalCost;
        var unitCost = line?.UnitCost ?? (quantity > 0 && totalCost > 0
            ? Math.Ceiling(totalCost / quantity)
            : 0m);
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
            Source: row.Source,
            HasChildren: row.HasChildren,
            AvailableSources: row.AvailableSources);
    }

    private static IReadOnlyDictionary<MaterialKey, TradeOrderMaterialSnapshot> BuildRetainedEvidenceLookup(
        TradeOrder order,
        string? livePlanId)
    {
        var source = order.SourceSnapshot;
        if (string.IsNullOrWhiteSpace(livePlanId) ||
            source == null ||
            !string.Equals(order.CraftPlanId, livePlanId, StringComparison.Ordinal) ||
            !string.Equals(source.SourcePlanId, livePlanId, StringComparison.Ordinal))
        {
            return new Dictionary<MaterialKey, TradeOrderMaterialSnapshot>();
        }

        return source.Materials
            .Where(material => TradeOrderWorkflow.IsResolvedMaterialEvidence(
                material.UnitCost,
                material.TotalCost,
                material.EvidenceSource))
            .GroupBy(ToMaterialKey)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
    }

    private static MaterialKey ToMaterialKey(CommissionPayrollInputLine line) =>
        new(line.ItemId, line.RequiresHq, line.Quantity);

    private static MaterialKey ToMaterialKey(TradeOrderMaterialSnapshot material) =>
        new(material.ItemId, material.RequiresHq, material.Quantity);

    private static CommissionPayrollInputLine ToPayrollLine(TradeOrderMaterialSnapshot material) =>
        new(material.ItemId, material.Name, material.Quantity, material.UnitCost,
            material.RequiresHq, CommissionMaterialResponsibility.Crafter,
            material.EvidenceSource, material.UnitCostExplanation,
            material.EvidenceTimestampUtc, material.Warnings ?? []);

    private readonly record struct MaterialKey(int ItemId, bool RequiresHq, int Quantity);

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

        if (row.Source == AcquisitionSource.OnHand)
        {
            return "On hand";
        }

        if (row.HasChildren && row.Source == AcquisitionSource.Craft && totalCost > 0)
        {
            return "Craft path";
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

        return Array.Empty<string>();
    }
}
