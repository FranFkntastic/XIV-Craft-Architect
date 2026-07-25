using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Services;

/// <summary>
/// Explicitly completes the work derived from a successful recipe build.
/// This is command orchestration, not state-change inference: callers decide
/// when a newly built plan should be priced and routed.
/// </summary>
public sealed class AutomaticPlanDerivationService : IDisposable
{
    private readonly WorkerSessionCoordinator _worker;
    private readonly AppState _settings;
    private readonly CancellableOperationService _operations;
    private readonly ISnackbar _snackbar;
    private readonly ILogger<AutomaticPlanDerivationService> _logger;
    private readonly object _sync = new();
    private CancellationTokenSource? _currentRun;
    private bool _disposed;

    public AutomaticPlanDerivationService(
        WorkerSessionCoordinator worker,
        AppState settings,
        CancellableOperationService operations,
        ISnackbar snackbar,
        ILogger<AutomaticPlanDerivationService> logger)
    {
        _worker = worker;
        _settings = settings;
        _operations = operations;
        _snackbar = snackbar;
        _logger = logger;
    }

    public void Schedule()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancellationTokenSource run;
        lock (_sync)
        {
            _currentRun?.Cancel();
            run = new CancellationTokenSource();
            _currentRun = run;
        }

        _ = RunAsync(run);
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _currentRun?.Cancel();
        }
    }

    private async Task RunAsync(CancellationTokenSource run)
    {
        using (run)
        using (var operation = _operations.Start(
            CancellableOperationWorkflow.PlanDerivation,
            "Plan pricing",
            "Analyzing market prices...",
            run.Token))
        {
            try
            {
                if (_settings.DeferAutomaticMarketAnalysisForBenchmark)
                {
                    operation.Cancel();
                    return;
                }

                var market = await _worker.GetMarketProjectionAsync(operation.Token);
                if (market is null || market.CandidateCount == 0)
                {
                    operation.Complete("Ready");
                    return;
                }

                var marketOutcome = await _worker.RunMarketAnalysisAsync(
                    new WorkerMarketAnalysisRequest(
                        ForceRefreshData: false,
                        market.Scope,
                        market.SelectedDataCenter,
                        market.SelectedRegion,
                        market.Lens),
                    operation.Token);
                if (!operation.IsCurrent ||
                    !marketOutcome.Published ||
                    _settings.DeferAutomaticProcurementReconciliationForBenchmark)
                {
                    operation.Complete("Ready");
                    return;
                }

                operation.ReportStatus(
                    "Building procurement route...",
                    progress: 75);
                await _worker.RunProcurementAsync(
                    new WorkerProcurementRequest(
                        _settings.ProcurementSearchEntireRegion
                            ? MarketFetchScope.EntireRegion
                            : MarketFetchScope.SelectedDataCenter,
                        marketOutcome.Market.SelectedDataCenter,
                        marketOutcome.Market.SelectedRegion,
                        marketOutcome.Market.Lens,
                        _settings.ProcurementTravelTolerance,
                        _settings.ProcurementEnableSplitWorldPurchases,
                        _settings.ProcurementStartFromHomeDataCenter,
                        _settings.ProcurementTravelPriority),
                    operation.Token);
                operation.Complete("Ready");
            }
            catch (OperationCanceledException) when (run.IsCancellationRequested)
            {
                operation.Cancel();
            }
            catch (Exception ex) when (operation.ShouldReportError(ex))
            {
                operation.Complete("Automatic plan pricing failed.");
                _logger.LogError(
                    ex,
                    "Automatic market analysis or procurement failed after a recipe build.");
                _snackbar.Add(
                    "Automatic plan pricing failed. Run Market Analysis to retry.",
                    Severity.Error);
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_currentRun, run))
                    {
                        _currentRun = null;
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
    }
}
