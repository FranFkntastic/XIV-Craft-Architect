using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class TradePaymentSpecificationTests
{
    [Fact]
    public void LegacyCommissionReimbursesCrafterButCommissionsAllMaterials()
    {
        var summary = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials:
            [
                Material(1, "Crafter ore", 2, 100m, CommissionMaterialResponsibility.Crafter),
                Material(2, "Provided cloth", 3, 50m, CommissionMaterialResponsibility.Provided)
            ],
            CraftLabor: [],
            Policy: new TradePaymentPolicy(TradePaymentContractMode.LegacyCommission, 20m, null),
            Warnings: []));

        Assert.Equal(350m, summary.EstimatedProcurementTotal);
        Assert.Equal(200m, summary.MaterialReimbursementTotal);
        Assert.Equal(150m, summary.ProvidedMaterialTotal);
        Assert.Equal(70m, summary.Legacy.CommissionAmount);
        Assert.Equal(270m, summary.TotalPayment);
    }

    [Fact]
    public void GilArithmeticRoundsMidpointsAwayFromZero()
    {
        var summary = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials: [Material(1, "Fractional", 1, 2.5m, CommissionMaterialResponsibility.Crafter)],
            CraftLabor: [],
            Policy: new TradePaymentPolicy(TradePaymentContractMode.LegacyCommission, 50m, null),
            Warnings: []));

        Assert.Equal(3m, summary.EstimatedProcurementTotal);
        Assert.Equal(2m, summary.Legacy.CommissionAmount);
        Assert.Equal(5m, summary.TotalPayment);
    }

    [Fact]
    public void LaborStandardPaysEveryRecordedSynth()
    {
        var summary = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials: [Material(1, "Ore", 10, 100m, CommissionMaterialResponsibility.Crafter)],
            CraftLabor:
            [
                new TradeCraftLaborInput("a", 10, "First craft", 1, 2, []),
                new TradeCraftLaborInput("b", 11, "Second craft", 1, 3, [])
            ],
            Policy: LaborPolicy(),
            Warnings: []));

        Assert.Equal(600m, summary.LaborStandard.GilPerSynth);
        Assert.Equal(5, summary.LaborStandard.CraftSynthCount);
        Assert.Equal(3_000m, summary.LaborStandard.CraftLaborTotal);
        Assert.Equal(4_100m, summary.TotalPayment);
    }

    [Fact]
    public void LaborMaterialBonusIncludesCommissionerProvidedMaterials()
    {
        var summary = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials: [Material(1, "Provided ore", 10, 100m, CommissionMaterialResponsibility.Provided)],
            CraftLabor: [new TradeCraftLaborInput("root", 10, "Craft", 1, 1, [])],
            Policy: LaborPolicy(),
            Warnings: []));

        Assert.Equal(0m, summary.MaterialReimbursementTotal);
        Assert.Equal(100m, summary.LaborStandard.CommissionAmount);
        Assert.Equal(600m, summary.LaborStandard.CraftLaborTotal);
        Assert.Equal(700m, summary.TotalPayment);
    }

    [Fact]
    public void ActiveLaborContractDoesNotFallbackWithoutSynthEvidence()
    {
        var summary = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials: [Material(1, "Ore", 10, 100m, CommissionMaterialResponsibility.Crafter)],
            CraftLabor: [],
            Policy: LaborPolicy(),
            Warnings: []));

        Assert.False(summary.LaborStandard.IsAvailable);
        Assert.Equal(0m, summary.TotalPayment);
    }

    [Fact]
    public void SelectedSourceEvidenceAndResponsibilityFlowIntoEffectivePayrollPolicy()
    {
        var resolver = new CommissionCostBasisResolver();
        var sourceLines = resolver.BuildSelectedSourceLines(
            [
                SelectedDemand(
                    10,
                    "HQ cloth",
                    2,
                    AcquisitionSource.MarketBuyHq,
                    unitPrice: 40m,
                    requiresHq: true,
                    hqUnitPrice: 150m),
                SelectedDemand(
                    11,
                    "Vendor ore",
                    3,
                    AcquisitionSource.VendorBuy,
                    unitPrice: 999m,
                    vendorUnitPrice: 20m)
            ],
            [Analysis(10, "HQ cloth", 999m)],
            [
                SpecificationFixtures.Evidence(
                    10,
                    "HQ cloth",
                    2,
                    SpecificationFixtures.World(
                        "Aether",
                        "Siren",
                        (2, 40, false),
                        (2, 150, true)))
            ]);
        var hqLine = Assert.Single(sourceLines, line => line.ItemId == 10);
        var vendorLine = Assert.Single(sourceLines, line => line.ItemId == 11);

        Assert.True(hqLine.RequiresHq);
        Assert.Equal(150m, hqLine.UnitCost);
        Assert.Equal("Procurement route", hqLine.EvidenceSource);
        Assert.Equal(20m, vendorLine.UnitCost);
        Assert.Equal("Vendor price", vendorLine.EvidenceSource);

        var policy = new CommissionPayoutPolicy(25m);
        var payroll = new CommissionPayrollService().Calculate(
            sourceLines.Select(line => line.ItemId == 11
                ? line with { Responsibility = CommissionMaterialResponsibility.Provided }
                : line),
            policy);

        Assert.Same(policy, payroll.Policy);
        Assert.Equal(25m, payroll.Policy.CommissionPercent);
        Assert.Equal(
            CommissionMaterialResponsibility.Crafter,
            Assert.Single(payroll.Lines, line => line.ItemId == 10).Responsibility);
        Assert.Equal(
            CommissionMaterialResponsibility.Provided,
            Assert.Single(payroll.Lines, line => line.ItemId == 11).Responsibility);
        Assert.Equal(360m, payroll.EstimatedMaterialTotal);
        Assert.Equal(300m, payroll.MaterialBasisTotal);
        Assert.Equal(90m, payroll.CommissionAmount);
        Assert.Equal(390m, payroll.TotalPay);
    }

    [Theory]
    [InlineData(-1, 100, 20)]
    [InlineData(1, -100, 20)]
    [InlineData(1, 100, -20)]
    [InlineData(1, 100, 101)]
    public void CommissionPayrollRejectsOutOfRangeMoneyInputs(
        int quantity,
        int unitCost,
        int commissionPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommissionPayrollService().Calculate(
            [PayrollMaterial(quantity, unitCost)],
            new CommissionPayoutPolicy(commissionPercent)));
    }

    [Theory]
    [InlineData(-1, 20, 10)]
    [InlineData(120_000, -1, 10)]
    [InlineData(120_000, 20, -1)]
    [InlineData(120_000, 101, 10)]
    [InlineData(120_000, 20, 101)]
    public void TradePaymentRejectsOutOfRangePolicyMoney(
        int benchmarkPayout,
        int legacyCommissionPercent,
        int laborMaterialBonusPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TradePaymentCalculator().Calculate(
            new TradePaymentCalculationRequest(
                Materials: [Material(1, "Ore", 1, 100m, CommissionMaterialResponsibility.Crafter)],
                CraftLabor: [new TradeCraftLaborInput("craft", 2, "Craft", 1, 1, [])],
                Policy: LaborPolicy(benchmarkPayout, legacyCommissionPercent, laborMaterialBonusPercent),
                Warnings: [])));
    }

    [Fact]
    public void ZeroCommissionPoliciesRemainZero()
    {
        var payroll = new CommissionPayrollService().Calculate(
            [PayrollMaterial(1, 100)],
            new CommissionPayoutPolicy(0));
        var payment = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials: [Material(1, "Ore", 1, 100m, CommissionMaterialResponsibility.Crafter)],
            CraftLabor: [],
            Policy: new TradePaymentPolicy(TradePaymentContractMode.LegacyCommission, 0, null),
            Warnings: []));

        Assert.Equal(0m, payroll.CommissionAmount);
        Assert.Equal(0m, payment.Legacy.CommissionPercent);
        Assert.Equal(100m, payment.TotalPayment);
    }

    [Fact]
    public void VendorRouteEvidencePrecedesMarketAnalysisAverage()
    {
        var line = Assert.Single(new CommissionCostBasisResolver().BuildMarketRecommendationLines(
            [new MaterialAggregate { ItemId = 10, Name = "Vendor ore", TotalQuantity = 5, UnitPrice = 999m }],
            [Analysis(10, "Vendor ore", 800m)],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 10,
                    Name = "Vendor ore",
                    QuantityNeeded = 10,
                    RecommendedWorld = new WorldShoppingSummary
                    {
                        WorldName = MarketShoppingConstants.VendorWorldName,
                        TotalCost = 1_200,
                        TotalQuantityPurchased = 10
                    }
                }
            ]));

        Assert.Equal(120m, line.UnitCost);
        Assert.Equal("Vendor price", line.EvidenceSource);
    }

    [Fact]
    public void SupportedSplitEvidencePrecedesMarketAnalysisAverage()
    {
        var line = Assert.Single(new CommissionCostBasisResolver().BuildMarketRecommendationLines(
            [new MaterialAggregate { ItemId = 11, Name = "Split leather", TotalQuantity = 5, UnitPrice = 999m }],
            [Analysis(11, "Split leather", 300m)],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 11,
                    Name = "Split leather",
                    QuantityNeeded = 10,
                    RecommendedSplit =
                    [
                        new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Siren", QuantityToBuy = 5, TotalCost = 300 },
                        new SplitWorldPurchase { DataCenter = "Aether", WorldName = "Faerie", QuantityToBuy = 5, TotalCost = 500 }
                    ]
                }
            ]));

        Assert.Equal(80m, line.UnitCost);
        Assert.Equal("Split procurement route", line.EvidenceSource);
    }

    [Fact]
    public void UnsupportedProjectionCannotDisplaceMarketAnalysisEvidence()
    {
        var line = Assert.Single(new CommissionCostBasisResolver().BuildMarketRecommendationLines(
            [new MaterialAggregate { ItemId = 12, Name = "Projected ore", TotalQuantity = 10, UnitPrice = 999m }],
            [Analysis(12, "Projected ore", 250m)],
            [
                new DetailedShoppingPlan
                {
                    ItemId = 12,
                    Name = "Projected ore",
                    QuantityNeeded = 10,
                    DCAveragePrice = 50m
                }
            ]));

        Assert.Equal(250m, line.UnitCost);
        Assert.Equal("Market evidence fallback", line.EvidenceSource);
    }

    private static TradePaymentMaterialInput Material(
        int itemId,
        string name,
        int quantity,
        decimal unitCost,
        CommissionMaterialResponsibility responsibility) => new(
            itemId,
            name,
            quantity,
            RequiresHq: false,
            unitCost,
            responsibility,
            EvidenceSource: "Fixed fixture",
            UnitCostExplanation: "Controlled input",
            EvidenceTimestampUtc: new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
            Warnings: []);

    private static CommissionPayrollInputLine PayrollMaterial(int quantity, decimal unitCost) => new(
        1,
        "Material",
        quantity,
        unitCost,
        RequiresHq: false,
        CommissionMaterialResponsibility.Crafter,
        EvidenceSource: "Fixed fixture",
        UnitCostExplanation: "Controlled input",
        EvidenceTimestampUtc: new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
        Warnings: []);

    private static RecipeDemandRow SelectedDemand(
        int itemId,
        string name,
        int quantity,
        AcquisitionSource source,
        decimal unitPrice,
        bool requiresHq = false,
        decimal hqUnitPrice = 0m,
        decimal vendorUnitPrice = 0m) => new(
            viewKind: RecipeDemandViewKind.ActiveProcurement,
            nodeId: $"node-{itemId}",
            itemId,
            itemName: name,
            iconId: 0,
            quantity,
            quantityBasis: RecipeDemandQuantityBasis.PlanNodeQuantity,
            mustBeHq: requiresHq,
            source,
            sourceReason: AcquisitionSourceReason.UserSelected,
            hasChildren: false,
            canBuyFromMarket: source is AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq,
            canBuyFromVendor: source == AcquisitionSource.VendorBuy,
            unitPrice,
            parentNodeId: null,
            parentItemName: null,
            parentOperationNodeId: null,
            parentRecipeId: null,
            operationNodeId: null,
            recipeId: null,
            suppressedByNodeId: null,
            suppressedByItemId: null,
            suppressedByItemName: null,
            canBeHq: requiresHq,
            hqUnitPrice: hqUnitPrice,
            vendorUnitPrice: vendorUnitPrice);

    private static TradePaymentPolicy LaborPolicy(
        decimal benchmarkPayout = 120_000m,
        decimal legacyCommissionPercent = 20m,
        decimal laborMaterialBonusPercent = TradePaymentPolicy.DefaultLaborStandardMaterialBonusPercent) => new(
        TradePaymentContractMode.LaborStandard,
        legacyCommissionPercent,
        new TradeLaborStandard(
            "Fixed benchmark",
            5_094,
            "Cobalt Rivets",
            999,
            false,
            BenchmarkLaborPayout: benchmarkPayout,
            BenchmarkSynthCount: 200,
            EffectiveFromUtc: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)))
        {
            LaborStandardMaterialBonusPercent = laborMaterialBonusPercent
        };

    private static MarketItemAnalysis Analysis(int itemId, string name, decimal competitiveAverage) => new()
    {
        ItemId = itemId,
        Name = name,
        LoadedAtUtc = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
        AnalysisCompetitiveAverageUnitPrice = competitiveAverage
    };
}
