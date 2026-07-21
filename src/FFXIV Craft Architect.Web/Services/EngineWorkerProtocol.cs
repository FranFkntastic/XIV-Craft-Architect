using System.Runtime.ExceptionServices;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;

namespace FFXIV_Craft_Architect.Web.Services;

public enum EngineWorkerLifecycleState
{
    Stopped = 0,
    Starting = 1,
    Ready = 2,
    Running = 3,
    Cancelling = 4,
    Terminating = 5,
    Faulted = 6,
    Quarantined = 7
}

public sealed record EngineWorkerCapability(
    string ProtocolVersion,
    long Generation,
    bool DedicatedWorker,
    bool CrossOriginIsolated,
    bool SharedArrayBufferAvailable,
    bool ThreadsAvailable,
    bool ExecutionSupported = false,
    string ResultKind = "computation-result",
    bool ManagedRuntimeReady = false,
    string? ManagedRuntimeAssembly = null,
    string? ManagedRuntimeProofHash = null);

public sealed record EngineWorkerMessage(
    string ProtocolVersion,
    string Kind,
    long Generation,
    Guid? ExecutionId,
    Guid? TransactionId,
    JsonElement? Payload);

public sealed record EngineWorkerQuarantineEvidence(
    Guid QuarantineId,
    long Generation,
    bool IsResolved,
    bool StartupOutcomePending,
    bool TerminationPending,
    bool TransportDisposalPending,
    int CleanupAttempts,
    IReadOnlyList<string> Failures);

public sealed class EngineWorkerQuarantineException : InvalidOperationException
{
    public EngineWorkerQuarantineException(
        string message,
        EngineWorkerQuarantineEvidence evidence,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Evidence = evidence;
    }

    public EngineWorkerQuarantineEvidence Evidence { get; }
}

public interface IEngineWorkerTransport : IAsyncDisposable
{
    event EventHandler<EngineWorkerMessage>? MessageReceived;

    Task<EngineWorkerCapability> StartAsync(long generation, CancellationToken cancellationToken);

    Task SendAsync(EngineWorkerMessage message, CancellationToken cancellationToken);

    Task TerminateAsync(CancellationToken cancellationToken);
}

