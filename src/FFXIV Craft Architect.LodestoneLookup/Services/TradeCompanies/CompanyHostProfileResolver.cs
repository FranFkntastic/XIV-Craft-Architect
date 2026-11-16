using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public enum CompanyHostProfileStatus
{
    Resolved,
    Missing,
    Ambiguous
}

public sealed record CompanyHostProfile(
    CompanyId CompanyId,
    CompanyHostProfileStatus Status,
    string? ProfileId,
    IReadOnlyList<string> CandidateProfileIds);

// Generic profile sync accepts any payload from any authenticated profile, so a row
// claiming to belong to a company proves nothing on its own. Company-scoped server
// logic only trusts rows held by the company's single hosting profile: the one profile
// that holds the company row. A company whose row is held by several profiles, or by
// none, has no trusted host and is not acted on.
public sealed class CompanyHostProfileResolver(SqliteProfileHostStore profiles)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyDictionary<Guid, CompanyHostProfile>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        var holders = new Dictionary<Guid, HashSet<string>>();
        foreach (var item in await profiles.LoadObjectsAsync(
                     ProfileSyncCollections.TradeCompanyProfiles,
                     cancellationToken))
        {
            TradeCompanyProfile? company;
            try
            {
                company = JsonSerializer.Deserialize<TradeCompanyProfile>(item.Object.PayloadJson, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (company == null ||
                company.Id == Guid.Empty ||
                !string.Equals(item.Object.ObjectId, company.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!holders.TryGetValue(company.Id, out var profileIds))
            {
                holders[company.Id] = profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            profileIds.Add(item.ProfileId);
        }

        return holders.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var candidates = pair.Value.Order(StringComparer.Ordinal).ToArray();
                return candidates is [var only]
                    ? new CompanyHostProfile(new CompanyId(pair.Key), CompanyHostProfileStatus.Resolved, only, candidates)
                    : new CompanyHostProfile(new CompanyId(pair.Key), CompanyHostProfileStatus.Ambiguous, null, candidates);
            });
    }

    public async Task<CompanyHostProfile> ResolveAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        (await LoadAllAsync(cancellationToken)).TryGetValue(companyId.Value, out var host)
            ? host
            : new CompanyHostProfile(companyId, CompanyHostProfileStatus.Missing, null, []);

    // Live crafter rows of the company held by its hosting profile, keyed by a unique ID.
    public async Task<IReadOnlyList<HostedCompanyCrafter>> LoadCraftersAsync(
        string hostProfileId,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        var crafters = new List<HostedCompanyCrafter>();
        foreach (var item in await profiles.LoadProfileObjectsAsync(
                     hostProfileId,
                     ProfileSyncCollections.TradeCrafters,
                     cancellationToken))
        {
            try
            {
                var crafter = JsonSerializer.Deserialize<TradeCrafterProfile>(item.Object.PayloadJson, JsonOptions);
                if (crafter?.CompanyProfileId == companyId.Value &&
                    crafter.Id != Guid.Empty &&
                    !item.Object.Deleted &&
                    string.Equals(item.Object.ObjectId, crafter.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
                {
                    crafters.Add(new HostedCompanyCrafter(
                        hostProfileId,
                        crafter,
                        item.Object.Revision,
                        item.Object.PayloadJson));
                }
            }
            catch (JsonException)
            {
            }
        }

        return crafters
            .GroupBy(item => item.Crafter.Id)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();
    }
}
