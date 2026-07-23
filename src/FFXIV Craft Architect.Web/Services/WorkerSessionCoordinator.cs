namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Compatibility-window bridge that commits main-thread candidates into the
/// revision-fenced Worker session. The Worker acknowledgement is authoritative;
/// this service will be deleted once every mutation is issued as a Worker command.
/// </summary>
public sealed class WorkerSessionCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan CoalescingDelay = TimeSpan.FromMilliseconds(150);
    private readonly CraftArchitectEngineHost _engineHost;
    private readonly WorkerProjectionStore _projections;
    private readonly AppState _appState;
    private readonly StoredPlanSnapshotBuilder _snapshotBuilder;
    private readonly ExperimentalProcurementEngineCapability _capability;
    private readonly SemaphoreSlim _commit = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _scheduledCommit;
    private bool _disposed;

    public WorkerSessionCoordinator(
        CraftArchitectEngineHost engineHost,
        WorkerProjectionStore projections,
        AppState appState,
        StoredPlanSnapshotBuilder snapshotBuilder,
        ExperimentalProcurementEngineCapability capability)
    {
        _engineHost = engineHost;
        _projections = projections;
        _appState = appState;
        _snapshotBuilder = snapshotBuilder;
        _capability = capability;
    }

    public bool IsEnabled => _capability.IsExecutionEnabled;

    public async Task<WorkerSessionShellProjection?> BootstrapAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.BootstrapSessionAsync(cancellationToken);
        if (!result.Accepted || !_projections.TryPublish(result))
        {
            throw new InvalidOperationException(
                result.Message ?? "The Worker did not publish a valid startup projection.");
        }
        return _projections.Shell;
    }

    public void ScheduleCompatibilityCommit()
    {
        if (!IsEnabled || _disposed)
        {
            return;
        }

        CancellationTokenSource cancellation;
        lock (_sync)
        {
            _scheduledCommit?.Cancel();
            _scheduledCommit?.Dispose();
            _scheduledCommit = new CancellationTokenSource();
            cancellation = _scheduledCommit;
        }
        _ = CommitAfterDelayAsync(cancellation);
    }

    public async Task<bool> CommitCurrentCandidateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || !_appState.HasPlanOrProjectItems)
        {
            return false;
        }

        await _commit.WaitAsync(cancellationToken);
        try
        {
            var snapshot = _snapshotBuilder.Build(
                "autosave",
                "Autosave",
                DateTime.UtcNow,
                includeSourcePlanIdentity: true,
                includeLegacyMarketAnalysisFields: false);
            var expectedRevision = _projections.Shell.Revision;
            var result = await _engineHost.ReplaceSessionAsync(
                expectedRevision,
                snapshot,
                trackStoredPlanIdentity: false,
                cancellationToken);
            if (result.Accepted)
            {
                return _projections.TryPublish(result);
            }

            if (string.Equals(result.RejectionCode, "stale-revision", StringComparison.Ordinal))
            {
                var current = await _engineHost.GetShellProjectionAsync(
                    result.Revision,
                    cancellationToken);
                _projections.TryPublish(current);
            }
            return false;
        }
        finally
        {
            _commit.Release();
        }
    }

    private async Task CommitAfterDelayAsync(CancellationTokenSource scheduled)
    {
        try
        {
            await Task.Delay(CoalescingDelay, scheduled.Token);
            await CommitCurrentCandidateAsync(scheduled.Token);
        }
        catch (OperationCanceledException) when (scheduled.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_scheduledCommit, scheduled))
                {
                    _scheduledCommit = null;
                }
            }
            scheduled.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }
            _disposed = true;
            _scheduledCommit?.Cancel();
            _scheduledCommit?.Dispose();
            _scheduledCommit = null;
        }
        _commit.Dispose();
        return ValueTask.CompletedTask;
    }
}
