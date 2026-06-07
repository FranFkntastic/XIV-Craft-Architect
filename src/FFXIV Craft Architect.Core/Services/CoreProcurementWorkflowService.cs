using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record CoreProcurementWorkflowRequest(
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    MarketAnalysisConfig ProcurementConfig,
    bool IncludeSplitPurchases,
    IReadOnlyList<DetailedShoppingPlan> SourceShoppingPlans,
    IReadOnlySet<MarketWorldKey> BlacklistedWorlds,
    IReadOnlySet<MarketItemWorldKey> ExcludedItemWorlds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedWorldsByDataCenter,
    Func<bool>? IsCurrentOperation = null,
    MarketAnalysisExecutionOptions? ExecutionOptions = null);

public sealed record CoreProcurementWorkflowResult(
    CoreProcurementWorkflowStatus Status,
    int ShoppingPlanCount)
{
    public static CoreProcurementWorkflowResult Noop(CoreProcurementWorkflowStatus status) => new(status, 0);
}

public enum CoreProcurementWorkflowStatus
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

public sealed record CoreProcurementItemRefreshWorkflowRequest(
    int ItemId,
    string ItemName,
    MarketFetchScope Scope,
    string SelectedDataCenter,
    string SelectedRegion,
    MarketAcquisitionLens Lens,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedWorldsByDataCenter,
    Func<bool>? IsCurrentOperation = null,
    MarketAnalysisExecutionOptions? ExecutionOptions = null);

public sealed record CoreProcurementItemRefreshWorkflowResult(
    CoreProcurementItemRefreshStatus Status,
    string? ItemName)
{
    public static CoreProcurementItemRefreshWorkflowResult Noop(CoreProcurementItemRefreshStatus status) =>
        new(status, null);
}

public enum CoreProcurementItemRefreshStatus
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

public sealed class CoreProcurementWorkflowService
{
    private readonly CraftSessionState _session;
    private readonly IProcurementRouteExecutionService _procurementRouteExecutionService;
    private readonly IMarketAnalysisExecutionService _marketAnalysisExecutionService;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly ICoreRecipeLayerWorkflowService _recipeLayerWorkflow;
    private readonly ICraftOperationCoordinator _operationCoordinator;

