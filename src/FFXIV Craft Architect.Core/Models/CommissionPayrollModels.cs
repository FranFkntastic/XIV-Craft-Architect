namespace FFXIV_Craft_Architect.Core.Models;

public enum CommissionCostBasis
{
    MarketRecommendation,
    SelectedAcquisitionSources,
    ProcurementRoute
}

public static class CommissionCostBasisDisplay
{
    /// <summary>Formats a stored cost-basis token (or an older stored label) for people to read.</summary>
    public static string Format(string? costBasis) =>
        Enum.TryParse<CommissionCostBasis>(costBasis, ignoreCase: false, out var basis)
            ? basis switch
            {
                CommissionCostBasis.MarketRecommendation => "Market recommendation",
                CommissionCostBasis.SelectedAcquisitionSources => "Selected acquisition sources",
                CommissionCostBasis.ProcurementRoute => "Procurement route",
                _ => basis.ToString()
            }
            : string.IsNullOrWhiteSpace(costBasis) ? "Unspecified" : costBasis;
}

public enum CommissionMaterialResponsibility
{
    Crafter,
    Provided
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
    string EvidenceSource,
    string UnitCostExplanation,
    DateTime? EvidenceTimestampUtc,
    IReadOnlyList<string> Warnings);

public sealed record CommissionPayrollRun(
    CommissionCostBasis CostBasis,
    IReadOnlyList<CommissionPayrollLine> Lines,
    IReadOnlyList<string> Warnings);

public enum TradePaymentContractMode
{
    LaborStandard = 1
}

/// <summary>Older stored policies expressed the labor rate as a benchmark payout over a synth count.</summary>
public sealed record StoredLaborBenchmark
{
    public decimal BenchmarkLaborPayout { get; init; }

    public int BenchmarkSynthCount { get; init; }
}

public sealed record TradePaymentPolicy(
    TradePaymentContractMode ActiveContract,
    decimal LaborGilPerSynth)
{
    public const decimal DefaultLaborGilPerSynth = 200m;

    // Read only: the normalizer turns it into LaborGilPerSynth and it is never written back.
    [System.Text.Json.Serialization.JsonPropertyName("LaborStandard")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public StoredLaborBenchmark? StoredLaborBenchmark { get; init; }

    public static TradePaymentPolicy Default { get; } = new(
        TradePaymentContractMode.LaborStandard,
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
    decimal MaterialSafetyAllowance = 0m,
    decimal? MaterialRouteCashRequired = null,
    bool RequireMaterialRouteQuote = false,
    string? MaterialRouteUnavailableReason = null);

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
    TradePaymentContractBreakdown Active,
    decimal TotalPayment,
    IReadOnlyList<string> Warnings,
    decimal OnHandMaterialValueTotal = 0m);
