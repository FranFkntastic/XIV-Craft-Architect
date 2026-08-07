using FFXIV_Craft_Architect.Core.Models;
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
    ProfileHostedTradeCompanyService companies)
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
            ct => profiles.AuthenticateAsync(key, accessKeyHasher, ct),
            cancellationToken);
        return profile != null &&
            Guid.TryParse(profile.ProfileId, out var profileId) &&
            profileId != Guid.Empty
                ? new MembershipAccount(profileId, profile)
                : null;
    }

    public async Task<TradeCompanyAccessContext?> ResolveCompanyAccessAsync(
        MembershipAccount account,
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        await companies.ResolveMembershipAccessAsync(
            account.ProfileId,
            companyId,
            cancellationToken);
}
