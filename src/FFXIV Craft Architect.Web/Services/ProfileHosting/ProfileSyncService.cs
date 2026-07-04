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
        await SyncNowAsync(ct);
    }

    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        var settings = await _localState.LoadConnectionSettingsAsync();
        if (!settings.IsConfigured)
        {
            SetStatus(ProfileSyncStatus.LocalOnly());
            return;
        }

        try
        {
            var lastRevision = await _localState.LoadLastSyncRevisionAsync();
            var changes = await _client.GetChangesAsync(settings.AccessKey!, lastRevision, ct);
            using (SuppressNotifications())
            {
                foreach (var item in changes.Objects)
                {
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
            SetStatus(new ProfileSyncStatus(
                true,
                true,
                changes.ServerRevision,
                _pendingSaves.Count,
                _conflicts.Count,
                DateTime.UtcNow,
                "Synced"));
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

        var expectedRevision = await _localState.LoadObjectRevisionAsync(collection, objectId);
        try
        {
            var response = await _client.PutObjectAsync(
                settings.AccessKey!,
                collection,
                objectId,
                new ProfileSyncPutRequest
                {
                    PayloadJson = localObject.PayloadJson,
                    ExpectedRevision = expectedRevision
                },
                ct);

            if (response.Conflict && response.RemoteObject != null)
            {
                _conflicts.RemoveAll(item => item.Collection == collection && item.ObjectId == objectId);
                _conflicts.Add(new ProfileSyncConflict(
                    collection,
                    objectId,
                    expectedRevision,
                    response.RemoteObject.Revision,
                    response.RemoteObject));
            }
            else if (response.Success && response.Object != null)
            {
                _pendingSaves.RemoveAll(item => item.Collection == collection && item.ObjectId == objectId);
                await _localState.SaveObjectRevisionAsync(collection, objectId, response.Object.Revision);
            }
        }
        catch
        {
            if (!_pendingSaves.Any(item => item.Collection == collection && item.ObjectId == objectId))
            {
                _pendingSaves.Add(new ProfileSyncPendingSave(collection, objectId));
            }
        }

        var lastRevision = await _localState.LoadLastSyncRevisionAsync();
        SetStatus(new ProfileSyncStatus(
            true,
            CurrentStatus.HostReachable,
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
        await _localState.SaveConnectionSettingsAsync(settings);
        if (mode == FirstConnectMode.UploadLocal)
        {
            var objects = new List<ProfileSyncObjectEnvelope>();
            foreach (var adapter in _adapters.Values)
            {
                objects.AddRange(await adapter.LoadLocalObjectsAsync(ct));
            }

            var response = await _client.UploadBootstrapAsync(
                settings.AccessKey ?? string.Empty,
                new ProfileHostBootstrapPayload { Objects = objects },
                ct);
            await _localState.SaveLastSyncRevisionAsync(response.ServerRevision);
        }
        else
        {
            await SyncNowAsync(ct);
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _localState.SaveConnectionSettingsAsync(new HostedProfileConnectionSettings());
        _pendingSaves.Clear();
        _conflicts.Clear();
        SetStatus(ProfileSyncStatus.LocalOnly());
    }

    public IDisposable SuppressNotifications()
    {
        _suppressionDepth++;
        return new SuppressionLease(this);
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
