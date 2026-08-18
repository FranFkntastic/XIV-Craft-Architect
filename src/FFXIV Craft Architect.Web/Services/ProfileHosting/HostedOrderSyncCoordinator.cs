using System.Diagnostics;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed record HostedOrderSyncDiagnostics(
    string? ProfileId,
    string Role,
    bool StreamConnected,
    long LastEventRevision,
    long LastAppliedRevision,
    int ReconnectCount,
    string? Message,
    DateTime UpdatedAtUtc)
{
    public int OwnerCandidateCount { get; init; }
    public int OwnerRequestCount { get; init; }
    public int OwnerUnchangedCount { get; init; }
    public int OwnerChangedCount { get; init; }
    public int OwnerMissingCount { get; init; }
    public int OwnerDiscardedCount { get; init; }
    public long OwnerRequestBytes { get; init; }
    public long OwnerResponseBytes { get; init; }
    public long OwnerDurationMilliseconds { get; init; }
}

public sealed class HostedOrderSyncCoordinator : IAsyncDisposable
{
    private const string ModulePath = "./profile-sync-session.js?v=2";
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OwnerAuthorizationRetryInterval = TimeSpan.FromMinutes(5);
    private const int OwnerComparisonPageSize = 50;
    private const int MaximumConcurrentOwnerCompanies = 4;
    private readonly IJSRuntime _jsRuntime;
    private readonly ProfileSyncService _profileSync;
    private readonly ProfileSyncLocalStateService _localState;
    private readonly HostedOrderProjectionStore _hostedOrders;
    private readonly TradeCommissionOperationsClient _commissionClient;
    private readonly TradeOperationsPersistenceService _tradeOperations;
    private readonly AppState _appState;
    private readonly ILogger<HostedOrderSyncCoordinator> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly object _ownerAdoptionPassGate = new();
    private readonly object _ownerCompanyPassesGate = new();
    private readonly SemaphoreSlim _ownerCompanyConcurrency = new(
        MaximumConcurrentOwnerCompanies,
        MaximumConcurrentOwnerCompanies);
    private readonly Dictionary<Guid, SemaphoreSlim> _ownerCompanyLocks = [];
    private readonly Dictionary<Guid, OwnerVerificationCompanyPass> _ownerCompanyPasses = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(string ConnectionScopeId, Guid CompanyId), DateTime>
        _ownerAuthorizationRetryAfter = [];
    private IJSObjectReference? _module;
    private IJSObjectReference? _controller;
    private DotNetObjectReference<HostedOrderSyncCoordinator>? _callback;
    private CancellationTokenSource? _session;
    private Task? _recoveryLoop;
    private Task? _ownerAdoptionPass;
    private bool _ownerAdoptionPassRequested;
    private string? _activeProfileId;
    private string? _activeHostUrl;
    private string? _activeAccessKey;
    private bool _started;
    private bool _disposed;

    public HostedOrderSyncCoordinator(
        IJSRuntime jsRuntime,
        ProfileSyncService profileSync,
        ProfileSyncLocalStateService localState,
        HostedOrderProjectionStore hostedOrders,
        TradeCommissionOperationsClient commissionClient,
        TradeOperationsPersistenceService tradeOperations,
        AppState appState,
        ILogger<HostedOrderSyncCoordinator> logger)
    {
        _jsRuntime = jsRuntime;
        _profileSync = profileSync;
        _localState = localState;
        _hostedOrders = hostedOrders;
        _commissionClient = commissionClient;
        _tradeOperations = tradeOperations;
        _appState = appState;
        _logger = logger;
        _profileSync.ConnectionChanged += OnConnectionChanged;
        _hostedOrders.Changed += OnHostedOrderChanged;
        _hostedOrders.BatchChanged += OnHostedOrderBatchChanged;
    }

    public event Action? DiagnosticsChanged;

