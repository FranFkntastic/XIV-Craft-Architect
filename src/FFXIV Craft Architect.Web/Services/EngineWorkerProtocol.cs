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
    private TaskCompletionSource<EngineResultEnvelope>? _completion;
    private EngineRequestEnvelope? _request;

    public EngineWorkerClient(IEngineWorkerTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _transport.MessageReceived += OnMessageReceived;
    }

    public EngineWorkerLifecycleState State { get; private set; } = EngineWorkerLifecycleState.Stopped;

    public EngineWorkerCapability? Capability { get; private set; }

    public event EventHandler<EngineProgress>? ProgressChanged;

    public async Task<EngineWorkerCapability> StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(EngineWorkerLifecycleState.Stopped, EngineWorkerLifecycleState.Faulted);
        State = EngineWorkerLifecycleState.Starting;
        try
        {
            Capability = await _transport.StartAsync(cancellationToken);
            if (!Capability.DedicatedWorker || !string.Equals(Capability.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The engine worker did not prove the required dedicated-worker protocol.");
            }

            State = EngineWorkerLifecycleState.Ready;
            return Capability;
        }
        catch
        {
            State = EngineWorkerLifecycleState.Faulted;
            throw;
        }
    }

    public async Task<EngineResultEnvelope> ExecuteAsync(
        EngineRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureState(EngineWorkerLifecycleState.Ready);
        _request = request;
        var completion = new TaskCompletionSource<EngineResultEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        State = EngineWorkerLifecycleState.Running;
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
        EnsureState(EngineWorkerLifecycleState.Running);
        State = EngineWorkerLifecycleState.Cancelling;
        var transactionId = _request!.TransactionId;
        await _transport.SendAsync(
            new EngineWorkerMessage(
                ProtocolVersion,
                "cancel",
                transactionId,
                JsonSerializer.SerializeToElement(new EngineCancelRequest(ProtocolVersion, transactionId, reason), WireJsonOptions)),
            cancellationToken);
    }

    public async Task ForceTerminateAndRestartAsync(CancellationToken cancellationToken = default)
    {
        if (State == EngineWorkerLifecycleState.Stopped)
        {
            await StartAsync(cancellationToken);
            return;
        }

        State = EngineWorkerLifecycleState.Terminating;
        await _transport.TerminateAsync(cancellationToken);
        _completion?.TrySetCanceled(cancellationToken);
        ClearExecution();
        State = EngineWorkerLifecycleState.Stopped;
        await StartAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _transport.MessageReceived -= OnMessageReceived;
        _completion?.TrySetCanceled();
        ClearExecution();
        await _transport.DisposeAsync();
        State = EngineWorkerLifecycleState.Stopped;
    }

    private void OnMessageReceived(object? sender, EngineWorkerMessage message)
    {
        if (!string.Equals(message.ProtocolVersion, ProtocolVersion, StringComparison.Ordinal))
        {
            FailExecution(new InvalidOperationException("Worker protocol version mismatch."));
            return;
        }

        if (message.TransactionId != _request?.TransactionId)
        {
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
            ValidateCompletion(_request!, result);
            State = result.Status == EngineTerminalStatus.Failed
                ? EngineWorkerLifecycleState.Faulted
                : EngineWorkerLifecycleState.Ready;
            _completion?.TrySetResult(result);
            ClearExecution();
        }
        catch (Exception ex)
        {
            FailExecution(ex);
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
            result.Completion.Basis != request.Basis)
        {
            throw new InvalidOperationException("Worker completion evidence does not match its request and result envelopes.");
        }

        if (result.Result is { } payload &&
            result.Completion.TerminalEvidence.TryGetValue("resultPayloadHash", out var payloadHash) &&
            !string.Equals(payloadHash, EngineCanonicalHash.Compute(payload), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Worker result payload hash validation failed.");
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

    private void FailExecution(Exception exception)
    {
        State = EngineWorkerLifecycleState.Faulted;
        _completion?.TrySetException(exception);
        ClearExecution();
    }

    private void ClearExecution()
    {
        _completion = null;
        _request = null;
    }

    private void EnsureState(params EngineWorkerLifecycleState[] allowed)
    {
        if (!allowed.Contains(State))
        {
            throw new InvalidOperationException($"Engine worker cannot perform this operation while {State}.");
        }
    }

    private sealed record WorkerProtocolError(string Code, string Message);
}
