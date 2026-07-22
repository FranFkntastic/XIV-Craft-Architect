using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketEvidenceReconciliationService : IMarketEvidenceReconciliationService
{
    private readonly IMarketAnalysisExecutionService _marketAnalysisExecution;
    private readonly MarketWorldEvidenceReconciliationEngine? _worldReconciliation;

    public MarketEvidenceReconciliationService(IMarketAnalysisExecutionService marketAnalysisExecution)
    {
        _marketAnalysisExecution = marketAnalysisExecution ?? throw new ArgumentNullException(nameof(marketAnalysisExecution));
    }

    public MarketEvidenceReconciliationService(
        IMarketAnalysisExecutionService marketAnalysisExecution,
        IMarketCacheService marketCache,
        IUniversalisService universalis,
        IMarketPriceLadderAnalysisService marketPriceLadderAnalysis)
        : this(marketAnalysisExecution)
    {
        _worldReconciliation = new MarketWorldEvidenceReconciliationEngine(
            marketCache,
            universalis,
            marketPriceLadderAnalysis);
    }

    public async Task<MarketEvidenceReconciliationResult> ReconcileAsync(
        MarketEvidenceReconciliationRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var items = request.Items.ToList();
        var publishedAnalyses = request.PublishedAnalyses.ToDictionary(analysis => analysis.ItemId);
        var publishedPlans = request.PublishedShoppingPlans.ToDictionary(plan => plan.ItemId);
        var requiredDataCenters = MarketFetchScopeResolver.GetDataCenters(
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion);
        var evaluatedAtUtc = CacheTimeHelper.NormalizeToUtc(request.EvaluatedAtUtc ?? DateTime.UtcNow);
        var decisions = items.ToDictionary(
            item => item.ItemId,
            item => EvaluatePublishedEvidence(
                item,
                publishedAnalyses.GetValueOrDefault(item.ItemId),
                publishedPlans.GetValueOrDefault(item.ItemId),
                requiredDataCenters,
                request,
                evaluatedAtUtc));
        var itemsToReconcile = items
            .Where(item => decisions[item.ItemId].Disposition != MarketEvidenceReconciliationDisposition.ReusedPublished)
            .ToList();
        var refreshedDecisions = decisions.ToDictionary(
            entry => entry.Key,
            entry => entry.Value with { Disposition = MarketEvidenceReconciliationDisposition.Refreshed });
        var unavailableDecisions = decisions.ToDictionary(
            entry => entry.Key,
            entry => entry.Value with { Disposition = MarketEvidenceReconciliationDisposition.Unavailable });
        var results = new List<MarketEvidenceReconciliationItemResult>(items.Count);
        var finalAnalyses = new List<MarketItemAnalysis>(items.Count);
        var finalPlans = new List<DetailedShoppingPlan>(items.Count);
        var reconciliationResult = new MarketEvidenceReconciliationResult(
            finalAnalyses,
            finalPlans,
            results,
            itemsToReconcile,
            fetchedCount: 0);

        MarketAnalysisExecutionResult? executionResult = null;
        DateTime? executionStartedAtUtc = null;
        if (itemsToReconcile.Count > 0)
        {
            progress?.Report(CreateProgressMessage(itemsToReconcile.Count, request.Policy.RefreshMode));
            executionStartedAtUtc = DateTime.UtcNow;
            executionResult = await _marketAnalysisExecution.ExecuteAsync(
                new MarketAnalysisExecutionRequest
                {
                    Items = itemsToReconcile,
                    Scope = request.Scope,
                    SelectedDataCenter = request.SelectedDataCenter,
                    SelectedRegion = request.SelectedRegion,
                    MaxAge = request.Policy.RefreshMode == MarketEvidenceRefreshMode.ForceRefresh
                        ? null
                        : request.Policy.ReusableCacheMaxAge,
                    ForceRefreshData = request.Policy.RefreshMode == MarketEvidenceRefreshMode.ForceRefresh,
                    RecommendationMode = request.RecommendationMode,
                    Lens = request.Lens,
                    AnalysisConfig = request.AnalysisConfig,
                    ExpectedWorldsByDataCenter = request.ExpectedWorldsByDataCenter
                },
                progress,
                ct,
                executionOptions);
        }

        ct.ThrowIfCancellationRequested();
        IReadOnlyDictionary<int, MarketItemAnalysis> reconciledAnalyses =
            executionResult?.AnalysesByItemId ??
            executionResult?.Analyses.ToDictionary(analysis => analysis.ItemId) ??
            new Dictionary<int, MarketItemAnalysis>();
        IReadOnlyDictionary<int, DetailedShoppingPlan> reconciledPlans =
            executionResult?.ShoppingPlansByItemId ??
            executionResult?.ShoppingPlans.ToDictionary(plan => plan.ItemId) ??
            new Dictionary<int, DetailedShoppingPlan>();
        var fetchedCount = executionResult?.Evidence.FetchedCount ?? 0;
        reconciliationResult.FetchedCount = fetchedCount;
        var fetchedItemIds = GetFetchedItemIds(
            executionResult,
            executionStartedAtUtc,
            request.Policy.RefreshMode);
        executionResult = null;
        foreach (var item in items)
        {
            var decision = decisions[item.ItemId];
            if (decision.Disposition == MarketEvidenceReconciliationDisposition.ReusedPublished)
            {
                if (publishedAnalyses.TryGetValue(item.ItemId, out var analysis))
                {
                    finalAnalyses.Add(analysis);
                }

                finalPlans.Add(publishedPlans[item.ItemId]);
                results.Add(decision);
                continue;
            }

            var hasAnalysis = reconciledAnalyses.TryGetValue(item.ItemId, out var reconciledAnalysis);
            var hasPlan = reconciledPlans.TryGetValue(item.ItemId, out var reconciledPlan);
            if (hasAnalysis)
            {
                reconciledAnalysis!.LastReconciledAtUtc = evaluatedAtUtc;
                finalAnalyses.Add(reconciledAnalysis!);
            }

            if (hasPlan)
            {
                finalPlans.Add(reconciledPlan!);
            }

            results.Add(hasAnalysis && hasPlan
                ? fetchedItemIds.Contains(item.ItemId)
                    ? refreshedDecisions[item.ItemId]
                    : decision
                : unavailableDecisions[item.ItemId]);
        }

        return reconciliationResult;
    }

    public Task<MarketWorldEvidenceReconciliationResult> ReconcileWorldAsync(
        MarketWorldEvidenceReconciliationRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        if (_worldReconciliation == null)
        {
            throw new InvalidOperationException(
                "World evidence reconciliation requires market cache, source, and ladder-analysis services.");
        }

        return _worldReconciliation.ReconcileAsync(request, progress, ct, executionOptions);
    }

    private static IReadOnlySet<int> GetFetchedItemIds(
        MarketAnalysisExecutionResult? executionResult,
        DateTime? executionStartedAtUtc,
        MarketEvidenceRefreshMode refreshMode)
    {
        if (executionResult == null || executionStartedAtUtc == null)
        {
            return new HashSet<int>();
        }

        if (executionResult.FetchedItemIds is { } fetchedItemIds)
        {
            return fetchedItemIds;
        }

        if (refreshMode == MarketEvidenceRefreshMode.ForceRefresh)
        {
            return executionResult.Analyses.Select(analysis => analysis.ItemId).ToHashSet();
        }

        var thresholdUtc = executionStartedAtUtc.Value - TimeSpan.FromSeconds(1);
        return executionResult.Evidence.Entries
            .Where(entry => CacheTimeHelper.NormalizeToUtc(entry.Value.FetchedAt) >= thresholdUtc)
            .Select(entry => entry.Key.itemId)
            .ToHashSet();
    }

    private static MarketEvidenceReconciliationItemResult EvaluatePublishedEvidence(
        MaterialAggregate item,
        MarketItemAnalysis? analysis,
        DetailedShoppingPlan? plan,
        IReadOnlyList<string> requiredDataCenters,
        MarketEvidenceReconciliationRequest request,
        DateTime evaluatedAtUtc)
    {
        if (request.Policy.RefreshMode == MarketEvidenceRefreshMode.ForceRefresh)
        {
            return Reconcile(item, MarketEvidenceReconciliationReason.ForcedRefresh);
        }

        if (plan == null)
        {
            return Reconcile(item, MarketEvidenceReconciliationReason.PublishedEvidenceMissing);
        }

        if (IsVendorPlan(plan) && plan.QuantityNeeded == item.TotalQuantity)
        {
            return Reuse(item);
        }

        if (analysis == null)
        {
            return Reconcile(item, MarketEvidenceReconciliationReason.PublishedEvidenceMissing);
        }

        if (analysis.QuantityNeeded != item.TotalQuantity || plan.QuantityNeeded != item.TotalQuantity)
        {
            return Reconcile(item, MarketEvidenceReconciliationReason.QuantityChanged);
        }

        if (analysis.Scope != request.Scope &&
            !(analysis.Scope == MarketFetchScope.EntireRegion &&
              request.Scope == MarketFetchScope.SelectedDataCenter))
        {
            return Reconcile(item, MarketEvidenceReconciliationReason.ScopeChanged);
        }

        if (request.Policy.RequireCompleteScope && !HasCompleteScope(analysis, requiredDataCenters))
        {
            return Reconcile(item, MarketEvidenceReconciliationReason.ScopeIncomplete);
        }

        var relevantWorlds = analysis.Worlds
            .Where(world => requiredDataCenters.Contains(world.DataCenter, StringComparer.OrdinalIgnoreCase))
            .Where(world => world.DataQualityBucket != MarketDataQualityBucket.Missing)
            .ToList();
        if (relevantWorlds.Count == 0)
        {
            var negativeEvidenceAge = GetElapsed(analysis.LoadedAtUtc, evaluatedAtUtc);
            return negativeEvidenceAge <= request.Policy.ReusableCacheMaxAge
                ? Reuse(item, negativeEvidenceAge)
                : Reconcile(item, MarketEvidenceReconciliationReason.RecommendationExpired, negativeEvidenceAge);
        }

        TimeSpan? oldestEvidenceAge = null;
        foreach (var world in relevantWorlds)
        {
            var age = GetCurrentAge(world, analysis.LoadedAtUtc, evaluatedAtUtc);
            if (age == null)
            {
                return Reconcile(item, MarketEvidenceReconciliationReason.FreshnessUnverifiable);
            }

            oldestEvidenceAge = oldestEvidenceAge == null || age > oldestEvidenceAge
                ? age
                : oldestEvidenceAge;
        }

        if (MarketEvidenceFreshness.IsRecommendationEligible(
                oldestEvidenceAge,
                request.Policy.MaximumRecommendationAge))
        {
            return Reuse(item, oldestEvidenceAge);
        }

        if (analysis.LastReconciledAtUtc.HasValue &&
            GetElapsed(analysis.LastReconciledAtUtc.Value, evaluatedAtUtc) <= request.Policy.ReusableCacheMaxAge)
        {
            return Reuse(
                item,
                oldestEvidenceAge,
                MarketEvidenceReconciliationReason.RecentlyReconciled);
        }

        return Reconcile(item, MarketEvidenceReconciliationReason.RecommendationExpired, oldestEvidenceAge);
    }

    private static bool HasCompleteScope(
        MarketItemAnalysis analysis,
        IReadOnlyList<string> requiredDataCenters)
    {
        var requested = analysis.RequestedDataCenters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = analysis.MissingDataCenters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requiredDataCenters.All(requested.Contains) &&
               requiredDataCenters.All(dataCenter => !missing.Contains(dataCenter));
    }

    private static TimeSpan? GetCurrentAge(
        WorldMarketAnalysis world,
        DateTime analysisLoadedAtUtc,
        DateTime evaluatedAtUtc)
    {
        var evidenceTimestamp = world.MarketUploadedAtUtc ?? world.FetchedAtUtc;
        if (evidenceTimestamp.HasValue)
        {
            return GetElapsed(evidenceTimestamp.Value, evaluatedAtUtc);
        }

        if (!world.DataAge.HasValue)
        {
            return null;
        }

        return world.DataAge.Value + GetElapsed(analysisLoadedAtUtc, evaluatedAtUtc);
    }

    private static TimeSpan GetElapsed(DateTime fromUtc, DateTime toUtc)
    {
        var elapsed = CacheTimeHelper.NormalizeToUtc(toUtc) - CacheTimeHelper.NormalizeToUtc(fromUtc);
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private static MarketEvidenceReconciliationItemResult Reuse(
        MaterialAggregate item,
        TimeSpan? oldestEvidenceAge = null,
        MarketEvidenceReconciliationReason reason = MarketEvidenceReconciliationReason.PublishedEvidenceEligible) =>
        new(
            item.ItemId,
            item.Name,
            MarketEvidenceReconciliationDisposition.ReusedPublished,
            reason,
            oldestEvidenceAge);

    private static MarketEvidenceReconciliationItemResult Reconcile(
        MaterialAggregate item,
        MarketEvidenceReconciliationReason reason,
        TimeSpan? oldestEvidenceAge = null) =>
        new(
            item.ItemId,
            item.Name,
            MarketEvidenceReconciliationDisposition.RebuiltFromCache,
            reason,
            oldestEvidenceAge);

    private static bool IsVendorPlan(DetailedShoppingPlan plan) =>
        plan.Vendors.Count > 0 ||
        string.Equals(
            plan.RecommendedWorld?.WorldName,
            MarketShoppingConstants.VendorWorldName,
            StringComparison.OrdinalIgnoreCase);

    private static string CreateProgressMessage(int itemCount, MarketEvidenceRefreshMode refreshMode) =>
        refreshMode == MarketEvidenceRefreshMode.ForceRefresh
            ? $"Refreshing market evidence for {itemCount} items..."
            : $"Reconciling market evidence for {itemCount} items...";

    private static void Validate(MarketEvidenceReconciliationRequest request)
    {
        if (request.Policy.ReusableCacheMaxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Policy.ReusableCacheMaxAge),
                request.Policy.ReusableCacheMaxAge,
                "Reusable cache age must be greater than zero.");
        }

        if (request.Policy.MaximumRecommendationAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Policy.MaximumRecommendationAge),
                request.Policy.MaximumRecommendationAge,
                "Recommendation evidence age must be greater than zero.");
        }

        if (request.Items.Select(item => item.ItemId).Distinct().Count() != request.Items.Count)
        {
            throw new ArgumentException("Market evidence reconciliation requires unique item IDs.", nameof(request));
        }

        if (request.PublishedAnalyses.Select(item => item.ItemId).Distinct().Count() != request.PublishedAnalyses.Count)
        {
            throw new ArgumentException("Published market analyses contain duplicate item IDs.", nameof(request));
        }

        if (request.PublishedShoppingPlans.Select(item => item.ItemId).Distinct().Count() != request.PublishedShoppingPlans.Count)
        {
            throw new ArgumentException("Published shopping plans contain duplicate item IDs.", nameof(request));
        }
    }
}