    public CoreProcurementWorkflowService(
        CraftSessionState session,
        IProcurementRouteExecutionService procurementRouteExecutionService,
        IMarketAnalysisExecutionService marketAnalysisExecutionService,
        MarketShoppingService marketShoppingService,
        ICoreRecipeLayerWorkflowService recipeLayerWorkflow,
        ICraftOperationCoordinator operationCoordinator)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _procurementRouteExecutionService = procurementRouteExecutionService ?? throw new ArgumentNullException(nameof(procurementRouteExecutionService));
        _marketAnalysisExecutionService = marketAnalysisExecutionService ?? throw new ArgumentNullException(nameof(marketAnalysisExecutionService));
        _marketShoppingService = marketShoppingService ?? throw new ArgumentNullException(nameof(marketShoppingService));
        _recipeLayerWorkflow = recipeLayerWorkflow ?? throw new ArgumentNullException(nameof(recipeLayerWorkflow));
        _operationCoordinator = operationCoordinator ?? throw new ArgumentNullException(nameof(operationCoordinator));
    }

    public async Task<CoreProcurementWorkflowResult> RunAnalysisAsync(
        CoreProcurementWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        using var operation = _operationCoordinator.Start(
            CraftOperationWorkflow.ProcurementAnalysis,
            "Running Procurement",
            "Analyzing procurement route...");

        try
        {
            var plan = _session.ActivePlan;
            var planSessionVersion = _session.PlanSessionVersion;
            if (plan == null)
            {
                operation.Cancel();
                return CoreProcurementWorkflowResult.Noop(CoreProcurementWorkflowStatus.NoPlan);
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Token);
            var activeItems = await _recipeLayerWorkflow.BuildCurrentActiveProcurementItemsAsync(
                plan,
                linkedCancellation.Token);
            if (activeItems == null || _session.PlanSessionVersion != planSessionVersion)
            {
                operation.Cancel();
                return CoreProcurementWorkflowResult.Noop(CoreProcurementWorkflowStatus.StalePlan);
            }

            var activeItemsList = activeItems
                .Where(item => item.TotalQuantity > 0)
                .ToList();
            if (activeItemsList.Count == 0)
            {
                operation.Cancel();
                return CoreProcurementWorkflowResult.Noop(CoreProcurementWorkflowStatus.NoActiveProcurementItems);
            }

            if (string.IsNullOrWhiteSpace(request.SelectedDataCenter))
            {
                operation.Cancel();
                return CoreProcurementWorkflowResult.Noop(CoreProcurementWorkflowStatus.MissingDataCenter);
            }

            var capturedVersions = _session.CaptureVersionStamp();
            var blacklistedWorlds = request.BlacklistedWorlds
                .Concat(_session.GetActiveBlacklistedMarketWorlds())
                .ToHashSet();
            var excludedItemWorlds = request.ExcludedItemWorlds
                .Concat(_session.TemporarilyExcludedItemWorlds)
                .ToHashSet();
            var guardedProgress = progress == null
                ? null
                : new Progress<string>(message =>
                {
                    if (IsCurrent(plan, planSessionVersion, capturedVersions, request.IsCurrentOperation))
                    {
                        progress.Report(message);
                    }
                });

            var result = await _procurementRouteExecutionService.AnalyzeAsync(
                new ProcurementRouteExecutionRequest
                {
                    Plan = plan,
                    ActiveProcurementItems = activeItemsList,
                    SourceShoppingPlans = request.SourceShoppingPlans,
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    Lens = request.Lens,
                    ProcurementConfig = request.ProcurementConfig,
                    IncludeSplitPurchases = request.IncludeSplitPurchases,
                    BlacklistedWorlds = blacklistedWorlds,
                    ExcludedItemWorlds = excludedItemWorlds,
                    ExpectedWorldsByDataCenter = request.ExpectedWorldsByDataCenter
                },
                guardedProgress,
                linkedCancellation.Token,
                executionOptions: request.ExecutionOptions);

            var currentVersions = _session.CaptureVersionStamp();
            if (currentVersions.PlanSession != capturedVersions.PlanSession)
            {
                operation.Cancel();
                return CoreProcurementWorkflowResult.Noop(CoreProcurementWorkflowStatus.StalePlan);
            }

            if (currentVersions.PlanDecision != capturedVersions.PlanDecision)
            {
                operation.Cancel();
                return CoreProcurementWorkflowResult.Noop(CoreProcurementWorkflowStatus.StaleDecision);
            }

            if (currentVersions.MarketAnalysis != capturedVersions.MarketAnalysis ||
                currentVersions.SettingsContext != capturedVersions.SettingsContext ||
                currentVersions.Procurement != capturedVersions.Procurement)
            {
                operation.Cancel();
                return CoreProcurementWorkflowResult.Noop(CoreProcurementWorkflowStatus.StaleConfiguration);
            }

            if (request.IsCurrentOperation?.Invoke() == false)
            {
                operation.Cancel();
                return CoreProcurementWorkflowResult.Noop(CoreProcurementWorkflowStatus.Superseded);
            }

            var shoppingPlans = result.ShoppingPlans.ToList();
            var routeCards = ProcurementWorldCardBuilder.BuildWorldCards(
                shoppingPlans,
                request.SelectedDataCenter);
            var published = operation.CompleteIfCurrent(
                () => _session.PublishProcurementOverlay(
                    new CraftSessionProcurementOverlay(
                        DateTime.UtcNow,
                        shoppingPlans.Select(plan => plan.ItemId).ToArray(),
                        "procurement route analysis",
                        shoppingPlans,
                        routeCards),
                    "procurement route published"),
                "Procurement route published.");
            if (!published)
            {
                operation.Cancel();
                return CoreProcurementWorkflowResult.Noop(CoreProcurementWorkflowStatus.Superseded);
            }

            return new CoreProcurementWorkflowResult(
                CoreProcurementWorkflowStatus.Published,
                shoppingPlans.Count);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested || ct.IsCancellationRequested)
        {
            operation.Cancel();
            return CoreProcurementWorkflowResult.Noop(CoreProcurementWorkflowStatus.Superseded);
        }
        catch (Exception ex)
        {
            if (!operation.CompleteStatusIfCurrent($"Failed: {ex.Message}"))
            {
                operation.Cancel();
            }

            throw;
        }
    }

    public async Task<CoreProcurementItemRefreshWorkflowResult> RefreshItemMarketDataAsync(
        CoreProcurementItemRefreshWorkflowRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        using var operation = _operationCoordinator.Start(
            CraftOperationWorkflow.ItemMarketRefresh,
            "Refreshing Item Market Data",
            $"Refreshing {request.ItemName}...");

        try
        {
            var plan = _session.ActivePlan;
            var planSessionVersion = _session.PlanSessionVersion;
            if (plan == null)
            {
                operation.Cancel();
                return CoreProcurementItemRefreshWorkflowResult.Noop(CoreProcurementItemRefreshStatus.NoPlan);
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Token);
            var marketCandidates = await _recipeLayerWorkflow.BuildCurrentMarketAnalysisCandidatesAsync(
                plan,
                linkedCancellation.Token);
            if (marketCandidates == null || _session.PlanSessionVersion != planSessionVersion)
            {
                operation.Cancel();
                return CoreProcurementItemRefreshWorkflowResult.Noop(CoreProcurementItemRefreshStatus.StalePlan);
            }

            var candidate = marketCandidates.FirstOrDefault(item => item.ItemId == request.ItemId);
            if (candidate == null)
            {
                operation.Cancel();
                return CoreProcurementItemRefreshWorkflowResult.Noop(CoreProcurementItemRefreshStatus.NotFound);
            }

            var capturedVersions = _session.CaptureVersionStamp();
            var guardedProgress = progress == null
                ? null
                : new Progress<string>(message =>
                {
                    if (IsCurrent(plan, planSessionVersion, capturedVersions, request.IsCurrentOperation))
                    {
                        progress.Report(message);
                    }
                });

            var result = await _marketAnalysisExecutionService.ExecuteAsync(
                new MarketAnalysisExecutionRequest
                {
                    Items = [candidate],
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    ForceRefreshData = true,
                    RecommendationMode = RecommendationMode.MinimizeTotalCost,
                    Lens = request.Lens,
                    ExpectedWorldsByDataCenter = request.ExpectedWorldsByDataCenter
                },
                guardedProgress,
                linkedCancellation.Token,
                executionOptions: request.ExecutionOptions);
            linkedCancellation.Token.ThrowIfCancellationRequested();

            var staleStatus = GetStaleRefreshStatus(planSessionVersion, capturedVersions, request.IsCurrentOperation);
            if (staleStatus != null)
            {
                operation.Cancel();
                return CoreProcurementItemRefreshWorkflowResult.Noop(staleStatus.Value);
            }

            var refreshedAnalysis = result.Analyses.SingleOrDefault();
            if (refreshedAnalysis == null || result.ShoppingPlans.Count == 0)
            {
                operation.Cancel();
                return CoreProcurementItemRefreshWorkflowResult.Noop(CoreProcurementItemRefreshStatus.NoData);
            }

            var refreshedPlans = new List<DetailedShoppingPlan> { result.ShoppingPlans.Single() };
            _marketShoppingService.ApplyVendorPurchaseOverrides(plan, refreshedPlans);
            staleStatus = GetStaleRefreshStatus(planSessionVersion, capturedVersions, request.IsCurrentOperation);
            if (staleStatus != null)
            {
                operation.Cancel();
                return CoreProcurementItemRefreshWorkflowResult.Noop(staleStatus.Value);
            }

            var existingEvidence = _session.MarketEvidence;
            var analyses = ReplaceAnalysisByItemId(existingEvidence.ItemAnalyses, refreshedAnalysis);
            var shoppingPlans = ReplaceShoppingPlanByItemId(existingEvidence.ShoppingPlans ?? [], refreshedPlans[0]);
            var published = false;
            var completed = operation.CompleteIfCurrent(
                () =>
                {
                    var stamp = _session.CaptureVersionStamp();
                    published = _session.TryPublishMarketAnalysis(
                        stamp,
                        plan,
                        planSessionVersion,
                        analyses,
                        shoppingPlans,
                        acquisitionDecisionsChanged: false,
                        "market item refreshed",
                        existingEvidence.UnavailableMarketItemIds,
                        existingEvidence.RecommendationMode,
                        existingEvidence.Lens,
                        existingEvidence.RecipeBasis);
                },
                $"{request.ItemName} market data refreshed.");
            if (!completed || !published)
            {
                operation.Cancel();
                return CoreProcurementItemRefreshWorkflowResult.Noop(CoreProcurementItemRefreshStatus.Superseded);
            }

            return new CoreProcurementItemRefreshWorkflowResult(
                CoreProcurementItemRefreshStatus.Refreshed,
                candidate.Name);
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested || ct.IsCancellationRequested)
        {
            operation.Cancel();
            return CoreProcurementItemRefreshWorkflowResult.Noop(CoreProcurementItemRefreshStatus.Superseded);
        }
        catch (Exception ex)
        {
            if (!operation.CompleteStatusIfCurrent($"Failed: {ex.Message}"))
            {
                operation.Cancel();
            }

            throw;
        }
    }

    private bool IsCurrent(
        CraftingPlan plan,
        long planSessionVersion,
        CraftSessionVersionStamp capturedVersions,
        Func<bool>? isCurrentOperation)
    {
        var currentVersions = _session.CaptureVersionStamp();
        if (currentVersions.PlanSession != planSessionVersion)
        {
            return false;
        }

        return currentVersions.PlanDecision == capturedVersions.PlanDecision &&
               currentVersions.MarketAnalysis == capturedVersions.MarketAnalysis &&
               currentVersions.SettingsContext == capturedVersions.SettingsContext &&
               currentVersions.Procurement == capturedVersions.Procurement &&
               (isCurrentOperation?.Invoke() ?? true);
    }

    private CoreProcurementItemRefreshStatus? GetStaleRefreshStatus(
        long planSessionVersion,
        CraftSessionVersionStamp capturedVersions,
        Func<bool>? isCurrentOperation)
    {
        var currentVersions = _session.CaptureVersionStamp();
        if (currentVersions.PlanSession != planSessionVersion)
        {
            return CoreProcurementItemRefreshStatus.StalePlan;
        }

        if (currentVersions.PlanDecision != capturedVersions.PlanDecision)
        {
            return CoreProcurementItemRefreshStatus.StaleDecision;
        }

        if (currentVersions.MarketAnalysis != capturedVersions.MarketAnalysis ||
            currentVersions.SettingsContext != capturedVersions.SettingsContext ||
            currentVersions.Procurement != capturedVersions.Procurement)
        {
            return CoreProcurementItemRefreshStatus.StaleConfiguration;
        }

        if (isCurrentOperation?.Invoke() == false)
        {
            return CoreProcurementItemRefreshStatus.Superseded;
        }

        return null;
    }

    private static List<MarketItemAnalysis> ReplaceAnalysisByItemId(
        IEnumerable<MarketItemAnalysis> analyses,
        MarketItemAnalysis replacement)
    {
        var replaced = false;
        var result = new List<MarketItemAnalysis>();
        foreach (var analysis in analyses)
        {
            if (analysis.ItemId == replacement.ItemId)
            {
                result.Add(replacement);
                replaced = true;
                continue;
            }

            result.Add(analysis);
        }

        if (!replaced)
        {
            result.Add(replacement);
        }

        return result;
    }

    private static List<DetailedShoppingPlan> ReplaceShoppingPlanByItemId(
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        DetailedShoppingPlan replacement)
    {
        var replaced = false;
        var result = new List<DetailedShoppingPlan>();
        foreach (var shoppingPlan in shoppingPlans)
        {
            if (shoppingPlan.ItemId == replacement.ItemId)
            {
                result.Add(replacement);
                replaced = true;
                continue;
            }

            result.Add(shoppingPlan);
        }

        if (!replaced)
        {
            result.Add(replacement);
        }

        return result;
    }
}
