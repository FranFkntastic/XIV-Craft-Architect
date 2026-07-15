using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class ProcurementWorkflowService
{
    private readonly AppState _appState;
    private readonly IProcurementRouteExecutionService _procurementRouteExecutionService;
    private readonly MarketAnalysisItemRefreshService _itemRefreshService;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;

    public ProcurementWorkflowService(
        AppState appState,
        IProcurementRouteExecutionService procurementRouteExecutionService,
        MarketAnalysisItemRefreshService itemRefreshService,
        IRecipeLayerWorkflowService recipeLayerWorkflow)
    {
        _appState = appState;
        _procurementRouteExecutionService = procurementRouteExecutionService;
        _itemRefreshService = itemRefreshService;
        _recipeLayerWorkflow = recipeLayerWorkflow;
    }

    public async Task<ProcurementWorkflowResult> RunAnalysisAsync(
        ProcurementWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var plan = _appState.CurrentPlan;
        var planSessionVersion = _appState.PlanSessionVersion;
        if (plan == null)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.NoPlan);
        }

        var marketCandidates = await _recipeLayerWorkflow.BuildCurrentMarketAnalysisCandidatesAsync(plan, ct);
        if (marketCandidates == null)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.StalePlan);
        }

        var activeItemsList = marketCandidates
            .Where(item => item.TotalQuantity > 0)
            .ToList();
        if (activeItemsList.Count == 0)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.NoActiveProcurementItems);
        }

        if (string.IsNullOrWhiteSpace(_appState.SelectedDataCenter))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.MissingDataCenter);
        }

        var capturedDecisionVersion = _appState.CurrentVersions.PlanDecisionVersion;
        var capturedMarketAnalysisVersion = _appState.CurrentVersions.MarketAnalysisVersion;
        var scope = _appState.ProcurementSearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var requestSnapshot = CreateRequestSnapshot(scope);
        var guardedProgress = progress == null
            ? null
            : new Progress<string>(message =>
            {
                if (IsCurrent(plan, planSessionVersion, capturedDecisionVersion, requestSnapshot, request.IsCurrentOperation))
                {
                    progress.Report(message);
                }
            });

        var result = await _procurementRouteExecutionService.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                ActiveProcurementItems = activeItemsList,
                SourceShoppingPlans = _appState.ShoppingPlans,
                SourceMarketAnalyses = _appState.MarketItemAnalyses,
                Scope = scope,
                SelectedDataCenter = _appState.SelectedDataCenter,
                SelectedRegion = _appState.SelectedRegion,
                Lens = _appState.MarketAnalysisLens,
                ProcurementConfig = CreateProcurementMarketConfig(),
                IncludeSplitPurchases = _appState.ProcurementEnableSplitWorldPurchases,
                BlacklistedWorlds = _appState.GetActiveBlacklistedMarketWorlds(),
                ExcludedItemWorlds = _appState.TemporarilyExcludedItemWorlds,
                ExpectedWorldsByDataCenter = _appState.GetExpectedMarketWorlds(scope)
            },
            guardedProgress,
            ct,
            executionOptions: request.ExecutionOptions);

        if (!_appState.IsCurrentPlanSession(plan, planSessionVersion))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.StalePlan);
        }

        if (_appState.CurrentVersions.PlanDecisionVersion != capturedDecisionVersion)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.StaleDecision);
        }

        if (_appState.CurrentVersions.MarketAnalysisVersion != capturedMarketAnalysisVersion)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.StaleConfiguration);
        }

        if (!IsCurrentRequest(requestSnapshot))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.StaleConfiguration);
        }

        if (request.IsCurrentOperation?.Invoke() == false)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        var optimizedPlan = result.OptimizedPlan ?? plan;
        var optimizedActiveItems = result.ActiveProcurementItems
            ?? AcquisitionPlanningService.GetActiveProcurementItems(optimizedPlan);
        if (!_appState.ApplyProcurementOptimization(
                plan,
                optimizedPlan,
                optimizedActiveItems,
                result.ShoppingPlans,
                result.RouteDecision))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.StalePlan);
        }
        return new ProcurementWorkflowResult(ProcurementWorkflowStatus.Published, result.ShoppingPlans.Count);
    }

    public async Task<ProcurementItemRefreshWorkflowResult> RefreshItemMarketDataAsync(
        ProcurementItemRefreshWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var scope = _appState.ProcurementSearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var requestSnapshot = CreateRequestSnapshot(scope);
        var result = await _itemRefreshService.RefreshItemMarketDataAsync(
            new MarketAnalysisItemRefreshWorkflowRequest(
                request.ItemId,
                request.IsCurrentOperation,
                request.ExecutionOptions)
            {
                Scope = scope,
                IsCurrentConfiguration = () => IsCurrentRequest(requestSnapshot),
                TargetDataCenter = request.TargetDataCenter,
                TargetWorldName = request.TargetWorldName,
                ObservedEvidence = request.ObservedEvidence
            },
            progress,
            ct);

        return result.Status switch
        {
            MarketAnalysisItemRefreshStatus.Refreshed => new ProcurementItemRefreshWorkflowResult(
                ProcurementItemRefreshStatus.Refreshed,
                result.ItemName),
            MarketAnalysisItemRefreshStatus.NoPlan => ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.NoPlan),
            MarketAnalysisItemRefreshStatus.StalePlan => ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.StalePlan),
            MarketAnalysisItemRefreshStatus.NotFound => ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.NotFound),
            MarketAnalysisItemRefreshStatus.NoData => ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.NoData),
            MarketAnalysisItemRefreshStatus.StaleDecision => ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.StaleDecision),
            MarketAnalysisItemRefreshStatus.StaleConfiguration => ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.StaleConfiguration),
            MarketAnalysisItemRefreshStatus.Superseded => ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.Superseded),
            _ => ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.Superseded)
        };
    }

    private bool IsCurrent(
        CraftingPlan plan,
        long planSessionVersion,
        long capturedDecisionVersion,
        ProcurementRequestSnapshot requestSnapshot,
        Func<bool>? isCurrentOperation)
    {
        return _appState.CurrentVersions.PlanDecisionVersion == capturedDecisionVersion &&
               IsCurrentRequest(requestSnapshot) &&
               _appState.IsCurrentPlanSession(plan, planSessionVersion) &&
               (isCurrentOperation?.Invoke() ?? true);
    }

    private ProcurementRequestSnapshot CreateRequestSnapshot(MarketFetchScope scope)
    {
        return new ProcurementRequestSnapshot(
            scope,
            _appState.SelectedDataCenter,
            _appState.SelectedRegion,
            _appState.MarketAnalysisLens,
            _appState.ProcurementEnableSplitWorldPurchases,
            _appState.ProcurementTravelTolerance,
            _appState.ProcurementTravelPriority,
            _appState.ProcurementStartFromHomeDataCenter,
            _appState.GetActiveBlacklistedMarketWorlds(),
            _appState.TemporarilyExcludedItemWorlds.ToHashSet());
    }

    private bool IsCurrentRequest(ProcurementRequestSnapshot snapshot)
    {
        var currentScope = _appState.ProcurementSearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;

        return snapshot.Scope == currentScope &&
               string.Equals(snapshot.SelectedDataCenter, _appState.SelectedDataCenter, StringComparison.Ordinal) &&
               string.Equals(snapshot.SelectedRegion, _appState.SelectedRegion, StringComparison.Ordinal) &&
               snapshot.Lens == _appState.MarketAnalysisLens &&
               snapshot.IncludeSplitPurchases == _appState.ProcurementEnableSplitWorldPurchases &&
               snapshot.TravelTolerance == _appState.ProcurementTravelTolerance &&
               snapshot.TravelPriority == _appState.ProcurementTravelPriority &&
               snapshot.StartFromHomeDataCenter == _appState.ProcurementStartFromHomeDataCenter &&
               snapshot.BlacklistedWorlds.SetEquals(_appState.GetActiveBlacklistedMarketWorlds()) &&
               snapshot.ExcludedItemWorlds.SetEquals(_appState.TemporarilyExcludedItemWorlds);
    }

    private MarketAnalysisConfig CreateProcurementMarketConfig()
    {
        return new MarketAnalysisConfig
        {
            EnableSplitWorld = _appState.ProcurementEnableSplitWorldPurchases,
            MaxWorldsPerItem = null,
            TravelTolerance = _appState.ProcurementTravelTolerance,
            TravelPriority = _appState.ProcurementTravelPriority,
            StartFromHomeDataCenter = _appState.ProcurementStartFromHomeDataCenter
        };
    }

    private sealed record ProcurementRequestSnapshot(
        MarketFetchScope Scope,
        string SelectedDataCenter,
        string SelectedRegion,
        MarketAcquisitionLens Lens,
        bool IncludeSplitPurchases,
        int TravelTolerance,
        MarketTravelPriority TravelPriority,
        bool StartFromHomeDataCenter,
        HashSet<MarketWorldKey> BlacklistedWorlds,
        HashSet<MarketItemWorldKey> ExcludedItemWorlds);
}

