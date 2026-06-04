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
