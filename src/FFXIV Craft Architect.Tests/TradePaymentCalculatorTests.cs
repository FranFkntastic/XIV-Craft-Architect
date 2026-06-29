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
            BenchmarkItemId: 5094,
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
        Assert.Equal(100m, summary.LaborStandard.CommissionAmount);
        Assert.Equal(2_900m, summary.LaborStandard.Total);
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
            5094,
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

    [Fact]
    public void Calculate_LaborStandardUsesDefaultTenPercentMaterialBonus()
    {
        var calculator = new TradePaymentCalculator();
        var standard = new TradeLaborStandard(
            "Cobalt Rivets benchmark",
            5094,
            "Cobalt Rivets",
            999,
            false,
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
            CraftLabor:
            [
                new TradeCraftLaborInput(
                    "root",
                    2,
                    "Finished Item",
                    1,
                    3,
                    [])
            ],
            Policy: new TradePaymentPolicy(TradePaymentContractMode.LaborStandard, 20m, standard),
            Warnings: []));

        Assert.Equal(20m, summary.Legacy.CommissionPercent);
        Assert.Equal(200m, summary.Legacy.CommissionAmount);
        Assert.Equal(10m, summary.LaborStandard.CommissionPercent);
        Assert.Equal(100m, summary.LaborStandard.CommissionAmount);
        Assert.Equal(2_900m, summary.LaborStandard.Total);
        Assert.Equal(summary.LaborStandard.Total, summary.TotalPayment);
    }

    [Fact]
    public void Calculate_SeparatesLegacyCommissionPercentFromLaborStandardMaterialBonusPercent()
    {
        var calculator = new TradePaymentCalculator();
        var standard = new TradeLaborStandard(
            "Cobalt Rivets benchmark",
            5094,
            "Cobalt Rivets",
            999,
            false,
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
            CraftLabor:
            [
                new TradeCraftLaborInput(
                    "root",
                    2,
                    "Finished Item",
                    1,
                    3,
                    [])
            ],
            Policy: new TradePaymentPolicy(TradePaymentContractMode.LaborStandard, 25m, standard)
            {
                LaborStandardMaterialBonusPercent = 15m
            },
            Warnings: []));

        Assert.Equal(25m, summary.Legacy.CommissionPercent);
        Assert.Equal(250m, summary.Legacy.CommissionAmount);
        Assert.Equal(15m, summary.LaborStandard.CommissionPercent);
        Assert.Equal(150m, summary.LaborStandard.CommissionAmount);
        Assert.Equal(2_950m, summary.LaborStandard.Total);
    }
}
