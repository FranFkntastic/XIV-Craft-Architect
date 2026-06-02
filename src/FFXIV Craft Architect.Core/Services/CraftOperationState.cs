using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CraftOperationState
{
    private readonly object _gate = new();

    public event Action<CraftOperationSnapshot>? Changed;

    public Guid? CurrentOperationId { get; private set; }
    public CraftOperationWorkflow Workflow { get; private set; } = CraftOperationWorkflow.Unknown;
    public string OperationName { get; private set; } = string.Empty;
    public string StatusMessage { get; private set; } = "Ready";
    public int ProgressPercent { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsCancellationRequested { get; private set; }

    public CraftOperationSnapshot Snapshot()
    {
        lock (_gate)
        {
            return SnapshotUnderLock();
        }
    }

    internal void Start(Guid operationId, CraftOperationWorkflow workflow, string name, string statusMessage)
    {
        CraftOperationSnapshot snapshot;
        lock (_gate)
        {
            CurrentOperationId = operationId;
            Workflow = workflow;
            OperationName = name;
            StatusMessage = statusMessage;
            ProgressPercent = 0;
            IsBusy = true;
            IsCancellationRequested = false;
            snapshot = SnapshotUnderLock();
        }

        RaiseChanged(snapshot);
    }

    internal bool ReportProgress(Guid operationId, int progressPercent, string statusMessage)
    {
        CraftOperationSnapshot snapshot;
        lock (_gate)
        {
            if (CurrentOperationId != operationId || !IsBusy)
            {
                return false;
            }

            ProgressPercent = Math.Clamp(progressPercent, 0, 100);
            StatusMessage = statusMessage;
            snapshot = SnapshotUnderLock();
        }

        RaiseChanged(snapshot);
        return true;
    }

    internal bool Complete(Guid operationId, string statusMessage)
    {
        CraftOperationSnapshot snapshot;
        lock (_gate)
        {
            if (CurrentOperationId != operationId || !IsBusy)
            {
                return false;
            }

            StatusMessage = statusMessage;
            ProgressPercent = 100;
            IsBusy = false;
            IsCancellationRequested = false;
            snapshot = SnapshotUnderLock();
        }

        RaiseChanged(snapshot);
        return true;
    }

    internal bool Cancel(Guid operationId)
    {
        CraftOperationSnapshot snapshot;
        lock (_gate)
        {
            if (CurrentOperationId != operationId || !IsBusy)
            {
                return false;
            }

            StatusMessage = "Ready";
            ProgressPercent = 0;
            IsBusy = false;
            IsCancellationRequested = true;
            snapshot = SnapshotUnderLock();
        }

        RaiseChanged(snapshot);
        return true;
    }

    internal bool Supersede(Guid operationId)
    {
        CraftOperationSnapshot snapshot;
        lock (_gate)
        {
            if (CurrentOperationId != operationId || !IsBusy)
            {
                return false;
            }

            StatusMessage = "Ready";
            ProgressPercent = 0;
            IsBusy = false;
            IsCancellationRequested = true;
            snapshot = SnapshotUnderLock();
        }

        RaiseChanged(snapshot);
        return true;
    }

    internal bool IsCurrent(Guid operationId)
    {
        lock (_gate)
        {
            return CurrentOperationId == operationId && IsBusy;
        }
    }

    private CraftOperationSnapshot SnapshotUnderLock() =>
        new(
            CurrentOperationId,
            Workflow,
            OperationName,
            StatusMessage,
            ProgressPercent,
            IsBusy,
            IsCancellationRequested);

    private void RaiseChanged(CraftOperationSnapshot snapshot) =>
        Changed?.Invoke(snapshot);
}
