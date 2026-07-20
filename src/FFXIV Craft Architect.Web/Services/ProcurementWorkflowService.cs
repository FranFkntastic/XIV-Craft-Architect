using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public interface IProcurementWorkflowService
{
    Task<ProcurementWorkflowResult> RunAnalysisAsync(
        ProcurementWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}

public sealed class ProcurementWorkflowService : IProcurementWorkflowService
{
    private readonly AppState _appState;
    private readonly ILogger<ProcurementWorkflowService>? _logger;
    private readonly IProcurementRouteExecutionService _procurementRouteExecutionService;
    private readonly MarketAnalysisItemRefreshService _itemRefreshService;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;

    public ProcurementWorkflowService(
        AppState appState,
        IProcurementRouteExecutionService procurementRouteExecutionService,
        MarketAnalysisItemRefreshService itemRefreshService,
        IRecipeLayerWorkflowService recipeLayerWorkflow,
        ILogger<ProcurementWorkflowService>? logger = null)
    {
        _appState = appState;
        _logger = logger;
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
        var requestSnapshot = _appState.CreateCurrentProcurementRouteBasis();
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

        if (result.ShoppingPlans.Count == 0)
        {
            return new ProcurementWorkflowResult(
                ProcurementWorkflowStatus.NoCompleteRoute,
                0,
                "No complete route could cover every purchase item with the current listings and exclusions. Refresh the affected items or clear temporary exclusions, then try again.");
        }

        _logger?.LogInformation(
            "[stage] route execution returned (plans={PlanCount}, routeDecision={HasRouteDecision}, activeItems={ActiveItemCount})",
            result.ShoppingPlans.Count,
            result.RouteDecision is not null,
            result.ActiveProcurementItems?.Count ?? activeItemsList.Count);
        var optimizedPlan = result.OptimizedPlan ?? plan;
        var optimizedActiveItems = result.ActiveProcurementItems
            ?? AcquisitionPlanningService.GetActiveProcurementItems(optimizedPlan);
        if (!_appState.ApplyProcurementOptimization(
                plan,
                optimizedPlan,
                optimizedActiveItems,
                result.ShoppingPlans,
                result.RouteDecision,
                result.EvidenceAnalyses,
                result.EvidencePlans,
                _appState.CreateMarketAnalysisScopeSnapshot(scope)))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.StalePlan);
        }
        _logger?.LogInformation(
            "[stage] route overlay published (validity={Validity}, decision={HasDecision}, basis={HasBasis})",
            _appState.ProcurementRouteValidity,
            _appState.ProcurementRouteDecision is not null,
            _appState.ProcurementRoutePublicationBasis is not null);
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
        var requestSnapshot = _appState.CreateCurrentProcurementRouteBasis();
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
        ProcurementRoutePublicationBasis requestSnapshot,
        Func<bool>? isCurrentOperation)
    {
        return _appState.CurrentVersions.PlanDecisionVersion == capturedDecisionVersion &&
               IsCurrentRequest(requestSnapshot) &&
               _appState.IsCurrentPlanSession(plan, planSessionVersion) &&
               (isCurrentOperation?.Invoke() ?? true);
    }

    private bool IsCurrentRequest(ProcurementRoutePublicationBasis snapshot)
    {
        var current = _appState.CreateCurrentProcurementRouteBasis();
        return snapshot.Matches(current);
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

}

public sealed record ProcurementWorkflowRequest(
    Func<bool>? IsCurrentOperation = null,
    MarketAnalysisExecutionOptions? ExecutionOptions = null);

public sealed record ProcurementWorkflowResult(
    ProcurementWorkflowStatus Status,
    int ShoppingPlanCount,
    string? Message = null)
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
    NoCompleteRoute,
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
