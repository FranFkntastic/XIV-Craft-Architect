using System.Text.Json;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class TradePaymentSpecificationTests
{
    [Fact]
    public void LaborPaymentReimbursesCrafterAndBonusesAllMaterialValue()
    {
        var summary = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials:
            [
                Material(1, "Crafter ore", 2, 100m, CommissionMaterialResponsibility.Crafter),
                Material(2, "Provided cloth", 3, 50m, CommissionMaterialResponsibility.Provided)
            ],
            CraftLabor: [new TradeCraftLaborInput("root", 10, "Craft", 1, 1, [])],
            Policy: LaborPolicy(materialValueBonusPercent: 20m),
            Warnings: []));

        Assert.Equal(350m, summary.EstimatedProcurementTotal);
        Assert.Equal(200m, summary.MaterialReimbursementTotal);
        Assert.Equal(150m, summary.ProvidedMaterialTotal);
        Assert.Equal(70m, summary.Active.CommissionAmount);
        Assert.Equal(200m, summary.Active.CraftLaborTotal);
        Assert.Equal(470m, summary.TotalPayment);

        var persistedPolicy = JsonSerializer.Deserialize<TradePaymentPolicy>(
            "{\"ActiveContract\":0,\"LegacyCommissionPercent\":20,\"LaborGilPerSynth\":200}")!;
        var normalized = TradePaymentPolicyNormalizer.Normalize(persistedPolicy);
        Assert.Equal(TradePaymentContractMode.LaborStandard, normalized.ActiveContract);
        Assert.Equal(20m, normalized.MaterialValueBonusPercent);
        Assert.Null(normalized.LegacyCommissionPercent);
    }

    [Fact]
    public void RequiredExecutableQuoteFailsClosedWhenRouteCashIsMissing()
    {
        var summary = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials: [Material(1, "Crafter ore", 2, 100m, CommissionMaterialResponsibility.Crafter)],
            CraftLabor: [new TradeCraftLaborInput("root", 10, "Craft", 1, 1, [])],
            Policy: LaborPolicy(materialValueBonusPercent: 20m),
            Warnings: [],
            RequireMaterialRouteQuote: true));

        Assert.Equal(200m, summary.EstimatedProcurementTotal);
        Assert.Equal(0m, summary.MaterialReimbursementTotal);
        Assert.False(summary.Active.IsAvailable);
        Assert.Equal(0m, summary.TotalPayment);
        Assert.Contains(summary.Warnings, warning => warning.Contains("quote is unavailable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RequiredExecutableQuoteUsesCanonicalFailureReason()
    {
        const string failureReason =
            "No complete executable route fits company policy and current listing evidence (8 worlds, 3 data-center transfers, 15% consolidation premium, listings at most 120 minutes old).";
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(
                        1,
                        "Crafter ore",
                        2,
                        false,
                        100m,
                        200m,
                        "Acquisition evaluation",
                        "Selected acquisition quote",
                        DateTime.UtcNow,
                        [])
                ],
                CraftLabor =
                    [new TradeOrderCraftLaborSnapshot("root", 10, "Craft", 1, 1, Warnings: [])],
                MaterialQuote = null,
                MaterialQuoteFailureReason = failureReason,
                Warnings = [failureReason]
            }
        };

        var summary = TradeCommissionPaymentSummary.FromOrder(
            order,
            draft: null,
            LaborPolicy(materialValueBonusPercent: 20m));

        Assert.Equal(0m, summary.MaterialReimbursementTotal);
        Assert.False(summary.Active.IsAvailable);
        Assert.Equal(0m, summary.TotalPayment);
        Assert.Equal(failureReason, Assert.Single(summary.Legacy.Warnings));
        Assert.Equal(failureReason, Assert.Single(summary.LaborStandard.Warnings));
        Assert.Equal(failureReason, summary.Warnings[0]);
        Assert.DoesNotContain(
            summary.Warnings,
            warning => string.Equals(
                warning,
                "Executable material quote is unavailable.",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OrderPaymentUsesWholeListingRouteCashAndAllowance()
    {
        var quotedAt = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(
                        1,
                        "Crafter ore",
                        2,
                        false,
                        100m,
                        200m,
                        "Market analysis",
                        "Material value evidence",
                        quotedAt,
                        [])
                ],
                CraftLabor = [new TradeOrderCraftLaborSnapshot("root", 10, "Craft", 1, 1, Warnings: [])],
                MaterialQuote = new TradeMaterialQuote
                {
                    PolicyFingerprint = "policy",
                    AppliedPolicy = TradeMaterialPricingPolicy.Default,
                    QuotedAtUtc = quotedAt,
                    ExpiresAtUtc = quotedAt.AddMinutes(30),
                    RouteCashRequired = 250m,
                    SafetyAllowance = 25m,
                    MaterialReimbursement = 275m,
                    WorldStops = 1,
                    DataCenterTransfers = 0,
                    Lines = [new TradeMaterialQuoteLine(1, "Crafter ore", 2, false, 250m, ["Siren"], quotedAt)]
                }
            }
        };

        var summary = TradeCommissionPaymentSummary.FromOrder(
            order,
            draft: null,
            LaborPolicy(materialValueBonusPercent: 20m));

        Assert.Equal(200m, summary.EstimatedProcurementTotal);
        Assert.Equal(275m, summary.MaterialReimbursementTotal);
        Assert.Equal(40m, summary.Active.CommissionAmount);
        Assert.Equal(515m, summary.TotalPayment);
    }

    [Fact]
    public void OnHandMaterialsContributeValueWithoutReimbursement()
    {
        var onHand = Material(
            1,
            "On-hand ore",
            10,
            100m,
            CommissionMaterialResponsibility.Crafter) with
        {
            IsOnHand = true
        };
        var summary = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials: [onHand],
            CraftLabor: [new TradeCraftLaborInput("root", 10, "Craft", 1, 1, [])],
            Policy: LaborPolicy(materialValueBonusPercent: 20m),
            Warnings: []));

        Assert.Equal(1_000m, summary.EstimatedProcurementTotal);
        Assert.Equal(1_000m, summary.OnHandMaterialValueTotal);
        Assert.Equal(0m, summary.MaterialReimbursementTotal);
        Assert.Equal(0m, summary.ProvidedMaterialTotal);
        Assert.Equal(200m, summary.Active.CommissionAmount);
        Assert.Equal(400m, summary.TotalPayment);
    }

    [Fact]
    public void GilArithmeticRoundsMidpointsAwayFromZero()
    {
        var summary = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials: [Material(1, "Fractional", 1, 2.5m, CommissionMaterialResponsibility.Crafter)],
            CraftLabor: [new TradeCraftLaborInput("root", 10, "Craft", 1, 1, [])],
            Policy: LaborPolicy(materialValueBonusPercent: 50m),
            Warnings: []));

        Assert.Equal(3m, summary.EstimatedProcurementTotal);
        Assert.Equal(2m, summary.Active.CommissionAmount);
        Assert.Equal(205m, summary.TotalPayment);
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

        Assert.Equal(200m, summary.LaborStandard.GilPerSynth);
        Assert.Equal(5, summary.LaborStandard.CraftSynthCount);
        Assert.Equal(1_000m, summary.LaborStandard.CraftLaborTotal);
        Assert.Equal(100m, summary.LaborStandard.CommissionAmount);
        Assert.Equal(2_100m, summary.TotalPayment);
    }

    [Fact]
    public void LaborStandardPaysMaterialValueBonusEvenWhenCompanyProvidesMaterials()
    {
        var summary = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials: [Material(1, "Provided ore", 10, 100m, CommissionMaterialResponsibility.Provided)],
            CraftLabor: [new TradeCraftLaborInput("root", 10, "Craft", 1, 1, [])],
            Policy: LaborPolicy(),
            Warnings: []));

        Assert.Equal(0m, summary.MaterialReimbursementTotal);
        Assert.Equal(100m, summary.LaborStandard.CommissionAmount);
        Assert.Equal(200m, summary.LaborStandard.CraftLaborTotal);
        Assert.Equal(300m, summary.TotalPayment);
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
        Assert.Equal("Acquisition evaluation", hqLine.EvidenceSource);
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

    [Fact]
    public void SelectedVendorSourceDoesNotInheritStaleMarketWarnings()
    {
        var staleWorld = SpecificationFixtures.World("Aether", "Golem", 100, 500);
        staleWorld.MarketDataQualityBucket = MarketDataQualityBucket.Ancient;
        staleWorld.MarketDataAge = TimeSpan.FromHours(37);
        staleWorld.MarketUploadedAtUtc = DateTime.UtcNow.AddHours(-37);
        var stalePlan = SpecificationFixtures.Evidence(20, "Copper Ore", 10, staleWorld);
        stalePlan.RecommendedWorld = staleWorld;

        var line = Assert.Single(new CommissionCostBasisResolver().BuildSelectedSourceLines(
            [SelectedDemand(
                20,
                "Copper Ore",
                10,
                AcquisitionSource.VendorBuy,
                unitPrice: 500m,
                vendorUnitPrice: 2m)],
            [Analysis(20, "Copper Ore", 500m)],
            [stalePlan]));

        Assert.Equal(2m, line.UnitCost);
        Assert.Equal("Vendor price", line.EvidenceSource);
        Assert.Empty(line.Warnings);
        Assert.DoesNotContain("upload age", line.UnitCostExplanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectedSourceEvidenceKeepsNqAndHqDemandSeparate()
    {
        var lines = new CommissionCostBasisResolver().BuildSelectedSourceLines(
            [
                SelectedDemand(
                    30,
                    "Mixed cloth",
                    4,
                    AcquisitionSource.MarketBuyNq,
                    unitPrice: 40m),
                SelectedDemand(
                    30,
                    "Mixed cloth",
                    2,
                    AcquisitionSource.MarketBuyHq,
                    unitPrice: 40m,
                    requiresHq: true,
                    hqUnitPrice: 150m)
            ],
            [Analysis(30, "Mixed cloth", 40m)],
            [
                SpecificationFixtures.Evidence(
                    30,
                    "Mixed cloth",
                    6,
                    SpecificationFixtures.World(
                        "Aether",
                        "Siren",
                        (4, 40, false),
                        (2, 150, true)))
            ]);

        Assert.Collection(
            lines.OrderBy(line => line.RequiresHq),
            line =>
            {
                Assert.False(line.RequiresHq);
                Assert.Equal(4, line.Quantity);
                Assert.Equal(40m, line.UnitCost);
            },
            line =>
            {
                Assert.True(line.RequiresHq);
                Assert.Equal(2, line.Quantity);
                Assert.Equal(150m, line.UnitCost);
            });
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
    [InlineData(-1, 20)]
    [InlineData(200, -1)]
    [InlineData(200, 101)]
    public void TradePaymentRejectsOutOfRangePolicyMoney(
        int gilPerSynth,
        int materialValueBonusPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TradePaymentCalculator().Calculate(
            new TradePaymentCalculationRequest(
                Materials: [Material(1, "Ore", 1, 100m, CommissionMaterialResponsibility.Crafter)],
                CraftLabor: [new TradeCraftLaborInput("craft", 2, "Craft", 1, 1, [])],
                Policy: LaborPolicy(gilPerSynth, materialValueBonusPercent),
                Warnings: [])));
    }

    [Fact]
    public void ZeroMaterialBonusPoliciesRemainZero()
    {
        var payroll = new CommissionPayrollService().Calculate(
            [PayrollMaterial(1, 100)],
            new CommissionPayoutPolicy(0));
        var payment = new TradePaymentCalculator().Calculate(new TradePaymentCalculationRequest(
            Materials: [Material(1, "Ore", 1, 100m, CommissionMaterialResponsibility.Crafter)],
            CraftLabor: [new TradeCraftLaborInput("root", 10, "Craft", 1, 1, [])],
            Policy: LaborPolicy(materialValueBonusPercent: 0),
            Warnings: []));

        Assert.Equal(0m, payroll.CommissionAmount);
        Assert.Equal(0m, payment.Active.CommissionPercent);
        Assert.Equal(300m, payment.TotalPayment);
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

    [Fact]
    public void ReceiptAndSummaryPreserveProvenanceWithoutCollapsingTheirRoles()
    {
        var order = new TradeOrder
        {
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                Materials =
                [
                    new TradeOrderMaterialSnapshot(
                        1,
                        "Crafter ore",
                        2,
                        false,
                        100m,
                        200m,
                        "Fixed fixture",
                        "Controlled input",
                        new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
                        [])
                ],
                CraftLabor =
                [
                    new TradeOrderCraftLaborSnapshot("root", 1, "Craft", 1, 1, Warnings: [])
                ],
                MaterialQuote = new TradeMaterialQuote
                {
                    PolicyFingerprint = "fixture",
                    AppliedPolicy = TradeMaterialPricingPolicy.Default,
                    QuotedAtUtc = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
                    ExpiresAtUtc = new DateTime(2026, 7, 20, 12, 30, 0, DateTimeKind.Utc),
                    RouteCashRequired = 200m,
                    SafetyAllowance = 20m,
                    MaterialReimbursement = 220m,
                    WorldStops = 1,
                    DataCenterTransfers = 0,
                    Lines =
                    [
                        new TradeMaterialQuoteLine(
                            1,
                            "Crafter ore",
                            2,
                            false,
                            200m,
                            ["Siren"],
                            new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc))
                    ]
                }
            }
        };
        var payment = TradeCommissionPaymentSummary.FromOrder(
            order,
            draft: null,
            LaborPolicy(materialValueBonusPercent: 20m));
        var context = new TradeOrderPaymentCopyContext(
            "Commission",
            "Crafter",
            [new TradeOrderPaymentOutput("Finished item", 1, true)],
            new TradeOrderPaymentProvenance(
                "Workshop plan",
                CommissionCostBasis.SelectedAcquisitionSources,
                MarketFetchScope.EntireRegion,
                "Aether",
                "North America",
                ["Aether", "Primal", "Chaos", "Light"],
                new DateTime(2026, 7, 28, 16, 30, 0, DateTimeKind.Utc)),
            payment);

        var receipt = TradeOrderPaymentCopyFormatter.BuildReceipt(context);
        var summary = TradeOrderPaymentCopyFormatter.BuildSummary(context);

        foreach (var copy in new[] { receipt, summary })
        {
            Assert.Contains("Plan: Workshop plan", copy);
            Assert.Contains("Material cost basis: Selected acquisition sources", copy);
            Assert.Contains(
                "Evidence scope: North America + Europe regions (Aether, Chaos, Light, Primal)",
                copy);
            Assert.Contains("Evidence snapshot: 2026-07-28 16:30 UTC", copy);
        }

        Assert.DoesNotContain("Crafter procures:", receipt);
        Assert.DoesNotContain("Legacy comparison", receipt);
        Assert.Contains("Material value bonus (20%)", receipt);
        Assert.Contains("Crafter ore x2: 200 gil", summary);
        Assert.DoesNotContain("Legacy comparison", summary);
        Assert.Contains("Material value bonus (20%)", summary);
        Assert.Equal("Agreed payment terms", CompanyCommissionPaymentDisplayFormatter.FormatContractLabel("Legacy commission"));
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
        decimal gilPerSynth = TradePaymentPolicy.DefaultLaborGilPerSynth,
        decimal materialValueBonusPercent = TradePaymentPolicy.DefaultMaterialValueBonusPercent) => new(
        TradePaymentContractMode.LaborStandard,
        materialValueBonusPercent,
        gilPerSynth);

    private static MarketItemAnalysis Analysis(int itemId, string name, decimal competitiveAverage) => new()
    {
        ItemId = itemId,
        Name = name,
        LoadedAtUtc = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
        AnalysisCompetitiveAverageUnitPrice = competitiveAverage
    };
}
