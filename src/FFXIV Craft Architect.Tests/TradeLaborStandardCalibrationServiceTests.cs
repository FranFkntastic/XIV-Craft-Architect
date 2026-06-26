using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradeLaborStandardCalibrationServiceTests
{
    [Fact]
    public void CreateFromLegacyBenchmark_UsesLegacyCommissionAsBenchmarkPayout()
    {
        var service = new TradeLaborStandardCalibrationService();
        var standard = service.CreateFromLegacyBenchmark(
            legacyCommissionAmount: 123_456m,
            benchmarkSynthCount: 206,
            effectiveFromUtc: new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(5099, standard.BenchmarkItemId);
        Assert.Equal("Cobalt Rivets", standard.BenchmarkItemName);
        Assert.Equal(999, standard.BenchmarkQuantity);
        Assert.True(standard.BenchmarkRequiresHq);
        Assert.Equal(123_000m, standard.BenchmarkLaborPayout);
        Assert.Equal(206, standard.BenchmarkSynthCount);
        Assert.Equal(123_000m / 206m, standard.GilPerSynth);
    }

    [Fact]
    public void CreateFromLegacyBenchmark_RejectsMissingSynthCount()
    {
        var service = new TradeLaborStandardCalibrationService();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.CreateFromLegacyBenchmark(
                legacyCommissionAmount: 100_000m,
                benchmarkSynthCount: 0,
                effectiveFromUtc: DateTime.UtcNow));

        Assert.Equal("benchmarkSynthCount", exception.ParamName);
    }
}
