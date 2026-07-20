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
    bool DedicatedWorker,
    bool CrossOriginIsolated,
    bool SharedArrayBufferAvailable,
    bool ThreadsAvailable);

public sealed record EngineWorkerMessage(
    string ProtocolVersion,
    string Kind,
    Guid? TransactionId,
    JsonElement? Payload);

public interface IEngineWorkerTransport : IAsyncDisposable
{
    event EventHandler<EngineWorkerMessage>? MessageReceived;

    Task<EngineWorkerCapability> StartAsync(CancellationToken cancellationToken);

    Task SendAsync(EngineWorkerMessage message, CancellationToken cancellationToken);

    Task TerminateAsync(CancellationToken cancellationToken);
}

public sealed class EngineWorkerClient : IAsyncDisposable
{
    public const string ProtocolVersion = "1";
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IEngineWorkerTransport _transport;
    private readonly object _sync = new();
    private TaskCompletionSource<EngineResultEnvelope>? _completion;
    private EngineRequestEnvelope? _request;
    private Task<EngineWorkerCapability>? _startTask;
    private EngineWorkerLifecycleState _state = EngineWorkerLifecycleState.Stopped;
    private bool _disposed;
    private CancellationTokenSource? _cancelTimeout;
    private readonly TimeSpan _cancellationTimeout;

    public EngineWorkerClient(IEngineWorkerTransport transport, TimeSpan? cancellationTimeout = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cancellationTimeout = cancellationTimeout ?? TimeSpan.FromSeconds(10);
        if (_cancellationTimeout <= TimeSpan.Zero)
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
        private set
        {
            lock (_sync)
            {
                _state = value;
            }
        }
    }

    public EngineWorkerCapability? Capability { get; private set; }

    public event EventHandler<EngineProgress>? ProgressChanged;

