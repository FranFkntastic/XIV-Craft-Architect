using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class FounderMembershipReconciler(
    ProfileHostOptions options,
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

        var companies = await profiles.LoadObjectsAsync(
            ProfileSyncCollections.TradeCompanyProfiles,
            stoppingToken);
        foreach (var hosted in companies)
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (!FounderMembershipBinding.TryRead(
                    hosted.ProfileId,
                    hosted.Object.PayloadJson,
                    out var companyId,
                    out var accountProfileId))
            {
                logger.LogWarning(
                    "Founder membership reconciliation skipped invalid hosted company {CompanyId}.",
                    hosted.Object.ObjectId);
                continue;
            }

            await founderBinder.BindFounderAsync(
                companyId,
                accountProfileId,
                stoppingToken);
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
            if (company?.Id is not { } id || id == Guid.Empty)
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
