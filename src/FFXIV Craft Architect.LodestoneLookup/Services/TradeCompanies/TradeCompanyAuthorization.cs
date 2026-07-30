using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class TradeCompanyAuthorization(
    ProfileHostOptions options,
    ProfileHostedTradeCompanyService companies)
{
    private const string AccessKeyHeader = "X-Profile-Key";

    public async Task<TradeCompanyAccessContext?> ResolveAsync(
        HttpRequest request,
        string rawCompanyId,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled ||
            !CompanyId.TryParse(rawCompanyId, out var companyId))
        {
            return null;
        }

        var key = request.Headers[AccessKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var access = await companies.AuthenticateAsync(
            key,
            companyId,
            cancellationToken);
        return access is
        {
            Role: TradeCompanyRole.Owner,
            HostProfileId: not null
        } &&
            access.CompanyId == companyId
                ? access
                : null;
    }
}
