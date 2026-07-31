using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed record ProfileSyncStatus(
    bool IsConnected,
    bool HostReachable,
    long LastSyncRevision,
    int PendingCount,
    int ConflictCount,
    DateTime? LastSyncedAtUtc,
    string? Message)
{
    public static ProfileSyncStatus LocalOnly() => new(false, false, 0, 0, 0, null, "Local only");
}

public enum FirstConnectMode
{
    UploadLocal,
    DownloadRemote
}

public sealed record ProfileSyncPendingSave(string Collection, string ObjectId);

public sealed record ProfileSyncConflict(
    string Collection,
    string ObjectId,
    long LocalRevision,
    long RemoteRevision,
    ProfileSyncObjectEnvelope RemoteObject);

public sealed record ProfileSyncBootstrapPreview(
    int LocalObjectCount,
    int RemoteObjectCount,
    bool ContentsMatch);

public sealed class ProfileSyncService
{
    private const int ChangePageSize = 1;
    private readonly ProfileHostClient _client;
    private readonly ProfileSyncLocalStateService _localState;
    private readonly IReadOnlyDictionary<string, IProfileSyncCollectionAdapter> _adapters;
    private readonly List<ProfileSyncPendingSave> _pendingSaves = [];
    private readonly List<ProfileSyncConflict> _conflicts = [];
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _suppressionDepth;
    private string? _pendingSavesProfileId;

    public ProfileSyncService(
        ProfileHostClient client,
        ProfileSyncLocalStateService localState,
        WebSettingsService settings,
        IEnumerable<IProfileSyncCollectionAdapter> adapters)
    {
        _client = client;
        _localState = localState;
        _adapters = adapters.ToDictionary(adapter => adapter.Collection, StringComparer.OrdinalIgnoreCase);
        settings.PortableSettingSaved += QueuePortableSettingSaveAsync;
    }

    public event Action? StatusChanged;

    public ProfileSyncStatus CurrentStatus { get; private set; } = ProfileSyncStatus.LocalOnly();

    public IReadOnlyList<ProfileSyncPendingSave> PendingSaves => _pendingSaves;
    public IReadOnlyList<ProfileSyncConflict> Conflicts => _conflicts;

    public bool IsSuppressed => _suppressionDepth > 0;

    public Task InitializeAsync(CancellationToken ct = default) =>
        RunSerializedAsync(() => SyncNowCoreAsync(ct), ct);

    public Task SyncNowAsync(CancellationToken ct = default) =>
        RunSerializedAsync(() => SyncNowCoreAsync(ct), ct);

    public Task<long> EnsureHostedObjectRevisionAsync(
        string collection,
        string objectId,
        CancellationToken ct = default) =>
        RunSerializedAsync(
            () => EnsureHostedObjectRevisionCoreAsync(collection, objectId, ct),
            ct);

    private async Task<long> EnsureHostedObjectRevisionCoreAsync(
        string collection,
        string objectId,
        CancellationToken ct)
    {
        var settings = await _localState.LoadConnectionSettingsAsync();
        var profileId = settings.ProfileScopeId;
        await EnsurePendingSavesLoadedAsync(profileId);
        if (!settings.IsConfigured || profileId == null)
        {
            return 0;
        }

        var knownRevision = await _localState.LoadObjectRevisionAsync(
            profileId,
            collection,
            objectId);
        if (knownRevision > 0)
        {
            return knownRevision;
        }

        var adapter = GetAdapter(collection);
        var localObject = (await adapter.LoadLocalObjectsAsync(ct))
            .FirstOrDefault(item => string.Equals(
                item.ObjectId,
                objectId,
                StringComparison.Ordinal));
        if (localObject == null)
        {
            return 0;
        }

        var remoteBootstrap = await _client.ExportBootstrapAsync(
            settings.HostUrl!,
            settings.AccessKey!,
            ct);
        var remoteObject = remoteBootstrap.Objects.FirstOrDefault(item =>
            string.Equals(item.Collection, collection, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.ObjectId, objectId, StringComparison.Ordinal));
        if (remoteObject == null)
        {
            await QueueLocalSaveCoreAsync(collection, objectId, ct);
            return await _localState.LoadObjectRevisionAsync(
                profileId,
                collection,
                objectId);
        }

        await _localState.SaveHostedObjectProvenanceAsync(
            profileId,
            collection,
            objectId);
        if (remoteObject.Deleted != localObject.Deleted ||
            !string.Equals(
                remoteObject.PayloadJson,
                localObject.PayloadJson,
                StringComparison.Ordinal))
        {
            await AddPendingSaveAsync(profileId, collection, objectId);
            _conflicts.RemoveAll(item => IsSameIdentity(
                item.Collection,
                item.ObjectId,
                collection,
                objectId));
            _conflicts.Add(new ProfileSyncConflict(
                collection,
                objectId,
                0,
                remoteObject.Revision,
                remoteObject));
            SetStatus(CurrentStatus with
            {
                ConflictCount = _conflicts.Count,
                PendingCount = _pendingSaves.Count,
                Message = "Conflicts need review"
            });
            return 0;
        }

        await _localState.SaveObjectRevisionAsync(
            profileId,
            collection,
            objectId,
            remoteObject.Revision);
        return remoteObject.Revision;
    }

