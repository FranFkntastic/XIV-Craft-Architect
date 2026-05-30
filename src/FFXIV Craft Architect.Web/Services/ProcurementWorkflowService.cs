using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class ProcurementWorkflowService
{
    private readonly AppState _appState;
    private readonly IProcurementRouteExecutionService _procurementRouteExecutionService;
    private readonly IMarketAnalysisExecutionService _marketAnalysisExecutionService;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly WebPlanPersistenceService _planPersistence;

    public ProcurementWorkflowService(
        AppState appState,
        IProcurementRouteExecutionService procurementRouteExecutionService,
        IMarketAnalysisExecutionService marketAnalysisExecutionService,
        MarketShoppingService marketShoppingService,
        WebPlanPersistenceService planPersistence)
    {
        _appState = appState;
        _procurementRouteExecutionService = procurementRouteExecutionService;
        _marketAnalysisExecutionService = marketAnalysisExecutionService;
        _marketShoppingService = marketShoppingService;
        _planPersistence = planPersistence;
    }

    public async Task<ProcurementWorkflowResult> RunAnalysisAsync(
        ProcurementWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var plan = _appState.CurrentPlan;
        if (plan == null)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.NoPlan);
        }

        var activeItems = AcquisitionPlanningService.GetActiveProcurementItems(plan)
            .Where(item => item.TotalQuantity > 0)
            .ToList();
        if (activeItems.Count == 0)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.NoActiveProcurementItems);
        }

        if (string.IsNullOrWhiteSpace(_appState.SelectedDataCenter))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.MissingDataCenter);
        }

        var capturedDecisionVersion = _appState.CurrentVersions.PlanDecisionVersion;
        var scope = _appState.ProcurementSearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var guardedProgress = progress == null
            ? null
            : new Progress<string>(message =>
            {
                if (IsCurrent(plan, capturedDecisionVersion, request.IsCurrentOperation))
                {
                    progress.Report(message);
                }
            });

        var result = await _procurementRouteExecutionService.AnalyzeAsync(
            new ProcurementRouteExecutionRequest
            {
                Plan = plan,
                SourceShoppingPlans = _appState.ShoppingPlans,
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

        if (_appState.CurrentVersions.PlanDecisionVersion != capturedDecisionVersion)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.StaleDecision);
        }

        if (!ReferenceEquals(_appState.CurrentPlan, plan))
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.StalePlan);
        }

        if (request.IsCurrentOperation?.Invoke() == false)
        {
            return ProcurementWorkflowResult.Noop(ProcurementWorkflowStatus.Superseded);
        }

        _appState.ReplaceProcurementOverlay(result.ShoppingPlans);
        return new ProcurementWorkflowResult(ProcurementWorkflowStatus.Published, result.ShoppingPlans.Count);
    }

    public async Task<ProcurementItemRefreshWorkflowResult> RefreshItemMarketDataAsync(
        ProcurementItemRefreshWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var plan = _appState.CurrentPlan;
        if (plan == null)
        {
            return ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.NoPlan);
        }

        var candidate = AcquisitionPlanningService.GetMarketAnalysisCandidates(plan)
            .FirstOrDefault(item => item.ItemId == request.ItemId);
        if (candidate == null)
        {
            return ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.NotFound);
        }

        var capturedPlanId = _appState.CurrentPlanId;
        var capturedDecisionVersion = _appState.CurrentVersions.PlanDecisionVersion;
        var scope = _appState.ProcurementSearchEntireRegion
            ? MarketFetchScope.EntireRegion
            : MarketFetchScope.SelectedDataCenter;
        var guardedProgress = progress == null
            ? null
            : new Progress<string>(message =>
            {
                if (ReferenceEquals(_appState.CurrentPlan, plan) &&
                    (request.IsCurrentOperation?.Invoke() ?? true))
                {
                    progress.Report(message);
                }
            });

        var result = await _marketAnalysisExecutionService.ExecuteAsync(
            new MarketAnalysisExecutionRequest
            {
                Items = [candidate],
                Scope = scope,
                SelectedDataCenter = _appState.SelectedDataCenter,
                SelectedRegion = _appState.SelectedRegion,
                MaxAge = TimeSpan.Zero,
                RecommendationMode = RecommendationMode.MinimizeTotalCost,
                Lens = _appState.MarketAnalysisLens,
                ExpectedWorldsByDataCenter = _appState.GetExpectedMarketWorlds(scope)
            },
            guardedProgress,
            ct,
            executionOptions: request.ExecutionOptions);

        if (!ReferenceEquals(_appState.CurrentPlan, plan))
        {
            return ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.StalePlan);
        }

        if (_appState.CurrentVersions.PlanDecisionVersion != capturedDecisionVersion)
        {
            return ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.StaleDecision);
        }

        if (request.IsCurrentOperation?.Invoke() == false)
        {
            return ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.Superseded);
        }

        var refreshedAnalysis = result.Analyses.SingleOrDefault();
        if (refreshedAnalysis == null || result.ShoppingPlans.Count == 0)
        {
            return ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.NoData);
        }

        var refreshedPlans = new List<DetailedShoppingPlan> { result.ShoppingPlans.Single() };
        _marketShoppingService.ApplyVendorPurchaseOverrides(plan, refreshedPlans);
        _appState.ReplaceMarketAnalysisItem(refreshedAnalysis, refreshedPlans[0]);

        if (!string.IsNullOrEmpty(capturedPlanId) &&
            ReferenceEquals(_appState.CurrentPlan, plan) &&
            string.Equals(_appState.CurrentPlanId, capturedPlanId, StringComparison.Ordinal))
        {
            await _planPersistence.SaveMarketAnalysisAsync(
                capturedPlanId,
                _appState.ShoppingPlans,
                _appState.MarketItemAnalyses,
                _appState.RecommendationMode,
                _appState.MarketAnalysisLens);
        }

        if (!ReferenceEquals(_appState.CurrentPlan, plan))
        {
            return ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.StalePlan);
        }

        if (request.IsCurrentOperation?.Invoke() == false)
        {
            return ProcurementItemRefreshWorkflowResult.Noop(ProcurementItemRefreshStatus.Superseded);
        }

        return new ProcurementItemRefreshWorkflowResult(
            ProcurementItemRefreshStatus.Refreshed,
            candidate.Name);
    }

    private bool IsCurrent(
        CraftingPlan plan,
        long capturedDecisionVersion,
        Func<bool>? isCurrentOperation)
    {
        return _appState.CurrentVersions.PlanDecisionVersion == capturedDecisionVersion &&
               ReferenceEquals(_appState.CurrentPlan, plan) &&
               (isCurrentOperation?.Invoke() ?? true);
    }

    private MarketAnalysisConfig CreateProcurementMarketConfig()
    {
        return new MarketAnalysisConfig
        {
            EnableSplitWorld = _appState.ProcurementEnableSplitWorldPurchases,
            MaxWorldsPerItem = null,
            TravelTolerance = _appState.ProcurementTravelTolerance
        };
    }
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
    StalePlan,
    Superseded
}

public sealed record ProcurementItemRefreshWorkflowRequest(
    int ItemId,
    string ItemName,
    Func<bool>? IsCurrentOperation = null,
    MarketAnalysisExecutionOptions? ExecutionOptions = null);

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
    StalePlan,
    Superseded
}
