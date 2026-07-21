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
    Faulted = 6
}

public sealed record EngineWorkerCapability(
    string ProtocolVersion,
    long Generation,
    bool DedicatedWorker,
    bool CrossOriginIsolated,
    bool SharedArrayBufferAvailable,
    bool ThreadsAvailable,
    bool ExecutionSupported = false,
    string ResultKind = "computation-result");

public sealed record EngineWorkerMessage(
    string ProtocolVersion,
    string Kind,
    long Generation,
    Guid? ExecutionId,
    Guid? TransactionId,
    JsonElement? Payload);

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
    private TaskCompletionSource<EngineComputationResult>? _completion;
    private EngineRequestEnvelope? _request;
    private WorkerExecutionIdentity? _execution;
    private EngineProgress? _lastProgress;
    private EngineWorkerCapability? _capability;
    private CancellationTokenSource? _cancelTimeout;
    private CancellationTokenSource? _responseTimeout;
    private CancellationTokenSource? _startupCancellation;
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
        TimeSpan? responseTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cancellationTimeout = cancellationTimeout ?? TimeSpan.FromSeconds(10);
        _responseTimeoutDuration = responseTimeout ?? TimeSpan.FromMinutes(2);
        if (_cancellationTimeout <= TimeSpan.Zero || _responseTimeoutDuration <= TimeSpan.Zero)
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
                transition = _state == EngineWorkerLifecycleState.Terminating
                    ? _transitionBarrier
                    : null;
            }
            if (transition is null)
            {
                break;
            }
            await transition.WaitAsync(cancellationToken);
        }

        await _lifecycle.WaitAsync(cancellationToken);
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
            await _transport.SendAsync(
                new EngineWorkerMessage(
                    ProtocolVersion,
                    "execute",
                    execution.Generation,
                    execution.ExecutionId,
                    execution.TransactionId,
                    JsonSerializer.SerializeToElement(request, WireJsonOptions)),
                cancellationToken);
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
        var replacementFailure = await TryTerminateWorkerAsync();
        if (replacementFailure is not null)
        {
            lock (_sync)
            {
                if (!_disposeRequested)
                {
                    _state = EngineWorkerLifecycleState.Faulted;
                }
            }
            throw new InvalidOperationException("The previous worker could not be terminated before startup.", replacementFailure);
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
        _workerMayBeAlive = true;

        try
        {
            var capability = await _transport.StartAsync(generation, startup.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (!capability.DedicatedWorker ||
                capability.Generation != generation ||
                !string.Equals(capability.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal) ||
                !string.Equals(capability.ResultKind, ComputationResultMessageKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The engine worker did not prove the required computation-only dedicated-worker protocol.");
            }

            lock (_sync)
            {
                if (_disposeRequested || generation != _generation || _state != EngineWorkerLifecycleState.Starting)
                {
                    throw new OperationCanceledException("Obsolete worker startup completion was ignored.");
                }
                _capability = capability;
                _state = EngineWorkerLifecycleState.Ready;
            }
            return capability;
        }
        catch (Exception startupFailure)
        {
            var terminationFailure = await TryTerminateWorkerAsync();
            lock (_sync)
            {
                _capability = null;
                if (!_disposeRequested)
                {
                    _state = EngineWorkerLifecycleState.Faulted;
                }
            }
            if (terminationFailure is not null)
            {
                throw new AggregateException(
                    "Worker startup failed and the partially started worker did not terminate.",
                    startupFailure,
                    terminationFailure);
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
        await _lifecycle.WaitAsync(CancellationToken.None);
        try
        {
            var terminationFailure = await TryTerminateWorkerAsync();
            if (terminationFailure is not null)
            {
                completion?.TrySetException(terminationFailure);
                lock (_sync)
                {
                    if (!_disposeRequested)
                    {
                        _state = EngineWorkerLifecycleState.Faulted;
                    }
                }
                throw new InvalidOperationException("The worker could not be terminated for restart.", terminationFailure);
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
            _lifecycle.Release();
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource<EngineComputationResult>? completion)
    {
        await Task.Yield();
        await _lifecycle.WaitAsync(CancellationToken.None);
        Exception? terminationFailure = null;
        Exception? disposalFailure = null;
        try
        {
            terminationFailure = await TryTerminateWorkerAsync();
            completion?.TrySetCanceled(new CancellationToken(canceled: true));
            try
            {
                await _transport.DisposeAsync();
                _workerMayBeAlive = false;
                terminationFailure = null;
            }
            catch (Exception ex)
            {
                disposalFailure = ex;
            }
        }
        finally
        {
            lock (_sync)
            {
                _state = EngineWorkerLifecycleState.Stopped;
            }
            _lifecycle.Release();
        }

        if (terminationFailure is not null || disposalFailure is not null)
        {
            throw new AggregateException(
                "The worker did not dispose cleanly.",
                new[] { terminationFailure, disposalFailure }.OfType<Exception>());
        }
    }

    private async Task<Exception?> TryTerminateWorkerAsync()
    {
        if (!_workerMayBeAlive)
        {
            return null;
        }
        try
        {
            await _transport.TerminateAsync(CancellationToken.None);
            _workerMayBeAlive = false;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

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
        await _transport.SendAsync(
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
            cancellationToken);
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
            EngineComputationResultValidation.Validate(
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
        await _lifecycle.WaitAsync(CancellationToken.None);
        Exception terminalException = exception;
        try
        {
            var terminationFailure = await TryTerminateWorkerAsync();
            if (terminationFailure is not null)
            {
                terminalException = new AggregateException(
                    "The engine worker faulted and did not terminate cleanly.",
                    exception,
                    terminationFailure);
            }
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
            _lifecycle.Release();
        }
        completion?.TrySetException(terminalException);
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

    private readonly record struct WorkerExecutionIdentity(long Generation, Guid ExecutionId, Guid TransactionId);
}
