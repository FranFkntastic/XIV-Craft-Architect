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

public sealed class ProfileSyncService
{
    private readonly ProfileHostClient _client;
    private readonly ProfileSyncLocalStateService _localState;
    private readonly IReadOnlyDictionary<string, IProfileSyncCollectionAdapter> _adapters;
    private readonly List<ProfileSyncPendingSave> _pendingSaves = [];
    private readonly List<ProfileSyncConflict> _conflicts = [];
    private int _suppressionDepth;
    private bool _pendingSavesLoaded;

    public ProfileSyncService(
        ProfileHostClient client,
        ProfileSyncLocalStateService localState,
        IEnumerable<IProfileSyncCollectionAdapter> adapters)
    {
        _client = client;
        _localState = localState;
        _adapters = adapters.ToDictionary(adapter => adapter.Collection, StringComparer.OrdinalIgnoreCase);
    }

    public event Action? StatusChanged;

    public ProfileSyncStatus CurrentStatus { get; private set; } = ProfileSyncStatus.LocalOnly();

    public IReadOnlyList<ProfileSyncPendingSave> PendingSaves => _pendingSaves;
    public IReadOnlyList<ProfileSyncConflict> Conflicts => _conflicts;

    public bool IsSuppressed => _suppressionDepth > 0;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await EnsurePendingSavesLoadedAsync();
        await SyncNowAsync(ct);
    }

    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        await EnsurePendingSavesLoadedAsync();
        var settings = await _localState.LoadConnectionSettingsAsync();
        if (!settings.IsConfigured)
        {
            SetStatus(ProfileSyncStatus.LocalOnly() with { PendingCount = _pendingSaves.Count });
            return;
        }

        try
        {
            var lastRevision = await _localState.LoadLastSyncRevisionAsync();
            var changes = await _client.GetChangesAsync(settings.HostUrl!, settings.AccessKey!, lastRevision, ct);
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
                        await adapter.ApplyRemoteObjectAsync(item, ct);
                    }

                    await _localState.SaveObjectRevisionAsync(item.Collection, item.ObjectId, item.Revision);
                }
            }

            await _localState.SaveLastSyncRevisionAsync(changes.ServerRevision);
            var hostReachable = await RetryPendingSavesAsync(settings, ct);
            SetStatus(new ProfileSyncStatus(
                true,
                hostReachable,
                changes.ServerRevision,
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
            var lastRevision = await _localState.LoadLastSyncRevisionAsync();
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

    public async Task QueueLocalSaveAsync(string collection, string objectId, CancellationToken ct = default)
    {
        if (IsSuppressed)
        {
            return;
        }

        await EnsurePendingSavesLoadedAsync();
        var settings = await _localState.LoadConnectionSettingsAsync();
        if (!settings.IsConfigured)
        {
            return;
        }

        var adapter = GetAdapter(collection);
        var localObject = (await adapter.LoadLocalObjectsAsync(ct)).FirstOrDefault(item => item.ObjectId == objectId);
        if (localObject == null)
        {
            return;
        }

        await AddPendingSaveAsync(collection, objectId);
        var hostReachable = await TryPushPendingSaveAsync(
            settings,
            new ProfileSyncPendingSave(collection, objectId),
            ct);

        var lastRevision = await _localState.LoadLastSyncRevisionAsync();
        SetStatus(new ProfileSyncStatus(
            true,
            hostReachable,
            lastRevision,
            _pendingSaves.Count,
            _conflicts.Count,
            CurrentStatus.LastSyncedAtUtc,
            _conflicts.Count > 0 ? "Conflicts need review" : CurrentStatus.Message));
    }

    public async Task ConnectAsync(
        HostedProfileConnectionSettings settings,
        FirstConnectMode mode,
        CancellationToken ct = default)
    {
        await EnsurePendingSavesLoadedAsync();
        await _localState.SaveConnectionSettingsAsync(settings);
        if (mode == FirstConnectMode.UploadLocal)
        {
            var objects = new List<ProfileSyncObjectEnvelope>();
            foreach (var adapter in _adapters.Values.OrderBy(adapter => adapter.Collection, StringComparer.Ordinal))
            {
                objects.AddRange(await adapter.LoadLocalObjectsAsync(ct));
            }

            var response = await _client.UploadBootstrapAsync(
                settings.HostUrl ?? string.Empty,
                settings.AccessKey ?? string.Empty,
                new ProfileHostBootstrapPayload { Objects = objects },
                ct);
            await _localState.SaveLastSyncRevisionAsync(response.ServerRevision);
            _pendingSaves.Clear();
            await PersistPendingSavesAsync();
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
            await SyncNowAsync(ct);
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await EnsurePendingSavesLoadedAsync();
        await _localState.SaveConnectionSettingsAsync(new HostedProfileConnectionSettings());
        _pendingSaves.Clear();
        await PersistPendingSavesAsync();
        _conflicts.Clear();
        SetStatus(ProfileSyncStatus.LocalOnly());
    }

    public async Task AcceptRemoteConflictAsync(ProfileSyncConflict conflict, CancellationToken ct = default)
    {
        var adapter = GetAdapter(conflict.Collection);
        using (SuppressNotifications())
        {
            await adapter.ApplyRemoteObjectAsync(conflict.RemoteObject, ct);
            await _localState.SaveObjectRevisionAsync(
                conflict.Collection,
                conflict.ObjectId,
                conflict.RemoteRevision);
        }

        _conflicts.Remove(conflict);
        await RemovePendingSaveAsync(conflict.Collection, conflict.ObjectId);
        RefreshStatusMessage("Remote version applied");
    }

    public async Task KeepLocalConflictAsync(ProfileSyncConflict conflict, CancellationToken ct = default)
    {
        var settings = await _localState.LoadConnectionSettingsAsync();
        if (!settings.IsConfigured)
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
                conflict.Collection,
                conflict.ObjectId,
                response.Object.Revision);
            _conflicts.Remove(conflict);
            await RemovePendingSaveAsync(conflict.Collection, conflict.ObjectId);
            RefreshStatusMessage("Local version kept");
        }
    }

    public IDisposable SuppressNotifications()
    {
        _suppressionDepth++;
        return new SuppressionLease(this);
    }

    private async Task EnsurePendingSavesLoadedAsync()
    {
        if (_pendingSavesLoaded)
        {
            return;
        }

        var persisted = await _localState.LoadPendingSavesAsync();
        _pendingSaves.Clear();
        _pendingSaves.AddRange(
            persisted
                .DistinctBy(
                    pending => $"{pending.Collection}\0{pending.ObjectId}",
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(pending => pending.Collection, StringComparer.Ordinal)
                .ThenBy(pending => pending.ObjectId, StringComparer.Ordinal));
        _pendingSavesLoaded = true;
    }

    private async Task<bool> RetryPendingSavesAsync(
        HostedProfileConnectionSettings settings,
        CancellationToken ct)
    {
        var hostReachable = true;
        foreach (var pending in _pendingSaves
                     .OrderBy(item => item.Collection, StringComparer.Ordinal)
                     .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                     .ToArray())
        {
            if (!await TryPushPendingSaveAsync(settings, pending, ct))
            {
                hostReachable = false;
            }
        }

        return hostReachable;
    }

    private async Task<bool> TryPushPendingSaveAsync(
        HostedProfileConnectionSettings settings,
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

        var expectedRevision = await _localState.LoadObjectRevisionAsync(
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
                pending.Collection,
                pending.ObjectId,
                response.Object.Revision);
            await RemovePendingSaveAsync(pending.Collection, pending.ObjectId);
        }

        return true;
    }

    private async Task AddPendingSaveAsync(string collection, string objectId)
    {
        if (IsPending(collection, objectId))
        {
            return;
        }

        _pendingSaves.Add(new ProfileSyncPendingSave(collection, objectId));
        await PersistPendingSavesAsync();
    }

    private async Task RemovePendingSaveAsync(string collection, string objectId)
    {
        if (_pendingSaves.RemoveAll(item => IsSameIdentity(
                item.Collection,
                item.ObjectId,
                collection,
                objectId)) == 0)
        {
            return;
        }

        await PersistPendingSavesAsync();
    }

    private Task PersistPendingSavesAsync()
    {
        var ordered = _pendingSaves
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToArray();
        return _localState.SavePendingSavesAsync(ordered);
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
