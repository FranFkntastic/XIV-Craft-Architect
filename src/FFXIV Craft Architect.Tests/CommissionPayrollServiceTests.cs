using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Tests;

public class CommissionPayrollServiceTests
{
    [Fact]
    public void Calculate_PaysMaterialBasisPlusTwentyPercentCommission()
    {
        var service = new CommissionPayrollService();

        var run = service.Calculate(
            [
                new CommissionPayrollInputLine(
                    ItemId: 1,
                    Name: "Ingot",
                    Quantity: 10,
                    UnitCost: 100m,
                    RequiresHq: false,
                    Responsibility: CommissionMaterialResponsibility.Crafter,
                    EvidenceSource: "Market recommendation",
                    UnitCostExplanation: "Uses market recommendation.",
                    EvidenceTimestampUtc: DateTime.UtcNow,
                    Warnings: [])
            ],
            CommissionPayoutPolicy.Default);

        Assert.Equal(1_000m, run.MaterialBasisTotal);
        Assert.Equal(1_000m, run.EstimatedMaterialTotal);
        Assert.Equal(200m, run.CommissionAmount);
        Assert.Equal(1_200m, run.TotalPay);
    }

    [Fact]
    public void Calculate_CommissionUsesFullMaterialEstimateButReimbursementExcludesProvidedLines()
    {
        var service = new CommissionPayrollService();

        var run = service.Calculate(
            [
                new CommissionPayrollInputLine(
                    ItemId: 1,
                    Name: "Cloth",
                    Quantity: 4,
                    UnitCost: 250m,
                    RequiresHq: false,
                    Responsibility: CommissionMaterialResponsibility.Crafter,
                    EvidenceSource: "Market recommendation",
                    UnitCostExplanation: "Uses market recommendation.",
                    EvidenceTimestampUtc: null,
                    Warnings: []),
                new CommissionPayrollInputLine(
                    ItemId: 2,
                    Name: "Gem",
                    Quantity: 2,
                    UnitCost: 500m,
                    RequiresHq: false,
                    Responsibility: CommissionMaterialResponsibility.Provided,
                    EvidenceSource: "Market recommendation",
                    UnitCostExplanation: "Uses market recommendation.",
                    EvidenceTimestampUtc: null,
                    Warnings: [])
            ],
            CommissionPayoutPolicy.Default);

        Assert.Equal(2_000m, run.EstimatedMaterialTotal);
        Assert.Equal(1_000m, run.MaterialBasisTotal);
        Assert.Equal(400m, run.CommissionAmount);
        Assert.Equal(1_400m, run.TotalPay);
        Assert.Equal(0m, run.Lines.Single(line => line.ItemId == 2).MaterialBasis);
    }
}
