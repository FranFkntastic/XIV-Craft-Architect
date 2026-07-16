using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class MarketEvidenceHydrationService : IDisposable
{
    private const int RefreshAttempts = 2;
    private static readonly TimeSpan RefreshRetryDelay = TimeSpan.FromSeconds(1);

    private readonly AppState _appState;
    private readonly MarketAnalysisWorkflowService _workflow;
    private readonly ILogger<MarketEvidenceHydrationService> _logger;
    private CancellationTokenSource? _cancellation;
    private long _generation;

    public MarketEvidenceHydrationRunSnapshot LastRun { get; private set; } =
        MarketEvidenceHydrationRunSnapshot.None;

    public MarketEvidenceHydrationService(
        AppState appState,
        MarketAnalysisWorkflowService workflow,
        ILogger<MarketEvidenceHydrationService> logger)
    {
        _appState = appState;
        _workflow = workflow;
        _logger = logger;
        _appState.OnStateChanged += OnAppStateChanged;
    }

    public void ScheduleAfterPlanLoad(PlanSessionLoadResult session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        var generation = ++_generation;

        if (!NeedsHydration(session, DateTime.UtcNow))
        {
            _appState.SetMarketEvidenceHydrating(false);
            LastRun = CreateSnapshot(
                session,
                MarketEvidenceHydrationStatus.NotNeeded,
                startedAtUtc: DateTime.UtcNow,
                completedAtUtc: DateTime.UtcNow,
                message: "Loaded market evidence was already reusable.");
            return;
        }

        var plan = session.Plan!;
        var planSessionVersion = _appState.PlanSessionVersion;
        var startedAtUtc = DateTime.UtcNow;
        var forceRefreshData = HasReusablePublishedEvidence(session);
        _cancellation = new CancellationTokenSource();
        LastRun = CreateSnapshot(
            session,
            MarketEvidenceHydrationStatus.Running,
            startedAtUtc,
            message: "Refreshing market evidence after plan load.");
        _appState.SetMarketEvidenceHydrating(true);
        _ = HydrateAsync(
            plan,
            planSessionVersion,
            generation,
            startedAtUtc,
            forceRefreshData,
            _cancellation.Token);
    }

    public void ScheduleForCurrentPlan()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        var generation = ++_generation;
        var plan = _appState.CurrentPlan;
        var nowUtc = DateTime.UtcNow;
        if (plan == null || !HasMarketCandidate(plan))
        {
            _appState.SetMarketEvidenceHydrating(false);
            LastRun = CreateCurrentSnapshot(
                MarketEvidenceHydrationStatus.NotNeeded,
                nowUtc,
                nowUtc,
                "The active plan has no market candidates.");
            return;
        }

        var hasPublishedEvidence = _appState.ShoppingPlans.Count > 0 &&
                                   _appState.MarketItemAnalyses.Count > 0;
        var publishedAtUtc = _appState.PublishedMarketAnalysisScope?.PublishedAtUtc;
        var isStale = publishedAtUtc == null ||
                      nowUtc - publishedAtUtc.Value > MarketEvidencePolicyDefaults.ReusableCacheMaxAge;
        if (hasPublishedEvidence && !isStale)
        {
            _appState.SetMarketEvidenceHydrating(false);
            LastRun = CreateCurrentSnapshot(
                MarketEvidenceHydrationStatus.NotNeeded,
                nowUtc,
                nowUtc,
                "Current market evidence is already reusable.");
            return;
        }

        var startedAtUtc = DateTime.UtcNow;
        _cancellation = new CancellationTokenSource();
        LastRun = CreateCurrentSnapshot(
            MarketEvidenceHydrationStatus.Running,
            startedAtUtc,
            message: hasPublishedEvidence
                ? "Refreshing stale market evidence for the active plan."
                : "Building actionable market evidence for the active plan.");
        _appState.SetMarketEvidenceHydrating(true);
        _ = HydrateAsync(
            plan,
            _appState.PlanSessionVersion,
            generation,
            startedAtUtc,
            forceRefreshData: hasPublishedEvidence,
            _cancellation.Token);
    }

    public static bool NeedsHydration(PlanSessionLoadResult session, DateTime nowUtc)
    {
        if (session.Plan == null || !HasMarketCandidate(session.Plan))
        {
            return false;
        }

        var hasActionableEvidence = session.ShoppingPlans.Count > 0 &&
                                    session.MarketItemAnalyses.Count > 0;
        if (!hasActionableEvidence)
        {
            return true;
        }

        var publishedAtUtc = session.PublishedMarketAnalysisScope?.PublishedAtUtc;
        return publishedAtUtc == null ||
               nowUtc - publishedAtUtc.Value > MarketEvidencePolicyDefaults.ReusableCacheMaxAge;
    }

    public static MarketAnalysisWorkflowRequest CreateRefreshRequest()
    {
        return CreateRefreshRequest(forceRefreshData: true);
    }

    public static MarketAnalysisWorkflowRequest CreateRefreshRequest(bool forceRefreshData)
    {
        return new MarketAnalysisWorkflowRequest(
            ForceRefreshData: forceRefreshData,
            PreserveExistingEvidence: true);
    }

    private static bool HasReusablePublishedEvidence(PlanSessionLoadResult session)
    {
        return session.ShoppingPlans.Count > 0 && session.MarketItemAnalyses.Count > 0;
    }

    private static bool HasMarketCandidate(CraftingPlan plan)
    {
        return plan.RootItems.Any(HasMarketCandidate);
    }

    private static bool HasMarketCandidate(PlanNode node)
    {
        return node.CanBuyFromMarket || node.Children.Any(HasMarketCandidate);
    }

    private async Task HydrateAsync(
        CraftingPlan plan,
        long planSessionVersion,
        long generation,
        DateTime startedAtUtc,
        bool forceRefreshData,
        CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
            {
                CompleteRun(
                    generation,
                    MarketEvidenceHydrationStatus.Superseded,
                    startedAtUtc,
                    0,
                    null,
                    "The active plan changed before refresh began.");
                return;
            }

            for (var attempt = 1; attempt <= RefreshAttempts; attempt++)
            {
                var result = await _workflow.RunAnalysisAsync(
                    CreateRefreshRequest(forceRefreshData || attempt > 1),
                    ct: ct);
                UpdateRunningAttempt(generation, startedAtUtc, attempt, result);
                if (result.Published)
                {
                    CompleteRun(
                        generation,
                        MarketEvidenceHydrationStatus.Published,
                        startedAtUtc,
                        attempt,
                        result,
                        $"Published actionable market evidence for {result.AnalyzedCount:N0} item(s).");
                    return;
                }

                if (attempt < RefreshAttempts)
                {
                    _logger.LogWarning(
                        "Automatic market price refresh did not publish a result; retrying ({Attempt}/{Attempts})",
                        attempt,
                        RefreshAttempts);
                    await Task.Delay(RefreshRetryDelay, ct);
                    if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
                    {
                        CompleteRun(
                            generation,
                            MarketEvidenceHydrationStatus.Superseded,
                            startedAtUtc,
                            attempt,
                            result,
                            "The active plan changed while market evidence was refreshing.");
                        return;
                    }
                }
            }

            _logger.LogWarning(
                "Automatic market price refresh did not publish a result after {Attempts} attempts",
                RefreshAttempts);
            CompleteRun(
                generation,
                MarketEvidenceHydrationStatus.NotPublished,
                startedAtUtc,
                RefreshAttempts,
                LastRun.Result,
                "Refresh completed without publishing market evidence.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CompleteRun(
                generation,
                MarketEvidenceHydrationStatus.Canceled,
                startedAtUtc,
                LastRun.AttemptCount,
                LastRun.Result,
                "Automatic market refresh was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatic market price refresh failed");
            CompleteRun(
                generation,
                MarketEvidenceHydrationStatus.Failed,
                startedAtUtc,
                LastRun.AttemptCount,
                LastRun.Result,
                ex.Message,
                ex.GetType().FullName);
        }
        finally
        {
            if (generation == _generation)
            {
                _appState.SetMarketEvidenceHydrating(false);
            }
        }
    }

    public void Dispose()
    {
        _appState.OnStateChanged -= OnAppStateChanged;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }

    private void OnAppStateChanged(AppStateChange change)
    {
        if (!change.HasScope(AppStateChangeScope.Settings) ||
            string.IsNullOrWhiteSpace(_appState.MarketAnalysisScopeWarning))
        {
            return;
        }

        ScheduleForCurrentPlan();
    }

    private void UpdateRunningAttempt(
        long generation,
        DateTime startedAtUtc,
        int attempt,
        MarketAnalysisWorkflowResult result)
    {
        if (generation != _generation)
        {
            return;
        }

        LastRun = LastRun with
        {
            Status = MarketEvidenceHydrationStatus.Running,
            StartedAtUtc = startedAtUtc,
            AttemptCount = attempt,
            Result = result,
            Message = result.Published
                ? "Market evidence was published."
                : $"Attempt {attempt:N0} did not publish market evidence."
        };
    }

    private void CompleteRun(
        long generation,
        MarketEvidenceHydrationStatus status,
        DateTime startedAtUtc,
        int attemptCount,
        MarketAnalysisWorkflowResult? result,
        string message,
        string? exceptionType = null)
    {
        if (generation != _generation)
        {
            return;
        }

        LastRun = LastRun with
        {
            Status = status,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = DateTime.UtcNow,
            AttemptCount = attemptCount,
            Result = result,
            Message = message,
            ExceptionType = exceptionType
        };
    }

    private MarketEvidenceHydrationRunSnapshot CreateSnapshot(
        PlanSessionLoadResult session,
        MarketEvidenceHydrationStatus status,
        DateTime startedAtUtc,
        DateTime? completedAtUtc = null,
        string? message = null)
    {
        return new MarketEvidenceHydrationRunSnapshot(
            status,
            _appState.PlanSessionVersion,
            _appState.CurrentPlanId,
            _appState.CurrentPlanName ?? session.Plan?.Name,
            startedAtUtc,
            completedAtUtc,
            0,
            null,
            message,
            null);
    }

    private MarketEvidenceHydrationRunSnapshot CreateCurrentSnapshot(
        MarketEvidenceHydrationStatus status,
        DateTime startedAtUtc,
        DateTime? completedAtUtc = null,
        string? message = null)
    {
        return new MarketEvidenceHydrationRunSnapshot(
            status,
            _appState.PlanSessionVersion,
            _appState.CurrentPlanId,
            _appState.CurrentPlanName ?? _appState.CurrentPlan?.Name,
            startedAtUtc,
            completedAtUtc,
            0,
            null,
            message,
            null);
    }
}

public enum MarketEvidenceHydrationStatus
{
    None,
    NotNeeded,
    Running,
    Published,
    NotPublished,
    Failed,
    Canceled,
    Superseded
}

public sealed record MarketEvidenceHydrationRunSnapshot(
    MarketEvidenceHydrationStatus Status,
    long PlanSessionVersion,
    string? PlanId,
    string? PlanName,
    DateTime? StartedAtUtc,
    DateTime? CompletedAtUtc,
    int AttemptCount,
    MarketAnalysisWorkflowResult? Result,
    string? Message,
    string? ExceptionType)
{
    public static MarketEvidenceHydrationRunSnapshot None { get; } = new(
        MarketEvidenceHydrationStatus.None,
        0,
        null,
        null,
        null,
        null,
        0,
        null,
        "No automatic market refresh has run in this browser session.",
        null);

    public TimeSpan? Duration => StartedAtUtc.HasValue && CompletedAtUtc.HasValue
        ? CompletedAtUtc.Value - StartedAtUtc.Value
        : null;
}
