using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class ProcurementRouteExecutionService : IProcurementRouteExecutionService
{
    private readonly IMarketAnalysisExecutionService _marketAnalysisExecutionService;
    private readonly MarketShoppingService _marketShoppingService;

    public ProcurementRouteExecutionService(
        IMarketAnalysisExecutionService marketAnalysisExecutionService,
        MarketShoppingService marketShoppingService)
    {
        _marketAnalysisExecutionService = marketAnalysisExecutionService;
        _marketShoppingService = marketShoppingService;
    }

    public async Task<ProcurementRouteExecutionResult> AnalyzeAsync(
        ProcurementRouteExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        progress?.Report("Selecting procurement market evidence...");
        var activeProcurementItems = GetActiveProcurementItems(request);
        var selection = SelectActiveProcurementEvidence(
            activeProcurementItems,
            request.SourceShoppingPlans,
            request.Scope,
            request.SelectedDataCenter);
        var reusableEvidence = selection.ReusablePlans.ToList();
        var missingItems = selection.MissingItems.ToList();
        if (request.Scope == MarketFetchScope.EntireRegion &&
            request.ExpectedWorldsByDataCenter.Count > 0)
        {
            var activeItemsByItemId = activeProcurementItems
                .Where(item => item.TotalQuantity > 0)
                .GroupBy(item => item.ItemId)
                .ToDictionary(group => group.Key, group => group.First());
            var incompleteRegionEvidence = reusableEvidence
                .Where(plan => !HasExpectedRegionEvidence(plan, request.ExpectedWorldsByDataCenter))
                .ToList();
            reusableEvidence = reusableEvidence
                .Except(incompleteRegionEvidence)
                .ToList();
            foreach (var incompletePlan in incompleteRegionEvidence)
            {
                if (activeItemsByItemId.TryGetValue(incompletePlan.ItemId, out var item) &&
                    missingItems.All(missingItem => missingItem.ItemId != item.ItemId))
                {
                    missingItems.Add(item);
                }
            }
        }

        var refreshedEvidence = new List<DetailedShoppingPlan>();
        if (missingItems.Count > 0)
        {
            progress?.Report($"Fetching procurement prices for {missingItems.Count} active items...");
            var marketResult = await _marketAnalysisExecutionService.ExecuteAsync(
                new MarketAnalysisExecutionRequest
                {
                    Items = missingItems,
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    RecommendationMode = RecommendationMode.MinimizeTotalCost,
                    Lens = request.Lens,
                    ExpectedWorldsByDataCenter = request.ExpectedWorldsByDataCenter
                },
                progress,
                ct,
                executionOptions);
            refreshedEvidence = marketResult.ShoppingPlans;
            _marketShoppingService.ApplyVendorPurchaseOverrides(request.Plan, refreshedEvidence);
        }

        var evidencePlans = MergeActiveProcurementEvidence(
            activeProcurementItems,
            reusableEvidence,
            refreshedEvidence);
        _marketShoppingService.ApplyVendorPurchaseOverrides(request.Plan, evidencePlans);

        var scopedEvidence = PrepareProcurementEvidenceForScope(evidencePlans, request);
        progress?.Report($"Optimizing procurement route for {scopedEvidence.Count} items...");
        var shoppingPlans = await _marketShoppingService.OptimizeProcurementRouteAsync(
            scopedEvidence,
            request.ProcurementConfig,
            request.IncludeSplitPurchases,
            executionOptions,
            progress,
            ct);

        return new ProcurementRouteExecutionResult(
            shoppingPlans,
            evidencePlans,
            reusableEvidence,
            refreshedEvidence,
            missingItems);
    }

    private static IReadOnlyList<MaterialAggregate> GetActiveProcurementItems(ProcurementRouteExecutionRequest request)
    {
        return request.ActiveProcurementItems.Count > 0
            ? request.ActiveProcurementItems
            : AcquisitionPlanningService.GetActiveProcurementItems(request.Plan);
    }

    private static ProcurementEvidenceSelection SelectActiveProcurementEvidence(
        IReadOnlyList<MaterialAggregate> activeProcurementItems,
        IEnumerable<DetailedShoppingPlan> sourceShoppingPlans,
        MarketFetchScope requiredScope,
        string selectedDataCenter)
    {
        var sourcePlanByItemId = sourceShoppingPlans
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());
        var reusablePlans = new List<DetailedShoppingPlan>();
        var missingItems = new List<MaterialAggregate>();
        foreach (var item in activeProcurementItems.Where(item => item.TotalQuantity > 0))
        {
            if (!sourcePlanByItemId.TryGetValue(item.ItemId, out var shoppingPlan) ||
                !AcquisitionPlanningService.HasUsableEvidenceForScope(shoppingPlan, requiredScope, selectedDataCenter))
            {
                missingItems.Add(item);
                continue;
            }

            reusablePlans.Add(shoppingPlan);
        }

        return new ProcurementEvidenceSelection(reusablePlans, missingItems);
    }

    private static List<DetailedShoppingPlan> MergeActiveProcurementEvidence(
        IReadOnlyList<MaterialAggregate> activeProcurementItems,
        IEnumerable<DetailedShoppingPlan> reusablePlans,
        IEnumerable<DetailedShoppingPlan> fetchedMissingPlans)
    {
        var activeItemIds = activeProcurementItems
            .Select(item => item.ItemId)
            .ToHashSet();
        var resultByItemId = reusablePlans
            .Where(shoppingPlan => activeItemIds.Contains(shoppingPlan.ItemId))
            .GroupBy(shoppingPlan => shoppingPlan.ItemId)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var fetchedPlan in fetchedMissingPlans.Where(shoppingPlan => activeItemIds.Contains(shoppingPlan.ItemId)))
        {
            resultByItemId[fetchedPlan.ItemId] = fetchedPlan;
        }

        return activeProcurementItems
            .Where(item => resultByItemId.ContainsKey(item.ItemId))
            .Select(item => resultByItemId[item.ItemId])
            .ToList();
    }

    private static bool HasExpectedRegionEvidence(
        DetailedShoppingPlan shoppingPlan,
        IReadOnlyDictionary<string, IReadOnlyList<string>> expectedWorldsByDataCenter)
    {
        if (IsVendorPlan(shoppingPlan))
        {
            return true;
        }

        var evidenceDataCenters = GetEvidenceDataCenters(shoppingPlan)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedWorldsByDataCenter.Keys.All(evidenceDataCenters.Contains);
    }

    private static IEnumerable<string> GetEvidenceDataCenters(DetailedShoppingPlan shoppingPlan)
    {
        foreach (var world in shoppingPlan.WorldOptions)
        {
            if (!string.IsNullOrWhiteSpace(world.DataCenter))
            {
                yield return world.DataCenter;
            }
        }

        if (!string.IsNullOrWhiteSpace(shoppingPlan.RecommendedWorld?.DataCenter))
        {
            yield return shoppingPlan.RecommendedWorld.DataCenter;
        }

        if (shoppingPlan.RecommendedSplit == null)
        {
            yield break;
        }

        foreach (var split in shoppingPlan.RecommendedSplit)
        {
            if (!string.IsNullOrWhiteSpace(split.DataCenter))
            {
                yield return split.DataCenter;
            }
        }
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
