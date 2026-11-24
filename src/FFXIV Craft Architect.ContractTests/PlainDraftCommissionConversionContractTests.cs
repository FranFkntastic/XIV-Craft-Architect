using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class PlainDraftCommissionConversionContractTests
{
    /// <summary>
    /// An existing plain draft becomes a canonical commission draft with exactly the terms the
    /// Orders page showed for it: same outputs, quantities, HQ flags, material responsibilities
    /// and payment totals, with the stored pricing evidence carried over and nothing repriced.
    /// </summary>
    [Fact]
    public void ExistingPlainDraftConvertsToCanonicalDraftWithExactTerms()
    {
        var companyId = Guid.NewGuid();
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var policy = new TradeMaterialPricingPolicy();
        var order = new TradeOrder
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = companyId,
            Title = "Iron Nails Commission",
            Status = TradeOrderStatus.ReadyToAssign,
            CommissionedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CraftPlanId = "plan-iron-nails",
            PaymentSchedule = CompanyCommissionPaymentSchedule.OnDelivery,
            PaymentPolicyOverride = new TradePaymentPolicy(TradePaymentContractMode.LaborStandard, 150),
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                CostBasis = CommissionCostBasis.ProcurementRoute,
                MarketFetchScope = MarketFetchScope.SelectedDataCenter,
                Region = "North-America",
                DataCenter = "Aether",
                PlanSessionVersion = 7,
                MarketAnalysisVersion = 3,
                ImportedAtUtc = now,
                RootItems =
                [
                    new(5061, "Iron Nails", 99, false, 0),
                    new(5062, "Iron Rivets", 12, true, 0)
                ],
                Materials =
                [
                    new(5111, "Iron Ore", 60, false, 18, 1080, "Market", "Cheapest listing", now),
                    new(5112, "Fire Shard", 40, false, 4, 160, "Market", "Cheapest listing", now),
                    new(5113, "Coke", 6, true, 250, 1500, "Market", "Cheapest listing", now)
                ],
                CraftLabor =
                [
                    new("nails", 5061, "Iron Nails", 99, 33),
                    new("rivets", 5062, "Iron Rivets", 12, 4)
                ],
                MaterialQuote = new TradeMaterialQuote
                {
                    CompanyProfileId = companyId,
                    SourcePlanId = "plan-iron-nails",
                    PlanSessionVersion = 7,
                    MarketAnalysisVersion = 3,
                    PolicyFingerprint = TradeMaterialPricingPolicyNormalizer.Fingerprint(policy),
                    AppliedPolicy = policy,
                    RouteSelectionKey = "aether-route",
                    QuotedAtUtc = now,
                    ExpiresAtUtc = now.AddHours(1),
                    RouteCashRequired = 1240,
                    SafetyAllowance = 124,
                    MaterialReimbursement = 1364,
                    WorldStops = 2,
                    Lines =
                    [
                        new(5111, "Iron Ore", 60, false, 1080, ["Siren"], now),
                        new(5112, "Fire Shard", 40, false, 160, ["Gilgamesh"], now)
                    ]
                }
            }
        };
        var payroll = new TradePayrollWorkflowDraft
        {
            CompanyProfileId = companyId,
            OrderId = order.Id,
            Responsibilities = [new(5113, true, CommissionMaterialResponsibility.Provided)]
        };
        var company = new TradeCompanyProfile
        {
            Id = companyId,
            Name = "Gooseworks",
            CommissionContact = "  operator#0001  ",
            PaymentPolicy = new TradePaymentPolicy(TradePaymentContractMode.LaborStandard, 100)
        };
        var shown = TradeCommissionPaymentSummary.FromOrder(
            order,
            payroll,
            TradeOrderWorkflow.ResolvePaymentPolicy(order, company.PaymentPolicy),
            requireMaterialRouteQuote: true);
        var quoteBefore = order.SourceSnapshot.MaterialQuote;

        var converted = TradeCompanyCommissionMigrationService.CreateCanonicalDraft(
            order,
            TradeCommissionDraftComposer.ComposeNewDraft(order, payroll, company));

        var commission = Assert.IsType<TradeCompanyCommission>(converted.CompanyCommission);
        Assert.Equal(CompanyCommissionPublicViewState.Draft, commission.PublicMetadata.ViewState);
        Assert.Null(commission.ActiveClaim);
        Assert.Null(converted.AssignedCrafterId);
        var terms = commission.CurrentTerms;
        Assert.Equal(
            order.SourceSnapshot.RootItems.Select(item => (item.ItemId, item.Quantity, item.MustBeHq)),
            terms.Outputs.Select(output => (output.ItemId, output.RequiredQuantity, output.MustBeHq)));
        Assert.Equal(
            shown.Materials
                .Select(material => (material.ItemId, material.Quantity, material.RequiresHq, material.Responsibility, material.TotalCost))
                .OrderBy(material => material.ItemId),
            terms.Materials
                .Select(material => (material.ItemId, material.Quantity, material.RequiresHq, material.Responsibility, material.TotalCost))
                .OrderBy(material => material.ItemId));
        Assert.Equal(shown.Active.MaterialReimbursementTotal, terms.Payment.MaterialReimbursement);
        Assert.Equal(shown.Active.CraftLaborTotal, terms.Payment.CraftLabor);
        Assert.Equal(shown.Active.Total, terms.Payment.Total);
        Assert.Equal(1364m, terms.Payment.MaterialReimbursement);
        Assert.Equal(CompanyCommissionPaymentSchedule.OnDelivery, terms.Payment.Schedule);
        Assert.Equal("operator#0001", terms.ContactInstructions);
        Assert.Same(quoteBefore, order.SourceSnapshot.MaterialQuote);
        var carriedQuote = Assert.IsType<TradeMaterialQuote>(terms.PricingEvidence.MaterialQuote);
        Assert.Equal(quoteBefore with { Lines = [] }, carriedQuote with { Lines = [] });
        Assert.Equal(quoteBefore!.Lines, carriedQuote.Lines);
    }
}
