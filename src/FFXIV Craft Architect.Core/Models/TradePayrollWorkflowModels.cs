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
    public TradePaymentContractMode ActivePaymentContract { get; set; } = TradePaymentContractMode.LaborStandard;
    public decimal LaborGilPerSynth { get; set; } = TradePaymentPolicy.DefaultLaborGilPerSynth;
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
    IReadOnlyList<string> Warnings,
    bool IsOnHand = false);

public sealed record TradeCommissionPaymentSummary(
    IReadOnlyList<TradeCommissionPaymentMaterial> Materials,
    decimal EstimatedProcurementTotal,
    decimal MaterialReimbursementTotal,
    decimal ProvidedMaterialTotal,
    decimal TotalPayment,
    IReadOnlyList<string> Warnings,
    TradePaymentContractBreakdown Active,
    decimal OnHandMaterialValueTotal = 0m)
{
    public static TradeCommissionPaymentSummary FromOrder(
        TradeOrder order,
        TradePayrollWorkflowDraft? draft,
        TradePaymentPolicy? effectivePolicy = null,
        bool requireMaterialRouteQuote = true)
    {
        ArgumentNullException.ThrowIfNull(order);

        return FromSnapshot(order.SourceSnapshot ?? new TradeOrderSourceSnapshot(), draft, effectivePolicy, requireMaterialRouteQuote);
    }

    public static TradeCommissionPaymentSummary FromSnapshot(
        TradeOrderSourceSnapshot sourceSnapshot,
        TradePayrollWorkflowDraft? draft,
        TradePaymentPolicy? effectivePolicy,
        bool requireMaterialRouteQuote = true)
    {
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
                    material.Warnings ?? Array.Empty<string>(),
                    IsOnHand: string.Equals(
                        material.EvidenceSource,
                        TradeOrderWorkflow.OnHandEvidenceSource,
                        StringComparison.OrdinalIgnoreCase));
            })
            .ToArray();
        var policy = effectivePolicy != null
            ? TradePaymentPolicyNormalizer.Normalize(effectivePolicy)
            : TradePaymentPolicyNormalizer.Normalize(new TradePaymentPolicy(
                draft?.ActivePaymentContract ?? TradePaymentContractMode.LaborStandard,
                draft?.LaborGilPerSynth > 0
                    ? draft.LaborGilPerSynth
                    : TradePaymentPolicy.DefaultLaborGilPerSynth));
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
                material.Warnings,
                material.IsOnHand))
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
        var quote = sourceSnapshot.MaterialQuote;
        var requiresMaterialQuote = materials.Any(material =>
            material.Responsibility == CommissionMaterialResponsibility.Crafter &&
            !material.IsOnHand);
        var crafterPurchases = materials
            .Where(m => m.Responsibility == CommissionMaterialResponsibility.Crafter && !m.IsOnHand)
            .ToArray();
        var (quotedCrafterCash, appliedAllowance) = TradePaymentCalculator.CrafterShare(quote,
            crafterPurchases.Select(m => (m.ItemId, m.RequiresHq)).ToHashSet());
        // Quotes route only the crafter's purchases. A material moved to the crafter after the
        // quote was taken keeps its last known value until the order is repriced.
        var unquoted = quote is null
            ? []
            : crafterPurchases
                .Where(m => !quote.Lines.Any(line => line.ItemId == m.ItemId && line.RequiresHq == m.RequiresHq))
                .ToArray();
        quotedCrafterCash += unquoted.Sum(m => m.TotalCost);
        var unquotedWarnings = unquoted
            .Select(m => $"{m.Name}: {m.TotalCost:N0} gil company value; not in the route quote.")
            .ToArray();
        var comparison = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            paymentMaterials,
            laborInputs,
            policy,
            (sourceSnapshot.Warnings ?? Array.Empty<string>())
                .Where(warning => sourceSnapshot.MaterialQuote == null ||
                    string.IsNullOrWhiteSpace(sourceSnapshot.MaterialQuoteFailureReason) ||
                    !string.Equals(
                        warning,
                        sourceSnapshot.MaterialQuoteFailureReason,
                        StringComparison.OrdinalIgnoreCase))
                .Concat(sourceSnapshot.MaterialQuote != null ||
                        string.IsNullOrWhiteSpace(sourceSnapshot.MaterialQuoteFailureReason)
                    ? []
                    : [sourceSnapshot.MaterialQuoteFailureReason])
                .Concat(unquotedWarnings)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            appliedAllowance,
            quote is null ? null : quotedCrafterCash,
            RequireMaterialRouteQuote: requireMaterialRouteQuote && requiresMaterialQuote,
            MaterialRouteUnavailableReason: sourceSnapshot.MaterialQuote == null
                ? sourceSnapshot.MaterialQuoteFailureReason
                : null));

        return new TradeCommissionPaymentSummary(
            materials,
            comparison.EstimatedProcurementTotal,
            comparison.MaterialReimbursementTotal,
            comparison.ProvidedMaterialTotal,
            comparison.TotalPayment,
            comparison.Warnings,
            comparison.Active,
            comparison.OnHandMaterialValueTotal);
    }
}
