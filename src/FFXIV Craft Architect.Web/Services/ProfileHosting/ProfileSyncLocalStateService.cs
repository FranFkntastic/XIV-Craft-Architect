using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class ProfileSyncLocalStateService
{
    private const string ConnectedProfileNameKey = "profileHost.connectedProfileName";
    private const string ObjectRevisionPrefix = "profileHost.objectRevision.";
    private const string PendingSavesKey = "profileHost.pendingSaves";
    private static readonly IReadOnlySet<string> PortableSettingKeys = new HashSet<string>(
        [
            "market.default_datacenter",
            "market.region",
            "market.comparison_region",
            "market.home_world",
            "market.default_search_scope",
            "market.include_cross_world",
            "market.exclude_congested_worlds",
            "market.search_entire_region",
            "market.analysis_evidence_overlay",
            "procurement.search_entire_region",
            "procurement.region",
            "procurement.enable_split_world_purchases",
            "procurement.travel_tolerance",
            "procurement.world_exclusion_duration_minutes",
            "procurement.start_from_home_data_center",
            "procurement.travel_priority",
            "ui.accent_color",
            "ui.use_split_pane_market_view",
            "planning.default_recommendation_mode"
        ],
        StringComparer.Ordinal);

    private readonly IndexedDbService _indexedDb;

    public ProfileSyncLocalStateService(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
    }

    public static bool IsSyncedSetting(string key)
    {
        return PortableSettingKeys.Contains(key);
    }

    public async Task<HostedProfileConnectionSettings> LoadConnectionSettingsAsync()
    {
        return new HostedProfileConnectionSettings
        {
            HostUrl = await _indexedDb.LoadSettingAsync<string>(ProfileSyncSettingsKeys.HostUrl),
            AccessKey = await _indexedDb.LoadSettingAsync<string>(ProfileSyncSettingsKeys.AccessKey),
            RememberAccessKey = await _indexedDb.LoadSettingAsync(ProfileSyncSettingsKeys.RememberAccessKey, false),
            ConnectedProfileId = await _indexedDb.LoadSettingAsync<string>(ProfileSyncSettingsKeys.ConnectedProfileId),
            ConnectedProfileName = await _indexedDb.LoadSettingAsync<string>(ConnectedProfileNameKey)
        };
    }

    public async Task SaveConnectionSettingsAsync(HostedProfileConnectionSettings settings)
    {
        await _indexedDb.SaveSettingAsync(ProfileSyncSettingsKeys.HostUrl, settings.HostUrl ?? string.Empty);
        await _indexedDb.SaveSettingAsync(
            ProfileSyncSettingsKeys.AccessKey,
            settings.RememberAccessKey ? settings.AccessKey ?? string.Empty : string.Empty);
        await _indexedDb.SaveSettingAsync(ProfileSyncSettingsKeys.RememberAccessKey, settings.RememberAccessKey);
        await _indexedDb.SaveSettingAsync(ProfileSyncSettingsKeys.ConnectedProfileId, settings.ConnectedProfileId ?? string.Empty);
        await _indexedDb.SaveSettingAsync(ConnectedProfileNameKey, settings.ConnectedProfileName ?? string.Empty);
    }

    public async Task<long> LoadLastSyncRevisionAsync()
    {
        return await _indexedDb.LoadSettingAsync(ProfileSyncSettingsKeys.LastSyncRevision, 0L);
    }

    public async Task SaveLastSyncRevisionAsync(long revision)
    {
        await _indexedDb.SaveSettingAsync(ProfileSyncSettingsKeys.LastSyncRevision, revision);
    }

    public async Task<long> LoadObjectRevisionAsync(string collection, string objectId)
    {
        return await _indexedDb.LoadSettingAsync(BuildObjectRevisionKey(collection, objectId), 0L);
    }

    public async Task SaveObjectRevisionAsync(string collection, string objectId, long revision)
    {
        await _indexedDb.SaveSettingAsync(BuildObjectRevisionKey(collection, objectId), revision);
    }

    public async Task<IReadOnlyList<ProfileSyncPendingSave>> LoadPendingSavesAsync()
    {
        return await _indexedDb.LoadSettingAsync(
                   PendingSavesKey,
                   Array.Empty<ProfileSyncPendingSave>())
               ?? Array.Empty<ProfileSyncPendingSave>();
    }

    public async Task SavePendingSavesAsync(IReadOnlyList<ProfileSyncPendingSave> pendingSaves)
    {
        if (!await _indexedDb.SaveSettingAsync(PendingSavesKey, pendingSaves))
        {
            throw new InvalidOperationException(
                "Browser storage could not persist pending hosted-profile writes.");
        }
    }

    private static string BuildObjectRevisionKey(string collection, string objectId)
    {
        return $"{ObjectRevisionPrefix}{collection}.{Uri.EscapeDataString(objectId)}";
    }
}
