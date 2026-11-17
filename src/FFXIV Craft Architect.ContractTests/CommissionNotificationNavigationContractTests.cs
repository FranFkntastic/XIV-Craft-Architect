using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CommissionNotificationNavigationContractTests
{
    private static readonly Guid CommissionId = Guid.Parse(
        "22222222-2222-2222-2222-222222222222");
    private static readonly Guid EventId = Guid.Parse(
        "33333333-3333-3333-3333-333333333333");

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
}
