using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class TradeLaborStandardCalibrationService
{
    public const int CobaltRivetsItemId = 5099;
    public const string CobaltRivetsItemName = "Cobalt Rivets";
    public const int CobaltRivetsBenchmarkQuantity = 999;
    public const bool CobaltRivetsBenchmarkRequiresHq = true;

    public TradeLaborStandard CreateFromLegacyBenchmark(
        decimal legacyCommissionAmount,
        int benchmarkSynthCount,
        DateTime effectiveFromUtc)
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
            EffectiveFromUtc: effectiveFromUtc);
    }

    private static decimal RoundToFriendlyGil(decimal value)
    {
        return Math.Round(value / 1_000m, 0, MidpointRounding.AwayFromZero) * 1_000m;
    }
}