    public HostedOrderSyncDiagnostics Diagnostics { get; private set; } =
        new(null, "inactive", false, 0, 0, 0, null, DateTime.UtcNow);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }
            _started = true;
        }
        finally
        {
            _lifecycle.Release();
        }

        await ReconfigureAsync(cancellationToken);
    }

    [JSInvokable]
    public async Task<long> ReceiveProfileRevision(
        string profileId,
        long serverRevision,
        string source,
        long replayAfterRevision)
    {
        if (!IsActiveProfile(profileId) || serverRevision <= 0)
        {
            return 0;
        }

        UpdateDiagnostics(Diagnostics with
        {
            Role = source,
            LastEventRevision = Math.Max(Diagnostics.LastEventRevision, serverRevision),
            UpdatedAtUtc = DateTime.UtcNow
        });
        return await SynchronizeAsync(
            profileId,
            serverRevision,
            replayAfterRevision,
            _lifetime.Token);
    }

    [JSInvokable]
    public Task ReceiveProfileStreamState(
        string profileId,
        string role,
        bool connected,
        string? message,
        int reconnectCount)
    {
        if (IsActiveProfile(profileId))
        {
            UpdateDiagnostics(Diagnostics with
            {
                Role = string.IsNullOrWhiteSpace(role) ? "unknown" : role,
                StreamConnected = connected,
                ReconnectCount = Math.Max(0, reconnectCount),
                Message = message,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task<long> RecoverProfileRevision(
        string profileId,
        long replayAfterRevision) =>
        IsActiveProfile(profileId)
            ? SynchronizeAsync(
                profileId,
                null,
                replayAfterRevision,
                _lifetime.Token)
            : Task.FromResult(0L);

    private void OnConnectionChanged() =>
        _ = ReconfigureSafelyAsync();

    private async Task ReconfigureSafelyAsync()
    {
        try
        {
            await ReconfigureAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Hosted order synchronization could not reconfigure.");
            UpdateDiagnostics(Diagnostics with
            {
                StreamConnected = false,
                Message = exception.Message,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
    }

    private async Task ReconfigureAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        cancellationToken = linkedCancellation.Token;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                return;
            }

            // Resolve any authenticated authority adoption before capturing the
            // settings that bind cursor replay, owner projection, and SSE.
            await _profileSync.PrepareAuthorityAsync(cancellationToken);
            var settings = await _localState.LoadConnectionSettingsAsync();
            var profileId = settings.ProfileScopeId;
            if (settings.IsConfigured &&
                string.Equals(profileId, _activeProfileId, StringComparison.Ordinal) &&
                string.Equals(settings.HostUrl, _activeHostUrl, StringComparison.Ordinal) &&
                string.Equals(settings.AccessKey, _activeAccessKey, StringComparison.Ordinal))
            {
                return;
            }

            await StopSessionAsync();
            if (!settings.IsConfigured || profileId == null)
            {
                _hostedOrders.ResetForProfile(null);
                UpdateDiagnostics(new HostedOrderSyncDiagnostics(
                    null,
                    "inactive",
                    false,
                    0,
                    0,
                    0,
                    null,
                    DateTime.UtcNow));
                return;
            }

            var cursor = await _localState.LoadLastSyncRevisionAsync(profileId);
            var sameProfile = string.Equals(
                _hostedOrders.RestoreState.ProfileId,
                profileId,
                StringComparison.OrdinalIgnoreCase);
            _hostedOrders.BeginProfileRestore(
                profileId,
                hasTrustedProjection: sameProfile &&
                                      _hostedOrders.RestoreState.ShowsCompleteProjection,
                cursor,
                DateTime.UtcNow,
                settings.ConnectionScopeId!);
            IJSObjectReference? controller = null;
            try
            {
                await _profileSync.InitializeAsync(cancellationToken);
                cursor = await _localState.LoadLastSyncRevisionAsync(profileId);
                if (_profileSync.CurrentStatus.Failure is
                    ProfileSyncFailure.Authentication or
                    ProfileSyncFailure.Incompatible or
                    ProfileSyncFailure.Unverifiable)
                {
                    UpdateDiagnostics(new HostedOrderSyncDiagnostics(
                        profileId,
                        "inactive",
                        false,
                        cursor,
                        cursor,
                        0,
                        _profileSync.CurrentStatus.Message,
                        DateTime.UtcNow));
                    return;
                }
                _callback ??= DotNetObjectReference.Create(this);
                _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>(
                    "import",
                    cancellationToken,
                    ModulePath);
                controller = await _module.InvokeAsync<IJSObjectReference>(
                    "createProfileSyncSession",
                    cancellationToken,
                    _callback,
                    settings.HostUrl,
                    settings.AccessKey,
                    profileId,
                    cursor);

                _controller = controller;
                _activeProfileId = profileId;
                _activeHostUrl = settings.HostUrl;
                _activeAccessKey = settings.AccessKey;
                _session = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _recoveryLoop = RunRecoveryLoopAsync(profileId, _session.Token);
                UpdateDiagnostics(new HostedOrderSyncDiagnostics(
                    profileId,
                    "follower",
                    false,
                    cursor,
                    cursor,
                    0,
                    null,
                    DateTime.UtcNow));
                StartOwnerAdoptionPass(profileId);
            }
            catch
            {
                if (controller != null)
                {
                    await controller.DisposeAsync();
                }
                _hostedOrders.ResetForProfile(null);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task RunRecoveryLoopAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RecoveryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var controller = _controller;
                if (controller != null && IsActiveProfile(profileId))
                {
                    await controller.InvokeAsync<long>(
                        "recover",
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<long> SynchronizeAsync(
        string profileId,
        long? targetRevision,
        long? replayAfterRevision,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!IsActiveProfile(profileId))
            {
                return 0;
            }

            var before = Math.Max(0, _profileSync.CurrentStatus.LastSyncRevision);
            if (replayAfterRevision.HasValue)
            {
                await _profileSync.SyncFromRevisionAsync(
                    replayAfterRevision.Value,
                    targetRevision,
                    cancellationToken);
            }
            else
            {
                await _profileSync.SyncNowAsync(cancellationToken);
            }
            var after = Math.Max(0, _profileSync.CurrentStatus.LastSyncRevision);
            if (after > before)
            {
                _profileSync.NotifyProfileMetadataMayHaveChanged();
            }
            _appState.NotifyTradeOperationsDataChanged();

            UpdateDiagnostics(Diagnostics with
            {
                LastAppliedRevision = Math.Max(Diagnostics.LastAppliedRevision, after),
                Message = _profileSync.CurrentStatus.HostReachable
                    ? null
                    : _profileSync.CurrentStatus.Message,
                UpdatedAtUtc = DateTime.UtcNow
            });
            StartOwnerAdoptionPass(profileId);
            return after;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task RefreshOwnerProjectionAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _hostedOrders.Get(orderId);
        var companyId = snapshot?.Order?.CompanyCommission?.CompanyId.Value;
        if (companyId == null)
        {
            return;
        }

        Task? scheduled = null;
        lock (_ownerCompanyPassesGate)
        {
            if (_ownerCompanyPasses.TryGetValue(companyId.Value, out var pass))
            {
                scheduled = pass.TryPrioritize(orderId);
            }
        }
        if (scheduled != null)
        {
            await scheduled.WaitAsync(cancellationToken);
            return;
        }

        var companyLock = GetOwnerCompanyLock(companyId.Value);
        await companyLock.WaitAsync(cancellationToken);
        try
        {
            await RefreshOwnerProjectionCoreAsync(orderId, cancellationToken);
        }
        finally
        {
            companyLock.Release();
        }
    }

    private void StartOwnerAdoptionPass(string profileId)
    {
        var session = _session;
        if (session == null || session.IsCancellationRequested)
        {
            return;
        }

        lock (_ownerAdoptionPassGate)
        {
            if (_ownerAdoptionPass is { IsCompleted: false })
            {
                _ownerAdoptionPassRequested = true;
                return;
            }
            _ownerAdoptionPassRequested = false;
            _ownerAdoptionPass = RunOwnerAdoptionPassLoopAsync(profileId, session.Token);
        }
    }

    private void OnHostedOrderChanged(HostedOrderProjectionSnapshot _)
    {
        if (_activeProfileId is { } profileId)
        {
            StartOwnerAdoptionPass(profileId);
        }
    }

    private void OnHostedOrderBatchChanged(
        IReadOnlyList<HostedOrderProjectionSnapshot> _)
    {
        if (_activeProfileId is { } profileId)
        {
            StartOwnerAdoptionPass(profileId);
        }
    }

    private async Task RunOwnerAdoptionPassLoopAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunOwnerAdoptionPassAsync(profileId, cancellationToken);
            lock (_ownerAdoptionPassGate)
            {
                if (!_ownerAdoptionPassRequested)
                {
                    _ownerAdoptionPass = null;
                    return;
                }
                _ownerAdoptionPassRequested = false;
            }
        }
    }

    private async Task RunOwnerAdoptionPassAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var stopwatch = Stopwatch.StartNew();
        var metrics = new OwnerVerificationPassMetrics();
        try
        {
            var candidates = _hostedOrders.GetAll()
                .Where(NeedsOwnerAdoption)
                .Select(snapshot => new
                {
                    snapshot.OrderId,
                    CompanyId = snapshot.Order!.CompanyCommission!.CompanyId.Value
                })
                .ToArray();
            metrics.CandidateCount = candidates.Length;
            var passes = candidates
                .GroupBy(candidate => candidate.CompanyId)
                .Select(group => new OwnerVerificationCompanyPass(
                    group.Key,
                    group.Select(candidate => candidate.OrderId)))
                .ToArray();
            lock (_ownerCompanyPassesGate)
            {
                foreach (var pass in passes)
                {
                    _ownerCompanyPasses[pass.CompanyId] = pass;
                }
            }
            await Task.WhenAll(passes.Select(pass => RunOwnerCompanyPassAsync(
                profileId,
                pass,
                metrics,
                cancellationToken)));
            if (metrics.ChangedCount > 0)
            {
                _appState.NotifyTradeOperationsDataChanged();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Background owner projection adoption could not complete.");
        }
        finally
        {
            stopwatch.Stop();
            var summary = metrics.Snapshot();
            UpdateDiagnostics(Diagnostics with
            {
                OwnerCandidateCount = summary.CandidateCount,
                OwnerRequestCount = summary.RequestCount,
                OwnerUnchangedCount = summary.UnchangedCount,
                OwnerChangedCount = summary.ChangedCount,
                OwnerMissingCount = summary.MissingCount,
                OwnerDiscardedCount = summary.DiscardedCount,
                OwnerRequestBytes = summary.RequestBytes,
                OwnerResponseBytes = summary.ResponseBytes,
                OwnerDurationMilliseconds = (long)Math.Ceiling(stopwatch.Elapsed.TotalMilliseconds),
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
    }

    private async Task RunOwnerCompanyPassAsync(
        string profileId,
        OwnerVerificationCompanyPass pass,
        OwnerVerificationPassMetrics metrics,
        CancellationToken cancellationToken)
    {
        await _ownerCompanyConcurrency.WaitAsync(cancellationToken);
        try
        {
            while (pass.TakeNext(OwnerComparisonPageSize) is { Length: > 0 } batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsActiveProfile(profileId))
                {
                    pass.Fail(new InvalidOperationException(
                        "The hosted profile changed before owner verification completed."));
                    return;
                }

                var companyLock = GetOwnerCompanyLock(pass.CompanyId);
                await companyLock.WaitAsync(cancellationToken);
                try
                {
                    var result = await VerifyOwnerComparisonBatchAsync(
                        profileId,
                        new CompanyId(pass.CompanyId),
                        batch,
                        cancellationToken);
                    metrics.Add(result);
                    pass.Complete(batch);
                }
                catch (TradeCompanyAuthorizationException exception)
                {
                    var connection = await _localState.LoadConnectionSettingsAsync();
                    if (connection.ConnectionScopeId is { } scope)
                    {
                        _ownerAuthorizationRetryAfter[(scope, pass.CompanyId)] =
                            DateTime.UtcNow.Add(OwnerAuthorizationRetryInterval);
                    }
                    pass.Fail(exception);
                    _logger.LogWarning(
                        exception,
                        "Owner comparison authorization failed for Trade company {CompanyId}; further attempts are paused for this connection.",
                        pass.CompanyId);
                    return;
                }
                catch (Exception exception)
                {
                    pass.Fail(exception);
                    _logger.LogWarning(
                        exception,
                        "Owner comparison failed for Trade company {CompanyId}; preserving its last truthful local projections.",
                        pass.CompanyId);
                    return;
                }
                finally
                {
                    companyLock.Release();
                }
            }
        }
        finally
        {
            lock (_ownerCompanyPassesGate)
            {
                if (_ownerCompanyPasses.GetValueOrDefault(pass.CompanyId) == pass)
                {
                    _ownerCompanyPasses.Remove(pass.CompanyId);
                }
            }
            _ownerCompanyConcurrency.Release();
        }
    }

    private async Task<OwnerVerificationBatchResult> VerifyOwnerComparisonBatchAsync(
        string profileId,
        CompanyId companyId,
        IReadOnlyList<Guid> orderIds,
        CancellationToken cancellationToken)
    {
        var connection = await _localState.LoadConnectionSettingsAsync();
        var authority = _hostedOrders.CaptureAuthorityScope();
        if (!IsCurrentAuthority(authority, connection, profileId))
        {
            throw new InvalidOperationException(
                "The hosted order authority changed before owner comparison began.");
        }
        var now = DateTime.UtcNow;
        var retryKey = (connection.ConnectionScopeId!, companyId.Value);
        if (_ownerAuthorizationRetryAfter.TryGetValue(retryKey, out var retryAfter) &&
            ShouldDeferOwnerAuthorizationRetry(retryAfter, now))
        {
            return new OwnerVerificationBatchResult(0, 0, 0, orderIds.Count, 0, 0, 0);
        }

        var snapshots = orderIds
            .Select(orderId => _hostedOrders.Get(orderId))
            .Where(snapshot => snapshot != null && NeedsOwnerAdoption(snapshot))
            .Cast<HostedOrderProjectionSnapshot>()
            .Where(snapshot => snapshot.Order!.CompanyCommission!.CompanyId == companyId)
            .ToArray();
        if (snapshots.Length == 0)
        {
            return new OwnerVerificationBatchResult(0, 0, 0, 0, 0, 0, 0);
        }
        var receipts = await _localState.LoadOwnerReceiptsAsync(
            profileId,
            snapshots.Select(snapshot => snapshot.OrderId));
        var requested = snapshots.Select(snapshot =>
        {
            var commission = snapshot.Order!.CompanyCommission!;
            var receipt = receipts.GetValueOrDefault(snapshot.OrderId);
            var receiptMatches = receipt != null &&
                receipt.CompanyId == companyId &&
                receipt.CommissionId == commission.CommissionId &&
                receipt.ProfileObjectRevision.Value == snapshot.ObjectRevision;
            return new CompanyCommissionOwnerComparisonItem
            {
                OrderId = snapshot.OrderId,
                CommissionId = commission.CommissionId,
                ProfileObjectRevision = new CompanyRecordRevision(snapshot.ObjectRevision),
                ObjectRevision = receiptMatches
                    ? receipt!.ObjectRevision
                    : CompanyRecordRevision.None,
                CompanyRevision = receiptMatches
                    ? receipt!.CompanyRevision
                    : CompanyRecordRevision.None
            };
        }).ToArray();
        var transport = await _commissionClient.CompareOwnerProjectionsAsync(
            connection,
            companyId,
            new CompanyCommissionOwnerComparisonRequest { Items = requested },
            cancellationToken);
        _ownerAuthorizationRetryAfter.Remove(retryKey);
        ValidateOwnerComparisonResponse(companyId, requested, transport.Response);
        if (!await IsCurrentAuthorityAsync(authority, connection, profileId))
        {
            return new OwnerVerificationBatchResult(
                0,
                0,
                0,
                requested.Length,
                transport.RequestBytes,
                transport.ResponseBytes,
                1);
        }

        var persistence = new List<HostedOwnerVerificationPersistenceItem>(requested.Length);
        var projections = new List<CompanyCommissionOwnerProjection>(requested.Length);
        var missing = new List<(Guid OrderId, HostedOrderProjectionSnapshot Expected)>();
        var unchanged = 0;
        var changed = 0;
        var discarded = 0;
        foreach (var item in transport.Response.Items)
        {
            var current = _hostedOrders.Get(item.OrderId);
            var requestItem = requested.Single(requestedItem => requestedItem.OrderId == item.OrderId);
            if (current == null ||
                current.ObjectRevision != requestItem.ProfileObjectRevision.Value ||
                current.Order?.CompanyCommission is not { } commission ||
                commission.CompanyId != companyId ||
                commission.CommissionId != item.CommissionId)
            {
                discarded++;
                continue;
            }
            if (item.Status == CompanyCommissionOwnerComparisonStatus.Missing)
            {
                persistence.Add(new HostedOwnerVerificationPersistenceItem(
                    item.OrderId,
                    current.ObjectRevision,
                    null,
                    null,
                    ClearReceipt: true));
                missing.Add((item.OrderId, current));
                continue;
            }

            ValidateOwnerReceipt(current, companyId, item);
            CompanyCommissionOwnerProjection projection;
            if (item.Status == CompanyCommissionOwnerComparisonStatus.Unchanged)
            {
                if (item.Projection != null)
                {
                    throw new InvalidOperationException(
                        "An unchanged owner acknowledgement included a full order payload.");
                }
                var receipt = item.Receipt!;
                projection = new CompanyCommissionOwnerProjection
                {
                    Order = current.Order,
                    ObjectRevision = receipt.ObjectRevision,
                    CompanyRevision = receipt.CompanyRevision,
                    ProfileObjectRevision = receipt.ProfileObjectRevision
                };
                unchanged++;
            }
            else if (item.Status == CompanyCommissionOwnerComparisonStatus.Changed &&
                     item.Projection != null)
            {
                projection = item.Projection;
                changed++;
            }
            else
            {
                throw new InvalidOperationException(
                    "The owner comparison response used an unsupported item status.");
            }

            ValidateOwnerProjection(current, projection);
            projections.Add(projection);
            persistence.Add(new HostedOwnerVerificationPersistenceItem(
                item.OrderId,
                current.ObjectRevision,
                item.Status == CompanyCommissionOwnerComparisonStatus.Changed
                    ? projection.Order
                    : null,
                item.Receipt));
        }

        if (persistence.Count > 0 &&
            !await _localState.PersistOwnerVerificationBatchAsync(connection, persistence))
        {
            return new OwnerVerificationBatchResult(
                0,
                0,
                0,
                requested.Length,
                transport.RequestBytes,
                transport.ResponseBytes,
                1);
        }
        if (!await IsCurrentAuthorityAsync(authority, connection, profileId))
        {
            return new OwnerVerificationBatchResult(
                0,
                0,
                0,
                requested.Length,
                transport.RequestBytes,
                transport.ResponseBytes,
                1);
        }

        var applied = _hostedOrders.TryAdoptCommittedOwnerBatch(authority, projections);
        foreach (var item in missing)
        {
            _hostedOrders.TryClearOwner(authority, item.OrderId, item.Expected);
        }
        return new OwnerVerificationBatchResult(
            unchanged,
            changed,
            missing.Count,
            discarded + applied.RejectedCount,
            transport.RequestBytes,
            transport.ResponseBytes,
            1);
    }

    private SemaphoreSlim GetOwnerCompanyLock(Guid companyId)
    {
        lock (_ownerCompanyPassesGate)
        {
            if (!_ownerCompanyLocks.TryGetValue(companyId, out var companyLock))
            {
                companyLock = new SemaphoreSlim(1, 1);
                _ownerCompanyLocks[companyId] = companyLock;
            }
            return companyLock;
        }
    }

    private async Task RefreshOwnerProjectionCoreAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var connection = await _localState.LoadConnectionSettingsAsync();
        var profileId = connection.ProfileScopeId;
        if (profileId == null)
        {
            return;
        }
        var authority = _hostedOrders.CaptureAuthorityScope();
        if (!IsCurrentAuthority(authority, connection, profileId))
        {
            throw new InvalidOperationException(
                "The hosted order authority changed before owner adoption began.");
        }

        var connectionScopeId = connection.ConnectionScopeId!;
        var now = DateTime.UtcNow;
        var candidate = _hostedOrders.Get(orderId);
        if (candidate == null || !NeedsOwnerAdoption(candidate))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var commission = candidate.Order!.CompanyCommission!;
        var companyId = commission.CompanyId.Value;
        var retryKey = (connectionScopeId, companyId);
        if (_ownerAuthorizationRetryAfter.TryGetValue(retryKey, out var retryAfter) &&
            ShouldDeferOwnerAuthorizationRetry(retryAfter, now))
        {
            return;
        }
        try
        {
            var projection = await _commissionClient.LoadOwnerProjectionAsync(
                connection,
                companyId,
                commission.CommissionId,
                cancellationToken);
            _ownerAuthorizationRetryAfter.Remove(retryKey);

            // The order can advance while the authenticated projection is in flight.
            // Re-read it before making any durable local change.
            if (!await IsCurrentAuthorityAsync(authority, connection, profileId))
            {
                throw new InvalidOperationException(
                    "The hosted order authority changed while owner adoption was in progress.");
            }
            var current = _hostedOrders.Get(candidate.OrderId);
            if (current == null || !NeedsOwnerAdoption(current))
            {
                return;
            }

            ValidateOwnerProjection(current, projection);
            var adoption = await _hostedOrders.AdoptAndPersistCommittedOwnerAsync(
                authority,
                projection,
                async winner =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _localState.PersistHostedTradeOrderStateAsync(
                        connection,
                        winner.Order,
                        winner.OrderId,
                        winner.ObjectRevision,
                        winner.Deleted);
                    if (!await IsCurrentAuthorityAsync(authority, connection, profileId))
                    {
                        throw new InvalidOperationException(
                            "The hosted order authority changed while owner persistence was in progress.");
                    }
                },
                () => IsCurrentAuthorityAsync(authority, connection, profileId));
            if (adoption is not (
                HostedOrderCommittedProjectionResult.Adopted or
                HostedOrderCommittedProjectionResult.AlreadyCurrent))
            {
                throw new InvalidOperationException(
                    $"The authenticated owner projection could not be adopted because its authority is {adoption}.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TradeCompanyAuthorizationException exception)
        {
            _ownerAuthorizationRetryAfter[retryKey] =
                DateTime.UtcNow.Add(OwnerAuthorizationRetryInterval);
            _logger.LogWarning(
                exception,
                "Owner projection authorization failed for Trade company {CompanyId}; further adoption attempts are paused for this connection.",
                companyId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Authenticated owner projection for hosted order {OrderId} could not be adopted; preserving the last truthful local projection.",
                candidate.OrderId);
        }
    }

    private bool IsCurrentAuthority(
        HostedOrderAuthorityScope authority,
        HostedProfileConnectionSettings connection,
        string profileId) =>
        _hostedOrders.IsCurrentAuthority(authority) &&
        string.Equals(
            authority.ProfileId,
            profileId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            connection.ProfileScopeId,
            profileId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            authority.ConnectionScopeId,
            connection.ConnectionScopeId,
            StringComparison.Ordinal);

    private async Task<bool> IsCurrentAuthorityAsync(
        HostedOrderAuthorityScope authority,
        HostedProfileConnectionSettings connection,
        string profileId)
    {
        if (!IsCurrentAuthority(authority, connection, profileId))
        {
            return false;
        }
        var current = await _localState.LoadConnectionSettingsAsync();
        return string.Equals(
                   connection.ConnectionScopeId,
                   current.ConnectionScopeId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   connection.ProfileScopeId,
                   current.ProfileScopeId,
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool NeedsOwnerAdoption(HostedOrderProjectionSnapshot snapshot) =>
        !snapshot.Deleted &&
        snapshot.Order?.CompanyCommission != null &&
        !TradeOrderStatusWorkflow.IsArchived(snapshot.Order.Status) &&
        (snapshot.OwnerProjection == null ||
         snapshot.OwnerProjection.ObjectRevision.Value < snapshot.ObjectRevision);

    internal static bool ShouldDeferOwnerAuthorizationRetry(
        DateTime retryAfterUtc,
        DateTime nowUtc) =>
        retryAfterUtc > nowUtc;

    internal static void ValidateOwnerProjection(
        HostedOrderProjectionSnapshot expected,
        CompanyCommissionOwnerProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var expectedOrder = expected.Order;
        var expectedCommission = expectedOrder?.CompanyCommission;
        var returnedCommission = projection.Order.CompanyCommission;
        if (expectedOrder == null ||
            expectedCommission == null ||
            projection.Order.Id != expected.OrderId ||
            projection.Order.CompanyProfileId != expected.CompanyProfileId ||
            returnedCommission == null ||
            returnedCommission.CompanyId != expectedCommission.CompanyId ||
            returnedCommission.CommissionId != expectedCommission.CommissionId ||
            projection.ObjectRevision.Value <= 0 ||
            (projection.ProfileObjectRevision ?? projection.ObjectRevision).Value <
                expected.ObjectRevision ||
            projection.CompanyRevision.Value <= 0)
        {
            throw new InvalidOperationException(
                "The authenticated owner endpoint returned the wrong commission, a stale order, or omitted authoritative revisions.");
        }
    }

    internal static void ValidateOwnerComparisonResponse(
        CompanyId expectedCompanyId,
        IReadOnlyList<CompanyCommissionOwnerComparisonItem> requested,
        CompanyCommissionOwnerComparisonResponse response)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(response);
        if (response.CompanyId != expectedCompanyId ||
            response.CompanyRevision.Value <= 0 ||
            response.Items.Count != requested.Count)
        {
            throw new InvalidOperationException(
                "The owner comparison response omitted its exact company or item basis.");
        }

        var requestedByOrder = requested.ToDictionary(item => item.OrderId);
        var returned = new HashSet<Guid>();
        foreach (var item in response.Items)
        {
            if (!returned.Add(item.OrderId) ||
                !requestedByOrder.TryGetValue(item.OrderId, out var expected) ||
                item.CommissionId != expected.CommissionId)
            {
                throw new InvalidOperationException(
                    "The owner comparison response duplicated, omitted, or crossed an identity boundary.");
            }
        }
    }

    internal static void ValidateOwnerReceipt(
        HostedOrderProjectionSnapshot expected,
        CompanyId companyId,
        CompanyCommissionOwnerComparisonResult result)
    {
        var commission = expected.Order?.CompanyCommission;
        var receipt = result.Receipt;
        if (commission == null ||
            receipt == null ||
            result.OrderId != expected.OrderId ||
            receipt.OrderId != expected.OrderId ||
            receipt.CompanyId != companyId ||
            receipt.CommissionId != commission.CommissionId ||
            result.CommissionId != commission.CommissionId ||
            receipt.ProfileObjectRevision.Value < expected.ObjectRevision ||
            receipt.ObjectRevision.Value <= 0 ||
            receipt.CompanyRevision.Value <= 0)
        {
            throw new InvalidOperationException(
                "The owner comparison acknowledgement omitted or crossed its exact receipt identity.");
        }
        if (result.Projection is { } projection &&
            (projection.Order.Id != receipt.OrderId ||
             projection.ObjectRevision != receipt.ObjectRevision ||
             projection.CompanyRevision != receipt.CompanyRevision ||
             projection.ProfileObjectRevision != receipt.ProfileObjectRevision))
        {
            throw new InvalidOperationException(
                "The changed owner projection disagreed with its receipt.");
        }
    }

    private sealed record OwnerVerificationBatchResult(
        int UnchangedCount,
        int ChangedCount,
        int MissingCount,
        int DiscardedCount,
        int RequestBytes,
        int ResponseBytes,
        int RequestCount);

    private sealed class OwnerVerificationPassMetrics
    {
        private readonly object _gate = new();
        private int _requestCount;
        private int _unchangedCount;
        private int _changedCount;
        private int _missingCount;
        private int _discardedCount;
        private long _requestBytes;
        private long _responseBytes;

        public int CandidateCount { get; set; }

        public int ChangedCount
        {
            get
            {
                lock (_gate)
                {
                    return _changedCount;
                }
            }
        }

        public void Add(OwnerVerificationBatchResult result)
        {
            lock (_gate)
            {
                _requestCount += result.RequestCount;
                _unchangedCount += result.UnchangedCount;
                _changedCount += result.ChangedCount;
                _missingCount += result.MissingCount;
                _discardedCount += result.DiscardedCount;
                _requestBytes += result.RequestBytes;
                _responseBytes += result.ResponseBytes;
            }
        }

        public OwnerVerificationPassSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new OwnerVerificationPassSnapshot(
                    CandidateCount,
                    _requestCount,
                    _unchangedCount,
                    _changedCount,
                    _missingCount,
                    _discardedCount,
                    _requestBytes,
                    _responseBytes);
            }
        }
    }

    private sealed record OwnerVerificationPassSnapshot(
        int CandidateCount,
        int RequestCount,
        int UnchangedCount,
        int ChangedCount,
        int MissingCount,
        int DiscardedCount,
        long RequestBytes,
        long ResponseBytes);

    private sealed class OwnerVerificationCompanyPass
    {
        private readonly object _gate = new();
        private readonly List<Guid> _pending;
        private readonly HashSet<Guid> _inFlight = [];
        private readonly Dictionary<Guid, TaskCompletionSource> _waiters = [];

        public OwnerVerificationCompanyPass(Guid companyId, IEnumerable<Guid> orderIds)
        {
            CompanyId = companyId;
            _pending = orderIds.Distinct().ToList();
        }

        public Guid CompanyId { get; }

        public Task? TryPrioritize(Guid orderId)
        {
            lock (_gate)
            {
                var pendingIndex = _pending.IndexOf(orderId);
                if (pendingIndex < 0 && !_inFlight.Contains(orderId))
                {
                    return null;
                }
                if (pendingIndex > 0)
                {
                    _pending.RemoveAt(pendingIndex);
                    _pending.Insert(0, orderId);
                }
                if (!_waiters.TryGetValue(orderId, out var waiter))
                {
                    waiter = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[orderId] = waiter;
                }
                return waiter.Task;
            }
        }

        public Guid[] TakeNext(int pageSize)
        {
            lock (_gate)
            {
                var count = Math.Min(pageSize, _pending.Count);
                if (count == 0)
                {
                    return [];
                }
                var batch = _pending.Take(count).ToArray();
                _pending.RemoveRange(0, count);
                foreach (var orderId in batch)
                {
                    _inFlight.Add(orderId);
                }
                return batch;
            }
        }

        public void Complete(IEnumerable<Guid> orderIds)
        {
            lock (_gate)
            {
                foreach (var orderId in orderIds)
                {
                    _inFlight.Remove(orderId);
                    if (_waiters.Remove(orderId, out var waiter))
                    {
                        waiter.TrySetResult();
                    }
                }
            }
        }

        public void Fail(Exception exception)
        {
            lock (_gate)
            {
                foreach (var waiter in _waiters.Values)
                {
                    waiter.TrySetException(exception);
                }
                _waiters.Clear();
                _pending.Clear();
                _inFlight.Clear();
            }
        }
    }

    private bool IsActiveProfile(string profileId) =>
        !_disposed &&
        string.Equals(profileId, _activeProfileId, StringComparison.OrdinalIgnoreCase);

    private async Task StopSessionAsync()
    {
        _session?.Cancel();
        _activeProfileId = null;
        _activeHostUrl = null;
        _activeAccessKey = null;
        _ownerAuthorizationRetryAfter.Clear();
        if (_controller != null)
        {
            try
            {
                await _controller.InvokeVoidAsync("stop");
            }
            catch (JSDisconnectedException)
            {
            }
            await _controller.DisposeAsync();
            _controller = null;
        }

        if (_recoveryLoop != null)
        {
            try
            {
                await _recoveryLoop;
            }
            catch (OperationCanceledException)
            {
            }
            _recoveryLoop = null;
        }
        if (_ownerAdoptionPass != null)
        {
            await _ownerAdoptionPass;
            _ownerAdoptionPass = null;
        }
        _session?.Dispose();
        _session = null;
    }

    private void UpdateDiagnostics(HostedOrderSyncDiagnostics diagnostics)
    {
        Diagnostics = diagnostics;
        DiagnosticsChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _profileSync.ConnectionChanged -= OnConnectionChanged;
            _hostedOrders.Changed -= OnHostedOrderChanged;
            _hostedOrders.BatchChanged -= OnHostedOrderBatchChanged;
            await StopSessionAsync();
            await _sync.WaitAsync();
            _sync.Release();
            if (_module != null)
            {
                await _module.DisposeAsync();
                _module = null;
            }
            _callback?.Dispose();
            _callback = null;
            lock (_ownerCompanyPassesGate)
            {
                foreach (var companyLock in _ownerCompanyLocks.Values)
                {
                    companyLock.Dispose();
                }
                _ownerCompanyLocks.Clear();
            }
            _ownerCompanyConcurrency.Dispose();
        }
        finally
        {
            _lifecycle.Release();
            _sync.Dispose();
            _lifecycle.Dispose();
            _lifetime.Dispose();
        }
    }
}
