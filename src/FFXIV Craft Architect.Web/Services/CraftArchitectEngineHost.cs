using FFXIV_Craft_Architect.Core.Engine;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record ExperimentalProcurementEngineCapability(bool IsExecutionEnabled);

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
/// Commands may create command-specific settlement handlers, but they all share one
/// replaceable Worker/client generation and one durable transaction ledger. The host
/// is deliberately lazy: constructing it does not start the second .NET runtime.
/// </summary>
public sealed class CraftArchitectEngineHost : IAsyncDisposable
{
    private static readonly TimeSpan BrowserComputationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BrowserSettlementPhaseTimeout = TimeSpan.FromMinutes(2);
    private readonly ExperimentalProcurementEngineCapability _capability;
    private readonly AppState _appState;
    private readonly IndexedDbService _indexedDb;
    private readonly IReferenceEngineSemanticSnapshotProvider _snapshots;
    private readonly ILogger<WebProcurementEngineSettlement>? _settlementLogger;
    private readonly EngineWorkerClient _client;
    private readonly EngineWorkerExecutionTransport _transport;
    private readonly IndexedDbEngineTransactionLedger _ledger;
    private readonly BrowserEngineCooperativeYield _cooperativeYield;
    private readonly object _queueSync = new();
    private readonly Queue<QueuedCommand>[] _queues =
        Enum.GetValues<EngineCommandPriority>()
            .Select(_ => new Queue<QueuedCommand>())
            .ToArray();
    private bool _queuePumpActive;
    private bool _isExecuting;
    private bool _disposed;
    private long _commandSequence;

    public CraftArchitectEngineHost(
        ExperimentalProcurementEngineCapability capability,
        IJSRuntime jsRuntime,
        AppState appState,
        IndexedDbService indexedDb,
        IReferenceEngineSemanticSnapshotProvider snapshots,
        ILogger<WebProcurementEngineSettlement>? settlementLogger = null)
    {
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
        ArgumentNullException.ThrowIfNull(jsRuntime);
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _indexedDb = indexedDb ?? throw new ArgumentNullException(nameof(indexedDb));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _settlementLogger = settlementLogger;

        _client = new EngineWorkerClient(
            new BrowserEngineWorkerTransport(jsRuntime),
            cancellationTimeout: TimeSpan.FromSeconds(2),
            responseTimeout: BrowserComputationTimeout + TimeSpan.FromSeconds(10),
            transportTimeout: TimeSpan.FromSeconds(10));
        _transport = new EngineWorkerExecutionTransport(_client);
        _ledger = new IndexedDbEngineTransactionLedger(jsRuntime);
        _cooperativeYield = new BrowserEngineCooperativeYield(jsRuntime);
    }

    public EngineExecutionTransportCapability TransportCapability => _transport.Capability;

    public EngineTransactionLedgerCapability LedgerCapability => _ledger.Capability;

    internal IndexedDbEngineTransactionLedger Ledger => _ledger;

    internal IReferenceEngineSemanticSnapshotProvider Snapshots => _snapshots;

    internal EngineLedgerCompleteTiming? LedgerCompleteTiming => _ledger.LastCompleteTiming;

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

    public CraftArchitectEngineExecution CreateExecution(
        WebProcurementSettlementRegistration registration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_capability.IsExecutionEnabled)
        {
            throw new NotSupportedException("Experimental procurement engine execution is disabled.");
        }
        ArgumentNullException.ThrowIfNull(registration);

        var settlement = new WebProcurementEngineSettlement(
            _appState,
            _indexedDb,
            _snapshots,
            registration,
            _settlementLogger);
        var executionHost = new EngineExecutionHost(
            _transport,
            settlement,
            _ledger,
            _snapshots,
            EngineExecutionHostOptions.Default with
            {
                MaxConcurrentExecutions = 1,
                ComputationTimeout = BrowserComputationTimeout,
                SettlementPhaseTimeout = BrowserSettlementPhaseTimeout,
                CooperativeYield = _cooperativeYield.YieldAsync
            });
        return new CraftArchitectEngineExecution(this, executionHost, settlement);
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

    internal Task<EngineResultEnvelope> ExecuteAsync(
        EngineExecutionHost executionHost,
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress,
        EngineCommandPriority priority,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EnqueueAsync(
            priority,
            token => executionHost.ExecuteAsync(request, progress, token),
            cancellationToken);
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

        try
        {
            await _transport.DisposeAsync();
        }
        finally
        {
            await _cooperativeYield.DisposeAsync();
        }
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

public sealed class CraftArchitectEngineExecution : IAsyncDisposable
{
    private readonly CraftArchitectEngineHost _owner;
    private readonly EngineExecutionHost _host;
    private readonly WebProcurementEngineSettlement _settlement;
    private bool _disposed;

    internal CraftArchitectEngineExecution(
        CraftArchitectEngineHost owner,
        EngineExecutionHost host,
        WebProcurementEngineSettlement settlement)
    {
        _owner = owner;
        _host = host;
        _settlement = settlement;
    }

    public EngineExecutionTransportCapability TransportCapability => _owner.TransportCapability;
    public EngineTransactionLedgerCapability LedgerCapability => _owner.LedgerCapability;
    public IReadOnlyDictionary<EnginePhase, long> SettlementPhaseElapsedMilliseconds =>
        _settlement.SettlementPhaseElapsedMilliseconds;
    public AutoSavePerformanceTiming? AutoSavePerformanceTiming => _settlement.AutoSavePerformanceTiming;
    public EngineLedgerCompleteTiming? LedgerCompleteTiming => _owner.LedgerCompleteTiming;
    public EngineWorkerResultTiming? WorkerResultTiming => _owner.WorkerResultTiming;
    public long? ComputationValidationMilliseconds => _host.LastComputationValidationMilliseconds;

    public Task<EngineResultEnvelope> ExecuteAsync(
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default,
        EngineCommandPriority priority = EngineCommandPriority.UserRequestedDerivation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _owner.ExecuteAsync(_host, request, progress, priority, cancellationToken);
    }

    public Task<EngineResultEnvelope> ReplayAsync(
        EngineRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var replayHost = new EngineExecutionHost(
            new UnsupportedBrowserWorkerEngineExecutionTransport(),
            _settlement,
            _owner.Ledger,
            _owner.Snapshots);
        return replayHost.ExecuteAsync(request, cancellationToken: cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class BrowserEngineCooperativeYield : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly SemaphoreSlim _moduleLock = new(1, 1);
    private IJSObjectReference? _module;

    public BrowserEngineCooperativeYield(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async ValueTask YieldAsync(CancellationToken cancellationToken)
    {
        var module = _module;
        if (module is null)
        {
            await _moduleLock.WaitAsync(cancellationToken);
            try
            {
                module = _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>(
                    "import",
                    cancellationToken,
                    "./engine-worker-bootstrap.js");
            }
            finally
            {
                _moduleLock.Release();
            }
        }
        await module.InvokeVoidAsync("yieldToBrowser", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _moduleLock.WaitAsync();
        try
        {
            if (_module is not null)
            {
                await _module.DisposeAsync();
                _module = null;
            }
        }
        finally
        {
            _moduleLock.Release();
        }
    }
}
