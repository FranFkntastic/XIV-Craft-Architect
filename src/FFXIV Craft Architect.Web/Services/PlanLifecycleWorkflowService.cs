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
    bool SkipMarketRefresh = false,
    bool UseCurrentSettingsContext = false,
    MarketFetchScope? RequiredMarketScope = null);

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
        CancellationToken cancellationToken = default,
        Guid? operationId = null)
    {
        Cancel();
        var result = await _worker.BuildRecipeAsync(
            request,
            cancellationToken,
            operationId);
        if (result.Built && derivation == PlanDerivationDispatch.Background)
        {
            Schedule(new PlanDerivationRequest(
                UseCurrentSettingsContext: true));
        }

        return result;
    }

    public async Task ReplaceStoredPlanAsync(
        StoredPlan storedPlan,
        bool trackStoredPlanIdentity,
        PlanDerivationDispatch derivation = PlanDerivationDispatch.Background,
        CancellationToken cancellationToken = default,
        Guid? operationId = null,
        long? expectedWorkerRevision = null)
    {
        Cancel();
        await _worker.ReplaceStoredPlanAsync(
            storedPlan,
            trackStoredPlanIdentity,
            cancellationToken,
            operationId,
            expectedWorkerRevision);
        if (derivation == PlanDerivationDispatch.Background)
        {
            Schedule();
        }
    }

    public void Schedule(PlanDerivationRequest? request = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancellationTokenSource run;
        lock (_sync)
        {
            _currentRun?.Cancel();
            run = new CancellationTokenSource();
            _currentRun = run;
        }

        _ = RunInBackgroundAsync(run, request ?? new PlanDerivationRequest());
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
        Func<bool>? isCurrent = null,
        Guid? operationId = null)
    {
        WorkerSessionOperationLease? ownedOperation = null;
        try
        {
            if (operationId is null)
            {
                ownedOperation = await _worker.BeginOperationAsync(
                    WorkerSessionOperationKind.PlanDerivation,
                    $"plan-derivation:{_worker.CurrentRevision}",
                    "Updating plan prices and route...",
                    cancellationToken);
                operationId = ownedOperation.OperationId;
            }

            var result = await EnsureDerivedCoreAsync(
                request,
                operationId.Value,
                cancellationToken,
                reportStatus,
                () =>
                    (isCurrent?.Invoke() ?? true) &&
                    (ownedOperation?.IsCurrent ?? true));
            if (ownedOperation is not null)
            {
                await ownedOperation.CompleteAsync(cancellationToken);
            }
            return result;
        }
        catch
        {
            if (ownedOperation is not null)
            {
                await ownedOperation.AbortAsync(CancellationToken.None);
            }
            throw;
        }
        finally
        {
            if (ownedOperation is not null)
            {
                await ownedOperation.DisposeAsync();
            }
        }
    }

    private async Task<PlanDerivationResult> EnsureDerivedCoreAsync(
        PlanDerivationRequest request,
        Guid operationId,
        CancellationToken cancellationToken,
        Action<string, double?>? reportStatus,
        Func<bool>? isCurrent)
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
        var marketScope = request.RequiredMarketScope ??
            (request.UseCurrentSettingsContext
                ? _settings.DefaultMarketFetchScope
                : market?.Scope ?? _settings.DefaultMarketFetchScope);
        var selectedDataCenter = request.UseCurrentSettingsContext
            ? _settings.SelectedDataCenter
            : market?.SelectedDataCenter ?? _settings.SelectedDataCenter;
        var selectedRegion = request.UseCurrentSettingsContext
            ? _settings.SelectedRegion
            : market?.SelectedRegion ?? _settings.SelectedRegion;
        var selectedRegions = request.RequiredMarketScope == MarketFetchScope.EntireRegion
            ? MarketFetchScopeResolver.NormalizeSelectedRegions(selectedRegion, null)
            : request.UseCurrentSettingsContext
                ? _settings.AnalysisRegions
                : MarketFetchScopeResolver.ResolveRegionsForDataCenters(
                    market?.RequestedDataCenters ?? Array.Empty<string>(),
                    selectedRegion);
        var requestedDataCenters = MarketFetchScopeResolver.GetDataCenters(
            marketScope,
            selectedDataCenter,
            selectedRegion,
            selectedRegions);
        var marketContextChanged = market is not null &&
            (market.Scope != marketScope ||
             !string.Equals(
                 market.SelectedDataCenter,
                 selectedDataCenter,
                 StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(
                 market.SelectedRegion,
                 selectedRegion,
                 StringComparison.OrdinalIgnoreCase) ||
             !(market.RequestedDataCenters ?? Array.Empty<string>())
                 .ToHashSet(StringComparer.OrdinalIgnoreCase)
                 .SetEquals(requestedDataCenters));

        if (!hadMarketCandidates &&
            request.UseCurrentSettingsContext &&
            marketContextChanged)
        {
            await _worker.MutateActiveContextAsync(
                new WorkerActiveContextMutation(
                    selectedDataCenter,
                    selectedRegion,
                    marketScope),
                cancellationToken,
                operationId);
            market = await _worker.GetMarketProjectionAsync(cancellationToken);
            marketContextChanged = false;
        }

        if (hadMarketCandidates)
        {
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
                var requestedItemIds = request.MarketItemIdsToRefresh
                    .Distinct()
                    .ToArray();
                for (var itemIndex = 0; itemIndex < requestedItemIds.Length; itemIndex++)
                {
                    var itemId = requestedItemIds[itemIndex];
                    if (!candidates.TryGetValue(itemId, out var item))
                    {
                        continue;
                    }

                    try
                    {
                        reportStatus?.Invoke(
                            $"Refreshing {item.ItemName} ({itemIndex + 1:N0} of {requestedItemIds.Length:N0})...",
                            20 + (45d * (itemIndex + 1) / requestedItemIds.Length));
                        await _worker.RefreshMarketItemAsync(
                            new WorkerMarketItemRefreshRequest(
                                item.ItemId,
                                item.ItemName,
                                marketScope,
                                selectedDataCenter,
                                selectedRegion,
                                market?.Lens ?? MarketAcquisitionLens.MinimumUpfrontCost,
                                RequestedDataCenters: requestedDataCenters),
                            cancellationToken,
                            operationId);
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
            else if (request.ForceRefreshMarketData ||
                     market?.HasAnalysis != true ||
                     marketContextChanged)
            {
                var outcome = await _worker.RunMarketAnalysisAsync(
                    new WorkerMarketAnalysisRequest(
                        request.ForceRefreshMarketData,
                        marketScope,
                        selectedDataCenter,
                        selectedRegion,
                        market?.Lens ?? MarketAcquisitionLens.MinimumUpfrontCost,
                        SelectedRegions: selectedRegions),
                    cancellationToken,
                    reportStatus,
                    operationId);
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
            var requestedScope = marketScope;
            var routeIsCurrent = !marketChanged &&
                procurement is
                {
                    HasRoute: true
                } &&
                procurement.Scope == requestedScope &&
                procurement.TravelTolerance == _settings.ProcurementTravelTolerance &&
                (procurement.IncludeSplitPurchases is null ||
                 procurement.IncludeSplitPurchases == _settings.ProcurementEnableSplitWorldPurchases) &&
                procurement.TravelPriority == _settings.ProcurementTravelPriority &&
                procurement.RouteDecision?.StartsFromHomeDataCenter ==
                    _settings.ProcurementStartFromHomeDataCenter;

            if (routeIsCurrent)
            {
                procurementPublished = true;
            }
            else
            {
                reportStatus?.Invoke("Gathering available purchase options...", 65);
                try
                {
                    var procurementRegion =
                        market?.SelectedRegion ?? _settings.SelectedRegion;
                    var procurementDataCenter =
                        MarketFetchScopeResolver.ResolveValidDataCenter(
                            procurementRegion,
                            market?.SelectedDataCenter ?? _settings.SelectedDataCenter);
                    var outcome = await _worker.RunProcurementAsync(
                        new WorkerProcurementRequest(
                            requestedScope,
                            procurementDataCenter,
                            procurementRegion,
                            market?.Lens ?? MarketAcquisitionLens.MinimumUpfrontCost,
                            _settings.ProcurementTravelTolerance,
                            _settings.ProcurementEnableSplitWorldPurchases,
                            _settings.ProcurementStartFromHomeDataCenter,
                            _settings.ProcurementTravelPriority),
                        cancellationToken,
                        reportStatus,
                        operationId);
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

    private async Task RunInBackgroundAsync(
        CancellationTokenSource run,
        PlanDerivationRequest request)
    {
        using (run)
        using (var operation = _operations.Start(
            CancellableOperationWorkflow.PlanDerivation,
            "Plan pricing",
            "Checking what the plan needs to buy...",
            run.Token,
            announceImmediately: false))
        {
            try
            {
                await EnsureDerivedAsync(
                    request,
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
            catch (WorkerSessionOperationBusyException)
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
