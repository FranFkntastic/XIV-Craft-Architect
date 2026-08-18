using System.Text.Json.Serialization;

namespace FFXIV_Craft_Architect.Core.Models;

public enum CommissionCostBasis
{
    MarketRecommendation,
    SelectedAcquisitionSources,
    ProcurementRoute
}

public enum CommissionMaterialResponsibility
{
    Crafter,
    Provided
}

public sealed record CommissionPayoutPolicy(decimal CommissionPercent)
{
    public static CommissionPayoutPolicy Default { get; } = new(CommissionPercent: 20m);
}

public sealed record CommissionPayrollInputLine(
    int ItemId,
    string Name,
    int Quantity,
    decimal UnitCost,
    bool RequiresHq,
    CommissionMaterialResponsibility Responsibility,
    string EvidenceSource,
    string UnitCostExplanation,
    DateTime? EvidenceTimestampUtc,
    IReadOnlyList<string> Warnings);

public sealed record CommissionPayrollLine(
    int ItemId,
    string Name,
    int Quantity,
    decimal UnitCost,
    bool RequiresHq,
    CommissionMaterialResponsibility Responsibility,
    decimal EstimatedMaterialCost,
    decimal MaterialBasis,
    string EvidenceSource,
    string UnitCostExplanation,
    DateTime? EvidenceTimestampUtc,
    IReadOnlyList<string> Warnings);

public sealed record CommissionPayrollRun(
    CommissionCostBasis CostBasis,
    CommissionPayoutPolicy Policy,
    IReadOnlyList<CommissionPayrollLine> Lines,
    decimal EstimatedMaterialTotal,
    decimal MaterialBasisTotal,
    decimal CommissionAmount,
    decimal TotalPay,
    IReadOnlyList<string> Warnings);

public enum TradePaymentContractMode
{
    LegacyCommission,
    LaborStandard
}

public sealed record LegacyTradeLaborStandard
{
    public decimal BenchmarkLaborPayout { get; init; }

    public int BenchmarkSynthCount { get; init; }
}

public sealed record TradePaymentPolicy(
    TradePaymentContractMode ActiveContract,
    decimal MaterialValueBonusPercent,
    decimal LaborGilPerSynth)
{
    public const decimal DefaultMaterialValueBonusPercent = 10m;
    public const decimal DefaultLaborGilPerSynth = 200m;

    [JsonPropertyName("LegacyCommissionPercent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? LegacyCommissionPercent { get; init; }

    [JsonPropertyName("LaborStandard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacyTradeLaborStandard? LegacyLaborStandard { get; init; }

    public static TradePaymentPolicy Default { get; } = new(
        TradePaymentContractMode.LaborStandard,
        DefaultMaterialValueBonusPercent,
        DefaultLaborGilPerSynth);
}

public sealed record TradePaymentMaterialInput(
    int ItemId,
    string Name,
    int Quantity,
    bool RequiresHq,
    decimal UnitCost,
    CommissionMaterialResponsibility Responsibility,
    string EvidenceSource,
    string UnitCostExplanation,
    DateTime? EvidenceTimestampUtc,
    IReadOnlyList<string> Warnings,
    bool IsOnHand = false);

public sealed record TradeCraftLaborInput(
    string NodeId,
    int ItemId,
    string Name,
    int RequestedQuantity,
    int CraftCount,
    IReadOnlyList<string> Warnings);

public sealed record TradePaymentCalculationRequest(
    IReadOnlyList<TradePaymentMaterialInput> Materials,
    IReadOnlyList<TradeCraftLaborInput> CraftLabor,
    TradePaymentPolicy Policy,
    IReadOnlyList<string> Warnings,
    decimal MaterialSafetyAllowance = 0m);

public sealed record TradeCraftLaborLine(
    string NodeId,
    int ItemId,
    string Name,
    int RequestedQuantity,
    int CraftCount,
    decimal GilPerSynth,
    decimal LaborTotal,
    IReadOnlyList<string> Warnings);

public sealed record TradePaymentContractBreakdown(
    TradePaymentContractMode Contract,
    bool IsAvailable,
    decimal MaterialReimbursementTotal,
    decimal CommissionPercent,
    decimal CommissionAmount,
    decimal CraftLaborTotal,
    int CraftSynthCount,
    decimal GilPerSynth,
    decimal Total,
    IReadOnlyList<TradeCraftLaborLine> CraftLaborLines,
    IReadOnlyList<string> Warnings);

public sealed record TradePaymentComparisonSummary(
    IReadOnlyList<TradePaymentMaterialInput> Materials,
    decimal EstimatedProcurementTotal,
    decimal MaterialReimbursementTotal,
    decimal ProvidedMaterialTotal,
    TradePaymentContractBreakdown Legacy,
    TradePaymentContractBreakdown LaborStandard,
    TradePaymentContractBreakdown Active,
    decimal TotalPayment,
    IReadOnlyList<string> Warnings,
    decimal OnHandMaterialValueTotal = 0m);
