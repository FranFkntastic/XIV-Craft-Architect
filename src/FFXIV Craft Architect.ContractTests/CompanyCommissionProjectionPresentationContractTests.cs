using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyCommissionProjectionPresentationContractTests
{
    private static readonly DateTime CapturedAt =
        new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DiscordPublicationUsesCraftingLanguageAndCurrentOutputProgress()
    {
        var payload = SerializePublication(CreateBrief(TradeOrderStatus.Assigned, clearedToWork: true));

        Assert.Contains("CRAFTING", payload, StringComparison.Ordinal);
        Assert.Contains("3 crafted, 2 ready", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("READY TO WORK", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("IN PROGRESS", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscordPublicationCallsTheDeliveryStateReadyForDelivery()
    {
        var payload = SerializePublication(CreateBrief(TradeOrderStatus.AwaitingDelivery, clearedToWork: true));

        Assert.Contains("READY FOR DELIVERY", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("AWAITING DELIVERY", payload, StringComparison.Ordinal);
    }

    private static string SerializePublication(CompanyCommissionPublicBrief brief)
    {
        var projection = new CommittedCompanyCommissionDiscordProjection(
            new CompanyId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            brief,
            new CompanyRecordRevision(12),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            12,
            CompanyCommissionActivityKind.ProgressReported,
            CapturedAt,
            "Progress updated.",
            new Uri("https://example.test/commission"),
            null);
        return JsonSerializer.Serialize(
            CompanyCommissionDiscordMessage.CreatePublication(projection),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static CompanyCommissionPublicBrief CreateBrief(
        TradeOrderStatus status,
        bool clearedToWork)
    {
        var lineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        return new CompanyCommissionPublicBrief
        {
            PublicBriefId = "projection-contract",
            CommissionId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Title = "Cobalt Joint Plate",
            CompanyDisplayName = "Test Company",
            Reference = "CA-PROGRESS",
            ViewState = CompanyCommissionPublicViewState.Published,
            Terms = new CompanyCommissionPublicTerms
            {
                Version = 4,
                Outputs =
                [
                    new CompanyCommissionOutputTerm(
                        lineId,
                        1,
                        "Cobalt Joint Plate",
                        10,
                        false)
                ],
                Payment = new CompanyCommissionPaymentTerms(
                    CompanyCommissionPaymentSchedule.Advance,
                    "Labor standard",
                    1_000,
                    0,
                    200,
                    1_200),
                PricingEvidence = new CompanyCommissionPricingEvidence(
                    "Selected routes",
                    "Aether",
                    "Siren",
                    CapturedAt)
            },
            Status = status,
            Gates = new CompanyCommissionPublicGateState(
                CompanyCommissionClearanceState.Satisfied,
                CompanyCommissionClearanceState.Satisfied,
                CompanyCommissionClearanceState.NotRequired),
            ClearedToWork = clearedToWork,
            IsClaimed = true,
            OutputProgress =
            [
                new CompanyCommissionPublicOutputProgress(
                    lineId,
                    1,
                    10,
                    3,
                    2,
                    0,
                    CapturedAt)
            ],
            DeliveryReadiness = new CompanyCommissionPublicDeliveryReadiness(
                status == TradeOrderStatus.AwaitingDelivery,
                status == TradeOrderStatus.AwaitingDelivery ? CapturedAt : null,
                null),
            SettlementState = CompanyCommissionSettlementState.NotDue,
            Closed = false,
            ProjectionRevision = 12
        };
    }
}
