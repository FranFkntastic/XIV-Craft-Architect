using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.Web.Pages;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CommissionNotificationNavigationContractTests
{
    private static readonly CompanyId CompanyId = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly Guid CommissionId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");
    private static readonly Guid EventId = Guid.Parse(
        "33333333-3333-3333-3333-333333333333");
    private static readonly Uri PublicUrl = new(
        "https://example.test/commission/brief");

    [Fact]
    public void NotificationLinksAreIdentityHintsWithoutCapabilities()
    {
        var operatorUrl = CompanyCommissionNotificationLinks.BuildOperatorActivityUrl(
            PublicUrl,
            CommissionId,
            EventId);
        var memberUrl = CompanyCommissionNotificationLinks.BuildMemberActivityUrl(
            PublicUrl,
            CompanyId,
            CommissionId,
            EventId);

        Assert.Equal(
            $"https://example.test/trade/orders?orderId={CommissionId:D}&activityId={EventId:D}",
            operatorUrl.AbsoluteUri);
        Assert.Equal(
            $"https://example.test/companies/{CompanyId.Value:D}" +
            $"?commissionId={CommissionId:D}&activityId={EventId:D}",
            memberUrl.AbsoluteUri);
        Assert.True(CompanyCommissionNotificationLinks.IsCanonicalOperatorActivityUrl(
            PublicUrl,
            operatorUrl,
            CommissionId,
            EventId));
        Assert.True(CompanyCommissionNotificationLinks.IsCanonicalMemberActivityUrl(
            PublicUrl,
            memberUrl,
            CompanyId,
            CommissionId,
            EventId));
        Assert.False(CompanyCommissionNotificationLinks.IsCanonicalOperatorActivityUrl(
            PublicUrl,
            new Uri(operatorUrl.AbsoluteUri + "&capability=secret"),
            CommissionId,
            EventId));
    }

    [Theory]
    [InlineData(CompanyCommissionActivityKind.ClaimResolutionRequired, "Review claim")]
    [InlineData(CompanyCommissionActivityKind.ProvisionalIdentitySubmitted, "Review identity")]
    [InlineData(CompanyCommissionActivityKind.PaymentPolicyChangeRequested, "Review payment")]
    [InlineData(CompanyCommissionActivityKind.CompanyMaterialsReceived, "View order")]
    [InlineData(CompanyCommissionActivityKind.ProgressReported, "View progress")]
    [InlineData(CompanyCommissionActivityKind.CommentAdded, "View comment")]
    [InlineData(CompanyCommissionActivityKind.DeliveryReadinessDeclared, "Review delivery")]
    public void EventSpecificActionsMatchTheOperatorDecision(
        CompanyCommissionActivityKind eventKind,
        string expectedLabel)
    {
        Assert.Equal(
            expectedLabel,
            DiscordCompanyCommissionPostCommitSink.ResolveActionLabel(
                eventKind,
                CreateBrief()));
    }

    [Fact]
    public void NotificationCopyStatesTheChangedFactAndResultingDecision()
    {
        var summary = DiscordCompanyCommissionPostCommitSink.BuildSummary(
            CreateActivity(
                CompanyCommissionActivityKind.ClaimAccepted,
                CompanyCommissionActorKind.Crafter),
            CreateBrief() with
            {
                Gates = new(
                    CompanyCommissionClearanceState.Pending,
                    CompanyCommissionClearanceState.Pending,
                    CompanyCommissionClearanceState.NotRequired)
            });

        Assert.Contains("claim was accepted", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Identity review is required", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiverResolvesOnlyAuthorizedOrderAndExactCanonicalActivity()
    {
        var order = CreateOrder(EventId);
        var url = CompanyCommissionNotificationLinks.BuildOperatorActivityUrl(
            PublicUrl,
            CommissionId,
            EventId);
        var hint = TradeOrderNotificationNavigation.Parse(url);
        var resolved = TradeOrderNotificationNavigation.Resolve(hint, [order]);

        Assert.True(hint.IsValid);
        Assert.Equal(TradeOrderNotificationNavigationStatus.Resolved, resolved.Status);
        Assert.Same(order, resolved.Order);
        Assert.Equal(EventId, resolved.ActivityEventId);

        var staleActivity = TradeOrderNotificationNavigation.Parse(
            CompanyCommissionNotificationLinks.BuildOperatorActivityUrl(
                PublicUrl,
                CommissionId,
                Guid.Parse("44444444-4444-4444-4444-444444444444")));
        var foreignOrder = TradeOrderNotificationNavigation.Parse(
            CompanyCommissionNotificationLinks.BuildOperatorActivityUrl(
                PublicUrl,
                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                EventId));
        var malformed = TradeOrderNotificationNavigation.Parse(new Uri(
            $"https://example.test/trade/orders?orderId={CommissionId:D}" +
            $"&orderId={CommissionId:D}&activityId={EventId:D}"));

        Assert.Equal(
            TradeOrderNotificationNavigationStatus.Unavailable,
            TradeOrderNotificationNavigation.Resolve(staleActivity, [order]).Status);
        Assert.Equal(
            TradeOrderNotificationNavigationStatus.Unavailable,
            TradeOrderNotificationNavigation.Resolve(foreignOrder, [order]).Status);
        Assert.Equal(
            TradeOrderNotificationNavigationStatus.Unavailable,
            TradeOrderNotificationNavigation.Resolve(malformed, [order]).Status);
    }

    [Fact]
    public void ExistingOrderOnlyNavigationStillRestoresDeviceLocalSelection()
    {
        var localOrder = new TradeOrder
        {
            Id = CommissionId,
            CompanyProfileId = CompanyId.Value,
            Title = "Local draft"
        };

        var resolved = TradeOrderNotificationNavigation.Resolve(
            TradeOrderNotificationNavigationHint.ForOrder(CommissionId),
            [localOrder]);

        Assert.Equal(TradeOrderNotificationNavigationStatus.Resolved, resolved.Status);
        Assert.Same(localOrder, resolved.Order);
        Assert.Null(resolved.ActivityEventId);
    }

    [Fact]
    public void AudienceRoutingKeepsOperatorAndMemberBoundariesDistinct()
    {
        var crafterProgress = CreateActivity(
            CompanyCommissionActivityKind.ProgressReported,
            CompanyCommissionActorKind.Crafter);
        var commissionerTerms = CreateActivity(
            CompanyCommissionActivityKind.TermsAmended,
            CompanyCommissionActorKind.Commissioner);
        var privateComment = CreateActivity(
            CompanyCommissionActivityKind.CommentAdded,
            CompanyCommissionActorKind.Crafter) with
        {
            Visibility = CompanyCommissionActivityVisibility.CompanyOnly
        };

        Assert.True(DiscordCompanyCommissionPostCommitSink.ShouldNotifyCommissioner(
            crafterProgress));
        Assert.True(DiscordCompanyCommissionPostCommitSink.ShouldNotifyMembers(
            crafterProgress));
        Assert.False(DiscordCompanyCommissionPostCommitSink.ShouldNotifyCommissioner(
            commissionerTerms));
        Assert.True(DiscordCompanyCommissionPostCommitSink.ShouldNotifyMembers(
            commissionerTerms));
        Assert.False(DiscordCompanyCommissionPostCommitSink.ShouldNotifyCommissioner(
            privateComment));
        Assert.False(DiscordCompanyCommissionPostCommitSink.ShouldNotifyMembers(
            privateComment));
    }

    private static CompanyCommissionActivityEvent CreateActivity(
        CompanyCommissionActivityKind kind,
        CompanyCommissionActorKind actorKind) =>
        new()
        {
            EventId = EventId,
            CommissionId = CommissionId,
            CommissionRevision = 1,
            Actor = new("actor", actorKind, "Contract actor"),
            SourceSurface = CompanyCommissionSourceSurface.TradeArchitect,
            CreatedAtUtc = DateTime.UtcNow,
            Kind = kind,
            TermsVersion = 1
        };

    private static TradeOrder CreateOrder(Guid eventId) => new()
    {
        Id = CommissionId,
        CompanyProfileId = CompanyId.Value,
        Title = "Contract commission",
        CompanyCommission = new TradeCompanyCommission
        {
            CommissionId = CommissionId,
            CompanyId = CompanyId,
            CommissionerActorId = "commissioner",
            Reference = "CONTRACT-1",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            CurrentTermsVersion = 1,
            PublicMetadata = new()
            {
                PublicBriefId = "brief",
                PublicUrl = PublicUrl.AbsoluteUri,
                ViewState = CompanyCommissionPublicViewState.Published
            },
            ActiveClaimCapabilityRevision = 1,
            Gates = new(
                new(CompanyCommissionClearanceState.Pending),
                new(CompanyCommissionClearanceState.Pending),
                new(CompanyCommissionClearanceState.NotRequired, [], null)),
            DeliveryReadiness = new(false),
            SettlementState = CompanyCommissionSettlementState.NotDue,
            Activity =
            [
                new CompanyCommissionActivityEvent
                {
                    EventId = eventId,
                    CommissionId = CommissionId,
                    CommissionRevision = 1,
                    Actor = new("crafter", CompanyCommissionActorKind.Crafter, "Contract Crafter"),
                    SourceSurface = CompanyCommissionSourceSurface.TradeArchitect,
                    CreatedAtUtc = DateTime.UtcNow,
                    Kind = CompanyCommissionActivityKind.ProgressReported,
                    TermsVersion = 1
                }
            ]
        }
    };

    private static CompanyCommissionPublicBrief CreateBrief() => new()
    {
        PublicBriefId = "brief",
        CommissionId = CommissionId,
        Title = "Contract commission",
        CompanyDisplayName = "Contract company",
        Reference = "CONTRACT-1",
        ViewState = CompanyCommissionPublicViewState.Published,
        Terms = new()
        {
            Version = 1,
            Payment = new(
                CompanyCommissionPaymentSchedule.Advance,
                "Advance",
                0,
                0,
                1_000,
                1_000),
            PricingEvidence = new("Contract", "Aether", "Siren", DateTime.UtcNow)
        },
        Status = TradeOrderStatus.Assigned,
        Gates = new(
            CompanyCommissionClearanceState.Satisfied,
            CompanyCommissionClearanceState.Pending,
            CompanyCommissionClearanceState.NotRequired),
        ClearedToWork = false,
        IsClaimed = true,
        DeliveryReadiness = new(false, null, null),
        SettlementState = CompanyCommissionSettlementState.NotDue,
        Closed = false,
        ProjectionRevision = 1
    };
}
