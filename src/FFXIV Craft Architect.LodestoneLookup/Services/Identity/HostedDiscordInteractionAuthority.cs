using FFXIV_Craft_Architect.Core.Models;
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
    SqliteDiscordNotificationStore discordContacts,
    LegacyCrafterAccountResolver crafterAccounts) :
    IDiscordCanonicalInteractionAuthority
{
    public async Task<DiscordParticipantAuthority?> ResolveAsync(
        DiscordIdentityLink link,
        DiscordInteractionTarget target,
        CancellationToken cancellationToken = default)
    {
        if (link.ProfileId == Guid.Empty ||
            link.DiscordUserId != target.DiscordUserId)
        {
            return null;
        }
        var profile = await profiles.LoadProfileAsync(
            link.ProfileId.ToString("D"),
            cancellationToken);
        if (profile == null)
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
        if (ownerAccess == null)
        {
            var route = await discordContacts.LoadRouteAsync(
                target.CompanyId,
                cancellationToken);
            if (route != null && string.Equals(
                    route.CommissionerDiscordUserId,
                    link.DiscordUserId,
                    StringComparison.Ordinal))
            {
                try
                {
                    ownerAccess = await companies.ResolveDelegatedOperatorAccessAsync(
                        link.ProfileId,
                        target.CompanyId,
                        cancellationToken);
                }
                catch (DuplicateHostedObjectIdentityException)
                {
                    return null;
                }
            }
        }
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
                target.PublicBriefId,
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
        // The claim belongs to the account that made it, member or not. Account
        // evidence is authoritative; legacy claims fall back to crafter bindings and the
        // committed Discord claim contact.
        var contactMatches = activeGrant &&
            await IsClaimantAsync(link, target, claim!, cancellationToken);
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

    private async Task<bool> IsClaimantAsync(
        DiscordIdentityLink link,
        DiscordInteractionTarget target,
        CompanyCommissionClaim claim,
        CancellationToken cancellationToken)
    {
        if (claim.AccountEvidence is { } evidence)
        {
            return string.Equals(
                       evidence.DiscordUserId,
                       target.DiscordUserId,
                       StringComparison.Ordinal) ||
                   evidence.ProfileId == link.ProfileId;
        }

        return await crafterAccounts.IsClaimOwnedByAccountAsync(
                   target.CompanyId,
                   claim,
                   link.ProfileId,
                   cancellationToken) ||
               await discordContacts.HasCommittedClaimContactAsync(
                   target.CompanyId,
                   target.CommissionId,
                   claim.ClaimId,
                   target.DiscordUserId,
                   cancellationToken);
    }
}