public sealed record ProcurementWorkflowRequest(
    Func<bool>? IsCurrentOperation = null,
    MarketAnalysisExecutionOptions? ExecutionOptions = null);

public sealed record ProcurementWorkflowResult(
    ProcurementWorkflowStatus Status,
    int ShoppingPlanCount)
{
    public static ProcurementWorkflowResult Noop(ProcurementWorkflowStatus status)
    {
        return new ProcurementWorkflowResult(status, 0);
    }
}

public enum ProcurementWorkflowStatus
{
    NoPlan,
    NoActiveProcurementItems,
    MissingDataCenter,
    Published,
    StaleDecision,
    StaleConfiguration,
    StalePlan,
    Superseded
}

public sealed record ProcurementItemRefreshWorkflowRequest(
    int ItemId,
    string ItemName,
    Func<bool>? IsCurrentOperation = null,
    MarketAnalysisExecutionOptions? ExecutionOptions = null)
{
    public string? TargetDataCenter { get; init; }

    public string? TargetWorldName { get; init; }

    public MarketWorldEvidenceSnapshot? ObservedEvidence { get; init; }
}

public sealed record ProcurementItemRefreshWorkflowResult(
    ProcurementItemRefreshStatus Status,
    string? ItemName)
{
    public static ProcurementItemRefreshWorkflowResult Noop(ProcurementItemRefreshStatus status)
    {
        return new ProcurementItemRefreshWorkflowResult(status, null);
    }
}

public enum ProcurementItemRefreshStatus
{
    NoPlan,
    NotFound,
    NoData,
    Refreshed,
    StaleDecision,
    StaleConfiguration,
    StalePlan,
    Superseded
}
