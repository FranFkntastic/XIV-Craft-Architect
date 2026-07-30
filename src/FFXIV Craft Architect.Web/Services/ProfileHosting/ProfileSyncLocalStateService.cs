using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class ProfileSyncLocalStateService
{
    private const string ConnectedProfileNameKey = "profileHost.connectedProfileName";
    private const string ProfileStatePrefix = "profileHost.profile.";
    private const string LastSyncRevisionSuffix = "lastSyncRevision";
    private const string ObjectRevisionSuffix = "objectRevision.";
    private const string HostedObjectSuffix = "hostedObject.";
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
        var settings = await _indexedDb.LoadAllSettingsRequiredAsync();
        return new HostedProfileConnectionSettings
        {
            HostUrl = ReadSetting<string>(settings, ProfileSyncSettingsKeys.HostUrl),
            AccessKey = ReadSetting<string>(settings, ProfileSyncSettingsKeys.AccessKey),
            RememberAccessKey = ReadSetting(
                settings,
                ProfileSyncSettingsKeys.RememberAccessKey,
                false),
            ConnectedProfileId = ReadSetting<string>(
                settings,
                ProfileSyncSettingsKeys.ConnectedProfileId),
            ConnectedProfileName = ReadSetting<string>(settings, ConnectedProfileNameKey)
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

    public async Task<long> LoadLastSyncRevisionAsync(string profileId)
    {
        return await _indexedDb.LoadRequiredSettingAsync(
            BuildProfileStateKey(profileId, LastSyncRevisionSuffix),
            0L);
    }

    public async Task SaveLastSyncRevisionAsync(string profileId, long revision)
    {
        var key = BuildProfileStateKey(profileId, LastSyncRevisionSuffix);
        if (!await _indexedDb.SaveSettingAsync(key, revision))
        {
            throw new InvalidOperationException(
                "Browser storage could not persist the hosted-profile sync cursor.");
        }
    }

    public async Task<long> LoadObjectRevisionAsync(
        string profileId,
        string collection,
        string objectId)
    {
        return await _indexedDb.LoadRequiredSettingAsync(
            BuildObjectRevisionKey(profileId, collection, objectId),
            0L);
    }

    public async Task<bool> HasKnownHostedObjectAsync(
        string collection,
        string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        var hostedSuffix =
            $".{HostedObjectSuffix}{collection}.{Uri.EscapeDataString(objectId)}";
        var revisionSuffix =
            $".{ObjectRevisionSuffix}{collection}.{Uri.EscapeDataString(objectId)}";
        var settings = await _indexedDb.LoadAllSettingsRequiredAsync();
        return settings.Any(item =>
            item.Key.StartsWith(ProfileStatePrefix, StringComparison.Ordinal) &&
            (item.Key.EndsWith(hostedSuffix, StringComparison.Ordinal) ||
             item.Key.EndsWith(revisionSuffix, StringComparison.Ordinal)));
    }

    public async Task SaveHostedObjectProvenanceAsync(
        string profileId,
        string collection,
        string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        var key = BuildProfileStateKey(
            profileId,
            $"{HostedObjectSuffix}{collection}.{Uri.EscapeDataString(objectId)}");
        if (!await _indexedDb.SaveSettingAsync(key, true))
        {
            throw new InvalidOperationException(
                $"Browser storage could not persist hosted provenance for '{collection}/{objectId}'.");
        }
    }

    public async Task SaveObjectRevisionAsync(
        string profileId,
        string collection,
        string objectId,
        long revision)
    {
        var key = BuildObjectRevisionKey(profileId, collection, objectId);
        if (!await _indexedDb.SaveSettingAsync(key, revision))
        {
            throw new InvalidOperationException(
                $"Browser storage could not persist the hosted revision for '{collection}/{objectId}'.");
        }
    }

    public async Task<IReadOnlyList<ProfileSyncPendingSave>> LoadPendingSavesAsync(
        string profileId)
    {
        return await _indexedDb.LoadRequiredSettingAsync(
                   BuildProfileStateKey(profileId, PendingSavesSuffix),
                   Array.Empty<ProfileSyncPendingSave>())
               ?? Array.Empty<ProfileSyncPendingSave>();
    }

    public async Task SavePendingSavesAsync(
        string profileId,
        IReadOnlyList<ProfileSyncPendingSave> pendingSaves)
    {
        var key = BuildProfileStateKey(profileId, PendingSavesSuffix);
        if (!await _indexedDb.SaveSettingAsync(key, pendingSaves))
        {
            throw new InvalidOperationException(
                "Browser storage could not persist pending hosted-profile writes.");
        }
    }

    public async Task<string?> LoadConnectedProfileScopeIdAsync()
    {
        return (await LoadConnectionSettingsAsync()).ProfileScopeId;
    }

    public async Task<long> LoadObjectRevisionAsync(
        string collection,
        string objectId)
    {
        var profileId = await RequireConnectedProfileScopeIdAsync();
        return await LoadObjectRevisionAsync(profileId, collection, objectId);
    }

    public async Task SaveObjectRevisionAsync(
        string collection,
        string objectId,
        long revision)
    {
        var profileId = await RequireConnectedProfileScopeIdAsync();
        await SaveObjectRevisionAsync(profileId, collection, objectId, revision);
    }

    private static string BuildObjectRevisionKey(
        string profileId,
        string collection,
        string objectId)
    {
        return BuildProfileStateKey(
            profileId,
            $"{ObjectRevisionSuffix}{collection}.{Uri.EscapeDataString(objectId)}");
    }

    private async Task<string> RequireConnectedProfileScopeIdAsync()
    {
        return await LoadConnectedProfileScopeIdAsync()
            ?? throw new InvalidOperationException(
                "Hosted-profile sync state requires a connected profile ID.");
    }

    private static string BuildProfileStateKey(string profileId, string suffix)
    {
        profileId = NormalizeProfileScopeId(profileId);
        return $"{ProfileStatePrefix}{profileId}.{suffix}";
    }

    private static string NormalizeProfileScopeId(string profileId)
    {
        return Guid.TryParse(profileId, out var parsed) &&
               parsed != Guid.Empty
            ? parsed.ToString("D")
            : throw new InvalidOperationException(
                "Hosted-profile sync state requires a valid profile ID.");
    }

    private static T? ReadSetting<T>(
        IReadOnlyDictionary<string, string> settings,
        string key,
        T? defaultValue = default)
    {
        if (!settings.TryGetValue(key, out var serialized) ||
            string.IsNullOrEmpty(serialized))
        {
            return defaultValue;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(serialized);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Required browser setting '{key}' is invalid.",
                ex);
        }
    }

}
