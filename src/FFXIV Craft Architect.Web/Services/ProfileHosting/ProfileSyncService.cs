using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed record ProfileSyncDeleteExpectation(
    string Collection,
    string ObjectId,
    long Revision);

public sealed record ProfileSyncStatus(
    bool IsConnected,
    bool HostReachable,
    long LastSyncRevision,
    int PendingCount,
    int ConflictCount,
    DateTime? LastSyncedAtUtc,
    string? Message)
{
    public string? ProfileId { get; init; }
    public ProfileSyncStage Stage { get; init; } = ProfileSyncStage.Inactive;
    public ProfileSyncFailure Failure { get; init; }
    public int AppliedObjectCount { get; init; }
    public long? TargetRevision { get; init; }
    public bool OrderScopeReady { get; init; }

    public static ProfileSyncStatus LocalOnly() => new(false, false, 0, 0, 0, null, "Local only");
}

public enum ProfileSyncStage
{
    Inactive,
    ReadingLocalState,
    DownloadingChanges,
    ApplyingChanges,
    PublishingLocalChanges,
    Ready,
    Failed
}

public enum ProfileSyncFailure
{
    None,
    Offline,
    Authentication,
    Incompatible,
    Unverifiable
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
    ProfileSyncObjectEnvelope RemoteObject,
    bool CanApplyRemote = true,
    bool CanKeepLocal = true);

public sealed record ProfileSyncBootstrapPreview(
    int LocalObjectCount,
    int RemoteObjectCount,
    bool ContentsMatch);

public sealed record ProfileSyncPublicationResult(
    bool Published,
    bool Pending,
    bool Conflict,
    long Revision,
    string Message);

public sealed class ProfileSyncService
{
    private const int ChangePageSize = 50;
    private const int RecoveryPageSize = 50;
    private const string CanonicalProfileHostUrl = "https://xivcraftarchitect.com/api/";
    private const string LegacyDevelopmentProfileHostUrl = "https://dev.xivcraftarchitect.com/api/";
    private static readonly JsonSerializerOptions JsonOptions =
        ProfileSyncJson.CreateOptions();
    private readonly ProfileHostClient _client;
    private readonly ProfileSyncLocalStateService _localState;
    private readonly HostedOrderProjectionStore _hostedOrders;
    private readonly TradeOrderArchiveSummaryStore? _archiveSummaries;
    private readonly IReadOnlyDictionary<string, IProfileSyncCollectionAdapter> _adapters;
    private readonly List<ProfileSyncPendingSave> _pendingSaves = [];
    private readonly List<ProfileSyncConflict> _conflicts = [];
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _backgroundPumpActive;
    private Task _backgroundPumpCompletion = Task.CompletedTask;
    private int _suppressionDepth;
    private string? _pendingSavesConnectionScopeId;

    public ProfileSyncService(
        ProfileHostClient client,
        ProfileSyncLocalStateService localState,
        WebSettingsService settings,
        HostedOrderProjectionStore hostedOrders,
        IEnumerable<IProfileSyncCollectionAdapter> adapters,
        TradeOrderArchiveSummaryStore? archiveSummaries = null)
    {
        _client = client;
        _localState = localState;
        _hostedOrders = hostedOrders;
        _archiveSummaries = archiveSummaries;
        _adapters = adapters.ToDictionary(adapter => adapter.Collection, StringComparer.OrdinalIgnoreCase);
        settings.PortableSettingSaved += QueuePortableSettingSaveAsync;
    }

    public event Action? StatusChanged;
    public event Action? ConnectionChanged;
    public event Action? ProfileMetadataMayHaveChanged;

    public ProfileSyncStatus CurrentStatus { get; private set; } = ProfileSyncStatus.LocalOnly();

    public IReadOnlyList<ProfileSyncPendingSave> PendingSaves => _pendingSaves;
    public IReadOnlyList<ProfileSyncConflict> Conflicts => _conflicts;

    public bool IsSuppressed => _suppressionDepth > 0;

    public Task InitializeAsync(CancellationToken ct = default) =>
        SyncAndAwaitBackgroundAsync(null, null, ct);

    private async Task SyncAndAwaitBackgroundAsync(
        long? targetRevision,
        long? replayAfterRevision,
        CancellationToken ct)
    {
        await RunSerializedAsync(
            () => SyncNowCoreAsync(targetRevision, replayAfterRevision, ct),
            ct);
        await _backgroundPumpCompletion.WaitAsync(ct);
    }

    public Task PrepareAuthorityAsync(CancellationToken ct = default) =>
        RunSerializedAsync(() => PrepareAuthorityCoreAsync(ct), ct);

    private async Task PrepareAuthorityCoreAsync(CancellationToken ct)
    {
        if (await TryAdoptCanonicalAuthorityAsync(ct))
        {
            ConnectionChanged?.Invoke();
        }
    }

    private async Task<bool> TryAdoptCanonicalAuthorityAsync(CancellationToken ct)
    {
        var settings = await _localState.LoadConnectionSettingsAsync();
        if (!settings.IsConfigured ||
            !string.Equals(
                ProfileHostClient.NormalizeHostUrl(_client.DefaultHostUrl),
                CanonicalProfileHostUrl,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                ProfileHostClient.NormalizeHostUrl(settings.HostUrl!),
                LegacyDevelopmentProfileHostUrl,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await EnsurePendingSavesLoadedAsync(settings);
        if (_pendingSaves.Count > 0 || _conflicts.Count > 0)
        {
            return false;
        }

        ProfileHostProfileResponse canonicalProfile;
        try
        {
            canonicalProfile = await _client.GetProfileAsync(
                CanonicalProfileHostUrl,
                settings.AccessKey!,
                ct);
        }
        catch (ProfileHostConnectionException)
        {
            return false;
        }

        var adopted = settings.Snapshot();
        adopted.HostUrl = CanonicalProfileHostUrl;
        adopted.ConnectedProfileId = canonicalProfile.ProfileId;
        adopted.ConnectedProfileName = canonicalProfile.DisplayName;
        adopted.ConnectedProfileMetadataRevision = canonicalProfile.MetadataRevision;
        await _localState.SaveConnectionSettingsAsync(adopted);
        _pendingSaves.Clear();
        _conflicts.Clear();
        _pendingSavesConnectionScopeId = null;
        return true;
    }

    public Task SyncNowAsync(CancellationToken ct = default) =>
        SyncAndAwaitBackgroundAsync(null, null, ct);

    internal void NotifyProfileMetadataMayHaveChanged() =>
        ProfileMetadataMayHaveChanged?.Invoke();

    public Task SyncFromRevisionAsync(
        long replayAfterRevision,
        long? targetRevision,
        CancellationToken ct = default) =>
        SyncAndAwaitBackgroundAsync(
            targetRevision is > 0 ? targetRevision : null,
            replayAfterRevision,
            ct);

    public Task<long> EnsureHostedObjectRevisionAsync(
        string collection,
        string objectId,
        CancellationToken ct = default) =>
        RunSerializedAsync(
            () => EnsureHostedObjectRevisionCoreAsync(collection, objectId, null, ct),
            ct);

    public Task<long> EnsureHostedObjectRevisionAsync(
        string collection,
        string objectId,
        HostedProfileConnectionSettings capturedAuthority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capturedAuthority);
        var snapshot = capturedAuthority.Snapshot();
        return RunSerializedAsync(
            () => EnsureHostedObjectRevisionCoreAsync(collection, objectId, snapshot, ct),
            ct);
    }

    public Task<ProfileSyncPublicationResult> PublishLocalObjectAsync(
        string collection,
        string objectId,
        HostedProfileConnectionSettings capturedAuthority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capturedAuthority);
        var snapshot = capturedAuthority.Snapshot();
        return RunSerializedAsync(
            () => PublishLocalObjectCoreAsync(collection, objectId, snapshot, ct),
            ct);
    }

