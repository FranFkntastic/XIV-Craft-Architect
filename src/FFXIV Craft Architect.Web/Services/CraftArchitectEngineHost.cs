using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record CraftArchitectEngineCapability(bool IsExecutionEnabled);

public enum EngineCommandPriority
{
    Interactive = 0,
    UserRequestedDerivation = 1,
    Persistence = 2,
    Maintenance = 3
}

public sealed record CraftArchitectEngineHostHealth(
    EngineWorkerLifecycleState State,
    long Generation,
    string? WorkerInstanceId,
    int PendingCommandCount,
    bool IsExecuting);

/// <summary>
/// Application-scoped owner of the managed browser Worker.
///
/// The host owns one replaceable Worker/client generation and serial command queue.
/// Constructing it does not start the second .NET runtime.
/// </summary>
public sealed class CraftArchitectEngineHost : IAsyncDisposable
{
    private static readonly TimeSpan BrowserComputationTimeout = TimeSpan.FromMinutes(5);
    private readonly CraftArchitectEngineCapability _capability;
    private readonly BrowserEngineWorkerTransport _workerTransport;
    private readonly EngineWorkerClient _client;
    private readonly object _queueSync = new();
    private readonly object _sessionSync = new();
    private readonly Dictionary<Guid, TaskCompletionSource<WorkerSessionResultEnvelope>>
        _pendingSessionCommands = [];
    private readonly Queue<QueuedCommand>[] _queues =
        Enum.GetValues<EngineCommandPriority>()
            .Select(_ => new Queue<QueuedCommand>())
            .ToArray();
    private bool _queuePumpActive;
    private bool _isExecuting;
    private bool _disposed;
    private long _commandSequence;

    public CraftArchitectEngineHost(
        CraftArchitectEngineCapability capability,
        IJSRuntime jsRuntime)
    {
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
        ArgumentNullException.ThrowIfNull(jsRuntime);
        _workerTransport = new BrowserEngineWorkerTransport(jsRuntime);
        _workerTransport.MessageReceived += OnWorkerMessageReceived;
        _client = new EngineWorkerClient(
            _workerTransport,
            cancellationTimeout: TimeSpan.FromSeconds(2),
            responseTimeout: BrowserComputationTimeout + TimeSpan.FromSeconds(10),
            transportTimeout: TimeSpan.FromSeconds(10));
    }

    internal EngineWorkerResultTiming? WorkerResultTiming => _client.LastResultTiming;

    public CraftArchitectEngineHostHealth Health
    {
        get
        {
            lock (_queueSync)
            {
                return new CraftArchitectEngineHostHealth(
                    _client.State,
                    _client.Capability?.Generation ?? 0,
                    _client.Capability?.WorkerInstanceId,
                    _queues.Sum(queue => queue.Count),
                    _isExecuting);
            }
        }
    }

    public Task CancelAsync(
        string reason = "The active engine command was cancelled.",
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _client.State is EngineWorkerLifecycleState.Running or EngineWorkerLifecycleState.Cancelling
            ? _client.CancelAsync(reason, cancellationToken)
            : Task.CompletedTask;
    }