    private async Task SyncNowCoreAsync(CancellationToken ct)
    {
        var settings = await _localState.LoadConnectionSettingsAsync();
        var profileId = settings.ProfileScopeId;
        await EnsurePendingSavesLoadedAsync(profileId);
        if (!settings.IsConfigured || profileId == null)
        {
            SetStatus(ProfileSyncStatus.LocalOnly() with { PendingCount = _pendingSaves.Count });
            return;
        }

        var lastRevision = 0L;
        try
        {
            lastRevision = await _localState.LoadLastSyncRevisionAsync(profileId);
            var serverRevision = lastRevision;
            var hasMore = true;
            while (hasMore)
            {
                var changes = await _client.GetChangesAsync(
                    settings.HostUrl!,
                    settings.AccessKey!,
                    serverRevision,
                    ChangePageSize,
                    ct);
                using (SuppressNotifications())
                {
                    foreach (var item in changes.Objects)
                    {
                        if (IsPending(item.Collection, item.ObjectId))
                        {
                            continue;
                        }

                        var adapter = GetAdapter(item.Collection);
                        if (item.Deleted)
                        {
                            await adapter.DeleteLocalObjectAsync(item.ObjectId, ct);
                        }
                        else
                        {
                            await _localState.SaveHostedObjectProvenanceAsync(
                                profileId,
                                item.Collection,
                                item.ObjectId);
                            await adapter.ApplyRemoteObjectAsync(item, ct);
                        }

                        await _localState.SaveObjectRevisionAsync(
                            profileId,
                            item.Collection,
                            item.ObjectId,
                            item.Revision);
                    }
                }

                if (changes.HasMore && changes.ServerRevision <= serverRevision)
                {
                    throw new InvalidOperationException(
                        "The profile host returned a non-advancing changes page.");
                }

                serverRevision = changes.ServerRevision;
                lastRevision = serverRevision;
                hasMore = changes.HasMore;
                await _localState.SaveLastSyncRevisionAsync(profileId, serverRevision);
            }

            var hostReachable = await RetryPendingSavesAsync(
                settings,
                profileId,
                ct);
            SetStatus(new ProfileSyncStatus(
                true,
                hostReachable,
                serverRevision,
                _pendingSaves.Count,
                _conflicts.Count,
                DateTime.UtcNow,
                _conflicts.Count > 0
                    ? "Conflicts need review"
                    : _pendingSaves.Count > 0
                        ? "Local changes pending"
                        : "Synced"));
        }
        catch (Exception ex)
        {
            SetStatus(new ProfileSyncStatus(
                true,
                false,
                lastRevision,
                _pendingSaves.Count,
                _conflicts.Count,
                CurrentStatus.LastSyncedAtUtc,
                $"Host unreachable: {ex.Message}"));
        }
    }

    public Task QueueLocalSaveAsync(
        string collection,
        string objectId,
        CancellationToken ct = default)
    {
        if (IsSuppressed)
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(
            () => QueueLocalSaveCoreAsync(collection, objectId, ct),
            ct);
    }

    public Task DeleteObjectAsync(
        string collection,
        string objectId,
        CancellationToken ct = default) =>
        DeleteObjectsAsync([(collection, objectId)], ct);

