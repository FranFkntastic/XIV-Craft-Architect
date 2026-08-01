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
    private readonly IJSRuntime _jsRuntime;
    private readonly ProfileSyncService _profileSync;
    private readonly ProfileSyncLocalStateService _localState;
    private readonly HostedOrderProjectionStore _hostedOrders;
    private readonly AppState _appState;
    private readonly ILogger<HostedOrderSyncCoordinator> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
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
        AppState appState,
        ILogger<HostedOrderSyncCoordinator> logger)
    {
        _jsRuntime = jsRuntime;
        _profileSync = profileSync;
        _localState = localState;
        _hostedOrders = hostedOrders;
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
            ? SynchronizeAsync(profileId, replayAfterRevision, _lifetime.Token)
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

            _hostedOrders.ResetForProfile(profileId);
            IJSObjectReference? controller = null;
            try
            {
                await _profileSync.InitializeAsync(cancellationToken);
                var cursor = await _localState.LoadLastSyncRevisionAsync(profileId);
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
        long replayAfterRevision,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            if (!IsActiveProfile(profileId))
            {
                return 0;
            }

            await _profileSync.SyncFromRevisionAsync(
                replayAfterRevision,
                cancellationToken);
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

    private bool IsActiveProfile(string profileId) =>
        !_disposed &&
        string.Equals(profileId, _activeProfileId, StringComparison.OrdinalIgnoreCase);

    private async Task StopSessionAsync()
    {
        _session?.Cancel();
        _activeProfileId = null;
        _activeHostUrl = null;
        _activeAccessKey = null;
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
