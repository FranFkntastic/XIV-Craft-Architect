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
            LaborStandardMaterialBonusPercent = 15m,
            LaborStandard = new TradeLaborStandard(
                "Cobalt Rivets benchmark",
                5094,
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
        Assert.Equal(30m, summary.LaborStandard.CommissionAmount);
        Assert.Equal(1_800m, summary.LaborStandard.CraftLaborTotal);
        Assert.Equal(2_030m, summary.LaborStandard.Total);
        Assert.Equal(240m, summary.TotalPayment);
    }

    [Fact]
    public void FromOrder_UsesProvidedEffectivePolicyWhenNoDraftExists()
    {
        var order = CreatePricedLaborOrder();
        var policy = CreateLaborPolicy(legacyCommissionPercent: 18m);

        var summary = TradeCommissionPaymentSummary.FromOrder(order, draft: null, effectivePolicy: policy);

        Assert.Equal(TradePaymentContractMode.LaborStandard, summary.Active.Contract);
        Assert.Equal(10m, summary.CommissionPercent);
        Assert.Equal(summary.LaborStandard.Total, summary.TotalPayment);
    }

    [Fact]
    public void FromOrder_UsesEffectivePolicyOverDraftPolicy()
    {
        var order = CreatePricedLaborOrder();
        var draft = new TradePayrollWorkflowDraft
        {
            ActivePaymentContract = TradePaymentContractMode.LegacyCommission,
            CommissionPercent = 20m
        };
        var policy = CreateLaborPolicy(legacyCommissionPercent: 18m);

        var summary = TradeCommissionPaymentSummary.FromOrder(order, draft, effectivePolicy: policy);

        Assert.Equal(TradePaymentContractMode.LaborStandard, summary.Active.Contract);
        Assert.Equal(summary.LaborStandard.Total, summary.TotalPayment);
    }

    private static TradeOrder CreatePricedLaborOrder()
    {
        return new TradeOrder
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
    }

    private static TradePaymentPolicy CreateLaborPolicy(decimal legacyCommissionPercent)
    {
        return new TradePaymentPolicy(
            TradePaymentContractMode.LaborStandard,
            legacyCommissionPercent,
            new TradeLaborStandard(
                "Cobalt Rivets benchmark",
                5094,
                "Cobalt Rivets",
                999,
                true,
                120_000m,
                200,
                new DateTime(2026, 6, 25, 0, 0, 0, DateTimeKind.Utc)));
    }
}
