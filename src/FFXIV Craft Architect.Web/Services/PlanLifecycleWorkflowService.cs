using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Services;

public enum PlanDerivationDispatch
{
    Background,
    Deferred
}

public sealed record PlanDerivationRequest(
    bool ForceRefreshMarketData = false,
    IReadOnlyCollection<int>? MarketItemIdsToRefresh = null,
    bool SkipMarketRefresh = false);

public sealed record PlanDerivationResult(
    bool MarketPublished,
    bool HasMarketCandidates,
    int MarketItemsAnalyzed,
    int MarketEntriesFetched,
    bool ProcurementPublished,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Owns the complete lifecycle of a plan entering the Worker. Every plan
/// construction and replacement crosses this boundary, so derived market and
/// procurement state cannot depend on which screen initiated the command.
/// </summary>
public sealed class PlanLifecycleWorkflowService : IDisposable
{
    private readonly WorkerSessionCoordinator _worker;
    private readonly AppState _settings;
    private readonly CancellableOperationService _operations;
    private readonly ISnackbar _snackbar;
    private readonly ILogger<PlanLifecycleWorkflowService> _logger;
    private readonly object _sync = new();
    private CancellationTokenSource? _currentRun;
    private bool _disposed;

    public PlanLifecycleWorkflowService(
        WorkerSessionCoordinator worker,
        AppState settings,
        CancellableOperationService operations,
        ISnackbar snackbar,
        ILogger<PlanLifecycleWorkflowService> logger)
    {
        _worker = worker;
        _settings = settings;
        _operations = operations;
        _snackbar = snackbar;
        _logger = logger;
    }

    public async Task<WorkerRecipeBuildOutcome> BuildRecipeAsync(
        WorkerRecipeBuildRequest request,
        PlanDerivationDispatch derivation = PlanDerivationDispatch.Background,
        CancellationToken cancellationToken = default)
    {
        Cancel();
        var result = await _worker.BuildRecipeAsync(request, cancellationToken);
        if (result.Built && derivation == PlanDerivationDispatch.Background)
        {
            Schedule();
        }

        return result;
    }

    public async Task ReplaceStoredPlanAsync(
        StoredPlan storedPlan,
        bool trackStoredPlanIdentity,
        PlanDerivationDispatch derivation = PlanDerivationDispatch.Background,
        CancellationToken cancellationToken = default)
    {
        Cancel();
        await _worker.ReplaceStoredPlanAsync(
            storedPlan,
            trackStoredPlanIdentity,
            cancellationToken);
        if (derivation == PlanDerivationDispatch.Background)
        {
            Schedule();
        }
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

        _ = RunInBackgroundAsync(run);
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _currentRun?.Cancel();
        }
    }

    public async Task<PlanDerivationResult> EnsureDerivedAsync(
        PlanDerivationRequest request,
        CancellationToken cancellationToken = default,
        Action<string, double?>? reportStatus = null,
        Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        isCurrent ??= static () => true;

        var acquisition = await _worker.GetAcquisitionProjectionAsync(
            "All",
            cancellationToken);
        if (acquisition is not { HasPlan: true })
        {
            return EmptyResult();
        }

        var warnings = new List<string>();
        var hadMarketCandidates = acquisition.MarketCandidateCount > 0;
        var marketPublished = !hadMarketCandidates;
        var analyzedCount = 0;
        var fetchedCount = 0;
        var marketChanged = false;
        var market = await _worker.GetMarketProjectionAsync(cancellationToken);

        if (hadMarketCandidates)
        {
            reportStatus?.Invoke("Analyzing market prices...", 45);
            if (request.SkipMarketRefresh)
            {
                marketPublished = market?.HasAnalysis == true;
                analyzedCount = market?.AvailableCount ?? 0;
                if (!marketPublished)
                {
                    warnings.Add(
                        "Market evidence is not available. Run Market Analysis before using payment totals.");
                }
            }
            else if (request.MarketItemIdsToRefresh is { Count: > 0 })
            {
                var candidates = acquisition.Rows
                    .Where(row => row.IsMarketCandidate)
                    .ToDictionary(row => row.ItemId);
                foreach (var itemId in request.MarketItemIdsToRefresh.Distinct())
                {
                    if (!candidates.TryGetValue(itemId, out var item))
                    {
                        continue;
                    }

                    try
                    {
                        await _worker.RefreshMarketItemAsync(
                            new WorkerMarketItemRefreshRequest(
                                item.ItemId,
                                item.ItemName,
                                market?.Scope ?? _settings.DefaultMarketFetchScope,
                                market?.SelectedDataCenter ?? _settings.SelectedDataCenter,
                                market?.SelectedRegion ?? _settings.SelectedRegion,
                                market?.Lens ?? MarketAcquisitionLens.MinimumUpfrontCost),
                            cancellationToken);
                        analyzedCount++;
                        fetchedCount++;
                        marketChanged = true;
                    }
                    catch (InvalidOperationException ex)
                    {
                        warnings.Add(
                            $"Market refresh for {item.ItemName} did not publish: {ex.Message}");
                    }
                }

                market = await _worker.GetMarketProjectionAsync(cancellationToken);
                marketPublished = market?.HasAnalysis == true;
            }
            else if (request.ForceRefreshMarketData || market?.HasAnalysis != true)
            {
                var outcome = await _worker.RunMarketAnalysisAsync(
                    new WorkerMarketAnalysisRequest(
                        request.ForceRefreshMarketData,
                        market?.Scope ?? _settings.DefaultMarketFetchScope,
                        market?.SelectedDataCenter ?? _settings.SelectedDataCenter,
                        market?.SelectedRegion ?? _settings.SelectedRegion,
                        market?.Lens ?? MarketAcquisitionLens.MinimumUpfrontCost),
                    cancellationToken);
                market = outcome.Market;
                marketPublished = outcome.Published;
                analyzedCount = outcome.AnalyzedCount;
                fetchedCount = outcome.FetchedCount;
                marketChanged = outcome.Published;
            }
            else
            {
                marketPublished = true;
                analyzedCount = market.AvailableCount;
            }
        }

        if (!isCurrent())
        {
            cancellationToken.ThrowIfCancellationRequested();
            return EmptyResult();
        }

        acquisition = await _worker.GetAcquisitionProjectionAsync(
            "All",
            cancellationToken);
        if (acquisition is not { HasPlan: true })
        {
            return EmptyResult();
        }

        var procurementPublished = acquisition.ActiveProcurementCount == 0;
        if (acquisition.ActiveProcurementCount > 0 &&
            (marketPublished || !hadMarketCandidates) &&
            !_settings.DeferAutomaticProcurementReconciliationForBenchmark)
        {
            var procurement = await _worker.GetProcurementProjectionAsync(
                cancellationToken);
            var requestedScope = _settings.ProcurementSearchEntireRegion
                ? MarketFetchScope.EntireRegion
                : MarketFetchScope.SelectedDataCenter;
            var routeIsCurrent = !marketChanged &&
                procurement is
                {
                    HasRoute: true
                } &&
                procurement.Scope == requestedScope &&
                procurement.TravelTolerance == _settings.ProcurementTravelTolerance &&
                procurement.IncludeSplitPurchases == _settings.ProcurementEnableSplitWorldPurchases &&
                procurement.TravelPriority == _settings.ProcurementTravelPriority;

            if (routeIsCurrent)
            {
                procurementPublished = true;
            }
            else
            {
                reportStatus?.Invoke("Building procurement route...", 75);
                try
                {
                    var outcome = await _worker.RunProcurementAsync(
                        new WorkerProcurementRequest(
                            requestedScope,
                            market?.SelectedDataCenter ?? _settings.SelectedDataCenter,
                            market?.SelectedRegion ?? _settings.SelectedRegion,
                            market?.Lens ?? MarketAcquisitionLens.MinimumUpfrontCost,
                            _settings.ProcurementTravelTolerance,
                            _settings.ProcurementEnableSplitWorldPurchases,
                            _settings.ProcurementStartFromHomeDataCenter,
                            _settings.ProcurementTravelPriority),
                        cancellationToken);
                    procurementPublished = outcome.Procurement.HasRoute;
                }
                catch (InvalidOperationException ex)
                {
                    warnings.Add($"Procurement route was not published: {ex.Message}");
                }
            }
        }

        return new PlanDerivationResult(
            marketPublished,
            hadMarketCandidates,
            analyzedCount,
            fetchedCount,
            procurementPublished,
            warnings);
    }

    private async Task RunInBackgroundAsync(CancellationTokenSource run)
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

                await EnsureDerivedAsync(
                    new PlanDerivationRequest(),
                    operation.Token,
                    (message, progress) => operation.ReportStatus(
                        message,
                        progress: progress),
                    () => operation.IsCurrent);
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
                    "Automatic market analysis or procurement failed after a plan lifecycle command.");
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

    private static PlanDerivationResult EmptyResult() =>
        new(
            MarketPublished: false,
            HasMarketCandidates: false,
            MarketItemsAnalyzed: 0,
            MarketEntriesFetched: 0,
            ProcurementPublished: false,
            Warnings: []);

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
