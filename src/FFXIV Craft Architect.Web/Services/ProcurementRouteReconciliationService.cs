using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class ProcurementRouteReconciliationService : IDisposable
{
    private static readonly AppStateChangeScope RelevantScopes =
        AppStateChangeScope.PlanStructure |
        AppStateChangeScope.PlanDecision |
        AppStateChangeScope.MarketAnalysis |
        AppStateChangeScope.ProcurementOverlay |
        AppStateChangeScope.Settings;

    private readonly AppState _appState;
    private readonly IProcurementWorkflowService _procurementWorkflow;
    private readonly CancellableOperationService _cancellableOperations;
    private readonly ILogger<ProcurementRouteReconciliationService> _logger;
    private readonly TimeSpan _debounce;
    private CancellationTokenSource? _scheduledRepair;

    public bool IsScheduled => _scheduledRepair is not null;
    private int _generation;
    private bool _started;
    private bool _publishingRepairState;

    public ProcurementRouteReconciliationService(
        AppState appState,
        IProcurementWorkflowService procurementWorkflow,
        CancellableOperationService cancellableOperations,
        ILogger<ProcurementRouteReconciliationService> logger)
        : this(appState, procurementWorkflow, cancellableOperations, logger, TimeSpan.FromMilliseconds(250))
    {
    }

    public ProcurementRouteReconciliationService(
        AppState appState,
        IProcurementWorkflowService procurementWorkflow,
        CancellableOperationService cancellableOperations,
        ILogger<ProcurementRouteReconciliationService> logger,
        TimeSpan debounce)
    {
        _appState = appState;
        _procurementWorkflow = procurementWorkflow;
        _cancellableOperations = cancellableOperations;
        _logger = logger;
        _debounce = debounce;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _appState.OnStateChanged += OnStateChanged;
        ScheduleRepairIfNeeded();
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _appState.OnStateChanged -= OnStateChanged;
        Interlocked.Increment(ref _generation);
        _scheduledRepair?.Cancel();
        SetRepairState(false);
    }

    private void OnStateChanged(AppStateChange change)
    {
        if (_publishingRepairState || (change.Scopes & RelevantScopes) == AppStateChangeScope.None)
        {
            return;
        }

        ScheduleRepairIfNeeded();
    }

    private void ScheduleRepairIfNeeded()
    {
        if (!_started ||
            _appState.DeferAutomaticProcurementReconciliationForBenchmark ||
            !NeedsRouteReconciliation() ||
            !string.IsNullOrWhiteSpace(_appState.ProcurementRouteFailure))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        _scheduledRepair?.Cancel();
        var cancellation = new CancellationTokenSource();
        _scheduledRepair = cancellation;
        SetRepairState(true);
        _ = RunRepairAsync(generation, cancellation);
    }

    private async Task RunRepairAsync(int generation, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(_debounce, cancellation.Token);
            while (_appState.IsBusy)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }

            if (generation != _generation || !NeedsRouteReconciliation())
            {
                return;
            }

            var isInitialRoute = _appState.ProcurementRouteValidity == ProcurementRoutePublicationValidity.None;
            _logger.LogInformation("[stage] route reconciliation starting (initial={IsInitial})", isInitialRoute);
            using var operation = _cancellableOperations.Start(
                CancellableOperationWorkflow.ProcurementAnalysis,
                "Procurement Analysis",
                isInitialRoute ? "Generating procurement route..." : "Updating procurement route...",
                cancellation.Token);
            ProcurementWorkflowResult result;
            try
            {
                var progress = new Progress<string>(message => operation.ReportStatus(message));
                result = await _procurementWorkflow.RunAnalysisAsync(
                    new ProcurementWorkflowRequest(
                        IsCurrentOperation: () => operation.IsCurrent,
                        ExecutionOptions: MarketAnalysisExecutionOptions.Interactive),
                    progress,
                    operation.Token);
                _logger.LogInformation("[stage] route workflow returned (status={Status}, plans={Count})", result.Status, result.ShoppingPlanCount);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                operation.Cancel();
                throw;
            }
            catch
            {
                operation.Cancel();
                throw;
            }

            if (result.Status == ProcurementWorkflowStatus.Published)
            {
                operation.Complete(isInitialRoute ? "Procurement route generated." : "Procurement route updated.");
                return;
            }

            if (result.Status == ProcurementWorkflowStatus.NoCompleteRoute)
            {
                var message = result.Message ??
                    "CA could not build a complete route from the current listings and exclusions.";
                _appState.MarkProcurementRouteFailed(message);
                operation.Complete(message);
                return;
            }

            operation.Cancel();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Automatic procurement route reconciliation failed");
            _appState.MarkProcurementRouteFailed(
                "CA could not update the procurement route automatically. Open Procurement Plan to retry.");
        }
        finally
        {
            cancellation.Dispose();
            if (generation == _generation)
            {
                _scheduledRepair = null;
                SetRepairState(false);
            }
        }
    }

    private bool NeedsRouteReconciliation()
    {
        return _appState.ProcurementRouteValidity switch
        {
            ProcurementRoutePublicationValidity.SelectionChanged or
                ProcurementRoutePublicationValidity.InputsChanged => true,
            ProcurementRoutePublicationValidity.None => CanGenerateInitialRoute(),
            _ => false
        };
    }

    private bool CanGenerateInitialRoute()
    {
        return _appState.CurrentPlan != null &&
            _appState.ShoppingPlans.Count > 0 &&
            !_appState.IsMarketEvidenceHydrating &&
            !string.IsNullOrWhiteSpace(_appState.SelectedDataCenter);
    }

    private void SetRepairState(bool isReconciling)
    {
        _publishingRepairState = true;
        try
        {
            _appState.SetProcurementRouteReconciling(isReconciling);
        }
        finally
        {
            _publishingRepairState = false;
        }
    }
}
