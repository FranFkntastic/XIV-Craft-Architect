using FFXIV_Craft_Architect.Core.Engine;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record ExperimentalProcurementEngineCapability(bool IsExecutionEnabled);

public sealed class ExperimentalProcurementEngineFactory
{
    private static readonly TimeSpan BrowserComputationTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BrowserSettlementPhaseTimeout = TimeSpan.FromMinutes(2);
    private readonly ExperimentalProcurementEngineCapability _capability;
    private readonly IJSRuntime _jsRuntime;
    private readonly AppState _appState;
    private readonly IndexedDbService _indexedDb;
    private readonly IReferenceEngineSemanticSnapshotProvider _snapshots;
    private readonly ILogger<WebProcurementEngineSettlement>? _settlementLogger;

    public ExperimentalProcurementEngineFactory(
        ExperimentalProcurementEngineCapability capability,
        IJSRuntime jsRuntime,
        AppState appState,
        IndexedDbService indexedDb,
        IReferenceEngineSemanticSnapshotProvider snapshots,
        ILogger<WebProcurementEngineSettlement>? settlementLogger = null)
    {
        _capability = capability ?? throw new ArgumentNullException(nameof(capability));
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _indexedDb = indexedDb ?? throw new ArgumentNullException(nameof(indexedDb));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _settlementLogger = settlementLogger;
    }

    public ExperimentalProcurementEngineExecution Create(WebProcurementSettlementRegistration registration)
    {
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
        var client = new EngineWorkerClient(
            new BrowserEngineWorkerTransport(_jsRuntime),
            cancellationTimeout: TimeSpan.FromSeconds(2),
            responseTimeout: BrowserComputationTimeout + TimeSpan.FromSeconds(10),
            transportTimeout: TimeSpan.FromSeconds(10));
        var transport = new EngineWorkerExecutionTransport(client);
        var ledger = new IndexedDbEngineTransactionLedger(_jsRuntime);
        var cooperativeYield = new BrowserEngineCooperativeYield(_jsRuntime);
        var host = new EngineExecutionHost(
            transport,
            settlement,
            ledger,
            _snapshots,
            EngineExecutionHostOptions.Default with
            {
                ComputationTimeout = BrowserComputationTimeout,
                SettlementPhaseTimeout = BrowserSettlementPhaseTimeout,
                CooperativeYield = cooperativeYield.YieldAsync
            });
        return new ExperimentalProcurementEngineExecution(
            host,
            transport,
            client,
            cooperativeYield,
            settlement,
            ledger,
            _snapshots);
    }
}

public sealed class ExperimentalProcurementEngineExecution : IAsyncDisposable
{
    private readonly EngineExecutionHost _host;
    private readonly EngineWorkerExecutionTransport _transport;
    private readonly EngineWorkerClient _client;
    private readonly BrowserEngineCooperativeYield _cooperativeYield;
    private readonly WebProcurementEngineSettlement _settlement;
    private readonly IndexedDbEngineTransactionLedger _ledger;
    private readonly IReferenceEngineSemanticSnapshotProvider _snapshots;
    private bool _disposed;

    internal ExperimentalProcurementEngineExecution(
        EngineExecutionHost host,
        EngineWorkerExecutionTransport transport,
        EngineWorkerClient client,
        BrowserEngineCooperativeYield cooperativeYield,
        WebProcurementEngineSettlement settlement,
        IndexedDbEngineTransactionLedger ledger,
        IReferenceEngineSemanticSnapshotProvider snapshots)
    {
        _host = host;
        _transport = transport;
        _client = client;
        _cooperativeYield = cooperativeYield;
        _settlement = settlement;
        _ledger = ledger;
        _snapshots = snapshots;
    }

    public EngineExecutionTransportCapability TransportCapability => _transport.Capability;

    public EngineTransactionLedgerCapability LedgerCapability => _ledger.Capability;

    public IReadOnlyDictionary<EnginePhase, long> SettlementPhaseElapsedMilliseconds =>
        _settlement.SettlementPhaseElapsedMilliseconds;

    public AutoSavePerformanceTiming? AutoSavePerformanceTiming =>
        _settlement.AutoSavePerformanceTiming;

    public EngineLedgerCompleteTiming? LedgerCompleteTiming =>
        _ledger.LastCompleteTiming;

    public EngineWorkerResultTiming? WorkerResultTiming =>
        _client.LastResultTiming;

    public long? ComputationValidationMilliseconds =>
        _host.LastComputationValidationMilliseconds;

    public Task<EngineResultEnvelope> ExecuteAsync(
        EngineRequestEnvelope request,
        IProgress<EngineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _host.ExecuteAsync(request, progress, cancellationToken);
    }

    public Task<EngineResultEnvelope> ReplayAsync(
        EngineRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var replayHost = new EngineExecutionHost(
            new UnsupportedBrowserWorkerEngineExecutionTransport(),
            _settlement,
            _ledger,
            _snapshots);
        return replayHost.ExecuteAsync(request, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            await _transport.DisposeAsync();
        }
        finally
        {
            await _cooperativeYield.DisposeAsync();
        }
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
