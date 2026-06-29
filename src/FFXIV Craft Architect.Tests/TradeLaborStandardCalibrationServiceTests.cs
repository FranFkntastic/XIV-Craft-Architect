using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public class TradeLaborStandardCalibrationServiceTests
{
    [Fact]
    public void ManagedCobaltRivetsBenchmark_UsesGarlandCobaltRivetsItemId()
    {
        Assert.Equal(5094, TradeLaborStandardCalibrationService.CobaltRivetsItemId);
    }

    [Fact]
    public void CreateFromLegacyBenchmark_UsesLegacyCommissionAsBenchmarkPayout()
    {
        var service = new TradeLaborStandardCalibrationService();
        var standard = service.CreateFromLegacyBenchmark(
            legacyCommissionAmount: 123_456m,
            benchmarkSynthCount: 206,
            effectiveFromUtc: new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(5094, standard.BenchmarkItemId);
        Assert.Equal("Cobalt Rivets", standard.BenchmarkItemName);
        Assert.Equal(999, standard.BenchmarkQuantity);
        Assert.False(standard.BenchmarkRequiresHq);
        Assert.Equal(123_000m, standard.BenchmarkLaborPayout);
        Assert.Equal(206, standard.BenchmarkSynthCount);
        Assert.Equal(123_000m / 206m, standard.GilPerSynth);
        Assert.Equal(TradeLaborBenchmarkMode.CobaltRivets, standard.BenchmarkMode);
        Assert.True(standard.IsManagedCobaltRivets);
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

    [Fact]
    public void CreateManagedCobaltRivetsBenchmark_RecordsManagedMetadata()
    {
        var service = new TradeLaborStandardCalibrationService();
        var calibratedAt = new DateTime(2026, 6, 25, 21, 0, 0, DateTimeKind.Utc);

        var standard = service.CreateManagedCobaltRivetsBenchmark(
            legacyCommissionAmount: 123_456m,
            benchmarkSynthCount: 206,
            calibratedAtUtc: calibratedAt,
            calibrationEvidence: "Reused fresh Cobalt Rivets evidence.");

        Assert.Equal("Cobalt Rivets benchmark", standard.Name);
        Assert.Equal(TradeLaborStandardCalibrationService.CobaltRivetsItemId, standard.BenchmarkItemId);
        Assert.Equal(TradeLaborBenchmarkMode.CobaltRivets, standard.BenchmarkMode);
        Assert.Equal(calibratedAt, standard.EffectiveFromUtc);
        Assert.Equal(calibratedAt, standard.CalibratedAtUtc);
        Assert.Equal("Reused fresh Cobalt Rivets evidence.", standard.CalibrationEvidence);
        Assert.True(standard.IsManagedCobaltRivets);
        Assert.False(standard.IsCustomBenchmark);
    }

    [Fact]
    public void NormalizeManagedCobaltRivetsBenchmark_CorrectsLegacyHqFlag()
    {
        var legacyManaged = new TradeLaborStandard(
            "Cobalt Rivets benchmark",
            5094,
            "Cobalt Rivets",
            999,
            true,
            120_000m,
            200,
            new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc));

        var normalized = TradeLaborStandardCalibrationService.NormalizeManagedCobaltRivetsBenchmark(legacyManaged);

        Assert.True(normalized.IsManagedCobaltRivets);
        Assert.False(normalized.BenchmarkRequiresHq);
        Assert.Equal(TradeLaborStandardCalibrationService.CobaltRivetsItemId, normalized.BenchmarkItemId);
        Assert.Equal(TradeLaborStandardCalibrationService.CobaltRivetsBenchmarkQuantity, normalized.BenchmarkQuantity);
    }

    [Fact]
    public void CreateCustomBenchmark_PreservesBenchmarkIdentity()
    {
        var service = new TradeLaborStandardCalibrationService();
        var calibratedAt = new DateTime(2026, 6, 25, 21, 0, 0, DateTimeKind.Utc);

        var standard = service.CreateCustomBenchmark(
            name: "Custom benchmark",
            benchmarkItemId: 42,
            benchmarkItemName: "Custom Item",
            benchmarkQuantity: 12,
            benchmarkRequiresHq: false,
            laborPayout: 55_555m,
            benchmarkSynthCount: 7,
            calibratedAtUtc: calibratedAt,
            calibrationEvidence: "Manual company policy.");

        Assert.Equal("Custom benchmark", standard.Name);
        Assert.Equal(42, standard.BenchmarkItemId);
        Assert.Equal("Custom Item", standard.BenchmarkItemName);
        Assert.Equal(12, standard.BenchmarkQuantity);
        Assert.False(standard.BenchmarkRequiresHq);
        Assert.Equal(56_000m, standard.BenchmarkLaborPayout);
        Assert.Equal(7, standard.BenchmarkSynthCount);
        Assert.Equal(TradeLaborBenchmarkMode.Custom, standard.BenchmarkMode);
        Assert.Equal(calibratedAt, standard.CalibratedAtUtc);
        Assert.Equal("Manual company policy.", standard.CalibrationEvidence);
        Assert.True(standard.IsCustomBenchmark);
    }

    [Fact]
    public void CreateManagedCobaltRivetsBenchmark_RejectsMissingPayout()
    {
        var service = new TradeLaborStandardCalibrationService();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.CreateManagedCobaltRivetsBenchmark(
                legacyCommissionAmount: 0m,
                benchmarkSynthCount: 200,
                calibratedAtUtc: DateTime.UtcNow,
                calibrationEvidence: "Bad evidence."));

        Assert.Equal("legacyCommissionAmount", exception.ParamName);
    }
}
