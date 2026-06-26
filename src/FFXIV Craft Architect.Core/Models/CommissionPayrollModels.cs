namespace FFXIV_Craft_Architect.Core.Models;

public enum CommissionCostBasis
{
    MarketRecommendation
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

public sealed record TradeLaborStandard(
    string Name,
    int BenchmarkItemId,
    string BenchmarkItemName,
    int BenchmarkQuantity,
    bool BenchmarkRequiresHq,
    decimal BenchmarkLaborPayout,
    int BenchmarkSynthCount,
    DateTime EffectiveFromUtc)
{
    public decimal GilPerSynth => BenchmarkSynthCount > 0
        ? BenchmarkLaborPayout / BenchmarkSynthCount
        : 0m;
}

public sealed record TradePaymentPolicy(
    TradePaymentContractMode ActiveContract,
    decimal LegacyCommissionPercent,
    TradeLaborStandard? LaborStandard)
{
    public static TradePaymentPolicy LegacyDefault { get; } = new(
        TradePaymentContractMode.LegacyCommission,
        CommissionPayoutPolicy.Default.CommissionPercent,
        null);
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
    IReadOnlyList<string> Warnings);

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
    IReadOnlyList<string> Warnings);

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
    IReadOnlyList<string> Warnings);
