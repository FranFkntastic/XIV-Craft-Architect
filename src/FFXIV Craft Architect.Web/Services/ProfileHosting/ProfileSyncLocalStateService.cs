using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class ProfileSyncLocalStateService
{
    private const string ConnectedProfileNameKey = "profileHost.connectedProfileName";
    private const string ObjectRevisionPrefix = "profileHost.objectRevision.";

    private readonly IndexedDbService _indexedDb;

    public ProfileSyncLocalStateService(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
    }

    public static bool IsSyncedSetting(string key)
    {
        return !ProfileSyncSettingsKeys.ConnectionSettingKeys.Contains(key) &&
               !string.Equals(key, ConnectedProfileNameKey, StringComparison.OrdinalIgnoreCase) &&
               !key.StartsWith(ObjectRevisionPrefix, StringComparison.OrdinalIgnoreCase);
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

    private static string BuildObjectRevisionKey(string collection, string objectId)
    {
        return $"{ObjectRevisionPrefix}{collection}.{Uri.EscapeDataString(objectId)}";
    }
}
