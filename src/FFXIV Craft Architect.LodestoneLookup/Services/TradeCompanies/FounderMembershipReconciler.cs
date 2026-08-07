using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class FounderMembershipReconciler(
    ProfileHostOptions options,
    TradeMembershipOptions membershipOptions,
    SqliteProfileHostStore profiles,
    ITradeCompanyFounderBinder founderBinder,
    ILogger<FounderMembershipReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunReconciliationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Founder membership reconciliation failed.");
            }

            try
            {
                await Task.Delay(
                    membershipOptions.FounderReconciliationInterval,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task RunReconciliationAsync(CancellationToken cancellationToken)
    {
        var bindings = new List<(CompanyId CompanyId, Guid AccountProfileId)>();
        var companies = await profiles.LoadObjectsAsync(
            ProfileSyncCollections.TradeCompanyProfiles,
            cancellationToken);
        foreach (var hosted in companies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FounderMembershipBinding.TryRead(
                    hosted.ProfileId,
                    hosted.Object.ObjectId,
                    hosted.Object.PayloadJson,
                    out var companyId,
                    out var accountProfileId))
            {
                bindings.Add((companyId, accountProfileId));
            }
            else
            {
                logger.LogError(
                    "Founder membership reconciliation skipped hosted company {CompanyId} on profile {ProfileId}: object and payload identities do not match.",
                    hosted.Object.ObjectId,
                    hosted.ProfileId);
            }
        }

        foreach (var group in bindings.GroupBy(item => item.CompanyId))
        {
            var holders = group
                .Select(item => item.AccountProfileId)
                .Distinct()
                .OrderBy(item => item)
                .ToArray();
            if (holders.Length != 1)
            {
                logger.LogError(
                    "Founder membership reconciliation refused ambiguous company {CompanyId} held by active profiles {ProfileIds}.",
                    group.Key,
                    string.Join(",", holders));
                continue;
            }

            await founderBinder.BindFounderAsync(
                group.Key,
                holders[0],
                cancellationToken);
        }
    }
}

internal static class FounderMembershipBinding
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryRead(
        string profileId,
        string objectId,
        string payloadJson,
        out CompanyId companyId,
        out Guid accountProfileId)
    {
        companyId = default;
        accountProfileId = default;
        if (!Guid.TryParse(profileId, out accountProfileId) || accountProfileId == Guid.Empty)
        {
            return false;
        }

        try
        {
            var company = JsonSerializer.Deserialize<TradeCompanyProfile>(payloadJson, JsonOptions);
            if (company?.Id is not { } id ||
                id == Guid.Empty ||
                !CompanyId.TryParse(objectId, out var objectCompanyId) ||
                objectCompanyId.Value != id)
            {
                return false;
            }

            companyId = new CompanyId(id);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
