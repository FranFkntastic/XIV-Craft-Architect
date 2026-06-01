using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public interface ICraftOperationCoordinator
{
    CraftOperationLease Start(CraftOperationWorkflow workflow, string operationName, string initialStatus);
    void Cancel(CraftOperationWorkflow workflow);
}

public sealed class CraftOperationCoordinator : ICraftOperationCoordinator
{
    private readonly CraftSessionState _session;
    private readonly CraftOperationState _state;
    private readonly object _gate = new();
    private readonly Dictionary<CraftOperationWorkflow, CraftOperationLease> _leases = new();

    public CraftOperationCoordinator(CraftSessionState session, CraftOperationState state)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public CraftOperationLease Start(CraftOperationWorkflow workflow, string operationName, string initialStatus)
    {
        var lease = new CraftOperationLease(
            this,
            _session,
            _state,
            workflow,
            Guid.NewGuid(),
            _session.CaptureVersionStamp());

        lock (_gate)
        {
            foreach (var current in _leases.Values)
            {
                current.CancelFromCoordinator();
            }

            _leases.Clear();
            _leases[workflow] = lease;
            _state.Start(lease.OperationId, workflow, operationName, initialStatus);
        }

        return lease;
    }

    public void Cancel(CraftOperationWorkflow workflow)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(workflow, out var lease))
            {
                lease.Cancel();
            }
        }
    }

    internal bool IsCurrent(CraftOperationLease lease)
    {
        lock (_gate)
        {
            return IsCurrentUnderLock(lease);
        }
    }

    internal bool Complete(CraftOperationLease lease, Action publish, string statusMessage)
    {
        CraftSessionChange[] changesToDispatch = [];
        bool published = false;
        bool completed;

        lock (_gate)
        {
            if (!IsCurrentUnderLock(lease))
            {
                return false;
            }

            try
            {
                published = _session.TryPublishFrom(lease.SessionStamp, publish, out changesToDispatch);
            }
            catch
            {
                lease.CancelFromCoordinator();
                _state.Supersede(lease.OperationId);
                _leases.Remove(lease.Workflow);
                throw;
            }

            if (!published)
            {
                lease.CancelFromCoordinator();
                _state.Supersede(lease.OperationId);
                _leases.Remove(lease.Workflow);
                return false;
            }

            completed = _state.Complete(lease.OperationId, statusMessage);
            if (completed)
            {
                _leases.Remove(lease.Workflow);
            }
        }

        if (published)
        {
            _session.DispatchPublicationChanges(changesToDispatch);
        }

        return completed;
    }

    internal bool CompleteStatus(CraftOperationLease lease, string statusMessage)
    {
        lock (_gate)
        {
            if (!IsCurrentUnderLock(lease) || !_session.IsCurrent(lease.SessionStamp))
            {
                return false;
            }

            var completed = _state.Complete(lease.OperationId, statusMessage);
            if (completed)
            {
                _leases.Remove(lease.Workflow);
            }

            return completed;
        }
    }

    internal void Release(CraftOperationLease lease)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(lease.Workflow, out var current) && ReferenceEquals(current, lease))
            {
                _leases.Remove(lease.Workflow);
            }
        }
    }

    internal bool Supersede(CraftOperationLease lease)
    {
        lock (_gate)
        {
            if (!IsCurrentUnderLock(lease))
            {
                return false;
            }

            lease.CancelFromCoordinator();
            _state.Supersede(lease.OperationId);
            _leases.Remove(lease.Workflow);
            return true;
        }
    }

    private bool IsCurrentUnderLock(CraftOperationLease lease) =>
        _leases.TryGetValue(lease.Workflow, out var current)
        && ReferenceEquals(current, lease)
        && _state.IsCurrent(lease.OperationId);
}

public sealed class CraftOperationLease : IDisposable
{
    private readonly CraftOperationCoordinator _coordinator;
    private readonly CraftSessionState _session;
    private readonly CraftOperationState _state;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _disposed;

    internal CraftOperationLease(
        CraftOperationCoordinator coordinator,
        CraftSessionState session,
        CraftOperationState state,
        CraftOperationWorkflow workflow,
        Guid operationId,
        CraftSessionVersionStamp sessionStamp)
    {
        _coordinator = coordinator;
        _session = session;
        _state = state;
        Workflow = workflow;
        OperationId = operationId;
        SessionStamp = sessionStamp;
    }

    public Guid OperationId { get; }
    public CraftOperationWorkflow Workflow { get; }
    internal CraftSessionVersionStamp SessionStamp { get; private set; }
    public CancellationToken Token => _cancellation.Token;
    public bool IsCurrent =>
        !_disposed &&
        !_cancellation.IsCancellationRequested &&
        _coordinator.IsCurrent(this) &&
        _session.IsCurrent(SessionStamp);

    public bool ReportProgress(int progressPercent, string statusMessage)
    {
        if (_disposed || _cancellation.IsCancellationRequested)
        {
            return false;
        }

        if (!_session.IsCurrent(SessionStamp))
        {
            _coordinator.Supersede(this);
            return false;
        }

        return _coordinator.IsCurrent(this) && _state.ReportProgress(OperationId, progressPercent, statusMessage);
    }

    public bool CompleteIfCurrent(Action publish, string statusMessage)
        => !_disposed && !_cancellation.IsCancellationRequested && _coordinator.Complete(this, publish, statusMessage);

    public bool CompleteStatusIfCurrent(string statusMessage)
        => !_disposed && !_cancellation.IsCancellationRequested && _coordinator.CompleteStatus(this, statusMessage);

    public void RefreshSessionStamp()
    {
        if (!_disposed && !_cancellation.IsCancellationRequested)
        {
            SessionStamp = _session.CaptureVersionStamp();
        }
    }

    public bool Cancel()
    {
        if (_disposed)
        {
            return false;
        }

        _cancellation.Cancel();
        var canceled = _state.Cancel(OperationId);
        _coordinator.Release(this);
        return canceled;
    }

    internal void CancelFromCoordinator()
    {
        if (!_disposed)
        {
            _cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Dispose();
        _coordinator.Release(this);
    }
}
