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
    CommissionMaterialResponsibility Responsibility);

public sealed record TradeCommissionPaymentSummary(
    IReadOnlyList<TradeCommissionPaymentMaterial> Materials,
    decimal EstimatedProcurementTotal,
    decimal MaterialReimbursementTotal,
    decimal CommissionAmount,
    decimal TotalPayment)
{
    public static TradeCommissionPaymentSummary FromOrder(
        TradeOrder order,
        TradePayrollWorkflowDraft? draft)
    {
        ArgumentNullException.ThrowIfNull(order);

        var responsibilities = draft?.Responsibilities.ToDictionary(
            line => (line.ItemId, line.RequiresHq),
            line => line.Responsibility) ?? [];
        var materials = order.SourceSnapshot.Materials
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
                    responsibility);
            })
            .ToArray();
        var estimatedProcurementTotal = materials.Sum(material => material.TotalCost);
        var materialReimbursementTotal = materials
            .Where(material => material.Responsibility == CommissionMaterialResponsibility.Crafter)
            .Sum(material => material.TotalCost);
        var commissionPercent = draft?.CommissionPercent > 0
            ? draft.CommissionPercent
            : CommissionPayoutPolicy.Default.CommissionPercent;
        var commissionAmount = estimatedProcurementTotal * (commissionPercent / 100m);

        return new TradeCommissionPaymentSummary(
            materials,
            estimatedProcurementTotal,
            materialReimbursementTotal,
            commissionAmount,
            materialReimbursementTotal + commissionAmount);
    }
}
