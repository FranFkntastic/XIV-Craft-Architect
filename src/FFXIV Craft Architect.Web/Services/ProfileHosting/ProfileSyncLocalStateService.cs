using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class ProfileSyncLocalStateService
{
    private const string ConnectedProfileNameKey = "profileHost.connectedProfileName";
    private const string ConnectedProfileMetadataRevisionKey =
        "profileHost.connectedProfileMetadataRevision";
    private const string AuthorityMigrationKey = "profileHost.authorityMigration.v1";
    private const string ProfileStatePrefix = "profileHost.authority.";
    private const string LegacyProfileStatePrefix = "profileHost.profile.";
    private const string LastSyncRevisionSuffix = "lastSyncRevision";
    private const string ObjectRevisionSuffix = "objectRevision.";
    private const string OwnerReceiptSuffix = "ownerReceipt.";
    private const string HostedObjectSuffix = "hostedObject.";
    private const string PendingSavesSuffix = "pendingSaves";
    private const string PendingOrderCleanupSuffix = "pendingOrderCleanup";
    private const string OrderTombstonesSuffix = "orderTombstones";
    private const string LinkedPlanSealMigrationSuffix = "migration.linkedPlanSeal.v2";
    private static readonly IReadOnlySet<string> PortableSettingKeys = new HashSet<string>(
        [
            "market.default_datacenter",
            "market.region",
            "market.comparison_region",
            "market.comparison_regions",
            "market.home_world",
            "market.default_search_scope",
            "market.include_cross_world",
            "market.exclude_congested_worlds",
            "market.analysis_evidence_overlay",
            "procurement.region",
            "procurement.enable_split_world_purchases",
            "procurement.travel_tolerance",
            "procurement.world_exclusion_duration_minutes",
            "procurement.start_from_home_data_center",
            "procurement.travel_priority",
            "ui.accent_color",
            "ui.use_split_pane_market_view",
            "ui.trade_orders_ops_pane_width",
            "planning.default_recommendation_mode"
        ],
        StringComparer.Ordinal);

    private readonly IndexedDbService _indexedDb;
    private readonly ProfileHostClientOptions _options;
    private string? _authorityScope;

    public ProfileSyncLocalStateService(
        IndexedDbService indexedDb,
        ProfileHostClientOptions options)
    {
        _indexedDb = indexedDb;
        _options = options;
    }

    public static bool IsSyncedSetting(string key)
    {
        return PortableSettingKeys.Contains(key);
    }

    public async Task<HostedProfileConnectionSettings> LoadConnectionSettingsAsync()
    {
        var settings = await _indexedDb.LoadAllSettingsRequiredAsync();
        var savedHostUrl = ReadSetting<string>(settings, ProfileSyncSettingsKeys.HostUrl);
        var authorityMigrationComplete = ReadSetting(settings, AuthorityMigrationKey, false);
        var hostUrl = ResolveEffectiveHostUrl(savedHostUrl);
        _authorityScope = NormalizeAuthorityScope(hostUrl);
        if (!authorityMigrationComplete)
        {
            var migrated = new Dictionary<string, string>
            {
                [ProfileSyncSettingsKeys.HostUrl] = JsonSerializer.Serialize(hostUrl),
                [AuthorityMigrationKey] = JsonSerializer.Serialize(true)
            };
            foreach (var item in settings.Where(item =>
                         item.Key.StartsWith(LegacyProfileStatePrefix, StringComparison.Ordinal)))
            {
                var destination = $"{ProfileStatePrefix}{_authorityScope}.profile." +
                    item.Key[LegacyProfileStatePrefix.Length..];
                if (!settings.ContainsKey(destination))
                {
                    migrated[destination] = item.Value;
                }
            }

            if (!await _indexedDb.SaveSettingsBatchAsync(migrated))
            {
                throw new InvalidOperationException(
                    "Browser storage could not migrate the hosted-profile authority.");
            }
        }

        return new HostedProfileConnectionSettings
        {
            HostUrl = hostUrl,
            AccessKey = ReadSetting<string>(settings, ProfileSyncSettingsKeys.AccessKey),
            RememberAccessKey = ReadSetting(
                settings,
                ProfileSyncSettingsKeys.RememberAccessKey,
                false),
            ConnectedProfileId = ReadSetting<string>(
                settings,
                ProfileSyncSettingsKeys.ConnectedProfileId),
            ConnectedProfileName = ReadSetting<string>(settings, ConnectedProfileNameKey),
            ConnectedProfileMetadataRevision = ReadSetting(
                settings,
                ConnectedProfileMetadataRevisionKey,
                0L)
        };
    }

    public async Task SaveConnectionSettingsAsync(HostedProfileConnectionSettings settings)
    {
        var hostUrl = ProfileHostClient.NormalizeHostUrl(
            settings.HostUrl ?? _options.DefaultHostUrl);
        var serialized = new Dictionary<string, string>
        {
            [ProfileSyncSettingsKeys.HostUrl] =
                JsonSerializer.Serialize(hostUrl),
            [ProfileSyncSettingsKeys.AccessKey] = JsonSerializer.Serialize(
                settings.RememberAccessKey
                    ? settings.AccessKey ?? string.Empty
                    : string.Empty),
            [ProfileSyncSettingsKeys.RememberAccessKey] =
                JsonSerializer.Serialize(settings.RememberAccessKey),
            [ProfileSyncSettingsKeys.ConnectedProfileId] =
                JsonSerializer.Serialize(settings.ConnectedProfileId ?? string.Empty),
            [ConnectedProfileNameKey] =
                JsonSerializer.Serialize(settings.ConnectedProfileName ?? string.Empty),
            [ConnectedProfileMetadataRevisionKey] =
                JsonSerializer.Serialize(settings.ConnectedProfileMetadataRevision),
            [AuthorityMigrationKey] = JsonSerializer.Serialize(true)
        };
        if (!await _indexedDb.SaveSettingsBatchAsync(serialized))
        {
            throw new InvalidOperationException(
                "Browser storage could not persist the hosted-profile connection.");
        }
        _authorityScope = NormalizeAuthorityScope(hostUrl);
    }

    public async Task<ConnectedProfileNameSaveResult> TrySaveConnectedProfileNameAsync(
        HostedProfileConnectionSettings expectedConnection,
        string displayName,
        long metadataRevision)
    {
        if (!expectedConnection.IsConfigured)
        {
            return ConnectedProfileNameSaveResult.ConnectionChanged;
        }

        var currentSettings = await _indexedDb.LoadAllSettingsRequiredAsync();
        var currentConnection = new HostedProfileConnectionSettings
        {
            HostUrl = ResolveEffectiveHostUrl(ReadSetting<string>(
                currentSettings,
                ProfileSyncSettingsKeys.HostUrl)),
            AccessKey = expectedConnection.AccessKey,
            ConnectedProfileId = ReadSetting<string>(
                currentSettings,
                ProfileSyncSettingsKeys.ConnectedProfileId)
        };
        if (!string.Equals(
                currentConnection.ConnectionScopeId,
                expectedConnection.ConnectionScopeId,
                StringComparison.Ordinal))
        {
            return ConnectedProfileNameSaveResult.ConnectionChanged;
        }

        var expectedSettings = new Dictionary<string, string>
        {
            [ProfileSyncSettingsKeys.HostUrl] = currentSettings[
                ProfileSyncSettingsKeys.HostUrl],
            [ProfileSyncSettingsKeys.ConnectedProfileId] = currentSettings[
                ProfileSyncSettingsKeys.ConnectedProfileId]
        };
        var settings = new Dictionary<string, string>
        {
            [ConnectedProfileNameKey] = JsonSerializer.Serialize(displayName),
            [ConnectedProfileMetadataRevisionKey] = JsonSerializer.Serialize(metadataRevision)
        };
        return (ConnectedProfileNameSaveResult)await _indexedDb
            .SaveSettingsWhenSettingsMatchAndRevisionNotNewerAsync(
                settings,
                expectedSettings,
                ConnectedProfileMetadataRevisionKey,
                metadataRevision);
    }

    public async Task<long> LoadLastSyncRevisionAsync(string profileId)
    {
        return await _indexedDb.LoadRequiredSettingAsync(
            await BuildProfileStateKeyAsync(profileId, LastSyncRevisionSuffix),
            0L);
    }

    public async Task SaveLastSyncRevisionAsync(string profileId, long revision)
    {
        var key = await BuildProfileStateKeyAsync(profileId, LastSyncRevisionSuffix);
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
        if (string.Equals(
                collection,
                ProfileSyncCollections.TradeOrders,
                StringComparison.Ordinal))
        {
            var revisions = await LoadObjectRevisionsAsync(
                profileId,
                collection,
                [objectId]);
            return revisions.GetValueOrDefault(objectId);
        }
        return await _indexedDb.LoadRequiredSettingAsync(
            await BuildObjectRevisionKeyAsync(profileId, collection, objectId),
            0L);
    }

    public async Task<IReadOnlyDictionary<string, long>> LoadObjectRevisionsAsync(
        string profileId,
        string collection,
        IEnumerable<string> objectIds)
    {
        var ids = objectIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var authorityScope = await RequireAuthorityScopeAsync();
        var settings = await _indexedDb.LoadAllSettingsRequiredAsync();
        var keys = ids.ToDictionary(
            objectId => objectId,
            objectId => BuildProfileStateKey(
                authorityScope,
                profileId,
                $"{ObjectRevisionSuffix}{collection}.{Uri.EscapeDataString(objectId)}"),
            StringComparer.Ordinal);
        var ownerState = string.Equals(
            collection,
            ProfileSyncCollections.TradeOrders,
            StringComparison.Ordinal)
            ? await _indexedDb.LoadHostedOwnerSettingsAsync(keys.Values.ToArray())
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var revisions = new Dictionary<string, long>(ids.Length, StringComparer.Ordinal);
        foreach (var objectId in ids)
        {
            var key = keys[objectId];
            revisions[objectId] = Math.Max(
                ReadSetting(ownerState, key, 0L),
                ReadSetting(settings, key, 0L));
        }

        return revisions;
    }

    public async Task<bool> HasKnownHostedObjectAsync(
        string collection,
        string objectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        var authorityPrefix = $"{ProfileStatePrefix}{await RequireAuthorityScopeAsync()}.profile.";
        var hostedSuffix =
            $".{HostedObjectSuffix}{collection}.{Uri.EscapeDataString(objectId)}";
        var revisionSuffix =
            $".{ObjectRevisionSuffix}{collection}.{Uri.EscapeDataString(objectId)}";
        var settings = await _indexedDb.LoadAllSettingsRequiredAsync();
        return settings.Any(item =>
            item.Key.StartsWith(authorityPrefix, StringComparison.Ordinal) &&
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
        var key = await BuildProfileStateKeyAsync(
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
        var key = await BuildObjectRevisionKeyAsync(profileId, collection, objectId);
        if (!await _indexedDb.SaveSettingAsync(key, revision))
        {
            throw new InvalidOperationException(
                $"Browser storage could not persist the hosted revision for '{collection}/{objectId}'.");
        }
    }

    public async Task<IReadOnlyDictionary<Guid, CompanyCommissionOwnerReceipt>>
        LoadOwnerReceiptsAsync(
            string profileId,
            IEnumerable<Guid> orderIds)
    {
        var ids = orderIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, CompanyCommissionOwnerReceipt>();
        }

        var authorityScope = await RequireAuthorityScopeAsync();
        var keys = ids.ToDictionary(
            orderId => orderId,
            orderId => BuildOwnerReceiptKey(authorityScope, profileId, orderId));
        var settings = await _indexedDb.LoadHostedOwnerSettingsAsync(keys.Values.ToArray());
        var receipts = new Dictionary<Guid, CompanyCommissionOwnerReceipt>();
        foreach (var orderId in ids)
        {
            var receipt = ReadSetting<CompanyCommissionOwnerReceipt>(
                settings,
                keys[orderId]);
            if (receipt?.OrderId == orderId)
            {
                receipts[orderId] = receipt;
            }
        }
        return receipts;
    }

    public async Task<bool> PersistOwnerVerificationBatchAsync(
        HostedProfileConnectionSettings expectedConnection,
        IReadOnlyList<HostedOwnerVerificationPersistenceItem> items)
    {
        ArgumentNullException.ThrowIfNull(expectedConnection);
        ArgumentNullException.ThrowIfNull(items);
        var profileId = expectedConnection.ProfileScopeId
            ?? throw new InvalidOperationException(
                "Hosted owner verification requires a captured profile authority.");
        var connectionScopeId = expectedConnection.ConnectionScopeId
            ?? throw new InvalidOperationException(
                "Hosted owner verification requires a captured connection scope.");
        var currentConnection = await LoadConnectionSettingsAsync();
        if (!string.Equals(
                connectionScopeId,
                currentConnection.ConnectionScopeId,
                StringComparison.Ordinal))
        {
            return false;
        }

        var authorityScope = NormalizeAuthorityScope(expectedConnection.HostUrl!);
        var currentSettings = await _indexedDb.LoadAllSettingsRequiredAsync();
        var expectedSettings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[]
                 {
                     ProfileSyncSettingsKeys.HostUrl,
                     ProfileSyncSettingsKeys.ConnectedProfileId
                 })
        {
            if (!currentSettings.TryGetValue(key, out var value))
            {
                return false;
            }
            expectedSettings[key] = value;
        }

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var deletedSettingKeys = new List<string>();
        var orders = new List<TradeOrder>();
        foreach (var item in items)
        {
            var revisionKey = BuildProfileStateKey(
                authorityScope,
                profileId,
                $"{ObjectRevisionSuffix}{ProfileSyncCollections.TradeOrders}." +
                Uri.EscapeDataString(item.OrderId.ToString("D")));
            expectedSettings[revisionKey] = JsonSerializer.Serialize(
                item.ExpectedProfileObjectRevision);
            var receiptKey = BuildOwnerReceiptKey(
                authorityScope,
                profileId,
                item.OrderId);
            if (item.Receipt != null)
            {
                settings[receiptKey] = JsonSerializer.Serialize(item.Receipt);
            }
            else if (item.ClearReceipt)
            {
                deletedSettingKeys.Add(receiptKey);
            }

            if (item.Order != null)
            {
                if (item.Order.Id != item.OrderId || item.Receipt == null)
                {
                    throw new InvalidOperationException(
                        "A changed hosted owner projection omitted its exact receipt identity.");
                }
                orders.Add(item.Order);
                settings[revisionKey] = JsonSerializer.Serialize(
                    item.Receipt.ProfileObjectRevision.Value);
            }
        }

        return await _indexedDb.ApplyHostedOwnerVerificationBatchAsync(
            orders,
            settings,
            deletedSettingKeys,
            expectedSettings);
    }

    public async Task SaveObjectRevisionAsync(
        HostedProfileConnectionSettings authority,
        string collection,
        string objectId,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var profileId = authority.ProfileScopeId
            ?? throw new InvalidOperationException(
                "Hosted-profile revision persistence requires a captured profile authority.");
        if (string.IsNullOrWhiteSpace(authority.HostUrl))
        {
            throw new InvalidOperationException(
                "Hosted-profile revision persistence requires a captured host authority.");
        }

        var key = BuildProfileStateKey(
            NormalizeAuthorityScope(authority.HostUrl),
            profileId,
            $"{ObjectRevisionSuffix}{collection}.{Uri.EscapeDataString(objectId)}");
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
                   await BuildProfileStateKeyAsync(profileId, PendingSavesSuffix),
                   Array.Empty<ProfileSyncPendingSave>())
               ?? Array.Empty<ProfileSyncPendingSave>();
    }

    public async Task SavePendingSavesAsync(
        string profileId,
        IReadOnlyList<ProfileSyncPendingSave> pendingSaves)
    {
        var key = await BuildProfileStateKeyAsync(profileId, PendingSavesSuffix);
        if (!await _indexedDb.SaveSettingAsync(key, pendingSaves))
        {
            throw new InvalidOperationException(
                "Browser storage could not persist pending hosted-profile writes.");
        }
    }

    public async Task<IReadOnlyList<string>> LoadPendingOrderCleanupAsync(string profileId)
    {
        return await _indexedDb.LoadRequiredSettingAsync(
                   await BuildProfileStateKeyAsync(profileId, PendingOrderCleanupSuffix),
                   Array.Empty<string>())
               ?? Array.Empty<string>();
    }

    public async Task SavePendingOrderCleanupAsync(
        string profileId,
        IReadOnlyList<string> orderObjectIds)
    {
        var key = await BuildProfileStateKeyAsync(profileId, PendingOrderCleanupSuffix);
        if (!await _indexedDb.SaveSettingAsync(key, orderObjectIds))
        {
            throw new InvalidOperationException(
                "Browser storage could not persist pending hosted-order cleanup.");
        }
    }

    public async Task<IReadOnlyDictionary<string, long>> LoadOrderTombstonesAsync(
        string profileId)
    {
        return await _indexedDb.LoadRequiredSettingAsync(
                   await BuildProfileStateKeyAsync(profileId, OrderTombstonesSuffix),
                   new Dictionary<string, long>())
               ?? new Dictionary<string, long>();
    }

    public async Task SaveOrderTombstoneAsync(
        string profileId,
        string orderObjectId,
        long revision)
    {
        var tombstones = new Dictionary<string, long>(
            await LoadOrderTombstonesAsync(profileId),
            StringComparer.OrdinalIgnoreCase);
        if (tombstones.TryGetValue(orderObjectId, out var existing) && existing >= revision)
        {
            return;
        }

        tombstones[orderObjectId] = revision;
        var key = await BuildProfileStateKeyAsync(profileId, OrderTombstonesSuffix);
        if (!await _indexedDb.SaveSettingAsync(key, tombstones))
        {
            throw new InvalidOperationException(
                $"Browser storage could not persist the hosted-order tombstone for '{orderObjectId}'.");
        }
    }

    public async Task ClearOrderTombstoneAsync(string profileId, string orderObjectId)
    {
        var tombstones = new Dictionary<string, long>(
            await LoadOrderTombstonesAsync(profileId),
            StringComparer.OrdinalIgnoreCase);
        if (!tombstones.Remove(orderObjectId))
        {
            return;
        }

        var key = await BuildProfileStateKeyAsync(profileId, OrderTombstonesSuffix);
        if (!await _indexedDb.SaveSettingAsync(key, tombstones))
        {
            throw new InvalidOperationException(
                $"Browser storage could not clear the hosted-order tombstone for '{orderObjectId}'.");
        }
    }

    public async Task<bool> IsLinkedPlanSealMigrationCompleteAsync(string profileId) =>
        await _indexedDb.LoadRequiredSettingAsync(
            await BuildProfileStateKeyAsync(profileId, LinkedPlanSealMigrationSuffix),
            false);

    public async Task SaveLinkedPlanSealMigrationCompleteAsync(string profileId)
    {
        var key = await BuildProfileStateKeyAsync(profileId, LinkedPlanSealMigrationSuffix);
        if (!await _indexedDb.SaveSettingAsync(key, true))
        {
            throw new InvalidOperationException(
                "Browser storage could not persist the linked-plan migration state.");
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

    private async Task<string> BuildObjectRevisionKeyAsync(
        string profileId,
        string collection,
        string objectId)
    {
        return await BuildProfileStateKeyAsync(
            profileId,
            $"{ObjectRevisionSuffix}{collection}.{Uri.EscapeDataString(objectId)}");
    }

    private async Task<string> RequireConnectedProfileScopeIdAsync()
    {
        return await LoadConnectedProfileScopeIdAsync()
            ?? throw new InvalidOperationException(
                "Hosted-profile sync state requires a connected profile ID.");
    }

    private async Task<string> BuildProfileStateKeyAsync(string profileId, string suffix)
    {
        var authorityScope = await RequireAuthorityScopeAsync();
        return BuildProfileStateKey(authorityScope, profileId, suffix);
    }

    private static string BuildProfileStateKey(
        string authorityScope,
        string profileId,
        string suffix) =>
        $"{ProfileStatePrefix}{authorityScope}.profile." +
        $"{NormalizeProfileScopeId(profileId)}.{suffix}";

    private static string BuildOwnerReceiptKey(
        string authorityScope,
        string profileId,
        Guid orderId) =>
        BuildProfileStateKey(
            authorityScope,
            profileId,
            $"{OwnerReceiptSuffix}{Uri.EscapeDataString(orderId.ToString("D"))}");

    private async Task<string> RequireAuthorityScopeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_authorityScope))
        {
            return _authorityScope;
        }

        await LoadConnectionSettingsAsync();
        return _authorityScope
            ?? throw new InvalidOperationException(
                "Hosted-profile sync state requires a valid authority URL.");
    }

    private string ResolveEffectiveHostUrl(string? savedHostUrl)
    {
        var defaultHostUrl = ProfileHostClient.NormalizeHostUrl(_options.DefaultHostUrl);
        if (string.IsNullOrWhiteSpace(savedHostUrl))
        {
            return defaultHostUrl;
        }

        return ProfileHostClient.NormalizeHostUrl(savedHostUrl);
    }

    private static string NormalizeAuthorityScope(string hostUrl)
    {
        var normalized = ProfileHostClient.NormalizeHostUrl(hostUrl);
        var uri = new Uri(normalized, UriKind.Absolute);
        var exactAuthority =
            $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}{uri.AbsolutePath}";
        return Uri.EscapeDataString(exactAuthority);
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

public sealed record HostedOwnerVerificationPersistenceItem(
    Guid OrderId,
    long ExpectedProfileObjectRevision,
    TradeOrder? Order,
    CompanyCommissionOwnerReceipt? Receipt,
    bool ClearReceipt = false);
