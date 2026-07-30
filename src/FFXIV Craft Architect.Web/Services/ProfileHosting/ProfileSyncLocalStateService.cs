using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class ProfileSyncLocalStateService
{
    private const string ConnectedProfileNameKey = "profileHost.connectedProfileName";
    private const string ProfileStatePrefix = "profileHost.profile.";
    private const string LastSyncRevisionSuffix = "lastSyncRevision";
    private const string ObjectRevisionSuffix = "objectRevision.";
    private const string PendingSavesSuffix = "pendingSaves";
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
        var serialized = new Dictionary<string, string>
        {
            [ProfileSyncSettingsKeys.HostUrl] =
                JsonSerializer.Serialize(settings.HostUrl ?? string.Empty),
            [ProfileSyncSettingsKeys.AccessKey] = JsonSerializer.Serialize(
                settings.RememberAccessKey
                    ? settings.AccessKey ?? string.Empty
                    : string.Empty),
            [ProfileSyncSettingsKeys.RememberAccessKey] =
                JsonSerializer.Serialize(settings.RememberAccessKey),
            [ProfileSyncSettingsKeys.ConnectedProfileId] =
                JsonSerializer.Serialize(settings.ConnectedProfileId ?? string.Empty),
            [ConnectedProfileNameKey] =
                JsonSerializer.Serialize(settings.ConnectedProfileName ?? string.Empty)
        };
        if (!await _indexedDb.SaveSettingsBatchAsync(serialized))
        {
            throw new InvalidOperationException(
                "Browser storage could not persist the hosted-profile connection.");
        }
    }

    public async Task<long> LoadLastSyncRevisionAsync()
    {
        return await _indexedDb.LoadSettingAsync(
            await BuildProfileStateKeyAsync(LastSyncRevisionSuffix),
            0L);
    }

    public async Task SaveLastSyncRevisionAsync(long revision)
    {
        var key = await BuildProfileStateKeyAsync(LastSyncRevisionSuffix);
        if (!await _indexedDb.SaveSettingAsync(key, revision))
        {
            throw new InvalidOperationException(
                "Browser storage could not persist the hosted-profile sync cursor.");
        }
    }

    public async Task<long> LoadObjectRevisionAsync(string collection, string objectId)
    {
        return await _indexedDb.LoadSettingAsync(
            await BuildObjectRevisionKeyAsync(collection, objectId),
            0L);
    }

    public async Task SaveObjectRevisionAsync(string collection, string objectId, long revision)
    {
        var key = await BuildObjectRevisionKeyAsync(collection, objectId);
        if (!await _indexedDb.SaveSettingAsync(key, revision))
        {
            throw new InvalidOperationException(
                $"Browser storage could not persist the hosted revision for '{collection}/{objectId}'.");
        }
    }

    public async Task<IReadOnlyList<ProfileSyncPendingSave>> LoadPendingSavesAsync()
    {
        return await _indexedDb.LoadSettingAsync(
                   await BuildProfileStateKeyAsync(PendingSavesSuffix),
                   Array.Empty<ProfileSyncPendingSave>())
               ?? Array.Empty<ProfileSyncPendingSave>();
    }

    public async Task SavePendingSavesAsync(IReadOnlyList<ProfileSyncPendingSave> pendingSaves)
    {
        var key = await BuildProfileStateKeyAsync(PendingSavesSuffix);
        if (!await _indexedDb.SaveSettingAsync(key, pendingSaves))
        {
            throw new InvalidOperationException(
                "Browser storage could not persist pending hosted-profile writes.");
        }
    }

    public async Task<string?> LoadConnectedProfileScopeIdAsync()
    {
        var rawProfileId = await _indexedDb.LoadSettingAsync<string>(
            ProfileSyncSettingsKeys.ConnectedProfileId);
        return Guid.TryParse(rawProfileId, out var profileId) &&
               profileId != Guid.Empty
            ? profileId.ToString("D")
            : null;
    }

    private async Task<string> BuildObjectRevisionKeyAsync(
        string collection,
        string objectId)
    {
        return await BuildProfileStateKeyAsync(
            $"{ObjectRevisionSuffix}{collection}.{Uri.EscapeDataString(objectId)}");
    }

    private async Task<string> BuildProfileStateKeyAsync(string suffix)
    {
        var profileId = await LoadConnectedProfileScopeIdAsync()
            ?? throw new InvalidOperationException(
                "Hosted-profile sync state requires a connected profile ID.");
        return $"{ProfileStatePrefix}{profileId}.{suffix}";
    }
}
