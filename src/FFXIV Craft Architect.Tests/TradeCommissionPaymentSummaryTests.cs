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

    [Fact]
    public void FromOrder_ExposesLaborStandardComparisonWhenCraftLaborSnapshotExists()
    {
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(100, "Crafter Ore", 2, RequiresHq: false, UnitCost: 100m, TotalCost: 200m)
                ],
                CraftLabor =
                [
                    new TradeOrderCraftLaborSnapshot("root", 200, "Finished Item", 1, 3)
                ]
            }
        };
        var draft = new TradePayrollWorkflowDraft
        {
            CommissionPercent = 20m,
            LaborStandard = new TradeLaborStandard(
                "Cobalt Rivets benchmark",
                5099,
                "Cobalt Rivets",
                999,
                true,
                120_000m,
                200,
                new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc))
        };

        var summary = TradeCommissionPaymentSummary.FromOrder(order, draft);

        Assert.Equal(240m, summary.Legacy.Total);
        Assert.True(summary.LaborStandard.IsAvailable);
        Assert.Equal(1_800m, summary.LaborStandard.CraftLaborTotal);
        Assert.Equal(2_000m, summary.LaborStandard.Total);
        Assert.Equal(240m, summary.TotalPayment);
    }
}
