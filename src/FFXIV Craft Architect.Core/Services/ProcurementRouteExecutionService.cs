using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class ProcurementRouteExecutionService : IProcurementRouteExecutionService
{
    private readonly IMarketEvidenceReconciliationService _marketEvidenceReconciliation;
    private readonly MarketShoppingService _marketShoppingService;

    public ProcurementRouteExecutionService(
        IMarketEvidenceReconciliationService marketEvidenceReconciliation,
        MarketShoppingService marketShoppingService)
    {
        _marketEvidenceReconciliation = marketEvidenceReconciliation;
        _marketShoppingService = marketShoppingService;
    }

    public async Task<ProcurementRouteExecutionResult> AnalyzeAsync(
        ProcurementRouteExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        progress?.Report(request.UsePublishedEvidenceAsAuthority
            ? "Preparing authoritative procurement evidence..."
            : "Reconciling procurement market evidence...");
        var activeProcurementItems = GetActiveProcurementItems(request);
        var vendorEvidence = new List<DetailedShoppingPlan>();
        _marketShoppingService.ApplySelectedVendorPurchases(
            request.Plan,
            vendorEvidence,
            activeProcurementItems);
        var vendorItemIds = vendorEvidence
            .Select(plan => plan.ItemId)
            .ToHashSet();
        var marketProcurementItems = activeProcurementItems
            .Where(item => !vendorItemIds.Contains(item.ItemId))
            .ToList();
        MarketEvidenceReconciliationResult? reconciliation = null;
        List<DetailedShoppingPlan> evidencePlans;
        if (request.UsePublishedEvidenceAsAuthority)
        {
            evidencePlans = request.SourceShoppingPlans
                .Where(plan => !vendorItemIds.Contains(plan.ItemId))
                .Concat(vendorEvidence)
                .ToList();
        }
        else
        {
            reconciliation = await _marketEvidenceReconciliation.ReconcileAsync(
                new MarketEvidenceReconciliationRequest
                {
                    Items = marketProcurementItems,
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
            evidencePlans = reconciliation.ShoppingPlans
                .Concat(vendorEvidence)
                .ToList();
        }
        var reusableEvidence = request.UsePublishedEvidenceAsAuthority
            ? evidencePlans
            : evidencePlans
                .Where(plan => reconciliation!.ReusedItemIds.Contains(plan.ItemId))
                .ToList();
        var refreshedEvidence = request.UsePublishedEvidenceAsAuthority
            ? []
            : evidencePlans
                .Where(plan => reconciliation!.RefreshedItemIds.Contains(plan.ItemId))
                .ToList();
        var scopedEvidence = PrepareProcurementEvidenceForScope(evidencePlans, request);
        progress?.Report($"Optimizing procurement route for {scopedEvidence.Count} items...");
        var procurementConfig = ProcurementRouteConfigFactory.Create(request);
        _marketShoppingService.ApplySelectedVendorPurchases(request.Plan, scopedEvidence);
        var optimization = await _marketShoppingService.OptimizeProcurementRouteWithDecisionAsync(
            scopedEvidence,
            procurementConfig,
            request.IncludeSplitPurchases,
            executionOptions,
            progress,
            ct);

        return new ProcurementRouteExecutionResult(
            request.IncludeReconciliationEvidenceInResult
                ? optimization.ShoppingPlans
                : CompactResultShoppingPlans(optimization.ShoppingPlans),
            request.IncludeReconciliationEvidenceInResult ? evidencePlans : [],
            request.IncludeReconciliationEvidenceInResult ? reusableEvidence : [],
            request.IncludeReconciliationEvidenceInResult ? refreshedEvidence : [],
            request.IncludeReconciliationEvidenceInResult
                ? reconciliation?.ReconciledItems ?? []
                : [],
            optimization.Decision,
            request.IncludeReconciliationEvidenceInResult
                ? reconciliation?.Items ?? []
                : [],
            ActiveProcurementItems: request.IncludeReconciliationEvidenceInResult ? activeProcurementItems : null,
            EvidenceAnalyses: request.IncludeReconciliationEvidenceInResult
                ? reconciliation?.Analyses ?? request.SourceMarketAnalyses
                : null,
            IsComplete: optimization.IsComplete);
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
            RecommendedSplit = plan.RecommendedSplit?.Select(CompactSplit).ToList(),
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
        Listings = [],
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
        BestSingleListing = null,
        Classification = world.Classification,
        IsHomeWorld = world.IsHomeWorld,
        IsBlacklisted = world.IsBlacklisted,
        IsTravelProhibited = world.IsTravelProhibited,
        CongestedWarning = world.CongestedWarning
    };

    private static SplitWorldPurchase CompactSplit(SplitWorldPurchase split) => new()
    {
        DataCenter = split.DataCenter,
        WorldName = split.WorldName,
        QuantityToBuy = split.QuantityToBuy,
        PricePerUnit = split.PricePerUnit,
        EffectivePricePerNeededUnit = split.EffectivePricePerNeededUnit,
        TotalCost = split.TotalCost,
        IsPartial = split.IsPartial,
        TravelContext = split.TravelContext,
        ExcessAvailable = split.ExcessAvailable,
        Listings = []
    };

    private static List<WorldShoppingSummary> GetSelectedWorldOptions(DetailedShoppingPlan plan)
    {
        if (plan.RecommendedWorld is { } recommended)
        {
            if (string.Equals(
                    recommended.WorldName,
                    MarketShoppingConstants.VendorWorldName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

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
        var compactCoverage = coverage with { Listings = [] };
        return new MarketCoverageSet(
            source.ItemId,
            source.ItemName,
            source.QuantityNeeded,
            compactCoverage.Tier == MarketCoverageTier.SingleWorld ? compactCoverage : null,
            compactCoverage.Tier == MarketCoverageTier.CompactSplit ? compactCoverage : null,
            compactCoverage.Tier == MarketCoverageTier.WideSplit ? compactCoverage : null,
            compactCoverage.Tier == MarketCoverageTier.CheapestObserved ? compactCoverage : null,
            [compactCoverage]);
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
