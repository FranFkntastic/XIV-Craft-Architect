namespace FFXIV_Craft_Architect.Core.Models;

public sealed class TradePayrollWorkflowDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public Guid CompanyProfileId { get; set; }
    public Guid? OrderId { get; set; }
    public long PlanSessionVersion { get; set; }
    public long MarketAnalysisVersion { get; set; }
    public string SourcePlanName { get; set; } = "Active craft plan";
    public Guid? AssignedCrafterId { get; set; }
    public string? AssignedCrafterDisplayName { get; set; }
    public decimal CommissionPercent { get; set; } = CommissionPayoutPolicy.Default.CommissionPercent;
    public IReadOnlyList<TradePayrollResponsibilityLine> Responsibilities { get; set; } = Array.Empty<TradePayrollResponsibilityLine>();
    public string? RemoteId { get; set; }
    public TradeSyncState SyncState { get; set; } = TradeSyncState.LocalOnly;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record TradePayrollResponsibilityLine(
    int ItemId,
    bool RequiresHq,
    CommissionMaterialResponsibility Responsibility);

public sealed record TradeCommissionPaymentMaterial(
    int ItemId,
    string Name,
    int Quantity,
    bool RequiresHq,
    decimal UnitCost,
    decimal TotalCost,
    CommissionMaterialResponsibility Responsibility,
    string EvidenceSource,
    string UnitCostExplanation,
    DateTime? EvidenceTimestampUtc,
    IReadOnlyList<string> Warnings);

public sealed record TradeCommissionPaymentSummary(
    IReadOnlyList<TradeCommissionPaymentMaterial> Materials,
    decimal EstimatedProcurementTotal,
    decimal MaterialReimbursementTotal,
    decimal ProvidedMaterialTotal,
    decimal CommissionPercent,
    decimal CommissionAmount,
    decimal TotalPayment,
    IReadOnlyList<string> Warnings)
{
    public static TradeCommissionPaymentSummary FromOrder(
        TradeOrder order,
        TradePayrollWorkflowDraft? draft)
    {
        ArgumentNullException.ThrowIfNull(order);

        var sourceSnapshot = order.SourceSnapshot ?? new TradeOrderSourceSnapshot();
        var responsibilities = (draft?.Responsibilities ?? Array.Empty<TradePayrollResponsibilityLine>()).ToDictionary(
            line => (line.ItemId, line.RequiresHq),
            line => line.Responsibility) ?? [];
        var materials = (sourceSnapshot.Materials ?? Array.Empty<TradeOrderMaterialSnapshot>())
            .Select(material =>
            {
                var responsibility = responsibilities.TryGetValue((material.ItemId, material.RequiresHq), out var saved)
                    ? saved
                    : CommissionMaterialResponsibility.Crafter;
                return new TradeCommissionPaymentMaterial(
                    material.ItemId,
                    material.Name,
                    material.Quantity,
                    material.RequiresHq,
                    material.UnitCost,
                    material.TotalCost,
                    responsibility,
                    material.EvidenceSource,
                    material.UnitCostExplanation,
                    material.EvidenceTimestampUtc,
                    material.Warnings ?? Array.Empty<string>());
            })
            .ToArray();
        var estimatedProcurementTotal = materials.Sum(material => material.TotalCost);
        var materialReimbursementTotal = materials
            .Where(material => material.Responsibility == CommissionMaterialResponsibility.Crafter)
            .Sum(material => material.TotalCost);
        var providedMaterialTotal = estimatedProcurementTotal - materialReimbursementTotal;
        var commissionPercent = draft?.CommissionPercent > 0
            ? draft.CommissionPercent
            : CommissionPayoutPolicy.Default.CommissionPercent;
        var commissionAmount = estimatedProcurementTotal * (commissionPercent / 100m);
        var warnings = (sourceSnapshot.Warnings ?? Array.Empty<string>())
            .Concat(materials.SelectMany(material => material.Warnings))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new TradeCommissionPaymentSummary(
            materials,
            estimatedProcurementTotal,
            materialReimbursementTotal,
            providedMaterialTotal,
            commissionPercent,
            commissionAmount,
            materialReimbursementTotal + commissionAmount,
            warnings);
    }
}
