using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradePaymentCalculatorTests
{
    [Fact]
    public void Calculate_KeepsLegacyTotalAndAddsLaborStandardComparison()
    {
        var calculator = new TradePaymentCalculator();
        var standard = new TradeLaborStandard(
            Name: "Cobalt Rivets benchmark",
            BenchmarkItemId: 5099,
            BenchmarkItemName: "Cobalt Rivets",
            BenchmarkQuantity: 999,
            BenchmarkRequiresHq: true,
            BenchmarkLaborPayout: 120_000m,
            BenchmarkSynthCount: 200,
            EffectiveFromUtc: new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc));

        var summary = calculator.Calculate(new TradePaymentCalculationRequest(
            Materials:
            [
                new TradePaymentMaterialInput(
                    ItemId: 1,
                    Name: "Crafter Ore",
                    Quantity: 10,
                    RequiresHq: false,
                    UnitCost: 100m,
                    Responsibility: CommissionMaterialResponsibility.Crafter,
                    EvidenceSource: "Market",
                    UnitCostExplanation: "Selected listing.",
                    EvidenceTimestampUtc: null,
                    Warnings: [])
            ],
            CraftLabor:
            [
                new TradeCraftLaborInput(
                    NodeId: "root",
                    ItemId: 2,
                    Name: "Finished Item",
                    RequestedQuantity: 1,
                    CraftCount: 3,
                    Warnings: [])
            ],
            Policy: new TradePaymentPolicy(
                ActiveContract: TradePaymentContractMode.LegacyCommission,
                LegacyCommissionPercent: 20m,
                LaborStandard: standard),
            Warnings: []));

        Assert.Equal(1_200m, summary.TotalPayment);
        Assert.Equal(1_200m, summary.Legacy.Total);
        Assert.True(summary.LaborStandard.IsAvailable);
        Assert.Equal(1_800m, summary.LaborStandard.CraftLaborTotal);
        Assert.Equal(2_800m, summary.LaborStandard.Total);
        Assert.Equal(1_000m, summary.MaterialReimbursementTotal);
        Assert.Equal(600m, summary.LaborStandard.GilPerSynth);
        Assert.Equal(1_200m, summary.Active.Total);
    }

    [Fact]
    public void Calculate_DoesNotFallbackWhenLaborStandardEvidenceIsMissing()
    {
        var calculator = new TradePaymentCalculator();
        var standard = new TradeLaborStandard(
            "Cobalt Rivets benchmark",
            5099,
            "Cobalt Rivets",
            999,
            true,
            120_000m,
            200,
            new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc));

        var summary = calculator.Calculate(new TradePaymentCalculationRequest(
            Materials:
            [
                new TradePaymentMaterialInput(
                    1,
                    "Crafter Ore",
                    10,
                    false,
                    100m,
                    CommissionMaterialResponsibility.Crafter,
                    "Market",
                    "Selected listing.",
                    null,
                    [])
            ],
            CraftLabor: [],
            Policy: new TradePaymentPolicy(TradePaymentContractMode.LaborStandard, 20m, standard),
            Warnings: []));

        Assert.False(summary.LaborStandard.IsAvailable);
        Assert.Equal(0m, summary.TotalPayment);
        Assert.Contains(summary.Warnings, warning => warning.Contains("Labor-standard evidence is unavailable", StringComparison.OrdinalIgnoreCase));
    }
}
