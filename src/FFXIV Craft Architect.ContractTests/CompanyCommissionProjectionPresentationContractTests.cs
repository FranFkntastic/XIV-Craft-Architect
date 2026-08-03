using System.Reflection;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using FFXIV_Craft_Architect.Web.Pages;
namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyCommissionProjectionPresentationContractTests
{
    private static readonly DateTime CapturedAt = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
    public static void AssertAll()
    {
        var scenarios = new CompanyCommissionProjectionPresentationContractTests();
        scenarios.DiscordPublicationUsesCraftingLanguageAndCurrentOutputProgress();
        scenarios.DiscordPublicationCallsTheDeliveryStateReadyForDelivery();
        scenarios.OrderCenterAndDraftMigrationKeepAuthorityBoundariesExplicit();
    }
    public void DiscordPublicationUsesCraftingLanguageAndCurrentOutputProgress() =>
        AssertPublication(TradeOrderStatus.Assigned, ["CRAFTING", "3 crafted, 2 ready"], "READY TO WORK", "IN PROGRESS");
    public void DiscordPublicationCallsTheDeliveryStateReadyForDelivery() =>
        AssertPublication(TradeOrderStatus.AwaitingDelivery, ["READY FOR DELIVERY"], "AWAITING DELIVERY");
    public void OrderCenterAndDraftMigrationKeepAuthorityBoundariesExplicit()
    {
        var repositoryRoot = LocateRepositoryRoot();
        Assert.Equal(2, ReadWebSource(repositoryRoot, "Services", "TradeOrderDraftFactory.cs").Split("AuthoringSchemaVersion = TradeOrder.CurrentAuthoringSchemaVersion").Length - 1);
        var (legacy, modern) = (JsonSerializer.Deserialize<TradeOrder>("{}")!, new TradeOrder { AuthoringSchemaVersion = TradeOrder.CurrentAuthoringSchemaVersion });
        Assert.Equal(0, legacy.AuthoringSchemaVersion);
        Assert.True(CompanyCommissionSchemaMigrationHostedService.RequiresMigration(legacy));
        Assert.False(CompanyCommissionSchemaMigrationHostedService.RequiresMigration(modern));
        Assert.False(CompanyCommissionSchemaMigrationHostedService.RequiresMigration(new TradeOrder { AuthoringSchemaVersion = 2 }));
        Assert.Equal(modern.AuthoringSchemaVersion, TradeOrderWorkflow.CopyOrder(modern).AuthoringSchemaVersion);
        modern.Status = TradeOrderStatus.Assigned;
        modern.AssignedCrafterId = Guid.NewGuid();
        modern.CompanyCommission = CreateClearedAssignedCommission() with { ActiveClaim = null };
        Assert.True(CompanyCommissionSchemaMigrationHostedService.RequiresMigration(modern));
        var source = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.razor");
        var pageStyles = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.razor.css").ReplaceLineEndings("\n");
        var overviewStyles = ReadWebSource(repositoryRoot, "Shared", "TradeOrderCenterOverview.razor.css");
        var detailsStart = source.IndexOf("<details class=\"trade-orders-work-details\"", StringComparison.Ordinal);
        var workspaceStart = source.LastIndexOf("<main class=\"trade-orders-workspace\">", detailsStart, StringComparison.Ordinal);
        var selectedOrderStart = source.LastIndexOf("else if (BuildSelectedOrderCenterOverview() is { } centerOverview)", detailsStart, StringComparison.Ordinal);
        var cardStart = source.IndexOf("<section class=\"trade-orders-workspace-card\">", selectedOrderStart, StringComparison.Ordinal);
        var detailsEnd = source.IndexOf("</details>", detailsStart, StringComparison.Ordinal);
        Assert.True(workspaceStart >= 0 && selectedOrderStart > workspaceStart && cardStart > selectedOrderStart && detailsStart > cardStart && detailsEnd > detailsStart);
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
        Contains(pageStyles,
            ".trade-orders-page {\n  box-sizing: border-box;\n  display: flex;\n  flex-direction: column;\n  gap: 8px;\n  height: calc(100vh - 112px);\n  max-width: none !important;\n  padding: 12px 16px;\n  overflow: hidden;\n}",
            ".trade-orders-board {\n  display: grid;\n  flex: 1 1 auto;\n  grid-template-columns: 280px minmax(640px, 1fr) 6px var(--trade-orders-ops-width, clamp(720px, 32vw, 860px));\n  gap: 0;\n  height: auto;\n  min-height: 0;\n}",
            ".trade-orders-workspace {\n  min-height: 0;\n  overflow: hidden;\n}",
            ".trade-orders-workspace-card {\n  display: flex;\n  flex-direction: column;\n  height: 100%;\n  padding: 14px;\n  overflow: auto;\n}",
            "@media (max-width: 1279px) {\n  .trade-orders-page {\n    height: auto;\n    overflow: visible;\n  }");
        Contains(overviewStyles, "container-type: inline-size", "@container (max-width: 620px)",
            ".trade-order-overview__crafter-update,",
            "button.trade-order-overview__step:focus-visible",
            "outline: 2px solid #f0cc62");
        var commission = CreateClearedAssignedCommission();
        var order = new TradeOrder { Title = "Cobalt Joint Plate", Status = TradeOrderStatus.Assigned, CompanyCommission = commission };
        Assert.True(commission.ClearedToWork);
        Assert.Equal(commission.CurrentTermsVersion, commission.ParticipantAcknowledgedTermsVersion);
        Assert.Empty(commission.OutputProgress);
        Assert.DoesNotContain(commission.Activity, item => item.Kind == CompanyCommissionActivityKind.ProgressReported);
        var status = typeof(TradeOrders).GetMethod("FormatWorkbenchStatus", BindingFlags.NonPublic | BindingFlags.Static,
            null, [typeof(TradeOrder), typeof(TradeCompanyCommission)], null)!.Invoke(null, [order, commission]);
        var progress = (IReadOnlyList<TradeOrderProgressStepPresentation>)typeof(TradeOrders)
            .GetMethod("BuildCenterProgress", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [order, commission])!;
        Assert.Equal("Crafting", status);
        Assert.Contains(progress, step => step.Label == "Crafting" && step.State == "Current" && step.IsCurrent);
    }
    private static TradeCompanyCommission CreateClearedAssignedCommission()
    {
        var lineId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var actor = new CompanyCommissionActor("commissioner", CompanyCommissionActorKind.Commissioner);
        return new()
        {
            CommissionId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
            CompanyId = new(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            CommissionerActorId = actor.ActorId,
            Reference = "CA-ZERO-PROGRESS",
            CreatedAtUtc = CapturedAt,
            UpdatedAtUtc = CapturedAt,
            CurrentTermsVersion = 1,
            TermsVersions = [new CompanyCommissionTermsVersion
            {
                Version = 1, CreatedAtUtc = CapturedAt, CreatedBy = actor,
                Outputs = [new(lineId, 1, "Cobalt Joint Plate", 10, false)],
                Payment = new(CompanyCommissionPaymentSchedule.Advance, "Labor standard", 0, 0, 1_000, 1_000),
                PricingEvidence = new("Selected routes", "Aether", "Siren", CapturedAt)
            }],
            PublicMetadata = new() { PublicBriefId = "zero-progress", ViewState = CompanyCommissionPublicViewState.Published },
            ActiveClaimCapabilityRevision = 1,
            ActiveClaim = new(Guid.Parse("77777777-7777-7777-7777-777777777777"), 1, CapturedAt, Guid.Parse("88888888-8888-8888-8888-888888888888"), null),
            ParticipantAcknowledgedTermsVersion = 1,
            Gates = new(new(CompanyCommissionClearanceState.Satisfied), new(CompanyCommissionClearanceState.Satisfied),
                new(CompanyCommissionClearanceState.NotRequired, [])),
            DeliveryReadiness = new(false),
            SettlementState = CompanyCommissionSettlementState.NotDue,
            OutputProgress = [],
            Activity = []
        };
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