    private async Task<ProfileSyncPublicationResult> PublishLocalObjectCoreAsync(
        string collection,
        string objectId,
        HostedProfileConnectionSettings capturedAuthority,
        CancellationToken ct)
    {
        var adapter = GetAdapter(collection);
        if (await LoadLocalObjectAsync(adapter, objectId, ct) == null)
        {
            return new ProfileSyncPublicationResult(
                Published: false,
                Pending: false,
                Conflict: false,
                Revision: 0,
                Message: "The exact local object is unavailable.");
        }

        await QueueLocalSaveCoreAsync(
            collection,
            objectId,
            ct,
            capturedAuthority,
            allowSuppressed: true);
        var current = await _localState.LoadConnectionSettingsAsync();
        if (!IsSameConnectionAuthority(current, capturedAuthority) ||
            current.ProfileScopeId is not { } profileId)
        {
            throw new InvalidOperationException(
                "The hosted profile authority changed before the local object was published.");
        }

        var conflict = _conflicts.Any(item => IsSameIdentity(
            item.Collection,
            item.ObjectId,
            collection,
            objectId));
        var pending = IsPending(collection, objectId);
        var revision = await _localState.LoadObjectRevisionAsync(
            profileId,
            collection,
            objectId);
        var published = revision > 0 && !pending && !conflict;
        return new ProfileSyncPublicationResult(
            published,
            pending,
            conflict,
            revision,
            published
                ? "Published"
                : conflict
                    ? "The hosted object changed and requires an explicit conflict decision."
                    : pending
                        ? "The local object is saved on this device and waiting to sync."
                        : "The hosted object could not be published.");
    }

