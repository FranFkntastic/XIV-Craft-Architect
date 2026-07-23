using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;

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
    private bool _suppressCompatibilityCommit;
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
        if (!IsEnabled || _disposed || _suppressCompatibilityCommit)
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

    public async Task<WorkerRecipePlannerProjection?> GetRecipeProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetRecipeProjectionAsync(
            _projections.Shell.Revision,
            cancellationToken);
        if (!_projections.TryPublishRecipe(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            return _projections.Recipe;
        }
        return _projections.Recipe;
    }

    public async Task<WorkerAcquisitionProjection?> GetAcquisitionProjectionAsync(
        string filter,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var result = await _engineHost.GetAcquisitionProjectionAsync(
            _projections.Shell.Revision,
            filter,
            cancellationToken);
        if (!_projections.TryPublishAcquisition(result))
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            return _projections.Acquisition;
        }
        return _projections.Acquisition;
    }

    public async Task<WorkerRecipePlannerProjection> MutateProjectItemsAsync(
        WorkerProjectItemsMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.MutateProjectItemsAsync(
            _projections.Shell.Revision,
            mutation,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerRecipePlannerProjection>(
                result,
                out var projection) ||
            projection is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        await HydrateCompatibilityMirrorAsync(cancellationToken);
        return projection;
    }

    public async Task<WorkerRecipeBuildOutcome> BuildRecipeAsync(
        WorkerRecipeBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.BuildRecipeAsync(
            _projections.Shell.Revision,
            request,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerRecipeBuildOutcome>(
                result,
                out var outcome) ||
            outcome is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        await HydrateCompatibilityMirrorAsync(cancellationToken);
        return outcome;
    }

    public async Task<WorkerRecipePlannerProjection> MutateAcquisitionAsync(
        WorkerAcquisitionMutation mutation,
        CancellationToken cancellationToken = default)
    {
        var result = await _engineHost.MutateAcquisitionAsync(
            _projections.Shell.Revision,
            mutation,
            cancellationToken);
        if (!_projections.TryPublishMutation<WorkerRecipePlannerProjection>(
                result,
                out var projection) ||
            projection is null)
        {
            await RefreshAfterConflictAsync(result, cancellationToken);
            throw CreateConflict(result);
        }

        await HydrateCompatibilityMirrorAsync(cancellationToken);
        return projection;
    }

    public async Task<WorkerAcquisitionProjection> MutateAcquisitionAndProjectAsync(
        WorkerAcquisitionMutation mutation,
        string filter,
        CancellationToken cancellationToken = default)
    {
        await MutateAcquisitionAsync(mutation, cancellationToken);
        return await GetAcquisitionProjectionAsync(filter, cancellationToken)
            ?? throw new InvalidOperationException("The Worker did not publish acquisition evaluation.");
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
                if (!_projections.TryPublish(result))
                {
                    return false;
                }
                var recipe = await _engineHost.GetRecipeProjectionAsync(
                    result.Revision,
                    cancellationToken);
                _projections.TryPublishRecipe(recipe);
                return true;
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

    private async Task HydrateCompatibilityMirrorAsync(CancellationToken cancellationToken)
    {
        var shell = _projections.Shell;
        var result = await _engineHost.ExportSessionAsync(
            shell.Revision,
            new WorkerSessionExportRequest(
                "autosave",
                "Autosave",
                IncludeSourcePlanIdentity: true,
                IncludeLegacyMarketAnalysisFields: true),
            cancellationToken);
        var export = result.Projection.Deserialize<WorkerSessionExportProjection>(
            EngineJsonSerializerOptions.CreateWire());
        if (!result.Accepted || export is null)
        {
            throw new InvalidOperationException(
                result.Message ?? "The Worker did not export its accepted session.");
        }

        _suppressCompatibilityCommit = true;
        try
        {
            if (export.StoredPlan is null)
            {
                _appState.ClearPlan();
            }
            else
            {
                var prepared = PlanSessionLoadService.Prepare(export.StoredPlan);
                _appState.ApplyLoadedPlanSession(
                    prepared,
                    trackStoredPlanIdentity: false,
                    markRestoredStatePersisted: true);
            }
        }
        finally
        {
            _suppressCompatibilityCommit = false;
        }
    }

    private async Task RefreshAfterConflictAsync(
        WorkerSessionResultEnvelope result,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(result.RejectionCode, "stale-revision", StringComparison.Ordinal))
        {
            return;
        }

        var shell = await _engineHost.GetShellProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublish(shell);
        var recipe = await _engineHost.GetRecipeProjectionAsync(
            result.Revision,
            cancellationToken);
        _projections.TryPublishRecipe(recipe);
    }

    private static InvalidOperationException CreateConflict(
        WorkerSessionResultEnvelope result) =>
        new(
            result.Message ??
            "The plan changed before this edit was accepted. The current Worker projection has been restored.");

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
