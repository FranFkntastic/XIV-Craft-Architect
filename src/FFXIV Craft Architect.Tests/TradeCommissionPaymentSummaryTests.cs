using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Tests;

public class TradeCommissionPaymentSummaryTests
{
    [Fact]
    public void FromOrder_UsesPayrollResponsibilityToCalculatePaymentAmount()
    {
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(100, "Crafter Ore", 2, RequiresHq: false, UnitCost: 100m, TotalCost: 200m),
                    new TradeOrderMaterialSnapshot(101, "Provided Cloth", 3, RequiresHq: true, UnitCost: 50m, TotalCost: 150m)
                ]
            }
        };
        var draft = new TradePayrollWorkflowDraft
        {
            CommissionPercent = 20m,
            Responsibilities =
            [
                new TradePayrollResponsibilityLine(101, RequiresHq: true, CommissionMaterialResponsibility.Provided)
            ]
        };

        var summary = TradeCommissionPaymentSummary.FromOrder(order, draft);

        Assert.Equal(350m, summary.EstimatedProcurementTotal);
        Assert.Equal(200m, summary.MaterialReimbursementTotal);
        Assert.Equal(70m, summary.CommissionAmount);
        Assert.Equal(270m, summary.TotalPayment);
        Assert.Equal(CommissionMaterialResponsibility.Crafter, summary.Materials[0].Responsibility);
        Assert.Equal(CommissionMaterialResponsibility.Provided, summary.Materials[1].Responsibility);
    }

    [Fact]
    public void FromOrder_DefaultsMaterialsToCrafterResponsibilityWithoutDraft()
    {
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(200, "Default Ore", 4, RequiresHq: false, UnitCost: 75m, TotalCost: 300m)
                ]
            }
        };

        var summary = TradeCommissionPaymentSummary.FromOrder(order, draft: null);

        Assert.Equal(300m, summary.EstimatedProcurementTotal);
        Assert.Equal(300m, summary.MaterialReimbursementTotal);
        Assert.Equal(60m, summary.CommissionAmount);
        Assert.Equal(360m, summary.TotalPayment);
        Assert.Equal(CommissionMaterialResponsibility.Crafter, Assert.Single(summary.Materials).Responsibility);
    }
}