    public Task RestartAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            EngineCommandPriority.Maintenance,
            async token =>
            {
                await _client.ForceTerminateAndRestartAsync(token);
                return true;
            },
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> BootstrapSessionAsync(
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            "bootstrap",
            expectedRevision: 0,
            new { },
            EngineCommandPriority.Interactive,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> GetShellProjectionAsync(
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            "shell",
            expectedRevision,
            new { },
            EngineCommandPriority.Interactive,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> ReplaceSessionAsync(
        long expectedRevision,
        StoredPlan? storedPlan,
        bool trackStoredPlanIdentity,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            "replace",
            expectedRevision,
            new WorkerSessionReplacePayload(storedPlan, trackStoredPlanIdentity),
            EngineCommandPriority.Persistence,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> ExportSessionAsync(
        long expectedRevision,
        WorkerSessionExportRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            "export",
            expectedRevision,
            request,
            EngineCommandPriority.Interactive,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> GetRecipeProjectionAsync(
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.RecipeProjection,
            expectedRevision,
            new { },
            EngineCommandPriority.Interactive,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> GetAcquisitionProjectionAsync(
        long expectedRevision,
        string filter,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.AcquisitionProjection,
            expectedRevision,
            new WorkerAcquisitionProjectionRequest(filter),
            EngineCommandPriority.Interactive,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> MutateProjectItemsAsync(
        long expectedRevision,
        WorkerProjectItemsMutation mutation,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.ProjectItemsMutation,
            expectedRevision,
            mutation,
            EngineCommandPriority.Interactive,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> MutatePlanIdentityAsync(
        long expectedRevision,
        WorkerPlanIdentityMutation mutation,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.PlanIdentityMutation,
            expectedRevision,
            mutation,
            EngineCommandPriority.Persistence,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> BuildRecipeAsync(
        long expectedRevision,
        WorkerRecipeBuildRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.RecipeBuild,
            expectedRevision,
            request,
            EngineCommandPriority.UserRequestedDerivation,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> MutateAcquisitionAsync(
        long expectedRevision,
        WorkerAcquisitionMutation mutation,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.AcquisitionMutation,
            expectedRevision,
            mutation,
            EngineCommandPriority.Interactive,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> GetMarketProjectionAsync(
        long expectedRevision,
        bool includeDetails = true,
        int? worldDetailItemId = null,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.MarketProjection,
            expectedRevision,
            new WorkerMarketProjectionRequest(includeDetails, worldDetailItemId),
            EngineCommandPriority.Interactive,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> RunMarketAnalysisAsync(
        long expectedRevision,
        WorkerMarketAnalysisRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.MarketAnalysisRun,
            expectedRevision,
            request,
            EngineCommandPriority.UserRequestedDerivation,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> PublishMarketEvidenceAsync(
        long expectedRevision,
        WorkerMarketEvidencePublicationRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.MarketEvidencePublication,
            expectedRevision,
            request,
            EngineCommandPriority.UserRequestedDerivation,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> StageMarketEvidenceAsync(
        long expectedRevision,
        WorkerMarketEvidencePublicationRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.MarketEvidencePublicationStage,
            expectedRevision,
            request,
            EngineCommandPriority.UserRequestedDerivation,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> ApplyMarketLensAsync(
        long expectedRevision,
        WorkerMarketLensMutation mutation,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.MarketLensMutation,
            expectedRevision,
            mutation,
            EngineCommandPriority.UserRequestedDerivation,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> RefreshMarketItemAsync(
        long expectedRevision,
        WorkerMarketItemRefreshRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.MarketItemRefresh,
            expectedRevision,
            request,
            EngineCommandPriority.UserRequestedDerivation,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> PublishMarketItemEvidenceAsync(
        long expectedRevision,
        WorkerMarketItemEvidencePublicationRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.MarketItemEvidencePublication,
            expectedRevision,
            request,
            EngineCommandPriority.UserRequestedDerivation,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> GetProcurementProjectionAsync(
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.ProcurementProjection,
            expectedRevision,
            new { },
            EngineCommandPriority.Interactive,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> GetTradeProjectionAsync(
        long expectedRevision,
        bool includeCraftLabor,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.TradeProjection,
            expectedRevision,
            new WorkerTradeProjectionRequest(includeCraftLabor),
            includeCraftLabor
                ? EngineCommandPriority.UserRequestedDerivation
                : EngineCommandPriority.Interactive,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> RunProcurementAsync(
        long expectedRevision,
        WorkerProcurementRequest request,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.ProcurementRun,
            expectedRevision,
            request,
            EngineCommandPriority.UserRequestedDerivation,
            cancellationToken);

    public Task<WorkerSessionResultEnvelope> SelectProcurementToleranceAsync(
        long expectedRevision,
        int travelTolerance,
        CancellationToken cancellationToken = default) =>
        EnqueueSessionCommandAsync(
            WorkerSessionCommandKinds.ProcurementToleranceMutation,
            expectedRevision,
            new WorkerProcurementToleranceMutation(travelTolerance),
            EngineCommandPriority.Interactive,
            cancellationToken);

    private Task<WorkerSessionResultEnvelope> EnqueueSessionCommandAsync<TPayload>(
        string commandKind,
        long expectedRevision,
        TPayload payload,
        EngineCommandPriority priority,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_capability.IsExecutionEnabled)
        {
            throw new NotSupportedException("Worker-owned session execution is disabled.");
        }
        return EnqueueAsync(
            priority,
            token => SendSessionCommandCoreAsync(
                commandKind,
                expectedRevision,
                payload,
                token),
            cancellationToken);
    }

    private async Task<WorkerSessionResultEnvelope> SendSessionCommandCoreAsync<TPayload>(
        string commandKind,
        long expectedRevision,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var capability = await _client.StartAsync(cancellationToken);
        if (!capability.ExecutionSupported)
        {
            throw new NotSupportedException("The managed browser Worker is not ready for session commands.");
        }

        var commandId = Guid.NewGuid();
        var completion = new TaskCompletionSource<WorkerSessionResultEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sessionSync)
        {
            _pendingSessionCommands.Add(commandId, completion);
        }

        var command = new WorkerSessionCommandEnvelope(
            WorkerSessionProtocol.ContractVersion,
            commandKind,
            expectedRevision,
            JsonSerializer.SerializeToElement(payload, EngineJsonSerializerOptions.CreateWire()));
        var message = new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            WorkerSessionProtocol.CommandMessageKind,
            capability.Generation,
            commandId,
            commandId,
            JsonSerializer.SerializeToElement(command, EngineJsonSerializerOptions.CreateWire()));
        using var timeout = new CancellationTokenSource(BrowserComputationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await _workerTransport.SendAsync(message, linked.Token);
            return await completion.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await _client.ForceTerminateAndRestartAsync(CancellationToken.None);
            throw new TimeoutException(
                $"Worker session command '{commandKind}' did not finish before its deadline.");
        }
        catch when (
            string.Equals(commandKind, "replace", StringComparison.Ordinal) ||
            WorkerSessionCommandKinds.IsMutation(commandKind))
        {
            // A replacement mutates managed Worker state before IndexedDB commits its
            // revision. If the protocol or durable write fails, replace the Worker so
            // the next command reloads the last committed revision instead of observing
            // an uncommitted in-memory candidate.
            await _client.ForceTerminateAndRestartAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            lock (_sessionSync)
            {
                _pendingSessionCommands.Remove(commandId);
            }
        }
    }

    private void OnWorkerMessageReceived(object? sender, EngineWorkerMessage message)
    {
        if (message.ExecutionId is not { } commandId ||
            message.TransactionId != commandId ||
            message.Kind is not (WorkerSessionProtocol.ResultMessageKind or "protocol-error"))
        {
            return;
        }

        TaskCompletionSource<WorkerSessionResultEnvelope>? completion;
        lock (_sessionSync)
        {
            _pendingSessionCommands.TryGetValue(commandId, out completion);
        }
        if (completion is null)
        {
            return;
        }

        try
        {
            if (message.Kind == "protocol-error")
            {
                var detail = message.Payload?.GetProperty("message").GetString()
                    ?? "The Worker rejected the session command.";
                completion.TrySetException(new InvalidOperationException(detail));
                return;
            }
            var result = message.Payload?.Deserialize<WorkerSessionResultEnvelope>(
                    EngineJsonSerializerOptions.CreateWire())
                ?? throw new InvalidOperationException("The Worker session result is empty.");
            completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private Task<T> EnqueueAsync<T>(
        EngineCommandPriority priority,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new QueuedCommand(
            Interlocked.Increment(ref _commandSequence),
            priority,
            async token => await action(token),
            completion,
            cancellationToken);
        var startPump = false;
        lock (_queueSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queues[(int)priority].Enqueue(queued);
            if (!_queuePumpActive)
            {
                _queuePumpActive = true;
                startPump = true;
            }
        }
        queued.RegisterCancellation();
        if (startPump)
        {
            _ = ProcessQueueAsync();
        }
        return AwaitTypedAsync<T>(completion.Task);
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            QueuedCommand? command;
            lock (_queueSync)
            {
                command = DequeueNextCommand();
                if (command is null)
                {
                    _queuePumpActive = false;
                    _isExecuting = false;
                    return;
                }
                _isExecuting = true;
            }

            if (command.Completion.Task.IsCompleted)
            {
                command.Dispose();
                continue;
            }

            try
            {
                var result = await command.Action(command.CancellationToken);
                command.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (command.CancellationToken.IsCancellationRequested)
            {
                command.Completion.TrySetCanceled(command.CancellationToken);
            }
            catch (Exception ex)
            {
                command.Completion.TrySetException(ex);
            }
            finally
            {
                command.Dispose();
                lock (_queueSync)
                {
                    _isExecuting = false;
                }
            }
        }
    }

    private QueuedCommand? DequeueNextCommand()
    {
        foreach (var queue in _queues)
        {
            if (queue.Count > 0)
            {
                return queue.Dequeue();
            }
        }
        return null;
    }

    private static async Task<T> AwaitTypedAsync<T>(Task<object?> task) =>
        (T)(await task ?? throw new InvalidOperationException("The engine command returned no result."));

    public async ValueTask DisposeAsync()
    {
        List<QueuedCommand> abandoned = [];
        lock (_queueSync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var queue in _queues)
            {
                while (queue.Count > 0)
                {
                    abandoned.Add(queue.Dequeue());
                }
            }
        }
        foreach (var command in abandoned)
        {
            command.Completion.TrySetException(
                new ObjectDisposedException(nameof(CraftArchitectEngineHost)));
            command.Dispose();
        }
        TaskCompletionSource<WorkerSessionResultEnvelope>[] pendingSessionCommands;
        lock (_sessionSync)
        {
            pendingSessionCommands = _pendingSessionCommands.Values.ToArray();
            _pendingSessionCommands.Clear();
        }
        foreach (var pending in pendingSessionCommands)
        {
            pending.TrySetException(new ObjectDisposedException(nameof(CraftArchitectEngineHost)));
        }

        _workerTransport.MessageReceived -= OnWorkerMessageReceived;
        await _client.DisposeAsync();
    }

    private sealed class QueuedCommand(
        long sequence,
        EngineCommandPriority priority,
        Func<CancellationToken, Task<object?>> action,
        TaskCompletionSource<object?> completion,
        CancellationToken cancellationToken) : IDisposable
    {
        private CancellationTokenRegistration _registration;

        public long Sequence { get; } = sequence;
        public EngineCommandPriority Priority { get; } = priority;
        public Func<CancellationToken, Task<object?>> Action { get; } = action;
        public TaskCompletionSource<object?> Completion { get; } = completion;
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public void RegisterCancellation()
        {
            if (CancellationToken.CanBeCanceled)
            {
                _registration = CancellationToken.Register(
                    static state =>
                    {
                        var command = (QueuedCommand)state!;
                        command.Completion.TrySetCanceled(command.CancellationToken);
                    },
                    this);
            }
        }

        public void Dispose() => _registration.Dispose();
    }
}
