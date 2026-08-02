using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services;

public enum TradeOrderPlanMissingDisposition
{
    WaitForHostedPlan,
    RetryExactPlanRead,
    ExactPlanUnavailable
}

public enum TradeOrderPlanReadOutcome
{
    Loaded,
    WaitForHostedPlan,
    ExactPlanUnavailable,
    RequestSuperseded
}

public sealed record TradeOrderPlanReadResult<T>(
    TradeOrderPlanReadOutcome Outcome,
    T? Payload,
    int Attempts,
    Exception? LastException = null)
    where T : class;

public readonly record struct TradeOrderPlanRestoreRequest(
    long Generation,
    Guid OrderId,
    string PlanId,
    long WorkerRevision);

public static class TradeOrderPlanRestorePolicy
{
    public const int MaximumExactPlanReadAttempts = 3;

    public static bool IsCurrent(
        TradeOrderPlanRestoreRequest request,
        long currentGeneration,
        Guid? selectedOrderId,
        string? selectedPlanId,
        int activeTab,
        int planTab,
        bool disposed) =>
        !disposed &&
        request.Generation == currentGeneration &&
        selectedOrderId == request.OrderId &&
        activeTab == planTab &&
        string.Equals(request.PlanId, selectedPlanId, StringComparison.Ordinal);

    public static bool CanAdoptExactPlan(
        TradeOrderPlanRestoreRequest request,
        long currentGeneration,
        Guid? selectedOrderId,
        string? selectedPlanId,
        int activeTab,
        int planTab,
        bool disposed,
        long currentWorkerRevision) =>
        IsCurrent(
            request,
            currentGeneration,
            selectedOrderId,
            selectedPlanId,
            activeTab,
            planTab,
            disposed) &&
        currentWorkerRevision == request.WorkerRevision;

    public static TradeOrderPlanMissingDisposition ResolveMissingExactPlan(
        bool waitsForProfilePlanAuthority,
        ProfileSyncStatus status,
        int attempt)
    {
        if (waitsForProfilePlanAuthority && status.Stage is
                ProfileSyncStage.ReadingLocalState or
                ProfileSyncStage.DownloadingChanges or
                ProfileSyncStage.ApplyingChanges or
                ProfileSyncStage.PublishingLocalChanges)
        {
            return TradeOrderPlanMissingDisposition.WaitForHostedPlan;
        }

        return attempt < MaximumExactPlanReadAttempts
            ? TradeOrderPlanMissingDisposition.RetryExactPlanRead
            : TradeOrderPlanMissingDisposition.ExactPlanUnavailable;
    }

    public static TimeSpan GetRetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(750, 150 * Math.Max(attempt, 1)));

    public static async Task<TradeOrderPlanReadResult<T>> ReadExactPlanAsync<T>(
        Func<CancellationToken, Task<T?>> loadExactPlan,
        Func<ProfileSyncStatus> getSyncStatus,
        bool waitsForProfilePlanAuthority,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<bool>? canContinue = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(loadExactPlan);
        ArgumentNullException.ThrowIfNull(getSyncStatus);
        delay ??= Task.Delay;

        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaximumExactPlanReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (canContinue != null && !canContinue())
            {
                return Superseded<T>(attempt - 1);
            }

            T? payload = null;
            try
            {
                payload = await loadExactPlan(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                lastException = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            if (canContinue != null && !canContinue())
            {
                return Superseded<T>(attempt);
            }

            if (payload != null)
            {
                return new TradeOrderPlanReadResult<T>(
                    TradeOrderPlanReadOutcome.Loaded,
                    payload,
                    attempt);
            }

            if (lastException == null &&
                ResolveMissingExactPlan(
                    waitsForProfilePlanAuthority,
                    getSyncStatus(),
                    attempt) == TradeOrderPlanMissingDisposition.WaitForHostedPlan)
            {
                return new TradeOrderPlanReadResult<T>(
                    TradeOrderPlanReadOutcome.WaitForHostedPlan,
                    null,
                    attempt);
            }

            if (attempt < MaximumExactPlanReadAttempts)
            {
                await delay(GetRetryDelay(attempt), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (canContinue != null && !canContinue())
                {
                    return Superseded<T>(attempt);
                }
            }
        }

        return new TradeOrderPlanReadResult<T>(
            TradeOrderPlanReadOutcome.ExactPlanUnavailable,
            null,
            MaximumExactPlanReadAttempts,
            lastException);
    }

    private static TradeOrderPlanReadResult<T> Superseded<T>(int attempts)
        where T : class =>
        new(TradeOrderPlanReadOutcome.RequestSuperseded, null, attempts);
}
