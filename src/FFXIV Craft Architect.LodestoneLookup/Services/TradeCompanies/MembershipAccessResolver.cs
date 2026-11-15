using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record MembershipAccount(
    Guid ProfileId,
    ProfileHostProfileResponse Profile);

public sealed class MembershipAccessResolver(
    ProfileHostOptions options,
    ProfileAuthenticationGate authentication,
    SqliteProfileHostStore profiles,
    ProfileAccessKeyHasher accessKeyHasher,
    ProfileHostedTradeCompanyService companies,
    SqliteDiscordIdentityStore identities,
    SqliteDiscordNotificationStore notifications)
{
    private const string AccessKeyHeader = "X-Profile-Key";

    public async Task<MembershipAccount?> ResolveAccountAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return null;
        }

        var key = request.Headers[AccessKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
        {
            return null;
        }

        var profile = await authentication.ExecuteAsync(
            key,
            ct => profiles.TryAuthenticateCachedAsync(key, accessKeyHasher, ct),
            ct => profiles.AuthenticateAsync(key, accessKeyHasher, ct),
            cancellationToken);
        return profile != null &&
            Guid.TryParse(profile.ProfileId, out var profileId) &&
            profileId != Guid.Empty
                ? new MembershipAccount(profileId, profile)
                : null;
    }

    public Task<TradeCompanyAccessContext?> ResolveCompanyAccessAsync(
        MembershipAccount account,
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        ResolveCompanyAccessCoreAsync(account, companyId, null, cancellationToken);

    // Same resolution, but reports when access was refused because the company identity is
    // ambiguous (duplicate hosted objects), so callers never treat that refusal as "merely
    // not an administrator".
    public async Task<CompanyAccessOutcome> ResolveCompanyAccessOutcomeAsync(
        MembershipAccount account,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        var ambiguity = new AmbiguityProbe();
        var access = await ResolveCompanyAccessCoreAsync(account, companyId, ambiguity, cancellationToken);
        return new CompanyAccessOutcome(access, ambiguity.Detected);
    }

    private async Task<TradeCompanyAccessContext?> ResolveCompanyAccessCoreAsync(
        MembershipAccount account,
        CompanyId companyId,
        AmbiguityProbe? ambiguity,
        CancellationToken cancellationToken)
    {
        var profileAccess = await companies.ResolveProfileAccessAsync(
            account.ProfileId,
            companyId,
            cancellationToken);
        if (profileAccess != null)
        {
            return profileAccess;
        }

        TradeCompanyAccessContext? membershipAccess;
        try
        {
            membershipAccess = await companies.ResolveMembershipAccessAsync(
                account.ProfileId,
                companyId,
                cancellationToken);
        }
        catch (DuplicateHostedObjectIdentityException)
        {
            ambiguity?.Mark();
            return null;
        }

        if (membershipAccess is
            { Role: TradeCompanyRole.Owner or TradeCompanyRole.Operator })
        {
            return membershipAccess;
        }

        var identity = await identities.LoadByProfileAsync(
            account.ProfileId,
            cancellationToken);
        var route = await notifications.LoadRouteAsync(
            companyId,
            cancellationToken);
        if (identity == null ||
            route == null ||
            !string.Equals(
                identity.DiscordUserId,
                route.CommissionerDiscordUserId,
                StringComparison.Ordinal))
        {
            return membershipAccess;
        }

        if (membershipAccess?.HostProfileId is { } hostProfileId &&
            hostProfileId != Guid.Empty)
        {
            return membershipAccess with
            {
                Role = TradeCompanyRole.Operator
            };
        }

        try
        {
            return await companies.ResolveDelegatedOperatorAccessAsync(
                account.ProfileId,
                companyId,
                cancellationToken);
        }
        catch (DuplicateHostedObjectIdentityException)
        {
            ambiguity?.Mark();
            return null;
        }
    }

    private sealed class AmbiguityProbe
    {
        public bool Detected { get; private set; }

        public void Mark() => Detected = true;
    }
}

public sealed record CompanyAccessOutcome(
    TradeCompanyAccessContext? Access,
    bool AmbiguousCompany);
