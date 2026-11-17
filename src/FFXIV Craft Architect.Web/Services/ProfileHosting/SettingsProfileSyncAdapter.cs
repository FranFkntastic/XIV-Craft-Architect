using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class SettingsProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private readonly IndexedDbService _indexedDb;
    private readonly WebSettingsService _settings;

    public SettingsProfileSyncAdapter(
        IndexedDbService indexedDb,
        WebSettingsService settings)
    {
        _indexedDb = indexedDb;
        _settings = settings;
    }

    public string Collection => ProfileSyncCollections.Settings;

    public static IReadOnlyList<ProfileSyncObjectEnvelope> ToSyncObjects(
        IReadOnlyDictionary<string, string> settings,
        DateTime updatedAtUtc)
    {
        return settings
            .Where(setting => ProfileSyncLocalStateService.IsSyncedSetting(setting.Key))
            .Select(setting => new ProfileSyncObjectEnvelope
            {
                Collection = ProfileSyncCollections.Settings,
                ObjectId = setting.Key,
                PayloadJson = setting.Value,
                UpdatedAtUtc = updatedAtUtc
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct)
    {
        var settings = await _indexedDb.LoadAllSettingsRequiredAsync();
        return ToSyncObjects(settings, DateTime.UtcNow);
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        if (IgnoreUnsyncedKey(envelope.ObjectId))
        {
            return;
        }

        await _settings.ApplyHostedSettingAsync(
            envelope.ObjectId,
            envelope.PayloadJson,
            ct);
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        if (IgnoreUnsyncedKey(objectId))
        {
            return;
        }

        await _settings.ApplyHostedSettingAsync(objectId, "null", ct);
    }

    // A hosted profile keeps rows for settings that later stopped syncing (retired or made
    // browser-local). They must never be applied, but one stale row must not fail the whole
    // replay either — that withholds every order's details behind the restore banner.
    private static bool IgnoreUnsyncedKey(string key)
    {
        if (ProfileSyncLocalStateService.IsSyncedSetting(key))
        {
            return false;
        }

        Console.Error.WriteLine(
            $"[ProfileSync] Ignored hosted setting '{key}': it is not synced by this version.");
        return true;
    }
}