public sealed class EngineWorkerClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2";
    public const string ComputationResultMessageKind = "computation-result";
    public const string ManagedRuntimeAssembly = "FFXIV_Craft_Architect.Web";
    private static readonly JsonSerializerOptions WireJsonOptions = EngineJsonSerializerOptions.CreateWire();
    private static readonly IReferenceEngineSemanticSnapshotProvider SemanticSnapshots =
        new ReferenceEngineSemanticSnapshotProvider();
    private static readonly IReadOnlySet<string> ComputationWireProperties = new HashSet<string>(
        [
            "contractVersion",
            "generation",
            "executionId",
            "transactionId",
            "status",
            "finalPhase",
            "result",
            "basis",
            "requestInputHash",
            "budgets",
            "rootIntentHash",
            "expandedGraphHash",
            "analysisBasisHash",
            "routeBasisHash",
            "analysisResultHash",
            "procurementRouteResultHash",
            "computationHash",
            "computationEvidence",
            "failure"
        ],
        StringComparer.OrdinalIgnoreCase);
    private readonly IEngineWorkerTransport _transport;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<ProgressSubscription> _progressSubscriptions = [];
    private readonly TimeSpan _cancellationTimeout;
    private readonly TimeSpan _responseTimeoutDuration;
    private readonly TimeSpan _transportTimeout;
    private TaskCompletionSource<EngineComputationResult>? _completion;
    private EngineRequestEnvelope? _request;
    private WorkerExecutionIdentity? _execution;
    private EngineProgress? _lastProgress;
    private EngineWorkerCapability? _capability;
    private CancellationTokenSource? _cancelTimeout;
    private CancellationTokenSource? _responseTimeout;
    private CancellationTokenSource? _startupCancellation;
    private Task<EngineWorkerCapability>? _startupTask;
    private QuarantinedWorker? _quarantinedWorker;
    private EngineWorkerQuarantineEvidence? _lastQuarantineEvidence;
    private Task _transitionBarrier = Task.CompletedTask;
    private Task? _restartTask;
    private Task? _disposeTask;
    private EngineWorkerLifecycleState _state = EngineWorkerLifecycleState.Stopped;
    private bool _disposeRequested;
    private bool _workerMayBeAlive;
    private long _generation;

    public EngineWorkerClient(
        IEngineWorkerTransport transport,
        TimeSpan? cancellationTimeout = null,
        TimeSpan? responseTimeout = null,
        TimeSpan? transportTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cancellationTimeout = cancellationTimeout ?? TimeSpan.FromSeconds(10);
        _responseTimeoutDuration = responseTimeout ?? TimeSpan.FromMinutes(2);
        _transportTimeout = transportTimeout ?? TimeSpan.FromSeconds(10);
        if (_cancellationTimeout <= TimeSpan.Zero ||
            _responseTimeoutDuration <= TimeSpan.Zero ||
            _transportTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cancellationTimeout));
        }
        _transport.MessageReceived += OnMessageReceived;
    }

    public EngineWorkerLifecycleState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public EngineWorkerCapability? Capability
    {
        get
        {
            lock (_sync)
            {
                return _capability;
            }
        }
    }

    public EngineWorkerQuarantineEvidence? QuarantineEvidence
    {
        get
        {
            lock (_sync)
            {
                return _quarantinedWorker?.CreateEvidence() ?? _lastQuarantineEvidence;
            }
        }
    }

    public event EventHandler<EngineProgress>? ProgressChanged
    {
        add
        {
            if (value is null)
            {
                return;
            }
            lock (_sync)
            {
                _progressSubscriptions.Add(new ProgressSubscription(this, value));
            }
        }
        remove
        {
            if (value is null)
            {
                return;
            }
            lock (_sync)
            {
                var index = _progressSubscriptions.FindLastIndex(subscription => subscription.Handler == value);
                if (index >= 0)
                {
                    _progressSubscriptions.RemoveAt(index);
                }
            }
        }
    }

    public async Task<EngineWorkerCapability> StartAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? transition;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                ThrowIfWorkerQuarantinedLocked("A replacement worker cannot start while prior worker cleanup is unresolved.");
                transition = _state == EngineWorkerLifecycleState.Terminating ? _transitionBarrier : null;
            }
            if (transition is null)
            {
                break;
            }
            await AwaitBoundedAsync(transition, cancellationToken, "worker restart barrier");
        }

        await WaitLifecycleAsync(cancellationToken, "worker startup lifecycle lock");
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                if (_state == EngineWorkerLifecycleState.Ready)
                {
                    return _capability!;
                }
                EnsureStateLocked(EngineWorkerLifecycleState.Stopped, EngineWorkerLifecycleState.Faulted);
            }
            return await StartWorkerUnderLifecycleAsync(cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<EngineComputationResult> ExecuteAsync(
        EngineRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        TaskCompletionSource<EngineComputationResult> completion;
        WorkerExecutionIdentity execution;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            EnsureStateLocked(EngineWorkerLifecycleState.Ready);
            if (_capability?.ExecutionSupported != true)
            {
                throw new NotSupportedException("The browser worker does not host the .NET engine.");
            }
            _request = request;
            execution = new WorkerExecutionIdentity(_generation, Guid.NewGuid(), request.TransactionId);
            _execution = execution;
            _lastProgress = null;
            completion = new TaskCompletionSource<EngineComputationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion;
            _state = EngineWorkerLifecycleState.Running;
            _responseTimeout = new CancellationTokenSource();
            _ = EnforceResponseTimeoutAsync(execution, _responseTimeout.Token);
        }

        try
        {
            await InvokeTransportAsync(
                token => _transport.SendAsync(
                    new EngineWorkerMessage(
                        ProtocolVersion,
                        "execute",
                        execution.Generation,
                        execution.ExecutionId,
                        execution.TransactionId,
                        JsonSerializer.SerializeToElement(request, WireJsonOptions)),
                    token),
                cancellationToken,
                "worker execution dispatch");
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancellationDispatch? cancellation = null;
            lock (_sync)
            {
                if (completion.Task.IsCompletedSuccessfully)
                {
                    return completion.Task.Result;
                }
                if (IsCurrentExecutionLocked(execution) && _state == EngineWorkerLifecycleState.Running)
                {
                    cancellation = BeginCancellationLocked(
                        execution,
                        request.ContractVersion,
                        "Caller cancelled the engine computation.");
                }
            }

            if (cancellation is not null)
            {
                try
                {
                    await SendCancellationAsync(cancellation, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    await FailExecutionAsync(ex, execution);
                }
            }

            lock (_sync)
            {
                if (completion.Task.IsCompletedSuccessfully)
                {
                    return completion.Task.Result;
                }
            }
            throw;
        }
        catch (Exception ex)
        {
            await FailExecutionAsync(ex, execution);
            return await completion.Task;
        }
    }

    public async Task CancelAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        CancellationDispatch cancellation;
        TaskCompletionSource<EngineComputationResult> completion;
        lock (_sync)
        {
            EnsureStateLocked(EngineWorkerLifecycleState.Running);
            var execution = _execution!.Value;
            completion = _completion!;
            cancellation = BeginCancellationLocked(execution, _request!.ContractVersion, reason);
        }

        try
        {
            await SendCancellationAsync(cancellation, cancellationToken);
        }
        catch (Exception ex)
        {
            await FailExecutionAsync(ex, cancellation.Execution);
            if (completion.Task.IsCompletedSuccessfully)
            {
                return;
            }
            await completion.Task;
        }
    }

    public Task ForceTerminateAndRestartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (_restartTask is not null)
            {
                return _restartTask;
            }

            _state = EngineWorkerLifecycleState.Terminating;
            _generation++;
            _capability = null;
            _startupCancellation?.Cancel();
            var completion = _completion;
            ClearExecutionLocked();
            var restart = RestartCoreAsync(completion, cancellationToken);
            _restartTask = restart;
            _transitionBarrier = restart;
            return restart;
        }
    }

    public async Task<EngineWorkerQuarantineEvidence> RetryQuarantinedWorkerCleanupAsync(
        CancellationToken cancellationToken = default)
    {
        await WaitLifecycleAsync(cancellationToken, "quarantined worker cleanup lifecycle lock");
        try
        {
            QuarantinedWorker quarantine;
            lock (_sync)
            {
                quarantine = _quarantinedWorker
                    ?? throw new InvalidOperationException("There is no unresolved quarantined worker cleanup.");
            }

            var evidence = await TryCleanupQuarantinedWorkerAsync(quarantine);
            lock (_sync)
            {
                if (evidence.IsResolved)
                {
                    _state = _disposeRequested
                        ? EngineWorkerLifecycleState.Stopped
                        : EngineWorkerLifecycleState.Faulted;
                    if (_disposeRequested)
                    {
                        _disposeTask = Task.CompletedTask;
                    }
                }
            }
            return evidence;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposeRequested = true;
            _state = EngineWorkerLifecycleState.Terminating;
            _generation++;
            _capability = null;
            _startupCancellation?.Cancel();
            var completion = _completion;
            ClearExecutionLocked();
            _transport.MessageReceived -= OnMessageReceived;
            var disposal = DisposeCoreAsync(completion);
            _disposeTask = disposal;
            _transitionBarrier = disposal;
            return new ValueTask(disposal);
        }
    }

    private async Task<EngineWorkerCapability> StartWorkerUnderLifecycleAsync(CancellationToken cancellationToken)
    {
        var replacementCleanup = await EnsureWorkerCleanupAsync(requireTransportDisposal: false);
        if (replacementCleanup is { IsResolved: false })
        {
            throw new EngineWorkerQuarantineException(
                "The previous worker could not be terminated before startup.",
                replacementCleanup);
        }

        long generation;
        CancellationTokenSource startup;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _state = EngineWorkerLifecycleState.Starting;
            generation = ++_generation;
            startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _startupCancellation = startup;
        }
        Task<EngineWorkerCapability>? startupTask = null;

        try
        {
            lock (_sync)
            {
                _workerMayBeAlive = true;
            }
            startupTask = _transport.StartAsync(generation, startup.Token);
            lock (_sync)
            {
                _startupTask = startupTask;
            }
            var capability = await AwaitTransportAsync(
                startupTask,
                startup.Token,
                "worker startup");
            cancellationToken.ThrowIfCancellationRequested();
            if (!capability.DedicatedWorker ||
                capability.Generation != generation ||
                !string.Equals(capability.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) ||
                !string.Equals(capability.ResultKind, ComputationResultMessageKind, StringComparison.Ordinal) ||
                !capability.ManagedRuntimeReady ||
                !string.Equals(capability.ManagedRuntimeAssembly, ManagedRuntimeAssembly, StringComparison.Ordinal) ||
                !IsSha256(capability.ManagedRuntimeProofHash))
            {
                throw new InvalidOperationException("The engine worker did not prove the required managed computation-only dedicated-worker protocol.");
            }

            lock (_sync)
            {
                if (_disposeRequested || generation != _generation || _state != EngineWorkerLifecycleState.Starting)
                {
                    throw new OperationCanceledException("Obsolete worker startup completion was ignored.");
                }
                _startupTask = null;
                _capability = capability;
                _state = EngineWorkerLifecycleState.Ready;
            }
            return capability;
        }
        catch (Exception startupFailure)
        {
            if (startupTask is { IsCompleted: false })
            {
                QuarantinedWorker quarantine;
                lock (_sync)
                {
                    quarantine = GetOrCreateQuarantineLocked(startupTask, requireTransportDisposal: false);
                }
                var lateCleanup = await TryCleanupQuarantinedWorkerAsync(quarantine);
                if (lateCleanup.Failures.Any(failure => failure.StartsWith("termination:", StringComparison.Ordinal)))
                {
                    throw new EngineWorkerQuarantineException(
                        "Worker startup failed and pre-completion termination did not complete cleanly; late startup remains quarantined.",
                        lateCleanup,
                        startupFailure);
                }
                ExceptionDispatchInfo.Capture(startupFailure).Throw();
            }

            lock (_sync)
            {
                if (ReferenceEquals(_startupTask, startupTask))
                {
                    _startupTask = null;
                }
            }
            var cleanup = await EnsureWorkerCleanupAsync(requireTransportDisposal: false);
            lock (_sync)
            {
                _capability = null;
                if (!_disposeRequested && cleanup is not { IsResolved: false })
                {
                    _state = EngineWorkerLifecycleState.Faulted;
                }
            }
            if (cleanup is { IsResolved: false })
            {
                throw new EngineWorkerQuarantineException(
                    "Worker startup failed and the partially started worker did not terminate.",
                    cleanup,
                    startupFailure);
            }
            ExceptionDispatchInfo.Capture(startupFailure).Throw();
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_startupCancellation, startup))
                {
                    _startupCancellation = null;
                }
            }
            startup.Dispose();
        }
    }

    private async Task RestartCoreAsync(
        TaskCompletionSource<EngineComputationResult>? completion,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var lifecycleAcquired = false;
        try
        {
            try
            {
                await WaitLifecycleAsync(CancellationToken.None, "worker restart lifecycle lock");
                lifecycleAcquired = true;
            }
            catch (Exception ex)
            {
                EngineWorkerQuarantineEvidence evidence;
                lock (_sync)
                {
                    var quarantine = GetOrCreateQuarantineLocked(
                        _startupTask,
                        requireTransportDisposal: false);
                    quarantine.Failures.Add(DescribeQuarantineFailure("restart-lifecycle", ex));
                    evidence = quarantine.CreateEvidence();
                }
                throw new EngineWorkerQuarantineException(
                    "Worker restart could not acquire cleanup ownership and remains quarantined.",
                    evidence,
                    ex);
            }

            var cleanup = await EnsureWorkerCleanupAsync(requireTransportDisposal: false);
            if (cleanup is { IsResolved: false })
            {
                var failure = new EngineWorkerQuarantineException(
                    "The worker could not be terminated for restart.",
                    cleanup);
                completion?.TrySetException(failure);
                throw failure;
            }

            completion?.TrySetCanceled(new CancellationToken(canceled: true));
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_disposeRequested)
                {
                    throw new OperationCanceledException("Worker restart was superseded by disposal.");
                }
                _state = EngineWorkerLifecycleState.Stopped;
            }
            await StartWorkerUnderLifecycleAsync(cancellationToken);
        }
        finally
        {
            lock (_sync)
            {
                _restartTask = null;
            }
            if (lifecycleAcquired)
            {
                _lifecycle.Release();
            }
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource<EngineComputationResult>? completion)
    {
        await Task.Yield();
        var lifecycleAcquired = false;
        try
        {
            try
            {
                await WaitLifecycleAsync(CancellationToken.None, "worker disposal lifecycle lock");
                lifecycleAcquired = true;
            }
            catch (Exception ex)
            {
                EngineWorkerQuarantineEvidence evidence;
                lock (_sync)
                {
                    var quarantine = GetOrCreateQuarantineLocked(
                        _startupTask,
                        requireTransportDisposal: true);
                    quarantine.Failures.Add(DescribeQuarantineFailure("disposal-lifecycle", ex));
                    evidence = quarantine.CreateEvidence();
                }
                throw new EngineWorkerQuarantineException(
                    "Worker disposal could not acquire cleanup ownership and remains quarantined.",
                    evidence,
                    ex);
            }

            completion?.TrySetCanceled(new CancellationToken(canceled: true));
            var cleanup = await EnsureWorkerCleanupAsync(requireTransportDisposal: true);
            if (cleanup is { IsResolved: false })
            {
                throw new EngineWorkerQuarantineException(
                    "The worker did not dispose cleanly and remains quarantined for explicit cleanup retry.",
                    cleanup);
            }
        }
        finally
        {
            lock (_sync)
            {
                _state = _quarantinedWorker is null
                    ? EngineWorkerLifecycleState.Stopped
                    : EngineWorkerLifecycleState.Quarantined;
            }
            if (lifecycleAcquired)
            {
                _lifecycle.Release();
            }
        }
    }

    private async Task<EngineWorkerQuarantineEvidence?> EnsureWorkerCleanupAsync(bool requireTransportDisposal)
    {
        QuarantinedWorker? quarantine;
        lock (_sync)
        {
            quarantine = _quarantinedWorker;
            if (quarantine is null && !_workerMayBeAlive && !requireTransportDisposal)
            {
                return null;
            }
            quarantine ??= GetOrCreateQuarantineLocked(
                _startupTask,
                requireTransportDisposal);
            quarantine.TransportDisposalPending |= requireTransportDisposal;
        }
        return await TryCleanupQuarantinedWorkerAsync(quarantine);
    }

    private QuarantinedWorker GetOrCreateQuarantineLocked(
        Task<EngineWorkerCapability>? startupTask,
        bool requireTransportDisposal)
    {
        if (_quarantinedWorker is { } existing)
        {
            if (startupTask is not null &&
                existing.StartupTask is not null &&
                !ReferenceEquals(existing.StartupTask, startupTask))
            {
                throw new InvalidOperationException("A different worker is already quarantined.");
            }
            existing.StartupTask ??= startupTask;
            existing.TerminationPending |= _workerMayBeAlive || startupTask is not null;
            existing.TransportDisposalPending |= requireTransportDisposal;
            return existing;
        }

        var quarantine = new QuarantinedWorker(
            Guid.NewGuid(),
            _generation,
            startupTask,
            _workerMayBeAlive || startupTask is not null,
            requireTransportDisposal);
        _quarantinedWorker = quarantine;
        _state = EngineWorkerLifecycleState.Quarantined;
        if (startupTask is not null)
        {
            _ = ObserveQuarantinedStartupAsync(quarantine, startupTask);
        }
        return quarantine;
    }

    private async Task<EngineWorkerQuarantineEvidence> TryCleanupQuarantinedWorkerAsync(
        QuarantinedWorker quarantine)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_quarantinedWorker, quarantine))
            {
                return quarantine.CreateEvidence();
            }
            quarantine.CleanupAttempts++;
        }

        var startupPending = quarantine.StartupTask is { IsCompleted: false };
        if (!startupPending && quarantine.StartupTask is { } startupTask)
        {
            try
            {
                _ = await startupTask;
            }
            catch
            {
                // Fault or cancellation closes startup uncertainty but does not prove termination.
            }
            lock (_sync)
            {
                quarantine.StartupTask = null;
                if (ReferenceEquals(_startupTask, startupTask))
                {
                    _startupTask = null;
                }
            }
        }

        if (quarantine.TerminationPending)
        {
            await TryCompleteQuarantinedTerminationAsync(quarantine, startupPending);
        }
        if (!startupPending && quarantine.TransportDisposalPending)
        {
            await TryCompleteQuarantinedTransportDisposalAsync(quarantine);
        }

        lock (_sync)
        {
            var resolved = quarantine.StartupTask is null &&
                !quarantine.TerminationPending &&
                !quarantine.TransportDisposalPending;
            if (resolved && ReferenceEquals(_quarantinedWorker, quarantine))
            {
                quarantine.IsResolved = true;
                _quarantinedWorker = null;
                _lastQuarantineEvidence = quarantine.CreateEvidence();
            }
            else if (ReferenceEquals(_quarantinedWorker, quarantine))
            {
                _state = EngineWorkerLifecycleState.Quarantined;
            }
            return quarantine.CreateEvidence();
        }
    }

    private async Task TryCompleteQuarantinedTerminationAsync(
        QuarantinedWorker quarantine,
        bool startupPending)
    {
        Task terminationTask;
        lock (_sync)
        {
            if (quarantine.TerminationTask is null)
            {
                quarantine.TerminationCancellation = new CancellationTokenSource(_transportTimeout);
                try
                {
                    quarantine.TerminationTask = _transport.TerminateAsync(
                        quarantine.TerminationCancellation.Token);
                }
                catch (Exception ex)
                {
                    quarantine.TerminationCancellation.Dispose();
                    quarantine.TerminationCancellation = null;
                    quarantine.Failures.Add(DescribeQuarantineFailure("termination", ex));
                    return;
                }
            }
            terminationTask = quarantine.TerminationTask;
        }

        try
        {
            await AwaitBoundedAsync(
                terminationTask,
                CancellationToken.None,
                "quarantined worker termination");
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                quarantine.Failures.Add(DescribeQuarantineFailure("termination", ex));
                if (terminationTask.IsCompleted)
                {
                    ClearQuarantinedTerminationTaskLocked(quarantine, terminationTask);
                }
            }
            return;
        }

        lock (_sync)
        {
            ClearQuarantinedTerminationTaskLocked(quarantine, terminationTask);
            if (!startupPending)
            {
                quarantine.TerminationPending = false;
                _workerMayBeAlive = false;
            }
        }
    }

    private async Task TryCompleteQuarantinedTransportDisposalAsync(QuarantinedWorker quarantine)
    {
        Task disposalTask;
        lock (_sync)
        {
            if (quarantine.TransportDisposalTask is null)
            {
                try
                {
                    quarantine.TransportDisposalTask = _transport.DisposeAsync().AsTask();
                }
                catch (Exception ex)
                {
                    quarantine.Failures.Add(DescribeQuarantineFailure("transport-disposal", ex));
                    return;
                }
            }
            disposalTask = quarantine.TransportDisposalTask;
        }

        try
        {
            await AwaitBoundedAsync(
                disposalTask,
                CancellationToken.None,
                "quarantined worker transport disposal");
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                quarantine.Failures.Add(DescribeQuarantineFailure("transport-disposal", ex));
                if (disposalTask.IsCompleted)
                {
                    quarantine.TransportDisposalTask = null;
                }
            }
            return;
        }

        lock (_sync)
        {
            if (ReferenceEquals(quarantine.TransportDisposalTask, disposalTask))
            {
                quarantine.TransportDisposalTask = null;
            }
            quarantine.TransportDisposalPending = false;
        }
    }

    private async Task ObserveQuarantinedStartupAsync(
        QuarantinedWorker quarantine,
        Task<EngineWorkerCapability> startupTask)
    {
        try
        {
            try
            {
                _ = await startupTask;
            }
            catch
            {
            }

            await _lifecycle.WaitAsync();
            try
            {
                lock (_sync)
                {
                    if (!ReferenceEquals(_quarantinedWorker, quarantine))
                    {
                        return;
                    }
                }
                var evidence = await TryCleanupQuarantinedWorkerAsync(quarantine);
                lock (_sync)
                {
                    if (evidence.IsResolved)
                    {
                        _state = _disposeRequested
                            ? EngineWorkerLifecycleState.Stopped
                            : EngineWorkerLifecycleState.Faulted;
                        if (_disposeRequested)
                        {
                            _disposeTask = Task.CompletedTask;
                        }
                    }
                }
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_quarantinedWorker, quarantine))
                {
                    quarantine.Failures.Add(DescribeQuarantineFailure("background-cleanup", ex));
                    _state = EngineWorkerLifecycleState.Quarantined;
                }
            }
        }
    }

    private void ClearQuarantinedTerminationTaskLocked(
        QuarantinedWorker quarantine,
        Task terminationTask)
    {
        if (!ReferenceEquals(quarantine.TerminationTask, terminationTask))
        {
            return;
        }
        quarantine.TerminationTask = null;
        quarantine.TerminationCancellation?.Dispose();
        quarantine.TerminationCancellation = null;
    }

    private static string DescribeQuarantineFailure(string stage, Exception exception) =>
        $"{stage}:{exception.GetType().Name}:{exception.Message}";

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private CancellationDispatch BeginCancellationLocked(
        WorkerExecutionIdentity execution,
        string contractVersion,
        string reason)
    {
        _state = EngineWorkerLifecycleState.Cancelling;
        _cancelTimeout?.Cancel();
        _cancelTimeout?.Dispose();
        var timeout = new CancellationTokenSource();
        _cancelTimeout = timeout;
        return new CancellationDispatch(execution, contractVersion, reason, timeout);
    }

    private async Task SendCancellationAsync(
        CancellationDispatch cancellation,
        CancellationToken cancellationToken)
    {
        await InvokeTransportAsync(
            token => _transport.SendAsync(
                new EngineWorkerMessage(
                    ProtocolVersion,
                    "cancel",
                    cancellation.Execution.Generation,
                    cancellation.Execution.ExecutionId,
                    cancellation.Execution.TransactionId,
                    JsonSerializer.SerializeToElement(
                        new EngineCancelRequest(
                            cancellation.ContractVersion,
                            cancellation.Execution.Generation,
                            cancellation.Execution.ExecutionId,
                            cancellation.Execution.TransactionId,
                            cancellation.Reason),
                        WireJsonOptions)),
                token),
            cancellationToken,
            "worker cancellation dispatch");
        lock (_sync)
        {
            if (IsCurrentExecutionLocked(cancellation.Execution) &&
                _state == EngineWorkerLifecycleState.Cancelling &&
                ReferenceEquals(_cancelTimeout, cancellation.Timeout))
            {
                _ = EnforceCancellationTimeoutAsync(cancellation.Execution, cancellation.Timeout.Token);
            }
        }
    }

    private void OnMessageReceived(object? sender, EngineWorkerMessage message)
    {
        EngineRequestEnvelope request;
        WorkerExecutionIdentity execution;
        lock (_sync)
        {
            if (_request is null || _execution is not { } current ||
                message.Generation != current.Generation ||
                message.ExecutionId != current.ExecutionId ||
                message.TransactionId != current.TransactionId ||
                _state is not (EngineWorkerLifecycleState.Running or EngineWorkerLifecycleState.Cancelling))
            {
                return;
            }
            request = _request;
            execution = current;
        }

        if (!string.Equals(message.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal))
        {
            _ = FailExecutionAsync(new InvalidOperationException("Worker protocol version mismatch."), execution);
            return;
        }

        try
        {
            if (message.Kind == "progress")
            {
                if (message.Payload is not { } progressPayload)
                {
                    throw new InvalidOperationException("Worker progress payload is missing.");
                }
                var progress = progressPayload.Deserialize<EngineProgress>(WireJsonOptions)
                    ?? throw new InvalidOperationException("Worker progress payload is empty.");
                lock (_sync)
                {
                    if (!IsCurrentExecutionLocked(execution) ||
                        _state is not (EngineWorkerLifecycleState.Running or EngineWorkerLifecycleState.Cancelling))
                    {
                        return;
                    }
                    ValidateProgress(execution, progress, _lastProgress);
                    _lastProgress = progress;
                }
                NotifyProgress(progress);
                return;
            }

            if (message.Kind == "protocol-error")
            {
                if (message.Payload is not { } errorPayload)
                {
                    throw new InvalidOperationException("Worker protocol-error payload is missing.");
                }
                var error = errorPayload.Deserialize<WorkerProtocolError>(WireJsonOptions)
                    ?? throw new InvalidOperationException("Worker protocol-error payload is malformed.");
                throw new InvalidOperationException(error.Message);
            }

            if (message.Kind != ComputationResultMessageKind)
            {
                throw new InvalidOperationException($"Unknown correlated worker message kind '{message.Kind}'.");
            }
            if (message.Payload is not { } resultPayload)
            {
                throw new InvalidOperationException("Worker computation-result payload is missing.");
            }

            RejectSettlementAuthority(resultPayload);
            var computation = resultPayload.Deserialize<EngineComputationResult>(WireJsonOptions)
                ?? throw new InvalidOperationException("Worker computation-result payload is empty.");
            computation = EngineComputationResultValidation.Validate(
                execution.Generation,
                execution.ExecutionId,
                request,
                computation,
                SemanticSnapshots);

            lock (_sync)
            {
                if (!IsCurrentExecutionLocked(execution) ||
                    _state is not (EngineWorkerLifecycleState.Running or EngineWorkerLifecycleState.Cancelling))
                {
                    return;
                }
                var completion = _completion!;
                _state = EngineWorkerLifecycleState.Ready;
                completion.TrySetResult(computation);
                ClearExecutionLocked();
            }
        }
        catch (Exception ex)
        {
            _ = FailExecutionAsync(ex, execution);
        }
    }

    private static void RejectSettlementAuthority(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Worker computation-result payload must be an object.");
        }

        var seenProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name.Equals("completion", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("terminalEvidence", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("terminalPhase", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("finalTransactionHash", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Worker messages cannot carry final transaction or settlement envelope authority.");
            }
            if (!seenProperties.Add(property.Name) ||
                !ComputationWireProperties.Contains(property.Name))
            {
                throw new InvalidOperationException(
                    $"Worker property '{property.Name}' is not part of the computation-only wire contract.");
            }
        }

        JsonElement? computationEvidence = null;
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name.Equals("computationEvidence", StringComparison.OrdinalIgnoreCase))
            {
                computationEvidence = property.Value;
                break;
            }
        }
        if (computationEvidence is not { ValueKind: JsonValueKind.Object } evidence)
        {
            return;
        }
        foreach (var property in evidence.EnumerateObject())
        {
            if (property.Name.StartsWith("phase:", StringComparison.Ordinal) &&
                (!Enum.TryParse(property.Name["phase:".Length..], ignoreCase: false, out EnginePhase phase) ||
                 !EnginePhaseValidation.IsComputationPhase(phase)) ||
                property.Name is "settlement" or "visibleEffects" or "commitPoint" or "commitState" ||
                property.Name.StartsWith("delivery:", StringComparison.Ordinal) ||
                property.Name.StartsWith("cleanup:", StringComparison.Ordinal) ||
                property.Name.StartsWith("ledgerWrite:", StringComparison.Ordinal) ||
                property.Name.StartsWith("gateRelease:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Worker computation evidence cannot attest settlement phases or final transaction evidence.");
            }
        }
    }

    private static void ValidateProgress(
        WorkerExecutionIdentity execution,
        EngineProgress progress,
        EngineProgress? previous)
    {
        if (progress.TransactionId != execution.TransactionId ||
            progress.Generation != execution.Generation ||
            progress.ExecutionId != execution.ExecutionId)
        {
            throw new InvalidOperationException("Worker progress identity does not match its active execution.");
        }
        if (!EnginePhaseValidation.IsComputationPhase(progress.Phase))
        {
            throw new InvalidOperationException("Worker progress phase must be a computation-only nonterminal phase.");
        }
        if (progress.CompletedWorkUnits < 0 || progress.TotalWorkUnits <= 0 ||
            progress.CompletedWorkUnits > progress.TotalWorkUnits || string.IsNullOrWhiteSpace(progress.Message))
        {
            throw new InvalidOperationException("Worker progress payload is malformed.");
        }
        if (previous is not null &&
            (progress.CompletedWorkUnits < previous.CompletedWorkUnits ||
             progress.TotalWorkUnits != previous.TotalWorkUnits))
        {
            throw new InvalidOperationException("Worker progress work units must be monotonic within an execution.");
        }
    }

    private void NotifyProgress(EngineProgress progress)
    {
        ProgressSubscription[] subscriptions;
        lock (_sync)
        {
            subscriptions = _progressSubscriptions.ToArray();
        }
        foreach (var subscription in subscriptions)
        {
            subscription.Report(progress);
        }
    }

    private async Task EnforceResponseTimeoutAsync(
        WorkerExecutionIdentity execution,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_responseTimeoutDuration, cancellationToken);
            await FailExecutionAsync(
                new TimeoutException("The engine worker did not produce a computation result before the timeout."),
                execution);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task EnforceCancellationTimeoutAsync(
        WorkerExecutionIdentity execution,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_cancellationTimeout, cancellationToken);
            lock (_sync)
            {
                if (!IsCurrentExecutionLocked(execution) || _state != EngineWorkerLifecycleState.Cancelling)
                {
                    return;
                }
            }
            await FailExecutionAsync(
                new TimeoutException("The engine worker failed to acknowledge computation cancellation before the timeout."),
                execution);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task FailExecutionAsync(Exception exception, WorkerExecutionIdentity expectedExecution)
    {
        lock (_sync)
        {
            if (!IsCurrentExecutionLocked(expectedExecution))
            {
                return Task.CompletedTask;
            }

            _state = EngineWorkerLifecycleState.Terminating;
            _generation++;
            _capability = null;
            var completion = _completion;
            ClearExecutionLocked();
            var termination = TerminateFaultedExecutionAsync(exception, completion);
            _transitionBarrier = termination;
            return termination;
        }
    }

    private async Task TerminateFaultedExecutionAsync(
        Exception exception,
        TaskCompletionSource<EngineComputationResult>? completion)
    {
        await Task.Yield();
        Exception terminalException = exception;
        var lifecycleAcquired = false;
        try
        {
            await WaitLifecycleAsync(CancellationToken.None, "fault termination lifecycle lock");
            lifecycleAcquired = true;
            var cleanup = await EnsureWorkerCleanupAsync(requireTransportDisposal: false);
            if (cleanup is { IsResolved: false })
            {
                terminalException = new AggregateException(
                    "The engine worker faulted and did not terminate cleanly.",
                    exception,
                    new EngineWorkerQuarantineException(
                        "The faulted worker remains quarantined.",
                        cleanup));
            }
            lock (_sync)
            {
                if (!_disposeRequested && cleanup is not { IsResolved: false })
                {
                    _state = EngineWorkerLifecycleState.Faulted;
                }
            }
        }
        catch (Exception terminationFailure)
        {
            terminalException = new AggregateException(
                "The engine worker faulted and cleanup did not terminate cleanly.",
                exception,
                terminationFailure);
            lock (_sync)
            {
                if (!_disposeRequested)
                {
                    _state = EngineWorkerLifecycleState.Faulted;
                }
            }
        }
        finally
        {
            if (lifecycleAcquired)
            {
                _lifecycle.Release();
            }
            completion?.TrySetException(terminalException);
        }
    }

    private async Task<T> InvokeTransportAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        string operationName)
    {
        using var deadline = new CancellationTokenSource(_transportTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await operation(linked.Token).WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The {operationName} did not terminate before its deadline.");
        }
    }

    private async Task<T> AwaitTransportAsync<T>(
        Task<T> task,
        CancellationToken cancellationToken,
        string operationName)
    {
        using var deadline = new CancellationTokenSource(_transportTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The {operationName} did not terminate before its deadline.");
        }
    }

    private async Task InvokeTransportAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        string operationName)
    {
        using var deadline = new CancellationTokenSource(_transportTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await operation(linked.Token).WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The {operationName} did not terminate before its deadline.");
        }
    }

    private async Task AwaitBoundedAsync(
        Task task,
        CancellationToken cancellationToken,
        string operationName)
    {
        using var deadline = new CancellationTokenSource(_transportTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The {operationName} did not terminate before its deadline.");
        }
    }

    private async Task WaitLifecycleAsync(CancellationToken cancellationToken, string operationName)
    {
        using var deadline = new CancellationTokenSource(_transportTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await _lifecycle.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The {operationName} did not terminate before its deadline.");
        }
    }

    private void ClearExecutionLocked()
    {
        _cancelTimeout?.Cancel();
        _cancelTimeout?.Dispose();
        _cancelTimeout = null;
        _responseTimeout?.Cancel();
        _responseTimeout?.Dispose();
        _responseTimeout = null;
        _completion = null;
        _request = null;
        _execution = null;
        _lastProgress = null;
    }

    private bool IsCurrentExecutionLocked(WorkerExecutionIdentity execution) =>
        _execution == execution && _request?.TransactionId == execution.TransactionId;

    private void EnsureStateLocked(params EngineWorkerLifecycleState[] allowed)
    {
        if (!allowed.Contains(_state))
        {
            throw new InvalidOperationException($"Engine worker cannot perform this operation while {_state}.");
        }
    }

    private void ThrowIfWorkerQuarantinedLocked(string message)
    {
        if (_quarantinedWorker is not { } quarantine)
        {
            return;
        }
        throw new EngineWorkerQuarantineException(message, quarantine.CreateEvidence());
    }

    private sealed class ProgressSubscription(EngineWorkerClient owner, EventHandler<EngineProgress> handler)
    {
        private readonly object _sync = new();
        private EngineProgress? _pending;
        private bool _dispatching;

        public EventHandler<EngineProgress> Handler { get; } = handler;

        public void Report(EngineProgress progress)
        {
            lock (_sync)
            {
                _pending = progress;
                if (_dispatching)
                {
                    return;
                }
                _dispatching = true;
            }
            ThreadPool.UnsafeQueueUserWorkItem(static state => state.Dispatch(), this, preferLocal: false);
        }

        private void Dispatch()
        {
            while (true)
            {
                EngineProgress progress;
                lock (_sync)
                {
                    if (_pending is not { } pending)
                    {
                        _dispatching = false;
                        return;
                    }
                    progress = pending;
                    _pending = null;
                }
                try
                {
                    Handler(owner, progress);
                }
                catch
                {
                    // UI observers cannot affect protocol execution or one another.
                }
            }
        }
    }

    private sealed record WorkerProtocolError(string Code, string Message);

    private sealed record CancellationDispatch(
        WorkerExecutionIdentity Execution,
        string ContractVersion,
        string Reason,
        CancellationTokenSource Timeout);

    private sealed class QuarantinedWorker(
        Guid quarantineId,
        long generation,
        Task<EngineWorkerCapability>? startupTask,
        bool terminationPending,
        bool transportDisposalPending)
    {
        public Guid QuarantineId { get; } = quarantineId;
        public long Generation { get; } = generation;
        public Task<EngineWorkerCapability>? StartupTask { get; set; } = startupTask;
        public bool TerminationPending { get; set; } = terminationPending;
        public bool TransportDisposalPending { get; set; } = transportDisposalPending;
        public Task? TerminationTask { get; set; }
        public CancellationTokenSource? TerminationCancellation { get; set; }
        public Task? TransportDisposalTask { get; set; }
        public int CleanupAttempts { get; set; }
        public bool IsResolved { get; set; }
        public List<string> Failures { get; } = [];

        public EngineWorkerQuarantineEvidence CreateEvidence() => new(
            QuarantineId,
            Generation,
            IsResolved,
            StartupTask is { IsCompleted: false },
            TerminationPending,
            TransportDisposalPending,
            CleanupAttempts,
            Failures.ToArray());
    }

    private readonly record struct WorkerExecutionIdentity(long Generation, Guid ExecutionId, Guid TransactionId);
}
