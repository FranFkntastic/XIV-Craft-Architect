using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public static class TradeCompanyContext
{
    public static async Task<TradeCompanyProfile> ResolveAsync(
        TradeOperationsPersistenceService persistence,
        CompanyHubClient hubs,
        string? requestedCompany = null)
    {
        Guid? companyId = await persistence.LoadSelectedWorkspaceCompanyIdAsync();
        if (!string.IsNullOrWhiteSpace(requestedCompany))
        {
            if (!Guid.TryParse(requestedCompany, out var requestedId))
            {
                throw new InvalidOperationException("The requested company workspace identity is invalid.");
            }
            companyId = requestedId;
        }

        var profiles = await persistence.LoadCompanyProfilesAsync();
        var local = companyId.HasValue
            ? profiles.FirstOrDefault(profile => profile.Id == companyId.Value)
            : await persistence.GetOrCreateActiveCompanyProfileAsync();
        companyId ??= local!.Id;
        // Hosted access is checked before changing either durable selector. Local-only
        // profiles remain usable without a hosted account.
        var hub = await hubs.LoadWorkspaceHubAsync(companyId.Value);
        if (hub != null && (hub.Kind != "hub" || hub.Standing.State != "active"))
        {
            throw new InvalidOperationException("The selected company workspace is unavailable to this account.");
        }
        if (hub != null || local == null)
        {
            var resolved = await new TradeWorkspaceProfileResolver(persistence, hubs).ResolveAsync(companyId.Value);
            if (resolved.Profile.Id != companyId.Value)
            {
                throw new InvalidOperationException("The selected company workspace returned a different company identity.");
            }
            local = resolved.Profile;
        }

        await persistence.SelectWorkspaceCompanyAsync(companyId.Value);
        if (profiles.Any(profile => profile.Id == companyId.Value))
        {
            await persistence.SelectCompanyProfileAsync(companyId.Value);
        }
        return local;
    }
}
