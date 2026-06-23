namespace FFXIV_Craft_Architect.Web.Services;

public sealed class CancellableOperationService : IDisposable
{
    private readonly AppState _appState;
    private readonly object _sync = new();
    private readonly Dictionary<CancellableOperationWorkflow, CancellableOperationLease> _activeLeases = new();
    private bool _disposed;

    public CancellableOperationService(AppState appState)
    {
        _appState = appState;
    }

    internal AppState AppState => _appState;

    public CancellableOperationLease Start(
        CancellableOperationWorkflow workflow,
        string operationName,
        string startMessage,
        CancellationToken externalToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancellableOperationLease? previous;
        CancellableOperationLease lease;
        lock (_sync)
        {
            _activeLeases.TryGetValue(workflow, out previous);
            var operation = _appState.BeginOperation(operationName, startMessage);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            lease = new CancellableOperationLease(this, workflow, operation, cts);
            _activeLeases[workflow] = lease;
        }

        previous?.Supersede();
        return lease;
    }

    public void Cancel(CancellableOperationWorkflow workflow, string? message = null)
    {
        CancellableOperationLease? lease;
        lock (_sync)
        {
            if (!_activeLeases.Remove(workflow, out lease))
            {
                return;
            }
        }

        lease.CancelFromOwner(message);
    }

    public void CancelAll(string? message = null)
    {
        List<CancellableOperationLease> leases;
        lock (_sync)
        {
            leases = _activeLeases.Values.ToList();
            _activeLeases.Clear();
        }

        foreach (var lease in leases)
        {
            lease.CancelFromOwner(message);
        }
    }

    public void CancelPlanDependentOperations(string? message = null)
    {
        Cancel(CancellableOperationWorkflow.PriceRefresh, message);
        Cancel(CancellableOperationWorkflow.MarketAnalysis, message);
        Cancel(CancellableOperationWorkflow.ProcurementAnalysis, message);
        Cancel(CancellableOperationWorkflow.ItemMarketRefresh, message);
        Cancel(CancellableOperationWorkflow.TradeOrderPricing, message);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAll();
    }

    internal bool IsCurrent(CancellableOperationLease lease)
    {
        lock (_sync)
        {
            return _activeLeases.TryGetValue(lease.Workflow, out var current)
                && ReferenceEquals(current, lease)
                && !lease.Token.IsCancellationRequested;
        }
    }

    internal bool Complete(CancellableOperationLease lease, string? message)
    {
        if (!TryRemoveCurrent(lease))
        {
            return false;
        }

        return _appState.EndOperation(lease.Operation, message);
    }

    internal bool Cancel(CancellableOperationLease lease, string? message)
    {
        TryRemoveCurrent(lease);
        lease.CancelToken();
        return _appState.CancelOperation(lease.Operation, message);
    }

    internal void DisposeLease(CancellableOperationLease lease)
    {
        TryRemoveCurrent(lease);
    }

    private bool TryRemoveCurrent(CancellableOperationLease lease)
    {
        lock (_sync)
        {
            if (!_activeLeases.TryGetValue(lease.Workflow, out var current) || !ReferenceEquals(current, lease))
            {
                return false;
            }

            _activeLeases.Remove(lease.Workflow);
            return true;
        }
    }
}

public sealed class CancellableOperationLease : IDisposable
{
    private readonly CancellableOperationService _owner;
    private readonly CancellationTokenSource _cts;
    private bool _disposed;

    internal CancellableOperationLease(
        CancellableOperationService owner,
        CancellableOperationWorkflow workflow,
        AppStateOperation operation,
        CancellationTokenSource cts)
    {
        _owner = owner;
        Workflow = workflow;
        Operation = operation;
        _cts = cts;
    }

    public CancellableOperationWorkflow Workflow { get; }

    public AppStateOperation Operation { get; }

    public CancellationToken Token => _cts.Token;

    public bool IsCurrent => _owner.IsCurrent(this);

    public bool ReportStatus(string message, bool busy = true, double? progress = null)
    {
        return IsCurrent && _owner.AppState.SetStatusForOperation(Operation, message, busy, progress);
    }

    public bool Complete(string? message = null)
    {
        return _owner.Complete(this, message);
    }

    public bool Cancel(string? message = "Ready")
    {
        return _owner.Cancel(this, message);
    }

    public bool ShouldReportError(Exception ex)
    {
        if (!IsCurrent)
        {
            return false;
        }

        return ex is not OperationCanceledException || !Token.IsCancellationRequested;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.DisposeLease(this);
        _cts.Dispose();
    }

    internal void Supersede()
    {
        CancelToken();
    }

    internal void CancelFromOwner(string? message)
    {
        CancelToken();
        _owner.AppState.CancelOperation(Operation, message ?? "Ready");
    }

    internal void CancelToken()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }
}

public enum CancellableOperationWorkflow
{
    RecipeBuild,
    PriceRefresh,
    MarketAnalysis,
    ProcurementAnalysis,
    ItemMarketRefresh,
    PlanActivation,
    TradeOrderPricing
}
