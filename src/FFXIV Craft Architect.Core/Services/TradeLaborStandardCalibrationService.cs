using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class TradeLaborStandardCalibrationService
{
    public const int CobaltRivetsItemId = 5099;
    public const string CobaltRivetsItemName = "Cobalt Rivets";
    public const int CobaltRivetsBenchmarkQuantity = 999;
    public const bool CobaltRivetsBenchmarkRequiresHq = false;

    public static TradePaymentPolicy NormalizeManagedCobaltRivetsBenchmark(TradePaymentPolicy policy)
    {
        return policy.LaborStandard == null
            ? policy
            : policy with { LaborStandard = NormalizeManagedCobaltRivetsBenchmark(policy.LaborStandard) };
    }

    public static TradeLaborStandard NormalizeManagedCobaltRivetsBenchmark(TradeLaborStandard standard)
    {
        return standard.IsManagedCobaltRivets
            ? standard with
            {
                Name = "Cobalt Rivets benchmark",
                BenchmarkItemId = CobaltRivetsItemId,
                BenchmarkItemName = CobaltRivetsItemName,
                BenchmarkQuantity = CobaltRivetsBenchmarkQuantity,
                BenchmarkRequiresHq = CobaltRivetsBenchmarkRequiresHq,
                BenchmarkMode = TradeLaborBenchmarkMode.CobaltRivets
            }
            : standard;
    }

    public TradeLaborStandard CreateFromLegacyBenchmark(
        decimal legacyCommissionAmount,
        int benchmarkSynthCount,
        DateTime effectiveFromUtc)
    {
        return CreateManagedCobaltRivetsBenchmark(
            legacyCommissionAmount,
            benchmarkSynthCount,
            effectiveFromUtc,
            "Legacy benchmark calibration.");
    }

    public TradeLaborStandard CreateManagedCobaltRivetsBenchmark(
        decimal legacyCommissionAmount,
        int benchmarkSynthCount,
        DateTime calibratedAtUtc,
        string calibrationEvidence)
    {
        if (legacyCommissionAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(legacyCommissionAmount),
                "Legacy commission amount must be greater than zero.");
        }

        if (benchmarkSynthCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(benchmarkSynthCount),
                "Benchmark synth count must be greater than zero.");
        }

        return new TradeLaborStandard(
            Name: "Cobalt Rivets benchmark",
            BenchmarkItemId: CobaltRivetsItemId,
            BenchmarkItemName: CobaltRivetsItemName,
            BenchmarkQuantity: CobaltRivetsBenchmarkQuantity,
            BenchmarkRequiresHq: CobaltRivetsBenchmarkRequiresHq,
            BenchmarkLaborPayout: RoundToFriendlyGil(legacyCommissionAmount),
            BenchmarkSynthCount: benchmarkSynthCount,
            EffectiveFromUtc: calibratedAtUtc,
            BenchmarkMode: TradeLaborBenchmarkMode.CobaltRivets,
            CalibratedAtUtc: calibratedAtUtc,
            CalibrationEvidence: calibrationEvidence);
    }

    public TradeLaborStandard CreateCustomBenchmark(
        string name,
        int benchmarkItemId,
        string benchmarkItemName,
        int benchmarkQuantity,
        bool benchmarkRequiresHq,
        decimal laborPayout,
        int benchmarkSynthCount,
        DateTime calibratedAtUtc,
        string calibrationEvidence)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Benchmark name is required.", nameof(name));
        }

        if (benchmarkItemId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(benchmarkItemId),
                "Benchmark item ID must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(benchmarkItemName))
        {
            throw new ArgumentException("Benchmark item name is required.", nameof(benchmarkItemName));
        }

        if (benchmarkQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(benchmarkQuantity),
                "Benchmark quantity must be greater than zero.");
        }

        if (laborPayout <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(laborPayout),
                "Labor payout must be greater than zero.");
        }

        if (benchmarkSynthCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(benchmarkSynthCount),
                "Benchmark synth count must be greater than zero.");
        }

        return new TradeLaborStandard(
            Name: name,
            BenchmarkItemId: benchmarkItemId,
            BenchmarkItemName: benchmarkItemName,
            BenchmarkQuantity: benchmarkQuantity,
            BenchmarkRequiresHq: benchmarkRequiresHq,
            BenchmarkLaborPayout: RoundToFriendlyGil(laborPayout),
            BenchmarkSynthCount: benchmarkSynthCount,
            EffectiveFromUtc: calibratedAtUtc,
            BenchmarkMode: TradeLaborBenchmarkMode.Custom,
            CalibratedAtUtc: calibratedAtUtc,
            CalibrationEvidence: calibrationEvidence);
    }

    private static decimal RoundToFriendlyGil(decimal value)
    {
        return Math.Round(value / 1_000m, 0, MidpointRounding.AwayFromZero) * 1_000m;
    }
}
