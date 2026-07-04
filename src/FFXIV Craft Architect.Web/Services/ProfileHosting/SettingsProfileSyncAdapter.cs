using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class SettingsProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private readonly IndexedDbService _indexedDb;

    public SettingsProfileSyncAdapter(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
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
        var settings = await _indexedDb.LoadAllSettingsAsync();
        return ToSyncObjects(settings, DateTime.UtcNow);
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        await _indexedDb.SaveSettingsBatchAsync(new Dictionary<string, string>
        {
            [envelope.ObjectId] = envelope.PayloadJson
        });
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        await _indexedDb.SaveSettingsBatchAsync(new Dictionary<string, string>
        {
            [objectId] = "null"
        });
    }
}
