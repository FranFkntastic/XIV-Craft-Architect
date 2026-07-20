using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class ProcurementRouteExecutionService : IProcurementRouteExecutionService
{
    private readonly IMarketEvidenceReconciliationService _marketEvidenceReconciliation;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly JointAcquisitionRouteOptimizationService _jointOptimizer;

    public ProcurementRouteExecutionService(
        IMarketEvidenceReconciliationService marketEvidenceReconciliation,
        MarketShoppingService marketShoppingService,
        JointAcquisitionRouteOptimizationService? jointOptimizer = null)
    {
        _marketEvidenceReconciliation = marketEvidenceReconciliation;
        _marketShoppingService = marketShoppingService;
        _jointOptimizer = jointOptimizer ?? new JointAcquisitionRouteOptimizationService(marketShoppingService);
    }

    public async Task<ProcurementRouteExecutionResult> AnalyzeAsync(
        ProcurementRouteExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        progress?.Report("Reconciling procurement market evidence...");
        var activeProcurementItems = GetActiveProcurementItems(request);
        var reconciliation = await _marketEvidenceReconciliation.ReconcileAsync(
            new MarketEvidenceReconciliationRequest
            {
                Items = activeProcurementItems,
                PublishedAnalyses = request.SourceMarketAnalyses,
                PublishedShoppingPlans = request.SourceShoppingPlans,
                Scope = request.Scope,
                SelectedDataCenter = request.SelectedDataCenter,
                SelectedRegion = request.SelectedRegion,
                RecommendationMode = RecommendationMode.MinimizeTotalCost,
                Lens = request.Lens,
                ExpectedWorldsByDataCenter = request.ExpectedWorldsByDataCenter,
                Policy = request.ReconciliationPolicy
            },
            progress,
            ct,
            executionOptions);
        var evidencePlans = reconciliation.ShoppingPlans.ToList();
        var reusableEvidence = evidencePlans
            .Where(plan => reconciliation.ReusedItemIds.Contains(plan.ItemId))
            .ToList();
        var refreshedEvidence = evidencePlans
            .Where(plan => reconciliation.RefreshedItemIds.Contains(plan.ItemId))
            .ToList();
        var scopedEvidence = PrepareProcurementEvidenceForScope(evidencePlans, request);
        progress?.Report($"Optimizing procurement route for {scopedEvidence.Count} items...");
        var procurementConfig = ProcurementRouteConfigFactory.Create(request);
        JointAcquisitionRouteOptimizationResult? jointOptimization = null;
        ProcurementRouteOptimizationResult optimization;
        var planCandidateIds = AcquisitionPlanningService.GetMarketAnalysisCandidates(request.Plan)
            .Select(item => item.ItemId)
            .ToHashSet();
        var requestedItemIds = activeProcurementItems.Select(item => item.ItemId).ToHashSet();
        var canOptimizeJointly = request.Plan != null && planCandidateIds.SetEquals(requestedItemIds);
        if (canOptimizeJointly && request.Plan is { } jointPlan)
        {
            jointOptimization = await _jointOptimizer.OptimizeAsync(
                jointPlan,
                scopedEvidence,
                procurementConfig,
                request.IncludeSplitPurchases,
                executionOptions,
                progress,
                ct);
            if (jointOptimization.FeasiblePlanCount > 0)
            {
                optimization = new ProcurementRouteOptimizationResult(
                    jointOptimization.ShoppingPlans,
                    jointOptimization.RouteDecision);
            }
            else if (request.Plan.RootItems.Count == 0)
            {
                jointOptimization = null;
                _marketShoppingService.ApplyVendorPurchaseOverrides(request.Plan, scopedEvidence);
                optimization = await _marketShoppingService.OptimizeProcurementRouteWithDecisionAsync(
                    scopedEvidence,
                    procurementConfig,
                    request.IncludeSplitPurchases,
                    executionOptions,
                    progress,
                    ct);
            }
            else
            {
                optimization = new ProcurementRouteOptimizationResult(
                    jointOptimization.ShoppingPlans,
                    jointOptimization.RouteDecision);
            }
        }
        else
        {
            optimization = await _marketShoppingService.OptimizeProcurementRouteWithDecisionAsync(
                scopedEvidence,
                procurementConfig,
                request.IncludeSplitPurchases,
                executionOptions,
                progress,
                ct);
        }

        return new ProcurementRouteExecutionResult(
            optimization.ShoppingPlans,
            evidencePlans,
            reusableEvidence,
            refreshedEvidence,
            reconciliation.ReconciledItems,
            optimization.Decision,
            reconciliation.Items,
            jointOptimization?.OptimizedPlan,
            jointOptimization?.ActiveProcurementItems,
            reconciliation.Analyses,
            optimization.IsComplete);
    }

    private static IReadOnlyList<MaterialAggregate> GetActiveProcurementItems(ProcurementRouteExecutionRequest request)
    {
        return request.ActiveProcurementItems.Count > 0
            ? request.ActiveProcurementItems
            : AcquisitionPlanningService.GetMarketAnalysisCandidates(request.Plan);
    }

    private static List<DetailedShoppingPlan> PrepareProcurementEvidenceForScope(
        IEnumerable<DetailedShoppingPlan> sourcePlans,
        ProcurementRouteExecutionRequest request)
    {
        var activeItemIds = GetActiveProcurementItems(request)
            .Select(item => item.ItemId)
            .ToHashSet();
        var activePlans = sourcePlans
            .Where(plan => activeItemIds.Contains(plan.ItemId))
            .ToList();
        activePlans = MarketAnalysisPlanAdjuster.ExcludeWorlds(
            activePlans,
            request.BlacklistedWorlds);
        activePlans = MarketAnalysisPlanAdjuster.ExcludeItemWorlds(
            activePlans,
            request.ExcludedItemWorlds);
        if (request.Scope == MarketFetchScope.EntireRegion)
        {
            return activePlans;
        }

        return activePlans
            .Select(plan => FilterPlanToSelectedDataCenter(plan, request.SelectedDataCenter))
            .ToList();
    }

    private static DetailedShoppingPlan FilterPlanToSelectedDataCenter(
        DetailedShoppingPlan plan,
        string selectedDataCenter)
    {
        if (IsVendorPlan(plan))
        {
            return plan;
        }

        var worldOptions = plan.WorldOptions
            .Where(world => IsSelectedDataCenter(world.DataCenter, selectedDataCenter))
            .ToList();
        var recommendedWorld = IsSelectedDataCenter(plan.RecommendedWorld?.DataCenter, selectedDataCenter)
            ? plan.RecommendedWorld
            : null;
        var recommendedSplit = plan.RecommendedSplit?
            .Where(split => IsSelectedDataCenter(split.DataCenter, selectedDataCenter))
            .ToList();

        return new DetailedShoppingPlan
        {
            ItemId = plan.ItemId,
            Name = plan.Name,
            IconId = plan.IconId,
            QuantityNeeded = plan.QuantityNeeded,
            DCAveragePrice = plan.DCAveragePrice,
            WorldOptions = worldOptions,
            RecommendedWorld = recommendedWorld,
            RecommendedSplit = recommendedSplit?.Count > 0 ? recommendedSplit : null,
            Error = plan.Error,
            MarketDataWarning = plan.MarketDataWarning,
            HQAveragePrice = plan.HQAveragePrice,
            Vendors = plan.Vendors.ToList()
        };
    }

    private static bool IsVendorPlan(DetailedShoppingPlan plan)
    {
        return string.Equals(
            plan.RecommendedWorld?.WorldName,
            MarketShoppingConstants.VendorWorldName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSelectedDataCenter(string? dataCenter, string selectedDataCenter)
    {
        return !string.IsNullOrWhiteSpace(dataCenter) &&
               string.Equals(dataCenter, selectedDataCenter, StringComparison.OrdinalIgnoreCase);
    }
}
