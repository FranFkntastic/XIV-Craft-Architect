using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyCommissionProjectionPresentationContractTests
{
    private static readonly DateTime CapturedAt = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    public static void AssertAll()
    {
        var scenarios = new CompanyCommissionProjectionPresentationContractTests();
        scenarios.DiscordPublicationUsesCraftingLanguageAndCurrentOutputProgress();
        scenarios.DiscordPublicationCallsTheDeliveryStateReadyForDelivery();
        scenarios.OrderCenterKeepsLifecycleAndPlanMutationsOutOfCalculationDetails();
    }
    public void DiscordPublicationUsesCraftingLanguageAndCurrentOutputProgress() =>
        AssertPublication(TradeOrderStatus.Assigned, ["CRAFTING", "3 crafted, 2 ready"], "READY TO WORK", "IN PROGRESS");

    public void DiscordPublicationCallsTheDeliveryStateReadyForDelivery() =>
        AssertPublication(TradeOrderStatus.AwaitingDelivery, ["READY FOR DELIVERY"], "AWAITING DELIVERY");

    public void OrderCenterKeepsLifecycleAndPlanMutationsOutOfCalculationDetails()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var source = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.razor");
        var pageStyles = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.razor.css");
        var overviewStyles = ReadWebSource(repositoryRoot, "Shared", "TradeOrderCenterOverview.razor.css");
        var detailsStart = source.IndexOf("<details class=\"trade-orders-work-details\"", StringComparison.Ordinal);
        var detailsEnd = source.IndexOf("</details>", detailsStart, StringComparison.Ordinal);

        Assert.True(detailsStart >= 0 && detailsEnd > detailsStart);
        var centerBeforeDetails = source[..detailsStart];
        var calculationDetails = source[detailsStart..detailsEnd];
        Contains(centerBeforeDetails, "Crafter confirmed payment receipt", "crafterReceipt.TermsVersion",
            "OnClick=\"DeleteSelectedOrderAsync\"", "OpenCloseOrderDialogAsync(TradeOrderStatus.Canceled)");
        Omits(centerBeforeDetails, "crafterReceipt.TermsVersion == operationsCommission.CurrentTermsVersion");
        Omits(calculationDetails, "@bind-Value", "ValueChanged=", "OpenSupplyPlanAsync",
            "DeleteSelectedOrderAsync", "OpenCloseOrderDialogAsync");
        Omits(source, "Edit Supply Plan");
        Contains(source, "Text=\"Plan\"", "ChangeProcurementRowResponsibilityValueAsync",
            "class=\"trade-orders-timeline-visibility\" role=\"group\" aria-label=\"Choose timeline entry visibility\"",
            "aria-pressed=\"@(_timelineComposerVisibility == CommissionTimelineVisibility.CompanyOnly)\"",
            "aria-pressed=\"@(_timelineComposerVisibility == CommissionTimelineVisibility.Shared)\"");
        Contains(pageStyles, ".trade-orders-rail-group-title:focus-visible", ".trade-orders-rail-order:focus-visible",
            ".trade-orders-search-result:focus-visible", ".trade-orders-procurement-filter:focus-visible",
            ".trade-orders-timeline-filter:focus-visible",
            ".trade-orders-timeline-visibility button:focus-visible",
            "outline: 2px solid #f0cc62");
        Contains(overviewStyles, "container-type: inline-size", "@container (max-width: 620px)",
            ".trade-order-overview__crafter-update,",
            "button.trade-order-overview__step:focus-visible",
            "outline: 2px solid #f0cc62");
    }

    private static void AssertPublication(TradeOrderStatus status, string[] contains, params string[] omits)
    {
        var payload = SerializePublication(CreateBrief(status, clearedToWork: true));
        Contains(payload, contains);
        Omits(payload, omits);
    }

    private static void Contains(string source, params string[] values) =>
        Array.ForEach(values, value => Assert.Contains(value, source, StringComparison.Ordinal));

    private static void Omits(string source, params string[] values) =>
        Array.ForEach(values, value => Assert.DoesNotContain(value, source, StringComparison.Ordinal));

    private static string SerializePublication(CompanyCommissionPublicBrief brief) =>
        JsonSerializer.Serialize(
            CompanyCommissionDiscordMessage.CreatePublication(new(
                new CompanyId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                brief, new CompanyRecordRevision(12), Guid.Parse("22222222-2222-2222-2222-222222222222"), 12,
                CompanyCommissionActivityKind.ProgressReported, CapturedAt,
                "Progress updated.", new Uri("https://example.test/commission"), null)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static CompanyCommissionPublicBrief CreateBrief(TradeOrderStatus status, bool clearedToWork)
    {
        var lineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        return new()
        {
            PublicBriefId = "projection-contract",
            CommissionId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Title = "Cobalt Joint Plate",
            CompanyDisplayName = "Test Company",
            Reference = "CA-PROGRESS",
            ViewState = CompanyCommissionPublicViewState.Published,
            Terms = new()
            {
                Version = 4,
                Outputs = [new(lineId, 1, "Cobalt Joint Plate", 10, false)],
                Payment = new(CompanyCommissionPaymentSchedule.Advance, "Labor standard", 1_000, 0, 200, 1_200),
                PricingEvidence = new("Selected routes", "Aether", "Siren", CapturedAt)
            },
            Status = status,
            Gates = new(CompanyCommissionClearanceState.Satisfied, CompanyCommissionClearanceState.Satisfied, CompanyCommissionClearanceState.NotRequired),
            ClearedToWork = clearedToWork,
            IsClaimed = true,
            OutputProgress = [new(lineId, 1, 10, 3, 2, 0, CapturedAt)],
            DeliveryReadiness = new(status == TradeOrderStatus.AwaitingDelivery, status == TradeOrderStatus.AwaitingDelivery ? CapturedAt : null, null),
            SettlementState = CompanyCommissionSettlementState.NotDue,
            Closed = false,
            ProjectionRevision = 12
        };
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string ReadWebSource(string repositoryRoot, params string[] relativePath) =>
        File.ReadAllText(Path.Combine(repositoryRoot, "src", "FFXIV Craft Architect.Web", Path.Combine(relativePath)));
}
