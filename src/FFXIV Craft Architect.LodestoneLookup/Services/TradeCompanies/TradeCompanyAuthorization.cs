using FFXIV_Craft_Architect.Core.Models;
namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class TradeCompanyAuthorization(
    MembershipAccessResolver accessResolver)
{
    public async Task<TradeCompanyAccessContext?> ResolveAsync(
        HttpRequest request,
        string rawCompanyId,
        CancellationToken cancellationToken = default)
    {
        if (!CompanyId.TryParse(rawCompanyId, out var companyId))
        {
            return null;
        }

        var account = await accessResolver.ResolveAccountAsync(
            request,
            cancellationToken);
        if (account == null)
        {
            return null;
        }

        var access = await accessResolver.ResolveCompanyAccessAsync(
            account,
            companyId,
            cancellationToken);
        return access is
        {
            Role: TradeCompanyRole.Owner or TradeCompanyRole.Operator,
            HostProfileId: not null
        } &&
            access.CompanyId == companyId &&
            access.GrantId == account.ProfileId
            ? access
            : null;
    }
}