    public Task DeleteObjectsAsync(
        IReadOnlyList<(string Collection, string ObjectId)> objects,
        CancellationToken ct = default) =>
        RunSerializedAsync(
            () => DeleteObjectsCoreAsync(objects, ct),
            ct);

    private async Task DeleteObjectsCoreAsync(
        IReadOnlyList<(string Collection, string ObjectId)> objects,
        CancellationToken ct)
    {
        if (objects.Count == 0)
        {
            return;
        }

        var distinct = objects
            .DistinctBy(item => $"{item.Collection}\0{item.ObjectId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var item in distinct)
        {
            _ = GetAdapter(item.Collection);
        }

        var settings = await _localState.LoadConnectionSettingsAsync();
        var profileId = settings.ProfileScopeId;
        if (!settings.IsConfigured || profileId == null)
        {
            foreach (var item in distinct)
            {
                if (await _localState.HasKnownHostedObjectAsync(
                        item.Collection,
                        item.ObjectId))
                {
                    throw new InvalidOperationException(
                        "Reconnect this browser to its hosted profile before deleting hosted data.");
                }
            }

            foreach (var item in distinct)
            {
                await GetAdapter(item.Collection).DeleteLocalObjectAsync(item.ObjectId, ct);
            }
            return;
        }

        await EnsurePendingSavesLoadedAsync(profileId);
        var remote = await _client.ExportBootstrapAsync(
            settings.HostUrl!,
            settings.AccessKey!,
            ct);
        foreach (var item in distinct)
        {
            var remoteObject = remote.Objects.FirstOrDefault(candidate =>
                string.Equals(candidate.Collection, item.Collection, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ObjectId, item.ObjectId, StringComparison.Ordinal));
            if (remoteObject == null)
            {
                continue;
            }

            var response = await _client.DeleteObjectAsync(
                settings.HostUrl!,
                settings.AccessKey!,
                item.Collection,
                item.ObjectId,
                remoteObject.Revision,
                ct);
            if (response.Conflict)
            {
                throw new InvalidOperationException(
                    $"Hosted {item.Collection}/{item.ObjectId} changed while it was being deleted. Try the action again.");
            }
            if (!response.Success || response.Object == null)
            {
                throw new InvalidOperationException(
                    $"The hosted profile did not confirm deletion of {item.Collection}/{item.ObjectId}.");
            }

            await _localState.SaveObjectRevisionAsync(
                profileId,
                item.Collection,
                item.ObjectId,
                response.Object.Revision);
        }

        foreach (var item in distinct)
        {
            await GetAdapter(item.Collection).DeleteLocalObjectAsync(item.ObjectId, ct);
            await RemovePendingSaveAsync(profileId, item.Collection, item.ObjectId);
            _conflicts.RemoveAll(conflict => IsSameIdentity(
                conflict.Collection,
                conflict.ObjectId,
                item.Collection,
                item.ObjectId));
        }
    }

    private async Task QueueLocalSaveCoreAsync(
        string collection,
        string objectId,
        CancellationToken ct)
    {
        if (IsSuppressed)
        {
            return;
        }

        var settings = await _localState.LoadConnectionSettingsAsync();
        var profileId = settings.ProfileScopeId;
        await EnsurePendingSavesLoadedAsync(profileId);
        if (!settings.IsConfigured || profileId == null)
        {
            return;
        }

        var adapter = GetAdapter(collection);
        var localObject = (await adapter.LoadLocalObjectsAsync(ct)).FirstOrDefault(item => item.ObjectId == objectId);
        if (localObject == null)
        {
            return;
        }

        await _localState.SaveHostedObjectProvenanceAsync(
            profileId,
            collection,
            objectId);
        await AddPendingSaveAsync(profileId, collection, objectId);
        var hostReachable = await TryPushPendingSaveAsync(
            settings,
            profileId,
            new ProfileSyncPendingSave(collection, objectId),
            ct);

        var lastRevision = await _localState.LoadLastSyncRevisionAsync(profileId);
        SetStatus(new ProfileSyncStatus(
            true,
            hostReachable,
            lastRevision,
            _pendingSaves.Count,
            _conflicts.Count,
            CurrentStatus.LastSyncedAtUtc,
            _conflicts.Count > 0 ? "Conflicts need review" : CurrentStatus.Message));
    }

    public Task ConnectAsync(
        HostedProfileConnectionSettings settings,
        FirstConnectMode mode,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.Snapshot();
        return RunSerializedAsync(
            () => ConnectCoreAsync(snapshot, mode, ct),
            ct);
    }

    public async Task<ProfileSyncBootstrapPreview> PreviewFirstConnectAsync(
        HostedProfileConnectionSettings settings,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                "A verified hosted profile ID, host URL, and access key are required.");
        }

