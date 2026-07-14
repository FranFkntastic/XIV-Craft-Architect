using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class DesktopMarketRefreshQueueService
{
    private readonly IMarketEvidenceReconciliationService _marketEvidenceReconciliation;

    public DesktopMarketRefreshQueueService(IMarketEvidenceReconciliationService marketEvidenceReconciliation)
    {
        _marketEvidenceReconciliation = marketEvidenceReconciliation ??
            throw new ArgumentNullException(nameof(marketEvidenceReconciliation));
    }

    public async Task<DesktopMarketRefreshQueueResult> RefreshSelectedItemAsync(
        CraftSessionState session,
        int itemId,
        string? selectedDataCenter,
        CancellationToken ct = default)
    {
        var item = session.ActivePlan?.FindNode(itemId);
        if (item == null)
        {
            return DesktopMarketRefreshQueueResult.NotFound;
        }

        var material = new MaterialAggregate
        {
            ItemId = item.ItemId,
            Name = item.Name,
            TotalQuantity = item.Quantity,
            RequiresHq = item.MustBeHq
        };
        return await RefreshMaterialsAsync(
            session,
            [material],
            selectedDataCenter,
            "selected item market evidence refreshed",
            replaceAllEvidence: false,
            ct);
    }

    public async Task<DesktopMarketRefreshQueueResult> RefreshPlanEvidenceAsync(
        CraftSessionState session,
        string? selectedDataCenter,
        CancellationToken ct = default)
    {
        var materials = AcquisitionPlanningService.GetActiveProcurementItems(session.ActivePlan)
            .Where(item => item.TotalQuantity > 0)
            .ToArray();
        if (materials.Length == 0)
        {
            return DesktopMarketRefreshQueueResult.NoPlanItems;
        }

        return await RefreshMaterialsAsync(
            session,
            materials,
            selectedDataCenter,
            "active plan market evidence refreshed",
            replaceAllEvidence: true,
            ct);
    }

    private async Task<DesktopMarketRefreshQueueResult> RefreshMaterialsAsync(
        CraftSessionState session,
        IReadOnlyList<MaterialAggregate> materials,
        string? selectedDataCenter,
        string reason,
        bool replaceAllEvidence,
        CancellationToken ct)
    {
        var dataCenter = string.IsNullOrWhiteSpace(selectedDataCenter)
            ? "Aether"
            : selectedDataCenter;
        var plan = session.ActivePlan;
        var planSessionVersion = session.PlanSessionVersion;
        if (plan == null)
        {
            return DesktopMarketRefreshQueueResult.NoPlanItems;
        }

        var capturedVersions = session.CaptureVersionStamp();
        MarketEvidenceReconciliationResult reconciliation;
        try
        {
            var existingEvidence = session.MarketEvidence;
            var itemIds = materials.Select(item => item.ItemId).ToHashSet();
            reconciliation = await _marketEvidenceReconciliation.ReconcileAsync(
                new MarketEvidenceReconciliationRequest
                {
                    Items = materials,
                    PublishedAnalyses = existingEvidence.ItemAnalyses
                        .Where(analysis => itemIds.Contains(analysis.ItemId))
                        .ToList(),
                    PublishedShoppingPlans = (existingEvidence.ShoppingPlans ?? [])
                        .Where(shoppingPlan => itemIds.Contains(shoppingPlan.ItemId))
                        .ToList(),
                    Scope = MarketFetchScope.SelectedDataCenter,
                    SelectedDataCenter = dataCenter,
                    SelectedRegion = MarketFetchScopeResolver.ResolveRegionForDataCenter(
                        dataCenter,
                        "North America"),
                    RecommendationMode = existingEvidence.RecommendationMode,
                    Lens = existingEvidence.Lens,
                    Policy = MarketEvidenceReconciliationPolicy.ForcedRefresh()
                },
                ct: ct,
                executionOptions: MarketAnalysisExecutionOptions.Synchronous);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DesktopMarketRefreshQueueResult(
                DesktopMarketRefreshQueueStatus.Failed,
                materials.Count,
                materials.FirstOrDefault()?.Name,
                ex.Message);
        }

        var existing = session.MarketEvidence;
        var analyses = replaceAllEvidence
            ? reconciliation.Analyses.ToList()
            : MarketEvidenceCollectionMerger.MergeAnalyses(existing.ItemAnalyses, reconciliation.Analyses);
        var shoppingPlans = replaceAllEvidence
            ? reconciliation.ShoppingPlans.ToList()
            : MarketEvidenceCollectionMerger.MergeShoppingPlans(
                existing.ShoppingPlans ?? [],
                reconciliation.ShoppingPlans);
        var unavailableItemIds = replaceAllEvidence
            ? reconciliation.UnavailableItemIds
            : existing.UnavailableMarketItemIds
                .Except(materials.Select(item => item.ItemId))
                .Concat(reconciliation.UnavailableItemIds)
                .ToHashSet();
        var published = session.TryPublishMarketAnalysis(
            capturedVersions,
            plan,
            planSessionVersion,
            analyses,
            shoppingPlans,
            acquisitionDecisionsChanged: false,
            reason: reason,
            unavailableMarketItemIds: unavailableItemIds,
            recommendationMode: existing.RecommendationMode,
            lens: existing.Lens,
            recipeBasis: existing.RecipeBasis);
        if (!published)
        {
            return new DesktopMarketRefreshQueueResult(
                DesktopMarketRefreshQueueStatus.Failed,
                materials.Count,
                materials.FirstOrDefault()?.Name,
                "The active plan changed before refreshed evidence could be published.");
        }

        var status = reconciliation.Analyses.Count == 0 ||
                     reconciliation.Analyses.All(analysis =>
                         analysis.WorstDataQualityBucket == MarketDataQualityBucket.Missing)
            ? DesktopMarketRefreshQueueStatus.NoData
            : DesktopMarketRefreshQueueStatus.Processed;
        var detail = reconciliation.FetchedCount > 0
            ? $"Fetched {reconciliation.FetchedCount} market evidence pair(s)."
            : "Rebuilt market analysis from reusable cache evidence.";
        return new DesktopMarketRefreshQueueResult(
            status,
            reconciliation.Analyses.Count,
            reconciliation.Analyses.FirstOrDefault()?.Name,
            detail);
    }
}

public sealed record DesktopMarketRefreshQueueResult(
    DesktopMarketRefreshQueueStatus Status,
    int ItemCount,
    string? ItemName,
    string? Detail = null)
{
    public static DesktopMarketRefreshQueueResult NotFound { get; } =
        new(DesktopMarketRefreshQueueStatus.NotFound, 0, null);

    public static DesktopMarketRefreshQueueResult NoQueuedItems { get; } =
        new(DesktopMarketRefreshQueueStatus.NoQueuedItems, 0, null);

    public static DesktopMarketRefreshQueueResult NoPlanItems { get; } =
        new(DesktopMarketRefreshQueueStatus.NoPlanItems, 0, null);
}

public enum DesktopMarketRefreshQueueStatus
{
    Processed,
    NoData,
    Failed,
    NoQueuedItems,
    NoPlanItems,
    NotFound
}
