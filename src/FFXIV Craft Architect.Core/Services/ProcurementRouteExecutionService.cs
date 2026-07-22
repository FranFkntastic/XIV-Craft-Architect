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
                    jointOptimization.RouteDecision,
                    IsComplete: false);
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
            request.IncludeReconciliationEvidenceInResult
                ? optimization.ShoppingPlans
                : CompactResultShoppingPlans(optimization.ShoppingPlans),
            request.IncludeReconciliationEvidenceInResult ? evidencePlans : [],
            request.IncludeReconciliationEvidenceInResult ? reusableEvidence : [],
            request.IncludeReconciliationEvidenceInResult ? refreshedEvidence : [],
            request.IncludeReconciliationEvidenceInResult ? reconciliation.ReconciledItems : [],
            optimization.Decision,
            request.IncludeReconciliationEvidenceInResult ? reconciliation.Items : [],
            request.IncludeReconciliationEvidenceInResult ? jointOptimization?.OptimizedPlan : null,
            request.IncludeReconciliationEvidenceInResult ? jointOptimization?.ActiveProcurementItems : null,
            request.IncludeReconciliationEvidenceInResult ? reconciliation.Analyses : null,
            request.IncludeReconciliationEvidenceInResult || jointOptimization is null
                ? null
                : CaptureAcquisitionDecisions(jointOptimization.OptimizedPlan),
            IsComplete: optimization.IsComplete);
    }

    private static IReadOnlyList<ProcurementAcquisitionDecision> CaptureAcquisitionDecisions(CraftingPlan plan) =>
        EnumerateNodes(plan.RootItems)
            .Select(node => new ProcurementAcquisitionDecision(node.NodeId, node.Source, node.SourceReason))
            .ToArray();

    private static IEnumerable<PlanNode> EnumerateNodes(IEnumerable<PlanNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children))
            {
                yield return child;
            }
        }
    }

    internal static List<DetailedShoppingPlan> CompactResultShoppingPlans(
        IEnumerable<DetailedShoppingPlan> shoppingPlans) =>
        shoppingPlans.Select(plan => new DetailedShoppingPlan
        {
            ItemId = plan.ItemId,
            Name = plan.Name,
            IconId = plan.IconId,
            QuantityNeeded = plan.QuantityNeeded,
            HqQuantityNeeded = plan.HqQuantityNeeded,
            DCAveragePrice = plan.DCAveragePrice,
            HQAveragePrice = plan.HQAveragePrice,
            RecommendedWorld = plan.RecommendedWorld is null ? null : CompactWorld(plan.RecommendedWorld),
            RecommendedSplit = plan.RecommendedSplit,
            CoverageSet = CompactCoverageSet(plan),
            WorldOptions = GetSelectedWorldOptions(plan).Select(CompactWorld).ToList(),
            Error = plan.Error,
            MarketDataWarning = plan.MarketDataWarning,
            Vendors = plan.Vendors.ToList()
        }).ToList();

    private static WorldShoppingSummary CompactWorld(WorldShoppingSummary world) => new()
    {
        DataCenter = world.DataCenter,
        WorldName = world.WorldName,
        WorldId = world.WorldId,
        TotalCost = world.TotalCost,
        AveragePricePerUnit = world.AveragePricePerUnit,
        ListingsUsed = world.ListingsUsed,
        Listings = world.Listings.Select(CloneListing).ToList(),
        IsFullyUnderAverage = world.IsFullyUnderAverage,
        TotalQuantityPurchased = world.TotalQuantityPurchased,
        ExcessQuantity = world.ExcessQuantity,
        ModePricePerUnit = world.ModePricePerUnit,
        ValueScore = world.ValueScore,
        MarketDataQualityScore = world.MarketDataQualityScore,
        MarketDataQualityBucket = world.MarketDataQualityBucket,
        MarketDataAgeSource = world.MarketDataAgeSource,
        MarketDataAge = world.MarketDataAge,
        MarketUploadedAtUtc = world.MarketUploadedAtUtc,
        LensRank = world.LensRank,
        LensScoreBucket = world.LensScoreBucket,
        ProcurementPriorityScore = world.ProcurementPriorityScore,
        VendorName = world.VendorName,
        HasSufficientStock = world.HasSufficientStock,
        ShortfallQuantity = world.ShortfallQuantity,
        BestSingleListing = world.BestSingleListing is null ? null : CloneListing(world.BestSingleListing),
        Classification = world.Classification,
        IsHomeWorld = world.IsHomeWorld,
        IsBlacklisted = world.IsBlacklisted,
        IsTravelProhibited = world.IsTravelProhibited,
        CongestedWarning = world.CongestedWarning
    };

    private static ShoppingListingEntry CloneListing(ShoppingListingEntry listing) => new()
    {
        Quantity = listing.Quantity,
        PricePerUnit = listing.PricePerUnit,
        RetainerName = listing.RetainerName,
        IsUnderAverage = listing.IsUnderAverage,
        IsHq = listing.IsHq,
        NeededFromStack = listing.NeededFromStack,
        ExcessQuantity = listing.ExcessQuantity,
        IsAdditionalOption = listing.IsAdditionalOption
    };

    private static List<WorldShoppingSummary> GetSelectedWorldOptions(DetailedShoppingPlan plan)
    {
        if (plan.RecommendedWorld is { } recommended)
        {
            return [recommended];
        }
        if (plan.RecommendedSplit?.Count > 0)
        {
            return plan.WorldOptions.Where(world => plan.RecommendedSplit.Any(split =>
                string.Equals(split.DataCenter, world.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(split.WorldName, world.WorldName, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        var coverage = GetSelectedCoverage(plan);
        return coverage is null
            ? []
            : plan.WorldOptions.Where(world => coverage.Worlds.Any(selected =>
                string.Equals(selected.DataCenter, world.DataCenter, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(selected.WorldName, world.WorldName, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static MarketCoverageOption? GetSelectedCoverage(DetailedShoppingPlan plan) =>
        plan.CoverageSet?.AllCandidates.FirstOrDefault(candidate => candidate.IsDefaultEligible)
            ?? plan.CoverageSet?.SingleWorld
            ?? plan.CoverageSet?.CompactSplit
            ?? plan.CoverageSet?.WideSplit
            ?? plan.CoverageSet?.CheapestObserved;

    private static MarketCoverageSet? CompactCoverageSet(DetailedShoppingPlan plan)
    {
        var coverage = GetSelectedCoverage(plan);
        if (coverage is null || plan.CoverageSet is not { } source)
        {
            return null;
        }
        return new MarketCoverageSet(
            source.ItemId,
            source.ItemName,
            source.QuantityNeeded,
            coverage.Tier == MarketCoverageTier.SingleWorld ? coverage : null,
            coverage.Tier == MarketCoverageTier.CompactSplit ? coverage : null,
            coverage.Tier == MarketCoverageTier.WideSplit ? coverage : null,
            coverage.Tier == MarketCoverageTier.CheapestObserved ? coverage : null,
            [coverage]);
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