    public Task<EngineWorkerCapability> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startTask is not null)
            {
                return _startTask;
            }

            EnsureStateLocked(EngineWorkerLifecycleState.Stopped, EngineWorkerLifecycleState.Faulted);
            _state = EngineWorkerLifecycleState.Starting;
            _startTask = StartCoreAsync(cancellationToken);
            return _startTask;
        }
    }

    private async Task<EngineWorkerCapability> StartCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            var capability = await _transport.StartAsync(cancellationToken);
            if (!capability.DedicatedWorker || !string.Equals(capability.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The engine worker did not prove the required dedicated-worker protocol.");
            }

            lock (_sync)
            {
                Capability = capability;
                _state = EngineWorkerLifecycleState.Ready;
                _startTask = null;
            }
            return capability;
        }
        catch
        {
            lock (_sync)
            {
                _state = EngineWorkerLifecycleState.Faulted;
                _startTask = null;
            }
            throw;
        }
    }

    public async Task<EngineResultEnvelope> ExecuteAsync(
        EngineRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        TaskCompletionSource<EngineResultEnvelope> completion;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureStateLocked(EngineWorkerLifecycleState.Ready);
            _request = request;
            completion = new TaskCompletionSource<EngineResultEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion;
            _state = EngineWorkerLifecycleState.Running;
        }
        try
        {
            await _transport.SendAsync(
                new EngineWorkerMessage(ProtocolVersion, "execute", request.TransactionId, JsonSerializer.SerializeToElement(request, WireJsonOptions)),
                cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (State == EngineWorkerLifecycleState.Running)
            {
                await CancelAsync("Caller cancelled the engine transaction.", CancellationToken.None);
            }

            throw;
        }
        catch
        {
            State = EngineWorkerLifecycleState.Faulted;
            ClearExecution();
            throw;
        }
    }

    public async Task CancelAsync(string reason, CancellationToken cancellationToken = default)
    {
        Guid transactionId;
        lock (_sync)
        {
            EnsureStateLocked(EngineWorkerLifecycleState.Running);
            _state = EngineWorkerLifecycleState.Cancelling;
            transactionId = _request!.TransactionId;
            _cancelTimeout?.Cancel();
            _cancelTimeout?.Dispose();
            _cancelTimeout = new CancellationTokenSource();
        }
        try
        {
            await _transport.SendAsync(
                new EngineWorkerMessage(
                    ProtocolVersion,
                    "cancel",
                    transactionId,
                    JsonSerializer.SerializeToElement(new EngineCancelRequest(ProtocolVersion, transactionId, reason), WireJsonOptions)),
                cancellationToken);
            _ = EnforceCancellationTimeoutAsync(transactionId, _cancelTimeout.Token);
        }
        catch (Exception ex)
        {
            FailExecution(ex);
            throw;
        }
    }

    public async Task ForceTerminateAndRestartAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<EngineResultEnvelope>? completion;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state == EngineWorkerLifecycleState.Stopped)
            {
                completion = null;
            }
            else
            {
                _state = EngineWorkerLifecycleState.Terminating;
                completion = _completion;
                ClearExecutionLocked();
            }
        }

        if (completion is null && State == EngineWorkerLifecycleState.Stopped)
        {
            await StartAsync(cancellationToken);
            return;
        }

        try
        {
            await _transport.TerminateAsync(cancellationToken);
            completion?.TrySetCanceled(cancellationToken);
            lock (_sync)
            {
                _state = EngineWorkerLifecycleState.Stopped;
                _startTask = null;
            }
            await StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            completion?.TrySetException(ex);
            FailExecution(ex);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource<EngineResultEnvelope>? completion;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            completion = _completion;
            ClearExecutionLocked();
            _state = EngineWorkerLifecycleState.Stopped;
        }
        _transport.MessageReceived -= OnMessageReceived;
        completion?.TrySetCanceled();
        await _transport.DisposeAsync();
    }

    private void OnMessageReceived(object? sender, EngineWorkerMessage message)
    {
        EngineRequestEnvelope request;
        lock (_sync)
        {
            if (_request is null || message.TransactionId != _request.TransactionId ||
                _state is not (EngineWorkerLifecycleState.Running or EngineWorkerLifecycleState.Cancelling))
            {
                return;
            }

            request = _request;
        }

        if (!string.Equals(message.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal))
        {
            FailExecution(new InvalidOperationException("Worker protocol version mismatch."), request.TransactionId);
            return;
        }

        try
        {
            if (message.Kind == "progress" && message.Payload is { } progressPayload)
            {
                var progress = progressPayload.Deserialize<EngineProgress>(WireJsonOptions)
                    ?? throw new InvalidOperationException("Worker progress payload is empty.");
                ProgressChanged?.Invoke(this, progress);
                return;
            }

            if (message.Kind == "protocol-error" && message.Payload is { } errorPayload)
            {
                var error = errorPayload.Deserialize<WorkerProtocolError>(WireJsonOptions);
                throw new InvalidOperationException(error?.Message ?? "The engine worker reported a protocol error.");
            }

            if (message.Kind != "result" || message.Payload is not { } resultPayload)
            {
                return;
            }

            var result = resultPayload.Deserialize<EngineResultEnvelope>(WireJsonOptions)
                ?? throw new InvalidOperationException("Worker result payload is empty.");
            ValidateCompletion(request, result);
            TaskCompletionSource<EngineResultEnvelope>? completion;
            lock (_sync)
            {
                if (_request?.TransactionId != request.TransactionId)
                {
                    return;
                }

                _state = result.Status == EngineTerminalStatus.Failed
                    ? EngineWorkerLifecycleState.Faulted
                    : EngineWorkerLifecycleState.Ready;
                completion = _completion;
                ClearExecutionLocked();
            }
            completion?.TrySetResult(result);
        }
        catch (Exception ex)
        {
            FailExecution(ex, request.TransactionId);
        }
    }

    private static void ValidateCompletion(EngineRequestEnvelope request, EngineResultEnvelope result)
    {
        if (!string.Equals(result.ContractVersion, ProtocolVersion, StringComparison.Ordinal) ||
            !string.Equals(result.Completion.ContractVersion, ProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Worker result contract version mismatch.");
        }

        if (request.TransactionId != result.TransactionId ||
            result.TransactionId != result.Completion.TransactionId ||
            result.Status != result.Completion.Status ||
            result.Completion.Basis != request.Basis ||
            !string.Equals(result.Completion.RootIntentHash, request.RootIntentHash, StringComparison.Ordinal) ||
            !string.Equals(result.Completion.ExpandedGraphHash, request.ExpandedGraphHash, StringComparison.Ordinal) ||
            result.Completion.TerminalPhase != ExpectedTerminalPhase(result.Status))
        {
            throw new InvalidOperationException("Worker completion evidence does not match its request and result envelopes.");
        }

        if (result.Status == EngineTerminalStatus.Succeeded)
        {
            if (result.Result is not { } payload ||
                !result.Completion.TerminalEvidence.TryGetValue("resultPayloadHash", out var payloadHash) ||
                string.IsNullOrWhiteSpace(payloadHash))
            {
                throw new InvalidOperationException("Successful worker results require a result payload hash.");
            }

            if (!string.Equals(payloadHash, EngineCanonicalHash.Compute(payload), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Worker result payload hash validation failed.");
            }
        }

        var expectedHash = EngineCanonicalHash.ComputeFinalTransactionHash(
            request,
            result.Status,
            result.Completion.AnalysisResultHash,
            result.Completion.ProcurementRouteResultHash,
            result.Completion.TerminalEvidence);
        if (!string.Equals(expectedHash, result.Completion.FinalTransactionHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Worker final transaction hash validation failed.");
        }
    }

    private async Task EnforceCancellationTimeoutAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_cancellationTimeout, cancellationToken);
            lock (_sync)
            {
                if (_request?.TransactionId != transactionId || _state != EngineWorkerLifecycleState.Cancelling)
                {
                    return;
                }
            }
            FailExecution(new TimeoutException("The engine worker did not acknowledge cancellation before the timeout."), transactionId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static EnginePhase ExpectedTerminalPhase(EngineTerminalStatus status) => status switch
    {
        EngineTerminalStatus.Succeeded => EnginePhase.Completed,
        EngineTerminalStatus.Cancelled => EnginePhase.Cancelled,
        EngineTerminalStatus.Failed => EnginePhase.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private void FailExecution(Exception exception, Guid? expectedTransactionId = null)
    {
        TaskCompletionSource<EngineResultEnvelope>? completion;
        lock (_sync)
        {
            if (expectedTransactionId is not null && _request?.TransactionId != expectedTransactionId)
            {
                return;
            }

            _state = EngineWorkerLifecycleState.Faulted;
            completion = _completion;
            ClearExecutionLocked();
        }
        completion?.TrySetException(exception);
    }

    private void ClearExecution()
    {
        lock (_sync)
        {
            ClearExecutionLocked();
        }
    }

    private void ClearExecutionLocked()
    {
        _cancelTimeout?.Cancel();
        _cancelTimeout?.Dispose();
        _cancelTimeout = null;
        _completion = null;
        _request = null;
    }

    private void EnsureState(params EngineWorkerLifecycleState[] allowed)
    {
        lock (_sync)
        {
            EnsureStateLocked(allowed);
        }
    }

    private void EnsureStateLocked(params EngineWorkerLifecycleState[] allowed)
    {
        if (!allowed.Contains(_state))
        {
            throw new InvalidOperationException($"Engine worker cannot perform this operation while {_state}.");
        }
    }

    private sealed record WorkerProtocolError(string Code, string Message);
}
