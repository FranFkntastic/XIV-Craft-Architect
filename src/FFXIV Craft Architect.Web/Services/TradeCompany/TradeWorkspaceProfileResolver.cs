using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed record TradeWorkspaceProfileResolution(
    TradeCompanyProfile Profile,
    bool IsHosted,
    long? HostedRevision);

public sealed class TradeWorkspaceProfileResolver(
    TradeOperationsPersistenceService persistence,
    CompanyHubClient companyHubs)
{
    public async Task<TradeWorkspaceProfileResolution> ResolveAsync(
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        var hosted = await companyHubs.TryLoadWorkspaceProfileAsync(companyId, cancellationToken);
        if (hosted != null)
        {
            return new TradeWorkspaceProfileResolution(
                hosted.ToTransientProfile(),
                IsHosted: true,
                hosted.Revision);
        }

        var local = (await persistence.LoadCompanyProfilesAsync())
            .FirstOrDefault(profile => profile.Id == companyId);
        return local != null
            ? new TradeWorkspaceProfileResolution(local, IsHosted: false, HostedRevision: null)
            : throw new InvalidOperationException(
                $"The selected Trade company '{companyId:D}' is unavailable.");
    }
}