    private async Task<long> EnsureHostedObjectRevisionCoreAsync(
        string collection,
        string objectId,
        HostedProfileConnectionSettings? capturedAuthority,
        CancellationToken ct)
    {
        var current = await _localState.LoadConnectionSettingsAsync();
        if (capturedAuthority != null &&
            !IsSameConnectionAuthority(current, capturedAuthority))
        {
            throw new InvalidOperationException(
                "The hosted profile authority changed before revision recovery began.");
        }
        var settings = capturedAuthority ?? current;
        var profileId = settings.ProfileScopeId;
        await EnsurePendingSavesLoadedAsync(settings);
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
        var localObject = await LoadLocalObjectAsync(adapter, objectId, ct);
        if (localObject == null)
        {
            return 0;
        }

        var remoteBootstrap = await _client.ExportBootstrapAsync(
            settings.HostUrl!,
            settings.AccessKey!,
            ct);
        if (capturedAuthority != null)
        {
            await RequireConnectionAuthorityAsync(capturedAuthority);
        }
        var remoteObject = remoteBootstrap.Objects.FirstOrDefault(item =>
            string.Equals(item.Collection, collection, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.ObjectId, objectId, StringComparison.Ordinal));
        if (remoteObject == null)
        {
            await QueueLocalSaveCoreAsync(
                collection,
                objectId,
                ct,
                capturedAuthority);
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
            await AdoptLinkedPlanConflictBaseRevisionAsync(
                profileId,
                remoteObject);
            await AddPendingSaveAsync(profileId, collection, objectId);
            _conflicts.RemoveAll(item => IsSameIdentity(
                item.Collection,
                item.ObjectId,
                collection,
                objectId));
            var capabilities = await ResolveConflictCapabilitiesAsync(
                remoteObject,
                ct);
            _conflicts.Add(new ProfileSyncConflict(
                collection,
                objectId,
                0,
                remoteObject.Revision,
                remoteObject,
                capabilities.CanApplyRemote,
                capabilities.CanKeepLocal));
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

    private async Task SyncNowCoreAsync(
        long? targetRevision,
        long? replayAfterRevision,
        CancellationToken ct)
    {
        var settings = await _localState.LoadConnectionSettingsAsync();
        var profileId = settings.ProfileScopeId;
        await EnsurePendingSavesLoadedAsync(settings);
        if (!settings.IsConfigured || profileId == null)
        {
            SetStatus(ProfileSyncStatus.LocalOnly() with { PendingCount = _pendingSaves.Count });
            return;
        }

        var lastRevision = 0L;
        var appliedObjectCount = 0;
        try
        {
            SetStatus(CurrentStatus with
            {
                ProfileId = profileId,
                IsConnected = true,
                LastSyncRevision = 0,
                Stage = ProfileSyncStage.ReadingLocalState,
                Failure = ProfileSyncFailure.None,
                AppliedObjectCount = 0,
                TargetRevision = targetRevision,
                OrderScopeReady = false,
                Message = "Reading saved profile state"
            });
            var persistedRevision = await _localState.LoadLastSyncRevisionAsync(profileId);
            lastRevision = ResolveSyncStartRevision(
                persistedRevision,
                replayAfterRevision);
            _hostedOrders.BeginProfileRestore(
                profileId,
                hasTrustedProjection: false,
                lastRevision,
                DateTime.UtcNow,
                settings.ConnectionScopeId!);
            var mayTrustSavedProjection =
                !_hostedOrders.RestoreState.RequiresIdentityOnly &&
                _hostedOrders.RestoreState.Stage != HostedOrderRestoreStage.ScopeChanging;
            var trustedOrderCount = mayTrustSavedProjection &&
                                    !_hostedOrders.RestoreState.HasTrustedProjection
                ? await HydrateTrustedOrderProjectionsAsync(profileId, ct)
                : 0;
            _hostedOrders.BeginProfileRestore(
                profileId,
                hasTrustedProjection: trustedOrderCount > 0,
                lastRevision,
                DateTime.UtcNow,
                settings.ConnectionScopeId!);
            var baseRevision = lastRevision;
            SetStatus(CurrentStatus with
            {
                LastSyncRevision = lastRevision,
                Stage = ProfileSyncStage.DownloadingChanges,
                Message = "Checking hosted revisions"
            });
            var serverRevision = baseRevision;
            var hasMore = true;
            var companyProfileRecovery = new Dictionary<Guid, bool>();
            while (hasMore)
            {
                var page = await ReplayChangesPageAsync(
                    settings,
                    profileId,
                    ProfileSyncCollections.OrderAuthorityScope,
                    serverRevision,
                    targetRevision,
                    appliedObjectCount,
                    companyProfileRecovery,
                    ct);
                appliedObjectCount = page.AppliedCount;
                serverRevision = page.ServerRevision;
                hasMore = page.HasMore;
                if (hasMore)
                {
                    SetStatus(CurrentStatus with
                    {
                        LastSyncRevision = serverRevision,
                        Stage = ProfileSyncStage.DownloadingChanges,
                        AppliedObjectCount = appliedObjectCount,
                        TargetRevision = targetRevision,
                        Message = "Checking the next hosted revision"
                    });
                }
            }

            lastRevision = serverRevision;
            SetStatus(CurrentStatus with
            {
                LastSyncRevision = serverRevision,
                Stage = ProfileSyncStage.PublishingLocalChanges,
                AppliedObjectCount = appliedObjectCount,
                TargetRevision = Math.Max(targetRevision ?? 0, serverRevision),
                Message = "Publishing local changes"
            });
            var hostReachable = await RetryPendingSavesAsync(
                settings,
                profileId,
                ct,
                ProfileSyncCollections.OrderAuthorityScope);
            if (hostReachable && !_hostedOrders.RestoreState.HasTrustedProjection)
            {
                await HydrateTrustedOrderProjectionsAsync(profileId, ct);
            }
            SetStatus(new ProfileSyncStatus(
                true,
                hostReachable,
                serverRevision,
                _pendingSaves.Count,
                _conflicts.Count,
                DateTime.UtcNow,
                hostReachable
                    ? "Orders restored"
                    : "Order restoration needs attention") with
            {
                ProfileId = profileId,
                Stage = hostReachable
                    ? ProfileSyncStage.ApplyingChanges
                    : ProfileSyncStage.Failed,
                Failure = hostReachable
                    ? ProfileSyncFailure.None
                    : ProfileSyncFailure.Offline,
                AppliedObjectCount = appliedObjectCount,
                TargetRevision = Math.Max(targetRevision ?? 0, serverRevision),
                OrderScopeReady = hostReachable
            });
            if (hostReachable)
            {
                StartBackgroundReplayPump(
                    settings.Snapshot(),
                    profileId,
                    baseRevision,
                    targetRevision);
            }
        }
        catch (Exception ex)
        {
            var failure = ClassifyFailure(ex);
            SetStatus(new ProfileSyncStatus(
                true,
                false,
                lastRevision,
                _pendingSaves.Count,
                _conflicts.Count,
                CurrentStatus.LastSyncedAtUtc,
                ex.Message) with
            {
                ProfileId = profileId,
                Stage = ProfileSyncStage.Failed,
                Failure = failure,
                AppliedObjectCount = appliedObjectCount,
                TargetRevision = targetRevision,
                OrderScopeReady = CurrentStatus.OrderScopeReady &&
                                  failure is not (
                                      ProfileSyncFailure.Authentication or
                                      ProfileSyncFailure.Incompatible or
                                      ProfileSyncFailure.Unverifiable)
            });
        }
    }

    private sealed record ReplayPageResult(
        long ServerRevision,
        bool HasMore,
        int AppliedCount);

    private async Task<ReplayPageResult> ReplayChangesPageAsync(
        HostedProfileConnectionSettings settings,
        string profileId,
        IReadOnlyList<string>? collections,
        long sinceRevision,
        long? targetRevision,
        int appliedObjectCount,
        Dictionary<Guid, bool> companyProfileRecovery,
        CancellationToken ct)
    {
        var changes = await _client.GetChangesAsync(
            settings.HostUrl!,
            settings.AccessKey!,
            sinceRevision,
            ChangePageSize,
            ct,
            collections);
        using (SuppressNotifications())
        {
            foreach (var item in changes.Objects)
            {
                SetStatus(CurrentStatus with
                {
                    Stage = ProfileSyncStage.ApplyingChanges,
                    AppliedObjectCount = appliedObjectCount,
                    TargetRevision = targetRevision,
                    Message = "Applying hosted changes"
                });
                if (IsPending(item.Collection, item.ObjectId))
                {
                    continue;
                }

                var adapter = GetAdapter(item.Collection);
                var orderDeletionPersisted = false;
                var orderDeletionDeferred = false;
                if (item.Deleted)
                {
                    orderDeletionDeferred =
                        string.Equals(
                            item.Collection,
                            ProfileSyncCollections.TradeOrders,
                            StringComparison.OrdinalIgnoreCase) &&
                        (await _localState.LoadPendingOrderCleanupAsync(profileId))
                        .Contains(item.ObjectId, StringComparer.OrdinalIgnoreCase);
                    var shouldDeleteLocalObject = !orderDeletionDeferred;
                    Guid? deletedOrderId = null;
                    Guid? deletedOrderCompanyProfileId = null;
                    if (!orderDeletionDeferred &&
                        string.Equals(
                            item.Collection,
                            ProfileSyncCollections.TradeOrders,
                            StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParse(item.ObjectId, out var parsedDeletedOrderId))
                    {
                        deletedOrderId = parsedDeletedOrderId;
                        deletedOrderCompanyProfileId =
                            await ResolveDeletedOrderCompanyProfileIdAsync(
                                adapter,
                                parsedDeletedOrderId,
                                ct);
                        shouldDeleteLocalObject =
                            deletedOrderCompanyProfileId.HasValue;
                    }
                    if (deletedOrderCompanyProfileId.HasValue)
                    {
                        if (adapter is IHostedOrderProfileSyncAdapter hostedOrderAdapter)
                        {
                            await hostedOrderAdapter.ApplyRemoteDeletionAsync(
                                deletedOrderId!.Value,
                                deletedOrderCompanyProfileId.Value,
                                item.Revision,
                                ct);
                            shouldDeleteLocalObject = false;
                            orderDeletionPersisted = true;
                        }
                        else
                        {
                            var adoption = _hostedOrders.TryAdoptCommittedTombstone(
                                _hostedOrders.CaptureAuthorityScope(),
                                deletedOrderId!.Value,
                                deletedOrderCompanyProfileId.Value,
                                item.Revision);
                            if (adoption is not (
                                HostedOrderCommittedProjectionResult.Adopted or
                                HostedOrderCommittedProjectionResult.AlreadyCurrent))
                            {
                                throw new InvalidOperationException(
                                    $"Hosted order deletion could not be applied because its authority is {adoption}.");
                            }
                        }
                    }
                    if (shouldDeleteLocalObject &&
                        adapter is PlansProfileSyncAdapter plansAdapter &&
                        await plansAdapter.IsDeleteProtectedAsync(item.ObjectId))
                    {
                        await _localState.SaveObjectRevisionAsync(
                            profileId,
                            ProfileSyncCollections.Plans,
                            item.ObjectId,
                            item.Revision);
                        await AddPendingSaveAsync(
                            profileId,
                            ProfileSyncCollections.Plans,
                            item.ObjectId);
                        shouldDeleteLocalObject = false;
                    }
                    if (shouldDeleteLocalObject)
                    {
                        await adapter.DeleteLocalObjectAsync(item.ObjectId, ct);
                    }
                }
                else
                {
                    await _localState.SaveHostedObjectProvenanceAsync(
                        profileId,
                        item.Collection,
                        item.ObjectId);
                    try
                    {
                        await adapter.ApplyRemoteObjectAsync(item, ct);
                    }
                    catch (ProfileSyncObjectReconciliationException reconciliation) when (
                        string.Equals(reconciliation.Collection, item.Collection, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(reconciliation.ObjectId, item.ObjectId, StringComparison.Ordinal))
                    {
                        if (reconciliation.Reconciliation ==
                            ProfileSyncObjectReconciliation.PromoteLocalAuthority)
                        {
                            await AddPendingSaveAsync(
                                profileId,
                                item.Collection,
                                item.ObjectId);
                        }
                        else
                        {
                            await RecordRemoteObjectConflictAsync(
                                profileId,
                                item,
                                ct);
                            continue;
                        }
                    }
                    catch (MissingTradeCompanyProfileException exception)
                    {
                        if (!companyProfileRecovery.TryGetValue(
                                exception.CompanyProfileId,
                                out var recovered))
                        {
                            recovered = await TryRestoreMissingCompanyProfileAsync(
                                settings,
                                profileId,
                                exception.CompanyProfileId,
                                ct);
                            companyProfileRecovery[exception.CompanyProfileId] = recovered;
                        }
                        if (!recovered)
                        {
                            // An unavailable parent cannot make every later profile object
                            // unverifiable. Preserve the browser's last truthful copy, mark
                            // this hosted revision as observed, and continue restoring the
                            // independent objects that follow it.
                            await _localState.SaveObjectRevisionAsync(
                                profileId,
                                item.Collection,
                                item.ObjectId,
                                item.Revision);
                            appliedObjectCount++;
                            continue;
                        }
                        await adapter.ApplyRemoteObjectAsync(item, ct);
                    }
                }

                if (!orderDeletionDeferred &&
                    ((item.Deleted && !orderDeletionPersisted) ||
                    !string.Equals(
                        item.Collection,
                        ProfileSyncCollections.TradeOrders,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    await _localState.SaveObjectRevisionAsync(
                        profileId,
                        item.Collection,
                        item.ObjectId,
                        item.Revision);
                }
                appliedObjectCount++;
                SetStatus(CurrentStatus with
                {
                    LastSyncRevision = item.Revision,
                    Stage = ProfileSyncStage.ApplyingChanges,
                    AppliedObjectCount = appliedObjectCount,
                    TargetRevision = targetRevision,
                    Message = $"Applied {appliedObjectCount:N0} hosted change{(appliedObjectCount == 1 ? string.Empty : "s")}"
                });
            }
        }

        if (changes.HasMore && changes.ServerRevision <= sinceRevision)
        {
            throw new InvalidOperationException(
                "The profile host returned a non-advancing changes page.");
        }

        return new ReplayPageResult(
            changes.ServerRevision,
            changes.HasMore,
            appliedObjectCount);
    }

    private void StartBackgroundReplayPump(
        HostedProfileConnectionSettings settings,
        string profileId,
        long baseRevision,
        long? targetRevision)
    {
        if (Interlocked.CompareExchange(ref _backgroundPumpActive, 1, 0) != 0)
        {
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _backgroundPumpCompletion = completion.Task;
        _ = Task.Run(async () =>
        {
            try
            {
                var cursor = baseRevision;
                var hasMore = true;
                var companyProfileRecovery = new Dictionary<Guid, bool>();
                while (hasMore)
                {
                    var page = await RunSerializedAsync(
                        async () =>
                        {
                            if (!await IsBackgroundPumpAuthorityCurrentAsync(
                                    settings,
                                    profileId))
                            {
                                return null;
                            }

                            return await ReplayChangesPageAsync(
                                settings,
                                profileId,
                                ProfileSyncCollections.BackgroundScope,
                                cursor,
                                targetRevision,
                                CurrentStatus.AppliedObjectCount,
                                companyProfileRecovery,
                                CancellationToken.None);
                        },
                        CancellationToken.None);
                    if (page == null)
                    {
                        return;
                    }

                    cursor = page.ServerRevision;
                    hasMore = page.HasMore;
                }

                await RunSerializedAsync(
                    () => CompleteBackgroundSyncCoreAsync(
                        settings,
                        profileId,
                        cursor,
                        targetRevision),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                var failure = ClassifyFailure(exception);
                SetStatus(CurrentStatus with
                {
                    Stage = ProfileSyncStage.Failed,
                    Failure = failure,
                    Message = exception.Message,
                    OrderScopeReady = CurrentStatus.OrderScopeReady &&
                                      failure is not (
                                          ProfileSyncFailure.Authentication or
                                          ProfileSyncFailure.Incompatible or
                                          ProfileSyncFailure.Unverifiable)
                });
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundPumpActive, 0);
                completion.TrySetResult();
            }
        });
    }

    private async Task<bool> IsBackgroundPumpAuthorityCurrentAsync(
        HostedProfileConnectionSettings settings,
        string profileId)
    {
        var current = await _localState.LoadConnectionSettingsAsync();
        return IsSameConnectionAuthority(current, settings) &&
               string.Equals(
                   current.ProfileScopeId,
                   profileId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task CompleteBackgroundSyncCoreAsync(
        HostedProfileConnectionSettings settings,
        string profileId,
        long serverRevision,
        long? targetRevision)
    {
        var current = await _localState.LoadConnectionSettingsAsync();
        if (!IsSameConnectionAuthority(current, settings) ||
            !string.Equals(
                current.ProfileScopeId,
                profileId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetStatus(CurrentStatus with
        {
            Stage = ProfileSyncStage.PublishingLocalChanges,
            TargetRevision = Math.Max(targetRevision ?? 0, serverRevision),
            Message = "Publishing local changes"
        });
        await BackfillRetainedOrderGeneratedPlansAsync(
            settings,
            profileId,
            CancellationToken.None);
        var hostReachable = await RetryPendingSavesAsync(
            settings,
            profileId,
            CancellationToken.None);
        if (_adapters.TryGetValue(
                ProfileSyncCollections.TradeOrders,
                out var orderAdapter) &&
            orderAdapter is TradeOrderProfileSyncAdapter hostedOrderAdapter)
        {
            await hostedOrderAdapter.ReapResurrectedOrdersAsync(profileId);
        }
        await _localState.SaveLastSyncRevisionAsync(profileId, serverRevision);
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
                    : "Synced") with
        {
            ProfileId = profileId,
            Stage = hostReachable
                ? ProfileSyncStage.Ready
                : ProfileSyncStage.Failed,
            Failure = hostReachable
                ? ProfileSyncFailure.None
                : ProfileSyncFailure.Offline,
            AppliedObjectCount = CurrentStatus.AppliedObjectCount,
            TargetRevision = Math.Max(targetRevision ?? 0, serverRevision),
            OrderScopeReady = hostReachable || CurrentStatus.OrderScopeReady
        });
    }

    private async Task BackfillRetainedOrderGeneratedPlansAsync(
        HostedProfileConnectionSettings settings,
        string profileId,
        CancellationToken ct)
    {
        if (await _localState.IsLinkedPlanSealMigrationCompleteAsync(profileId))
        {
            return;
        }

        var orderAdapter = GetAdapter(ProfileSyncCollections.TradeOrders);
        var planAdapter = GetAdapter(ProfileSyncCollections.Plans);
        var references = (await orderAdapter.LoadLocalObjectsAsync(ct))
            .Where(item => !item.Deleted)
            .Select(item => JsonSerializer.Deserialize<TradeOrder>(item.PayloadJson, JsonOptions))
            .Where(order =>
                order is
                {
                    CraftPlanLinkKind: TradeOrderCraftPlanLinkKind.OrderGenerated,
                    CraftPlanId: not null,
                    CraftPlanSavedAtUtc: not null
                } &&
                !string.IsNullOrWhiteSpace(order.CraftPlanId))
            .Select(order => new
            {
                OrderId = order!.Id,
                PlanId = order.CraftPlanId!,
                SavedAt = order.CraftPlanSavedAtUtc!.Value
            })
            .GroupBy(item => item.PlanId, StringComparer.Ordinal)
            .Where(group => group
                .Select(item => (item.OrderId, item.SavedAt))
                .Distinct()
                .Count() == 1)
            .Select(group => group.First())
            .ToArray();
        if (references.Length == 0)
        {
            await _localState.SaveLinkedPlanSealMigrationCompleteAsync(profileId);
            return;
        }

        var retainedPlans = (await planAdapter.LoadLocalObjectsAsync(ct))
            .ToDictionary(item => item.ObjectId, StringComparer.Ordinal);
        var remote = await _client.ExportBootstrapAsync(
            settings.HostUrl!,
            settings.AccessKey!,
            ct);
        var remotePlans = remote.Objects
            .Where(item => string.Equals(
                item.Collection,
                ProfileSyncCollections.Plans,
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => item.ObjectId, StringComparer.Ordinal);
        foreach (var reference in references)
        {
            ct.ThrowIfCancellationRequested();
            if (!retainedPlans.TryGetValue(reference.PlanId, out var retained))
            {
                continue;
            }

            var localSnapshot = ProfileSyncPlanPayloadCodec.Deserialize(
                retained.PayloadJson,
                retained.ObjectId);
            if (localSnapshot.SavedAt != reference.SavedAt ||
                localSnapshot.LinkedOrderId is { } linkedOrderId && linkedOrderId != reference.OrderId)
            {
                continue;
            }
            if (!localSnapshot.LinkedOrderId.HasValue)
            {
                localSnapshot.LinkedOrderId = reference.OrderId;
                retained.PayloadJson = ProfileSyncPlanPayloadCodec.Serialize(localSnapshot);
                retained.UpdatedAtUtc = DateTime.UtcNow;
                await planAdapter.ApplyRemoteObjectAsync(retained, ct);
            }

            await _localState.SaveHostedObjectProvenanceAsync(
                profileId,
                ProfileSyncCollections.Plans,
                retained.ObjectId);
            if (!remotePlans.TryGetValue(retained.ObjectId, out var remotePlan))
            {
                await _localState.SaveObjectRevisionAsync(
                    profileId,
                    ProfileSyncCollections.Plans,
                    retained.ObjectId,
                    0);
                await AddPendingSaveAsync(
                    profileId,
                    ProfileSyncCollections.Plans,
                    retained.ObjectId);
                continue;
            }

            if (remotePlan.Deleted)
            {
                await _localState.SaveObjectRevisionAsync(
                    profileId,
                    ProfileSyncCollections.Plans,
                    retained.ObjectId,
                    remotePlan.Revision);
                await AddPendingSaveAsync(
                    profileId,
                    ProfileSyncCollections.Plans,
                    retained.ObjectId);
                continue;
            }

            if (string.Equals(
                    remotePlan.PayloadJson,
                    retained.PayloadJson,
                    StringComparison.Ordinal))
            {
                await _localState.SaveObjectRevisionAsync(
                    profileId,
                    ProfileSyncCollections.Plans,
                    retained.ObjectId,
                    remotePlan.Revision);
                await RemovePendingSaveAsync(
                    profileId,
                    ProfileSyncCollections.Plans,
                    retained.ObjectId);
                continue;
            }

            if (!remotePlan.Deleted)
            {
                var remoteSnapshot = ProfileSyncPlanPayloadCodec.Deserialize(
                    remotePlan.PayloadJson,
                    remotePlan.ObjectId);
                if (!remoteSnapshot.LinkedOrderId.HasValue &&
                    ProfileSyncPlanPayloadCodec.HasSameRevisionContent(
                        remoteSnapshot,
                        localSnapshot))
                {
                    await _localState.SaveObjectRevisionAsync(
                        profileId,
                        ProfileSyncCollections.Plans,
                        retained.ObjectId,
                        remotePlan.Revision);
                    await AddPendingSaveAsync(
                        profileId,
                        ProfileSyncCollections.Plans,
                        retained.ObjectId);
                    continue;
                }
            }

            await AdoptLinkedPlanConflictBaseRevisionAsync(
                profileId,
                remotePlan);
            await AddPendingSaveAsync(
                profileId,
                ProfileSyncCollections.Plans,
                retained.ObjectId);
            _conflicts.RemoveAll(item => IsSameIdentity(
                item.Collection,
                item.ObjectId,
                ProfileSyncCollections.Plans,
                retained.ObjectId));
            var capabilities = await ResolveConflictCapabilitiesAsync(
                remotePlan,
                ct);
            _conflicts.Add(new ProfileSyncConflict(
                ProfileSyncCollections.Plans,
                retained.ObjectId,
                await _localState.LoadObjectRevisionAsync(
                    profileId,
                    ProfileSyncCollections.Plans,
                    retained.ObjectId),
                remotePlan.Revision,
                remotePlan,
                capabilities.CanApplyRemote,
                capabilities.CanKeepLocal));
        }
        await _localState.SaveLinkedPlanSealMigrationCompleteAsync(profileId);
    }

    private static long ResolveSyncStartRevision(
        long persistedRevision,
        long? replayAfterRevision) =>
        replayAfterRevision.HasValue
            ? Math.Min(persistedRevision, Math.Max(0, replayAfterRevision.Value))
            : persistedRevision;

    private async Task<int> HydrateTrustedOrderProjectionsAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(ProfileSyncCollections.TradeOrders, out var adapter))
        {
            return 0;
        }

        var envelopes = await adapter.LoadLocalObjectsAsync(cancellationToken);
        var revisions = await _localState.LoadObjectRevisionsAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            envelopes.Select(envelope => envelope.ObjectId));
        var projections = new List<(TradeOrder Order, long ObjectRevision)>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = revisions.GetValueOrDefault(envelope.ObjectId);
            if (revision <= 0)
            {
                continue;
            }

            var order = JsonSerializer.Deserialize<TradeOrder>(envelope.PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Saved Trade order '{envelope.ObjectId}' could not be restored safely.");
            projections.Add((order, revision));
        }

        return _hostedOrders.PublishRemoteOrders(projections);
    }

    private static ProfileSyncFailure ClassifyFailure(Exception exception) =>
        exception switch
        {
            ProfileHostConnectionException
            {
                Failure: ProfileHostConnectionFailure.AccessKeyRejected
            } => ProfileSyncFailure.Authentication,
            ProfileHostConnectionException
            {
                Failure: ProfileHostConnectionFailure.IncompatibleHost or
                    ProfileHostConnectionFailure.ProfileHostingDisabled
            } => ProfileSyncFailure.Incompatible,
            ProfileHostConnectionException => ProfileSyncFailure.Offline,
            HttpRequestException => ProfileSyncFailure.Offline,
            _ => ProfileSyncFailure.Unverifiable
        };

    private async Task<bool> TryRestoreMissingCompanyProfileAsync(
        HostedProfileConnectionSettings settings,
        string profileId,
        Guid companyProfileId,
        CancellationToken ct)
    {
        var companyObjectId = companyProfileId.ToString("D");
        ProfileSyncObjectEnvelope? companyObject = null;
        var recoveryRevision = 0L;
        var hasMore = true;
        while (hasMore && companyObject == null)
        {
            var page = await _client.GetChangesAsync(
                settings.HostUrl!,
                settings.AccessKey!,
                recoveryRevision,
                RecoveryPageSize,
                ct);
            companyObject = page.Objects.FirstOrDefault(item =>
                !item.Deleted &&
                string.Equals(
                    item.Collection,
                    ProfileSyncCollections.TradeCompanyProfiles,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ObjectId, companyObjectId, StringComparison.OrdinalIgnoreCase));
            if (page.HasMore && page.ServerRevision <= recoveryRevision)
            {
                throw new InvalidOperationException(
                    "The profile host returned a non-advancing recovery page.");
            }

            recoveryRevision = page.ServerRevision;
            hasMore = page.HasMore;
        }

        if (companyObject == null)
        {
            return false;
        }

        var companyAdapter = GetAdapter(ProfileSyncCollections.TradeCompanyProfiles);
        await _localState.SaveHostedObjectProvenanceAsync(
            profileId,
            companyObject.Collection,
            companyObject.ObjectId);
        await companyAdapter.ApplyRemoteObjectAsync(companyObject, ct);
        await _localState.SaveObjectRevisionAsync(
            profileId,
            companyObject.Collection,
            companyObject.ObjectId,
            companyObject.Revision);
        return true;
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
        DeleteObjectsAsync(objects, [], ct);

    public Task DeleteObjectsAsync(
        IReadOnlyList<(string Collection, string ObjectId)> objects,
        IReadOnlyList<ProfileSyncDeleteExpectation> expectations,
        CancellationToken ct = default) =>
        RunSerializedAsync(
            () => DeleteObjectsCoreAsync(objects, expectations, ct),
            ct);

    private async Task DeleteObjectsCoreAsync(
        IReadOnlyList<(string Collection, string ObjectId)> objects,
        IReadOnlyList<ProfileSyncDeleteExpectation> expectations,
        CancellationToken ct)
    {
        if (objects.Count == 0)
        {
            return;
        }

        var distinct = objects
            .DistinctBy(item => $"{item.Collection}\0{item.ObjectId}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var deletingOrderIds = distinct
            .Where(item => string.Equals(
                item.Collection,
                ProfileSyncCollections.TradeOrders,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => Guid.TryParse(item.ObjectId, out var orderId)
                ? orderId
                : (Guid?)null)
            .Where(orderId => orderId.HasValue)
            .Select(orderId => orderId!.Value)
            .ToHashSet();
        foreach (var item in distinct)
        {
            _ = GetAdapter(item.Collection);
        }

        var settings = await _localState.LoadConnectionSettingsAsync();
        var profileId = settings.ProfileScopeId;
        if (!settings.IsConfigured || profileId == null)
        {
            if (expectations.Count > 0)
            {
                throw new InvalidOperationException(
                    "The hosted object revision cannot be verified while profile hosting is disconnected.");
            }

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
                await DeleteLocalObjectAsync(item, deletingOrderIds, ct);
            }
            return;
        }

        await EnsurePendingSavesLoadedAsync(settings);
        var authority = _hostedOrders.CaptureAuthorityScope();
        var pendingOrderDeletions = new List<(
            string Identity,
            Guid OrderId,
            Guid CompanyProfileId,
            long Revision,
            IHostedOrderProfileSyncAdapter? Adapter)>();
        var remote = await _client.ExportBootstrapAsync(
            settings.HostUrl!,
            settings.AccessKey!,
            ct);
        foreach (var expectation in expectations)
        {
            if (!distinct.Any(item =>
                    string.Equals(item.Collection, expectation.Collection, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.ObjectId, expectation.ObjectId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"The guarded hosted deletion omitted {expectation.Collection}/{expectation.ObjectId}.");
            }

            var expectedObject = remote.Objects.FirstOrDefault(candidate =>
                string.Equals(candidate.Collection, expectation.Collection, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ObjectId, expectation.ObjectId, StringComparison.Ordinal));
            var isConfirmedOrderTombstone = expectedObject is
            {
                Deleted: true
            } &&
                expectedObject.Revision > expectation.Revision &&
                string.Equals(
                    expectation.Collection,
                    ProfileSyncCollections.TradeOrders,
                    StringComparison.OrdinalIgnoreCase);
            if (expectedObject == null ||
                (!isConfirmedOrderTombstone &&
                    (expectedObject.Deleted ||
                     expectedObject.Revision != expectation.Revision)))
            {
                throw new InvalidOperationException(
                    $"Hosted {expectation.Collection}/{expectation.ObjectId} changed before deletion; its current state was preserved.");
            }
        }

        foreach (var remoteOrder in remote.Objects.Where(item =>
                     !item.Deleted &&
                     string.Equals(
                         item.Collection,
                         ProfileSyncCollections.TradeOrders,
                         StringComparison.OrdinalIgnoreCase) &&
                     Guid.TryParse(item.ObjectId, out var orderId) &&
                     deletingOrderIds.Contains(orderId)))
        {
            await _localState.SaveObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.TradeOrders,
                remoteOrder.ObjectId,
                remoteOrder.Revision);
        }
        await UpdatePendingOrderCleanupAsync(profileId, deletingOrderIds, pending: true);

        var remoteDeletionOrder = distinct.OrderByDescending(item => string.Equals(
            item.Collection,
            ProfileSyncCollections.TradeOrders,
            StringComparison.OrdinalIgnoreCase));
        var confirmedOrderDeletionIds = new HashSet<Guid>();
        try
        {
            foreach (var item in remoteDeletionOrder)
            {
                var remoteObject = remote.Objects.FirstOrDefault(candidate =>
                    string.Equals(candidate.Collection, item.Collection, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.ObjectId, item.ObjectId, StringComparison.Ordinal));
                if (remoteObject == null)
                {
                    continue;
                }

                if (remoteObject.Deleted)
                {
                    if (Guid.TryParse(item.ObjectId, out var deletedOrderId) &&
                        string.Equals(
                            item.Collection,
                            ProfileSyncCollections.TradeOrders,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        confirmedOrderDeletionIds.Add(deletedOrderId);
                        var adapter = GetAdapter(item.Collection);
                        var companyProfileId = await ResolveDeletedOrderCompanyProfileIdAsync(
                            adapter,
                            deletedOrderId,
                            ct) ?? throw new InvalidOperationException(
                                $"Hosted TradeOrders/{item.ObjectId} was already deleted, but its local company identity could not be verified.");
                        pendingOrderDeletions.Add((
                            $"{item.Collection}\0{item.ObjectId}",
                            deletedOrderId,
                            companyProfileId,
                            remoteObject.Revision,
                            adapter as IHostedOrderProfileSyncAdapter));
                    }
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
                        $"Hosted {item.Collection}/{item.ObjectId} changed while it was being deleted. Its current state was preserved.");
                }
                if (!response.Success || response.Object == null)
                {
                    throw new InvalidOperationException(
                        $"The hosted profile did not confirm deletion of {item.Collection}/{item.ObjectId}.");
                }

                if (string.Equals(
                        item.Collection,
                        ProfileSyncCollections.TradeOrders,
                        StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParse(item.ObjectId, out var orderId))
                {
                    confirmedOrderDeletionIds.Add(orderId);
                    var companyProfileId = ReadOrderCompanyProfileId(remoteObject);
                    var adapter = GetAdapter(item.Collection);
                    pendingOrderDeletions.Add((
                        $"{item.Collection}\0{item.ObjectId}",
                        orderId,
                        companyProfileId,
                        response.Object.Revision,
                        adapter as IHostedOrderProfileSyncAdapter));
                    continue;
                }

                await _localState.SaveObjectRevisionAsync(
                    profileId,
                    item.Collection,
                    item.ObjectId,
                    response.Object.Revision);
            }
        }
        catch
        {
            await UpdatePendingOrderCleanupAsync(
                profileId,
                deletingOrderIds.Except(confirmedOrderDeletionIds).ToHashSet(),
                pending: false);
            throw;
        }

        var pendingOrderDeletionIdentities = pendingOrderDeletions
            .Select(deletion => deletion.Identity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in distinct)
        {
            if (!pendingOrderDeletionIdentities.Contains(
                    $"{item.Collection}\0{item.ObjectId}"))
            {
                await DeleteLocalObjectAsync(item, deletingOrderIds, ct);
            }
            await RemovePendingSaveAsync(profileId, item.Collection, item.ObjectId);
            _conflicts.RemoveAll(conflict => IsSameIdentity(
                conflict.Collection,
                conflict.ObjectId,
                item.Collection,
                item.ObjectId));
        }

        foreach (var deletion in pendingOrderDeletions)
        {
            if (deletion.Adapter != null)
            {
                await deletion.Adapter.ApplyRemoteDeletionAsync(
                    deletion.OrderId,
                    deletion.CompanyProfileId,
                    deletion.Revision,
                    ct);
                continue;
            }

            var adoption = _hostedOrders.TryAdoptCommittedTombstone(
                authority,
                deletion.OrderId,
                deletion.CompanyProfileId,
                deletion.Revision);
            if (adoption is not (
                HostedOrderCommittedProjectionResult.Adopted or
                HostedOrderCommittedProjectionResult.AlreadyCurrent))
            {
                throw new InvalidOperationException(
                    $"The confirmed order deletion could not be adopted because its authority is {adoption}.");
            }
            await _localState.SaveObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.TradeOrders,
                deletion.OrderId.ToString("D"),
                deletion.Revision);
            await DeleteLocalObjectAsync(
                (ProfileSyncCollections.TradeOrders, deletion.OrderId.ToString("D")),
                deletingOrderIds,
                ct);
        }
        await UpdatePendingOrderCleanupAsync(profileId, deletingOrderIds, pending: false);
    }

    private async Task UpdatePendingOrderCleanupAsync(
        string profileId,
        IReadOnlySet<Guid> orderIds,
        bool pending)
    {
        if (orderIds.Count == 0)
        {
            return;
        }

        var cleanup = (await _localState.LoadPendingOrderCleanupAsync(profileId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var orderId in orderIds)
        {
            changed |= pending
                ? cleanup.Add(orderId.ToString("D"))
                : cleanup.Remove(orderId.ToString("D"));
        }
        if (changed)
        {
            await _localState.SavePendingOrderCleanupAsync(
                profileId,
                cleanup.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    private async Task DeleteLocalObjectAsync(
        (string Collection, string ObjectId) item,
        IReadOnlySet<Guid> deletingOrderIds,
        CancellationToken ct)
    {
        var adapter = GetAdapter(item.Collection);
        if (adapter is PlansProfileSyncAdapter plansAdapter &&
            await plansAdapter.LoadLinkedOrderIdAsync(item.ObjectId) is { } linkedOrderId &&
            deletingOrderIds.Contains(linkedOrderId))
        {
            await plansAdapter.DeleteLocalObjectForOrderDeletionAsync(
                item.ObjectId,
                linkedOrderId,
                ct);
            return;
        }

        await adapter.DeleteLocalObjectAsync(item.ObjectId, ct);
    }

    private async Task<Guid?> ResolveDeletedOrderCompanyProfileIdAsync(
        IProfileSyncCollectionAdapter adapter,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        if (_hostedOrders.Get(orderId)?.CompanyProfileId is { } projectedCompanyProfileId)
        {
            return projectedCompanyProfileId;
        }

        var local = (await adapter.LoadLocalObjectsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(
                item.ObjectId,
                orderId.ToString("D"),
                StringComparison.OrdinalIgnoreCase));
        if (local != null)
        {
            return ReadOrderCompanyProfileId(local);
        }

        if (_archiveSummaries != null)
        {
            var connection = await _localState.LoadConnectionSettingsAsync();
            return _archiveSummaries
                .GetAll(connection.ConnectionScopeId)
                .FirstOrDefault(record => record.OrderId == orderId)
                ?.CompanyProfileId;
        }

        return null;
    }

    private static Guid ReadOrderCompanyProfileId(ProfileSyncObjectEnvelope envelope)
    {
        var order = JsonSerializer.Deserialize<TradeOrder>(envelope.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Hosted Trade order '{envelope.ObjectId}' did not contain its company identity.");
        if (order.CompanyProfileId == Guid.Empty ||
            !string.Equals(
                order.Id.ToString("D"),
                envelope.ObjectId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Hosted Trade order '{envelope.ObjectId}' returned the wrong order or company identity.");
        }
        return order.CompanyProfileId;
    }

    private async Task QueueLocalSaveCoreAsync(
        string collection,
        string objectId,
        CancellationToken ct,
        HostedProfileConnectionSettings? capturedAuthority = null,
        bool allowSuppressed = false)
    {
        if (IsSuppressed && !allowSuppressed)
        {
            return;
        }

        var current = await _localState.LoadConnectionSettingsAsync();
        if (capturedAuthority != null &&
            !IsSameConnectionAuthority(current, capturedAuthority))
        {
            throw new InvalidOperationException(
                "The hosted profile authority changed before revision recovery could publish the local order.");
        }
        var settings = capturedAuthority ?? current;
        var profileId = settings.ProfileScopeId;
        await EnsurePendingSavesLoadedAsync(settings);
        if (!settings.IsConfigured || profileId == null)
        {
            return;
        }

        var adapter = GetAdapter(collection);
        var localObject = await LoadLocalObjectAsync(adapter, objectId, ct);
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
            ct,
            localObject);

        var lastRevision = await _localState.LoadLastSyncRevisionAsync(profileId);
        var previousStatus = CurrentStatus;
        SetStatus(new ProfileSyncStatus(
            true,
            hostReachable,
            Math.Max(previousStatus.LastSyncRevision, lastRevision),
            _pendingSaves.Count,
            _conflicts.Count,
            previousStatus.LastSyncedAtUtc,
            _conflicts.Count > 0 ? "Conflicts need review" : previousStatus.Message) with
        {
            ProfileId = profileId,
            Stage = hostReachable
                ? ProfileSyncStage.Ready
                : ProfileSyncStage.Failed,
            Failure = hostReachable
                ? ProfileSyncFailure.None
                : ProfileSyncFailure.Offline,
            AppliedObjectCount = previousStatus.AppliedObjectCount,
            TargetRevision = previousStatus.TargetRevision,
            OrderScopeReady = previousStatus.OrderScopeReady
        });
    }

    private async Task RequireConnectionAuthorityAsync(
        HostedProfileConnectionSettings expected)
    {
        var current = await _localState.LoadConnectionSettingsAsync();
        if (!IsSameConnectionAuthority(current, expected))
        {
            throw new InvalidOperationException(
                "The hosted profile authority changed while revision recovery was in progress.");
        }
    }

    private static bool IsSameConnectionAuthority(
        HostedProfileConnectionSettings left,
        HostedProfileConnectionSettings right) =>
        string.Equals(
            left.ConnectionScopeId,
            right.ConnectionScopeId,
            StringComparison.Ordinal) &&
        string.Equals(left.AccessKey, right.AccessKey, StringComparison.Ordinal);

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
        await EnsurePendingSavesLoadedAsync(settings);
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
            await SyncNowCoreAsync(null, null, ct);
        }

        ConnectionChanged?.Invoke();
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
        _pendingSavesConnectionScopeId = null;
        SetStatus(ProfileSyncStatus.LocalOnly());
        ConnectionChanged?.Invoke();
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
        if (!conflict.CanApplyRemote)
        {
            throw new InvalidOperationException(
                "This hosted conflict cannot be applied directly.");
        }

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
            if (conflict.RemoteObject.Deleted)
            {
                await ApplyAcceptedRemoteDeletionAsync(
                    adapter,
                    conflict.RemoteObject,
                    ct);
            }
            else
            {
                if (adapter is PlansProfileSyncAdapter plansAdapter &&
                    await plansAdapter.IsLinkedOrderPlanAsync(conflict.ObjectId))
                {
                    await plansAdapter.AdoptProtectedRemoteObjectAsync(
                        conflict.RemoteObject,
                        ct);
                }
                else
                {
                    await adapter.ApplyRemoteObjectAsync(conflict.RemoteObject, ct);
                }
            }
            if (!string.Equals(
                    conflict.Collection,
                    ProfileSyncCollections.TradeOrders,
                    StringComparison.OrdinalIgnoreCase))
            {
                await _localState.SaveObjectRevisionAsync(
                    profileId,
                    conflict.Collection,
                    conflict.ObjectId,
                    conflict.RemoteRevision);
            }
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
        if (!conflict.CanKeepLocal)
        {
            throw new InvalidOperationException(
                "This hosted linked plan is immutable. Use the hosted plan; the local version will be preserved as a separate plan.");
        }

        var settings = await _localState.LoadConnectionSettingsAsync();
        var profileId = settings.ProfileScopeId;
        if (!settings.IsConfigured || profileId == null)
        {
            throw new InvalidOperationException(
                "A connected hosted profile is required to keep the local version.");
        }

        var adapter = GetAdapter(conflict.Collection);
        var localObject = (await adapter.LoadLocalObjectsAsync(ct))
            .FirstOrDefault(item => item.ObjectId == conflict.ObjectId);
        if (localObject == null)
        {
            throw new InvalidOperationException(
                "The local object is no longer available to publish.");
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
            if (!await AdoptCommittedTradeOrderPutAsync(
                    settings,
                    adapter,
                    conflict.Collection,
                    conflict.ObjectId,
                    response.Object,
                    ct))
            {
                await _localState.SaveObjectRevisionAsync(
                    profileId,
                    conflict.Collection,
                    conflict.ObjectId,
                    response.Object.Revision);
            }
            _conflicts.Remove(conflict);
            await RemovePendingSaveAsync(
                profileId,
                conflict.Collection,
                conflict.ObjectId);
            RefreshStatusMessage("Local version kept");
            return;
        }

        throw new InvalidOperationException(
            response.ErrorMessage ??
            "The hosted version changed before the local version could be kept.");
    }

    public IDisposable SuppressNotifications()
    {
        _suppressionDepth++;
        return new SuppressionLease(this);
    }

    private async Task EnsurePendingSavesLoadedAsync(HostedProfileConnectionSettings settings)
    {
        var profileId = settings.ProfileScopeId;
        var connectionScopeId = settings.ConnectionScopeId;
        if (string.Equals(
                connectionScopeId,
                _pendingSavesConnectionScopeId,
                StringComparison.Ordinal) &&
            connectionScopeId != null)
        {
            return;
        }

        _pendingSaves.Clear();
        _conflicts.Clear();
        _pendingSavesConnectionScopeId = null;
        if (profileId == null || connectionScopeId == null)
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
        _pendingSavesConnectionScopeId = connectionScopeId;
    }

    private Task QueuePortableSettingSaveAsync(string key) =>
        QueueLocalSaveAsync(ProfileSyncCollections.Settings, key);

    private async Task<bool> RetryPendingSavesAsync(
        HostedProfileConnectionSettings settings,
        string profileId,
        CancellationToken ct,
        IReadOnlyCollection<string>? collections = null)
    {
        var hostReachable = true;
        foreach (var pending in _pendingSaves
                     .OrderBy(item => item.Collection, StringComparer.Ordinal)
                     .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                     .ToArray())
        {
            if (collections != null &&
                !collections.Contains(pending.Collection, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

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
        CancellationToken ct,
        ProfileSyncObjectEnvelope? knownLocalObject = null)
    {
        var adapter = GetAdapter(pending.Collection);
        var localObject = knownLocalObject ?? await LoadLocalObjectAsync(
            adapter,
            pending.ObjectId,
            ct);
        if (localObject == null)
        {
            return false;
        }

        if (string.Equals(
                pending.Collection,
                ProfileSyncCollections.TradeOrders,
                StringComparison.OrdinalIgnoreCase) &&
            HasPendingLinkedPlanPrerequisite(localObject))
        {
            return true;
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

            if (ShouldRepairLegacyLinkedPlan(
                    pending,
                    localObject,
                    response))
            {
                response = await RepairLegacyLinkedPlanAsync(
                    settings,
                    profileId,
                    pending,
                    localObject,
                    response,
                    ct);
            }
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
            var capabilities = await ResolveConflictCapabilitiesAsync(
                response.RemoteObject,
                ct);
            var conflictLocalRevision = await _localState.LoadObjectRevisionAsync(
                profileId,
                pending.Collection,
                pending.ObjectId);
            _conflicts.Add(new ProfileSyncConflict(
                pending.Collection,
                pending.ObjectId,
                conflictLocalRevision,
                response.RemoteObject.Revision,
                response.RemoteObject,
                capabilities.CanApplyRemote,
                capabilities.CanKeepLocal));
        }
        else if (response.Success && response.Object != null)
        {
            if (!await AdoptCommittedTradeOrderPutAsync(
                    settings,
                    adapter,
                    pending.Collection,
                    pending.ObjectId,
                    response.Object,
                    ct))
            {
                await _localState.SaveObjectRevisionAsync(
                    profileId,
                    pending.Collection,
                    pending.ObjectId,
                    response.Object.Revision);
            }
            await RemovePendingSaveAsync(
                profileId,
                pending.Collection,
                pending.ObjectId);
            _conflicts.RemoveAll(item => IsSameIdentity(
                item.Collection,
                item.ObjectId,
                pending.Collection,
                pending.ObjectId));
        }

        return true;
    }

    private async Task<bool> AdoptCommittedTradeOrderPutAsync(
        HostedProfileConnectionSettings expectedAuthority,
        IProfileSyncCollectionAdapter adapter,
        string collection,
        string objectId,
        ProfileSyncObjectEnvelope committed,
        CancellationToken ct)
    {
        if (adapter is not IHostedOrderProfileSyncAdapter ||
            !string.Equals(
                collection,
                ProfileSyncCollections.TradeOrders,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsSameIdentity(
                committed.Collection,
                committed.ObjectId,
                collection,
                objectId) ||
            committed.Deleted ||
            committed.Revision <= 0)
        {
            throw new InvalidOperationException(
                $"The profile host returned the wrong committed Trade order for '{objectId}'.");
        }

        _ = ReadOrderCompanyProfileId(committed);
        await RequireConnectionAuthorityAsync(expectedAuthority);
        await adapter.ApplyRemoteObjectAsync(committed, ct);
        return true;
    }

    private bool HasPendingLinkedPlanPrerequisite(ProfileSyncObjectEnvelope orderEnvelope)
    {
        var order = JsonSerializer.Deserialize<TradeOrder>(orderEnvelope.PayloadJson, JsonOptions);
        if (order?.CraftPlanLinkKind != TradeOrderCraftPlanLinkKind.OrderGenerated ||
            string.IsNullOrWhiteSpace(order.CraftPlanId))
        {
            return false;
        }

        return IsPending(ProfileSyncCollections.Plans, order.CraftPlanId) ||
               _conflicts.Any(conflict => IsSameIdentity(
                   conflict.Collection,
                   conflict.ObjectId,
                   ProfileSyncCollections.Plans,
                   order.CraftPlanId));
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

    private async Task RecordRemoteObjectConflictAsync(
        string profileId,
        ProfileSyncObjectEnvelope remoteObject,
        CancellationToken ct)
    {
        await AdoptLinkedPlanConflictBaseRevisionAsync(profileId, remoteObject);
        var localRevision = await _localState.LoadObjectRevisionAsync(
            profileId,
            remoteObject.Collection,
            remoteObject.ObjectId);
        await AddPendingSaveAsync(
            profileId,
            remoteObject.Collection,
            remoteObject.ObjectId);
        _conflicts.RemoveAll(item => IsSameIdentity(
            item.Collection,
            item.ObjectId,
            remoteObject.Collection,
            remoteObject.ObjectId));
        var capabilities = await ResolveConflictCapabilitiesAsync(remoteObject, ct);
        _conflicts.Add(new ProfileSyncConflict(
            remoteObject.Collection,
            remoteObject.ObjectId,
            localRevision,
            remoteObject.Revision,
            remoteObject,
            capabilities.CanApplyRemote,
            capabilities.CanKeepLocal));
    }

    private async Task<ProfileSyncConflictCapabilities> ResolveConflictCapabilitiesAsync(
        ProfileSyncObjectEnvelope remoteObject,
        CancellationToken ct)
    {
        var adapter = GetAdapter(remoteObject.Collection);
        if (adapter is PlansProfileSyncAdapter plansAdapter &&
            await plansAdapter.LoadLinkedOrderIdAsync(remoteObject.ObjectId) is { } localOrderId)
        {
            if (remoteObject.Deleted)
            {
                return new ProfileSyncConflictCapabilities(
                    CanApplyRemote: false,
                    CanKeepLocal: true);
            }

            var remotePlan = ProfileSyncPlanPayloadCodec.Deserialize(
                remoteObject.PayloadJson,
                remoteObject.ObjectId);
            return new ProfileSyncConflictCapabilities(
                CanApplyRemote: remotePlan.LinkedOrderId == localOrderId,
                CanKeepLocal: false);
        }

        if (remoteObject.Deleted &&
            string.Equals(
                remoteObject.Collection,
                ProfileSyncCollections.TradeOrders,
                StringComparison.OrdinalIgnoreCase))
        {
            var canDeleteOrder = Guid.TryParse(remoteObject.ObjectId, out var orderId) &&
                                 adapter is IHostedOrderProfileSyncAdapter &&
                                 await ResolveDeletedOrderCompanyProfileIdAsync(
                                     adapter,
                                     orderId,
                                     ct) != null;
            return new ProfileSyncConflictCapabilities(
                CanApplyRemote: canDeleteOrder,
                CanKeepLocal: true);
        }

        return new ProfileSyncConflictCapabilities(
            CanApplyRemote: true,
            CanKeepLocal: true);
    }

    private async Task ApplyAcceptedRemoteDeletionAsync(
        IProfileSyncCollectionAdapter adapter,
        ProfileSyncObjectEnvelope remoteObject,
        CancellationToken ct)
    {
        if (string.Equals(
                remoteObject.Collection,
                ProfileSyncCollections.TradeOrders,
                StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(remoteObject.ObjectId, out var orderId) ||
                await ResolveDeletedOrderCompanyProfileIdAsync(adapter, orderId, ct) is not { } companyId ||
                adapter is not IHostedOrderProfileSyncAdapter hostedOrderAdapter)
            {
                throw new InvalidOperationException(
                    "The hosted order deletion cannot be applied without its local company identity.");
            }

            await hostedOrderAdapter.ApplyRemoteDeletionAsync(
                orderId,
                companyId,
                remoteObject.Revision,
                ct);
            return;
        }

        await adapter.DeleteLocalObjectAsync(remoteObject.ObjectId, ct);
    }

    private static bool ShouldRepairLegacyLinkedPlan(
        ProfileSyncPendingSave pending,
        ProfileSyncObjectEnvelope localObject,
        ProfileSyncPutResponse response) =>
        response.Conflict &&
        ((response.RemoteObject?.Deleted ?? false) ||
         string.Equals(
             response.ErrorCode,
             "missing_remote_object",
             StringComparison.Ordinal) ||
         string.Equals(
             response.ErrorCode,
             "linked_plan_promotion_mismatch",
             StringComparison.Ordinal)) &&
        string.Equals(
            pending.Collection,
            ProfileSyncCollections.Plans,
            StringComparison.OrdinalIgnoreCase) &&
        ProfileSyncPlanPayloadCodec.Deserialize(
            localObject.PayloadJson,
            pending.ObjectId).LinkedOrderId.HasValue;

    private async Task<ProfileSyncPutResponse> RepairLegacyLinkedPlanAsync(
        HostedProfileConnectionSettings settings,
        string profileId,
        ProfileSyncPendingSave pending,
        ProfileSyncObjectEnvelope localObject,
        ProfileSyncPutResponse conflict,
        CancellationToken ct)
    {
        var baseRevision = conflict.RemoteObject?.Revision ?? 0;
        if (conflict.RemoteObject is { Deleted: false })
        {
            var deletion = await _client.DeleteObjectAsync(
                settings.HostUrl!,
                settings.AccessKey!,
                pending.Collection,
                pending.ObjectId,
                conflict.RemoteObject.Revision,
                ct);
            if (!deletion.Success || deletion.Object == null)
            {
                return deletion;
            }
            baseRevision = deletion.Object.Revision;
        }

        await _localState.SaveObjectRevisionAsync(
            profileId,
            pending.Collection,
            pending.ObjectId,
            baseRevision);
        return await _client.PutObjectAsync(
            settings.HostUrl!,
            settings.AccessKey!,
            pending.Collection,
            pending.ObjectId,
            new ProfileSyncPutRequest
            {
                PayloadJson = localObject.PayloadJson,
                ExpectedRevision = baseRevision
            },
            ct);
    }

    private async Task AdoptLinkedPlanConflictBaseRevisionAsync(
        string profileId,
        ProfileSyncObjectEnvelope remoteObject)
    {
        if (GetAdapter(remoteObject.Collection) is PlansProfileSyncAdapter plansAdapter &&
            await plansAdapter.IsLinkedOrderPlanAsync(remoteObject.ObjectId))
        {
            await _localState.SaveObjectRevisionAsync(
                profileId,
                remoteObject.Collection,
                remoteObject.ObjectId,
                remoteObject.Revision);
        }
    }

    private sealed record ProfileSyncConflictCapabilities(
        bool CanApplyRemote,
        bool CanKeepLocal);

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

    private static async Task<ProfileSyncObjectEnvelope?> LoadLocalObjectAsync(
        IProfileSyncCollectionAdapter adapter,
        string objectId,
        CancellationToken ct)
    {
        if (adapter is IProfileSyncSingleObjectAdapter singleObjectAdapter)
        {
            return await singleObjectAdapter.LoadLocalObjectAsync(objectId, ct);
        }

        return (await adapter.LoadLocalObjectsAsync(ct))
            .FirstOrDefault(item => string.Equals(
                item.ObjectId,
                objectId,
                StringComparison.Ordinal));
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
        var currentRestore = _hostedOrders.RestoreState;
        if (status.ProfileId != null &&
            string.Equals(
                currentRestore.ProfileId,
                status.ProfileId,
                StringComparison.OrdinalIgnoreCase))
        {
            _hostedOrders.TryPublishRestoreState(
                currentRestore.Apply(status, DateTime.UtcNow));
        }
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
