using FFXIV_Craft_Architect.Core.Services;

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
    public decimal LaborStandardMaterialBonusPercent { get; set; } = TradePaymentPolicy.DefaultLaborStandardMaterialBonusPercent;
    public TradePaymentContractMode ActivePaymentContract { get; set; } = TradePaymentContractMode.LegacyCommission;
    public TradeLaborStandard? LaborStandard { get; set; }
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
    IReadOnlyList<string> Warnings,
    TradePaymentContractBreakdown Legacy,
    TradePaymentContractBreakdown LaborStandard,
    TradePaymentContractBreakdown Active)
{
    public static TradeCommissionPaymentSummary FromOrder(
        TradeOrder order,
        TradePayrollWorkflowDraft? draft,
        TradePaymentPolicy? effectivePolicy = null)
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
        var policy = effectivePolicy != null
            ? TradeLaborStandardCalibrationService.NormalizeManagedCobaltRivetsBenchmark(effectivePolicy)
            : new TradePaymentPolicy(
                draft?.ActivePaymentContract ?? TradePaymentContractMode.LegacyCommission,
                draft?.CommissionPercent > 0 ? draft.CommissionPercent : CommissionPayoutPolicy.Default.CommissionPercent,
                draft?.LaborStandard)
            {
                LaborStandardMaterialBonusPercent = draft == null || draft.LaborStandardMaterialBonusPercent < 0
                    ? TradePaymentPolicy.DefaultLaborStandardMaterialBonusPercent
                    : draft.LaborStandardMaterialBonusPercent
            };
        var paymentMaterials = materials
            .Select(material => new TradePaymentMaterialInput(
                material.ItemId,
                material.Name,
                material.Quantity,
                material.RequiresHq,
                material.UnitCost,
                material.Responsibility,
                material.EvidenceSource,
                material.UnitCostExplanation,
                material.EvidenceTimestampUtc,
                material.Warnings))
            .ToArray();
        var laborInputs = (sourceSnapshot.CraftLabor ?? Array.Empty<TradeOrderCraftLaborSnapshot>())
            .Select(labor => new TradeCraftLaborInput(
                labor.NodeId,
                labor.ItemId,
                labor.Name,
                labor.RequestedQuantity,
                labor.CraftCount,
                labor.Warnings ?? Array.Empty<string>()))
            .ToArray();
        var comparison = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            paymentMaterials,
            laborInputs,
            policy,
            sourceSnapshot.Warnings ?? Array.Empty<string>()));

        return new TradeCommissionPaymentSummary(
            materials,
            comparison.EstimatedProcurementTotal,
            comparison.MaterialReimbursementTotal,
            comparison.ProvidedMaterialTotal,
            comparison.Active.CommissionPercent,
            comparison.Active.CommissionAmount,
            comparison.TotalPayment,
            comparison.Warnings,
            comparison.Legacy,
            comparison.LaborStandard,
            comparison.Active);
    }
}