        var local = await LoadLocalBootstrapObjectsAsync(ct);
        var remote = await _client.ExportBootstrapAsync(
            settings.HostUrl!,
            settings.AccessKey!,
            ct);
        return new ProfileSyncBootstrapPreview(
            local.Count,
            remote.Objects.Count,
            BootstrapContentsMatch(local, remote.Objects));
    }

    private async Task ConnectCoreAsync(
        HostedProfileConnectionSettings settings,
        FirstConnectMode mode,
        CancellationToken ct)
    {
        var profileId = settings.ProfileScopeId;
        if (!settings.IsConfigured || profileId == null)
        {
            throw new InvalidOperationException(
                "A verified hosted profile ID, host URL, and access key are required.");
        }

        await _localState.SaveConnectionSettingsAsync(settings);
        await EnsurePendingSavesLoadedAsync(profileId);
        if (mode == FirstConnectMode.UploadLocal)
        {
            var objects = await LoadLocalBootstrapObjectsAsync(ct);
            foreach (var item in objects)
            {
                await _localState.SaveHostedObjectProvenanceAsync(
                    profileId,
                    item.Collection,
                    item.ObjectId);
            }

            var response = await _client.UploadBootstrapAsync(
                settings.HostUrl ?? string.Empty,
                settings.AccessKey ?? string.Empty,
                new ProfileHostBootstrapPayload { Objects = objects },
                ct);
            await _localState.SaveLastSyncRevisionAsync(
                profileId,
                response.ServerRevision);
            _pendingSaves.Clear();
            await PersistPendingSavesAsync(profileId);
            SetStatus(new ProfileSyncStatus(
                true,
                true,
                response.ServerRevision,
                _pendingSaves.Count,
                _conflicts.Count,
                DateTime.UtcNow,
                "Uploaded local profile"));
        }
        else
        {
            await SyncNowCoreAsync(ct);
        }
    }

    private async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalBootstrapObjectsAsync(
        CancellationToken ct)
    {
        var objects = new List<ProfileSyncObjectEnvelope>();
        foreach (var adapter in _adapters.Values.OrderBy(adapter => adapter.Collection, StringComparer.Ordinal))
        {
            objects.AddRange(await adapter.LoadLocalObjectsAsync(ct));
        }

        return objects;
    }

    private static bool BootstrapContentsMatch(
        IReadOnlyList<ProfileSyncObjectEnvelope> local,
        IReadOnlyList<ProfileSyncObjectEnvelope> remote)
    {
        if (local.Count != remote.Count)
        {
            return false;
        }

        var remoteByIdentity = remote.ToDictionary(
            item => $"{item.Collection}\0{item.ObjectId}",
            StringComparer.Ordinal);
        return local.All(item =>
            remoteByIdentity.TryGetValue(
                $"{item.Collection}\0{item.ObjectId}",
                out var remoteItem) &&
            item.Deleted == remoteItem.Deleted &&
            string.Equals(item.PayloadJson, remoteItem.PayloadJson, StringComparison.Ordinal));
    }

    public Task DisconnectAsync(CancellationToken ct = default) =>
        RunSerializedAsync(DisconnectCoreAsync, ct);

    private async Task DisconnectCoreAsync()
    {
        await _localState.SaveConnectionSettingsAsync(new HostedProfileConnectionSettings());
        _pendingSaves.Clear();
        _conflicts.Clear();
        _pendingSavesProfileId = null;
        SetStatus(ProfileSyncStatus.LocalOnly());
    }

    public Task AcceptRemoteConflictAsync(
        ProfileSyncConflict conflict,
        CancellationToken ct = default) =>
        RunSerializedAsync(
            () => AcceptRemoteConflictCoreAsync(conflict, ct),
            ct);

    private async Task AcceptRemoteConflictCoreAsync(
        ProfileSyncConflict conflict,
        CancellationToken ct)
    {
        var settings = await _localState.LoadConnectionSettingsAsync();
        var profileId = settings.ProfileScopeId
            ?? throw new InvalidOperationException(
                "A connected hosted profile is required to resolve conflicts.");
        var adapter = GetAdapter(conflict.Collection);
        using (SuppressNotifications())
        {
            await _localState.SaveHostedObjectProvenanceAsync(
                profileId,
                conflict.Collection,
                conflict.ObjectId);
            await adapter.ApplyRemoteObjectAsync(conflict.RemoteObject, ct);
            await _localState.SaveObjectRevisionAsync(
                profileId,
                conflict.Collection,
                conflict.ObjectId,
                conflict.RemoteRevision);
        }

        _conflicts.Remove(conflict);
        await RemovePendingSaveAsync(
            profileId,
            conflict.Collection,
            conflict.ObjectId);
        RefreshStatusMessage("Remote version applied");
    }

    public Task KeepLocalConflictAsync(
        ProfileSyncConflict conflict,
        CancellationToken ct = default) =>
        RunSerializedAsync(
            () => KeepLocalConflictCoreAsync(conflict, ct),
            ct);

    private async Task KeepLocalConflictCoreAsync(
        ProfileSyncConflict conflict,
        CancellationToken ct)
    {
        var settings = await _localState.LoadConnectionSettingsAsync();
        var profileId = settings.ProfileScopeId;
        if (!settings.IsConfigured || profileId == null)
        {
            return;
        }

        var adapter = GetAdapter(conflict.Collection);
        var localObject = (await adapter.LoadLocalObjectsAsync(ct))
            .FirstOrDefault(item => item.ObjectId == conflict.ObjectId);
        if (localObject == null)
        {
            return;
        }

        var response = await _client.PutObjectAsync(
            settings.HostUrl!,
            settings.AccessKey!,
            conflict.Collection,
            conflict.ObjectId,
            new ProfileSyncPutRequest
            {
                PayloadJson = localObject.PayloadJson,
                ExpectedRevision = conflict.RemoteRevision
            },
            ct);
        if (response.Success && response.Object != null)
        {
            await _localState.SaveObjectRevisionAsync(
                profileId,
                conflict.Collection,
                conflict.ObjectId,
                response.Object.Revision);
            _conflicts.Remove(conflict);
            await RemovePendingSaveAsync(
                profileId,
                conflict.Collection,
                conflict.ObjectId);
            RefreshStatusMessage("Local version kept");
        }
    }

    public IDisposable SuppressNotifications()
    {
        _suppressionDepth++;
        return new SuppressionLease(this);
    }

    private async Task EnsurePendingSavesLoadedAsync(string? profileId)
    {
        if (string.Equals(
                profileId,
                _pendingSavesProfileId,
                StringComparison.Ordinal) &&
            profileId != null)
        {
            return;
        }

        _pendingSaves.Clear();
        _conflicts.Clear();
        _pendingSavesProfileId = null;
        if (profileId == null)
        {
            return;
        }

        var persisted = await _localState.LoadPendingSavesAsync(profileId);
        _pendingSaves.AddRange(
            persisted
                .DistinctBy(
                    pending => $"{pending.Collection}\0{pending.ObjectId}",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(pending => pending.Collection, StringComparer.Ordinal)
                .ThenBy(pending => pending.ObjectId, StringComparer.Ordinal));
        _pendingSavesProfileId = profileId;
    }

    private Task QueuePortableSettingSaveAsync(string key) =>
        QueueLocalSaveAsync(ProfileSyncCollections.Settings, key);

    private async Task<bool> RetryPendingSavesAsync(
        HostedProfileConnectionSettings settings,
        string profileId,
        CancellationToken ct)
    {
        var hostReachable = true;
        foreach (var pending in _pendingSaves
                     .OrderBy(item => item.Collection, StringComparer.Ordinal)
                     .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                     .ToArray())
        {
            if (!await TryPushPendingSaveAsync(
                    settings,
                    profileId,
                    pending,
                    ct))
            {
                hostReachable = false;
            }
        }

        return hostReachable;
    }

    private async Task<bool> TryPushPendingSaveAsync(
        HostedProfileConnectionSettings settings,
        string profileId,
        ProfileSyncPendingSave pending,
        CancellationToken ct)
    {
        var adapter = GetAdapter(pending.Collection);
        var localObject = (await adapter.LoadLocalObjectsAsync(ct))
            .FirstOrDefault(item => string.Equals(
                item.ObjectId,
                pending.ObjectId,
                StringComparison.Ordinal));
        if (localObject == null)
        {
            return false;
        }

        await _localState.SaveHostedObjectProvenanceAsync(
            profileId,
            pending.Collection,
            pending.ObjectId);
        var expectedRevision = await _localState.LoadObjectRevisionAsync(
            profileId,
            pending.Collection,
            pending.ObjectId);
        ProfileSyncPutResponse response;
        try
        {
            response = await _client.PutObjectAsync(
                settings.HostUrl!,
                settings.AccessKey!,
                pending.Collection,
                pending.ObjectId,
                new ProfileSyncPutRequest
                {
                    PayloadJson = localObject.PayloadJson,
                    ExpectedRevision = expectedRevision
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        if (response.Conflict && response.RemoteObject != null)
        {
            _conflicts.RemoveAll(item => IsSameIdentity(
                item.Collection,
                item.ObjectId,
                pending.Collection,
                pending.ObjectId));
            _conflicts.Add(new ProfileSyncConflict(
                pending.Collection,
                pending.ObjectId,
                expectedRevision,
                response.RemoteObject.Revision,
                response.RemoteObject));
        }
        else if (response.Success && response.Object != null)
        {
            await _localState.SaveObjectRevisionAsync(
                profileId,
                pending.Collection,
                pending.ObjectId,
                response.Object.Revision);
            await RemovePendingSaveAsync(
                profileId,
                pending.Collection,
                pending.ObjectId);
        }

        return true;
    }

    private async Task AddPendingSaveAsync(
        string profileId,
        string collection,
        string objectId)
    {
        if (IsPending(collection, objectId))
        {
            return;
        }

        _pendingSaves.Add(new ProfileSyncPendingSave(collection, objectId));
        await PersistPendingSavesAsync(profileId);
    }

    private async Task RemovePendingSaveAsync(
        string profileId,
        string collection,
        string objectId)
    {
        if (_pendingSaves.RemoveAll(item => IsSameIdentity(
                item.Collection,
                item.ObjectId,
                collection,
                objectId)) == 0)
        {
            return;
        }

        await PersistPendingSavesAsync(profileId);
    }

    private Task PersistPendingSavesAsync(string profileId)
    {
        var ordered = _pendingSaves
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToArray();
        return _localState.SavePendingSavesAsync(profileId, ordered);
    }

    private bool IsPending(string collection, string objectId)
    {
        return _pendingSaves.Any(item => IsSameIdentity(
            item.Collection,
            item.ObjectId,
            collection,
            objectId));
    }

    private static bool IsSameIdentity(
        string leftCollection,
        string leftObjectId,
        string rightCollection,
        string rightObjectId)
    {
        return string.Equals(leftCollection, rightCollection, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(leftObjectId, rightObjectId, StringComparison.Ordinal);
    }

    private IProfileSyncCollectionAdapter GetAdapter(string collection)
    {
        if (_adapters.TryGetValue(collection, out var adapter))
        {
            return adapter;
        }

        throw new InvalidOperationException($"No hosted profile sync adapter is registered for collection '{collection}'.");
    }

    private async Task RunSerializedAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await operation();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<T> RunSerializedAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void SetStatus(ProfileSyncStatus status)
    {
        CurrentStatus = status;
        StatusChanged?.Invoke();
    }

    private void RefreshStatusMessage(string message)
    {
        CurrentStatus = CurrentStatus with
        {
            PendingCount = _pendingSaves.Count,
            ConflictCount = _conflicts.Count,
            Message = message
        };
        StatusChanged?.Invoke();
    }

    private sealed class SuppressionLease : IDisposable
    {
        private ProfileSyncService? _service;

        public SuppressionLease(ProfileSyncService service)
        {
            _service = service;
        }

        public void Dispose()
        {
            if (_service == null)
            {
                return;
            }

            _service._suppressionDepth = Math.Max(0, _service._suppressionDepth - 1);
            _service = null;
        }
    }
}
