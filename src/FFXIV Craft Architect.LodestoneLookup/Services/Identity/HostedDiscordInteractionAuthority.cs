using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

internal interface IDiscordCanonicalInteractionAuthority
{
    Task<DiscordParticipantAuthority?> ResolveAsync(
        DiscordIdentityLink link,
        DiscordInteractionTarget target,
        CancellationToken cancellationToken = default);
}

internal sealed class HostedDiscordInteractionAuthority(
    SqliteProfileHostStore profiles,
    ProfileHostedTradeCompanyService companies,
    SqliteMembershipStore memberships,
    HostedCompanyCommissionService commissions,
    SqliteDiscordNotificationStore discordContacts) :
    IDiscordCanonicalInteractionAuthority
{
    public async Task<DiscordParticipantAuthority?> ResolveAsync(
        DiscordIdentityLink link,
        DiscordInteractionTarget target,
        CancellationToken cancellationToken = default)
    {
        if (link.ProfileId == Guid.Empty ||
            link.DiscordUserId != target.DiscordUserId ||
            await profiles.LoadProfileAsync(
                link.ProfileId.ToString("D"),
                cancellationToken) == null)
        {
            return null;
        }

        var ownerAccess = await companies.ResolveProfileAccessAsync(
            link.ProfileId,
            target.CompanyId,
            cancellationToken);
        var membership = await memberships.LoadAsync(
            target.CompanyId,
            link.ProfileId,
            cancellationToken);
        var canonicalAccess = ownerAccess;
        if (canonicalAccess == null)
        {
            var ownership = await companies.ResolvePublicationOwnershipAsync(
                target.PublicBriefId,
                cancellationToken);
            if (ownership == null ||
                ownership.CompanyId != target.CompanyId ||
                ownership.OrderId != target.CommissionId)
            {
                return null;
            }

            canonicalAccess = await companies.ResolvePublicAccessAsync(
                ownership,
                cancellationToken);
        }

        if (canonicalAccess == null)
        {
            return null;
        }

        var snapshot = await commissions.LoadOwnerAsync(
            canonicalAccess,
            target.CommissionId,
            cancellationToken);
        var commission = snapshot?.Order.CompanyCommission;
        if (commission == null ||
            commission.CompanyId != target.CompanyId ||
            !string.Equals(
                commission.PublicMetadata.PublicBriefId,
                target.PublicBriefId,
                StringComparison.Ordinal) ||
            !Uri.TryCreate(
                commission.PublicMetadata.PublicUrl,
                UriKind.Absolute,
                out var publicUrl) ||
            publicUrl.Scheme != Uri.UriSchemeHttps &&
            !(publicUrl.Scheme == Uri.UriSchemeHttp && publicUrl.IsLoopback))
        {
            return null;
        }

        var participant = commission.ParticipantGrant;
        var claim = commission.ActiveClaim;
        var activeGrant = participant is { RevokedAtUtc: null } &&
            claim != null &&
            participant.ClaimId == claim.ClaimId;
        var isCompanyOperator = ownerAccess != null ||
            membership is
            {
                State: MembershipState.Active,
                Role: MembershipRole.Owner or MembershipRole.Operator
            };
        var contactMatches = activeGrant &&
            membership is { State: MembershipState.Active } &&
            await discordContacts.HasCommittedClaimContactAsync(
                  target.CompanyId,
                  target.CommissionId,
                  claim!.ClaimId,
                  target.DiscordUserId,
                  cancellationToken);
        if (!isCompanyOperator && !contactMatches)
        {
            return null;
        }

        return new DiscordParticipantAuthority(
            link.ProfileId,
            target.DiscordUserId,
            target.CompanyId,
            target.CommissionId,
            target.PublicBriefId,
            participant?.GrantId ?? Guid.Empty,
            participant?.CapabilityRevision ?? 0,
            publicUrl,
            IsCompanyOperator: isCompanyOperator,
            IsActiveParticipant: contactMatches);
    }
}
