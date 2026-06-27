using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradeOrderPaymentCopyFormatterTests
{
    [Fact]
    public void BuildReceipt_UsesOnlyActiveLaborStandardBasis()
    {
        var summary = CreateSummary(TradePaymentContractMode.LaborStandard);

        var receipt = TradeOrderPaymentCopyFormatter.BuildReceipt(CreateContext(summary));

        Assert.Contains("Payment receipt", receipt);
        Assert.Contains("Active basis: labor standard", receipt);
        Assert.Contains("Material commission (10%): 100 gil", receipt);
        Assert.Contains("Craft labor: 3 synths x 600.00 gil = 1,800 gil", receipt);
        Assert.DoesNotContain("Legacy comparison", receipt);
        Assert.DoesNotContain("Difference vs legacy", receipt);
    }

    [Fact]
    public void BuildSummary_IncludesLegacyAndLaborComparison()
    {
        var summary = CreateSummary(TradePaymentContractMode.LaborStandard);

        var text = TradeOrderPaymentCopyFormatter.BuildSummary(CreateContext(summary));

        Assert.Contains("Payment summary", text);
        Assert.Contains("Active basis: labor standard", text);
        Assert.Contains("Legacy comparison: 1,200 gil", text);
        Assert.Contains("Labor-standard comparison: 2,900 gil", text);
        Assert.Contains("Difference vs legacy: +1,700 gil", text);
    }

    private static TradeOrderPaymentCopyContext CreateContext(TradePaymentComparisonSummary comparison)
    {
        var material = comparison.Materials.Single();
        var summary = new TradeCommissionPaymentSummary(
            [new TradeCommissionPaymentMaterial(
                material.ItemId,
                material.Name,
                material.Quantity,
                material.RequiresHq,
                material.UnitCost,
                material.UnitCost * material.Quantity,
                material.Responsibility,
                material.EvidenceSource,
                material.UnitCostExplanation,
                material.EvidenceTimestampUtc,
                material.Warnings)],
            comparison.EstimatedProcurementTotal,
            comparison.MaterialReimbursementTotal,
            comparison.ProvidedMaterialTotal,
            comparison.Active.CommissionPercent,
            comparison.Active.CommissionAmount,
            comparison.TotalPayment,
            comparison.Warnings,
            comparison.Legacy,
            comparison.LaborStandard,
            comparison.Active);

        return new TradeOrderPaymentCopyContext(
            "Cobalt Rivets Commission",
            "Riviene Cahernaut",
            [new TradeOrderPaymentOutput("Cobalt Rivets", 999, false)],
            summary);
    }

    private static TradePaymentComparisonSummary CreateSummary(TradePaymentContractMode activeContract)
    {
        var standard = new TradeLaborStandard(
            "Cobalt Rivets benchmark",
            5099,
            "Cobalt Rivets",
            999,
            false,
            120_000m,
            200,
            new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc));

        return new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
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
                    "Cobalt Rivets",
                    999,
                    3,
                    [])
            ],
            Policy: new TradePaymentPolicy(activeContract, 20m, standard),
            Warnings: []));
    }
}
