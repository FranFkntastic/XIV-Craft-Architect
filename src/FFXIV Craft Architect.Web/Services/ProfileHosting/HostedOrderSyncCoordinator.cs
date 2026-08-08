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
    DateTime UpdatedAtUtc);

public sealed class HostedOrderSyncCoordinator : IAsyncDisposable
{
    private const string ModulePath = "./profile-sync-session.js?v=2";
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OwnerAuthorizationRetryInterval = TimeSpan.FromMinutes(5);
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
    private readonly SemaphoreSlim _ownerAdoption = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(string ConnectionScopeId, Guid CompanyId), DateTime>
        _ownerAuthorizationRetryAfter = [];
    private IJSObjectReference? _module;
    private IJSObjectReference? _controller;
    private DotNetObjectReference<HostedOrderSyncCoordinator>? _callback;
    private CancellationTokenSource? _session;
    private Task? _recoveryLoop;
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
            _appState.NotifyTradeOperationsDataChanged();

            UpdateDiagnostics(Diagnostics with
            {
                LastAppliedRevision = Math.Max(Diagnostics.LastAppliedRevision, after),
                Message = _profileSync.CurrentStatus.HostReachable
                    ? null
                    : _profileSync.CurrentStatus.Message,
                UpdatedAtUtc = DateTime.UtcNow
            });
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
        await _ownerAdoption.WaitAsync(cancellationToken);
        try
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
                        var persisted = winner.Deleted
                            ? await _tradeOperations.DeleteOrderAsync(winner.OrderId)
                            : await _tradeOperations.ApplyCanonicalOrderAsync(winner.Order!);
                        if (!persisted)
                        {
                            throw new InvalidOperationException(
                                "The authenticated owner projection could not be persisted locally.");
                        }
                        if (!await IsCurrentAuthorityAsync(authority, connection, profileId))
                        {
                            throw new InvalidOperationException(
                                "The hosted order authority changed while owner persistence was in progress.");
                        }
                        await _localState.SaveObjectRevisionAsync(
                            connection,
                            ProfileSyncCollections.TradeOrders,
                            winner.OrderId.ToString("D"),
                            winner.ObjectRevision);
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
        finally
        {
            _ownerAdoption.Release();
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
            projection.ObjectRevision.Value < expected.ObjectRevision ||
            projection.ObjectRevision.Value <= 0 ||
            projection.CompanyRevision.Value <= 0)
        {
            throw new InvalidOperationException(
                "The authenticated owner endpoint returned the wrong commission, a stale order, or omitted authoritative revisions.");
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
