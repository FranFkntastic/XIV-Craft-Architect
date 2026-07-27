namespace FFXIV_Craft_Architect.Web.Services;

public sealed class WorkerSessionOperationLease : IAsyncDisposable
{
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(30);
    private readonly WorkerSessionCoordinator _owner;
    private readonly CancellationTokenSource _renewalCancellation = new();
    private readonly Task _renewal;
    private int _terminal;

    internal WorkerSessionOperationLease(
        WorkerSessionCoordinator owner,
        Guid operationId,
        WorkerSessionOperationKind kind,
        string intentKey)
    {
        _owner = owner;
        OperationId = operationId;
        Kind = kind;
        IntentKey = intentKey;
        _renewal = RenewUntilCompleteAsync();
    }

    public Guid OperationId { get; }
    public WorkerSessionOperationKind Kind { get; }
    public string IntentKey { get; }
    public bool IsCurrent =>
        Volatile.Read(ref _terminal) == 0 &&
        _owner.IsOperationCurrent(OperationId);

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0)
        {
            return;
        }

        _renewalCancellation.Cancel();
        await _owner.CompleteOperationAsync(OperationId, cancellationToken);
    }

    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0)
        {
            return;
        }

        _renewalCancellation.Cancel();
        await _owner.AbortOperationAsync(OperationId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) == 0)
        {
            _renewalCancellation.Cancel();
            await _owner.AbortOperationAsync(OperationId, CancellationToken.None);
        }

        try
        {
            await _renewal;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _renewalCancellation.Dispose();
        }
    }

    private async Task RenewUntilCompleteAsync()
    {
        using var timer = new PeriodicTimer(RenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_renewalCancellation.Token))
            {
                if (!await _owner.RenewOperationAsync(
                        OperationId,
                        _renewalCancellation.Token))
                {
                    Interlocked.CompareExchange(ref _terminal, 1, 0);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_renewalCancellation.IsCancellationRequested)
        {
        }
    }
}

public sealed class WorkerSessionOperationBusyException(string message)
    : InvalidOperationException(message);
