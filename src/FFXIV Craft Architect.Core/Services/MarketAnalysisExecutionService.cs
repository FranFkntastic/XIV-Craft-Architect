using System.Diagnostics;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class MarketAnalysisExecutionService : IMarketAnalysisExecutionService
{
    private const int SuspectCacheRefreshAttemptLimit = 3;

    private readonly IMarketCacheService _marketCache;
    private readonly IMarketPriceLadderAnalysisService _marketPriceLadderAnalysisService;
    private readonly MarketCacheShapeDiagnosticService _cacheShapeDiagnosticService = new();

    public MarketAnalysisExecutionService(
        IMarketCacheService marketCache,
        IMarketPriceLadderAnalysisService marketPriceLadderAnalysisService)
    {
        _marketCache = marketCache;
        _marketPriceLadderAnalysisService = marketPriceLadderAnalysisService;
    }

    public async Task<MarketAnalysisExecutionResult> ExecuteAsync(
        MarketAnalysisExecutionRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        MarketAnalysisExecutionOptions? executionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var items = request.Items.ToList();
        var fetchStopwatch = Stopwatch.StartNew();
        var evidence = await MarketEvidenceLoader.LoadAsync(
            _marketCache,
            items.Select(item => item.ItemId),
            request.Scope,
            request.SelectedDataCenter,
            request.SelectedRegion,
            request.MaxAge,
            progress,
            ct);
        var suspectCacheRefresh = await RefreshSuspectCacheEntriesAsync(evidence, progress, ct);
        evidence = suspectCacheRefresh.Evidence;
        fetchStopwatch.Stop();

        var ladderAnalysisStopwatch = Stopwatch.StartNew();
        var analyses = await _marketPriceLadderAnalysisService.AnalyzeAsync(
            new MarketAnalysisRequest
            {
                Items = items,
                Evidence = evidence,
                RecommendationMode = request.RecommendationMode,
                AnalysisConfig = request.AnalysisConfig,
                ExpectedWorldsByDataCenter = request.ExpectedWorldsByDataCenter
            },
            progress,
            ct,
            executionOptions);
        ladderAnalysisStopwatch.Stop();

        progress?.Report($"Projecting market recommendations for {analyses.Count} items...");
        var shoppingPlanProjectionStopwatch = Stopwatch.StartNew();
        var shoppingPlans = analyses
            .Select(analysis => _marketPriceLadderAnalysisService.ProjectToShoppingPlan(
                analysis,
                request.Lens,
                request.AnalysisConfig))
            .ToList();
        shoppingPlanProjectionStopwatch.Stop();
        analyses = AppendBlockedCacheShapeWarnings(analyses, suspectCacheRefresh.UnresolvedIssues);
        BlockCacheShapeFailures(shoppingPlans, suspectCacheRefresh.UnresolvedIssues);

        return new MarketAnalysisExecutionResult(
            evidence,
            analyses,
            shoppingPlans,
            new MarketAnalysisExecutionTimings(
                fetchStopwatch.Elapsed,
                ladderAnalysisStopwatch.Elapsed,
                shoppingPlanProjectionStopwatch.Elapsed));
    }

    private async Task<SuspectCacheRefreshResult> RefreshSuspectCacheEntriesAsync(
        MarketEvidenceSet evidence,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var initialReport = _cacheShapeDiagnosticService.Analyze(evidence.Entries);
        if (!initialReport.HasIssues)
        {
            return new SuspectCacheRefreshResult(evidence, MarketCacheShapeReport.Empty);
        }

        var suspectPairs = initialReport.Issues
            .Select(issue => (issue.ItemId, issue.DataCenter))
            .Distinct()
            .ToList();
        var unresolvedIssues = new List<MarketCacheShapeIssue>();
        var cleanRefreshedEntries = new Dictionary<(int itemId, string dataCenter), CachedMarketData>();
        var forcedFetchedCount = 0;
        var lastFailureReason = "Refresh did not return clean fresh data.";

        try
        {
            progress?.Report($"Refreshing suspicious cached market evidence for {suspectPairs.Count} item scopes...");
            var pendingPairs = suspectPairs.ToList();
            for (var attempt = 1; attempt <= SuspectCacheRefreshAttemptLimit && pendingPairs.Count > 0; attempt++)
            {
                var forceRefreshStartedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                forcedFetchedCount += await _marketCache.EnsurePopulatedAsync(pendingPairs, TimeSpan.Zero, progress, ct);
                var refreshedEntries = await _marketCache.GetManyAsync(pendingPairs, maxAge: null);
                refreshedEntries = refreshedEntries
                    .Where(entry => entry.Value.FetchedAtUnix >= forceRefreshStartedAtUnix)
                    .ToDictionary(entry => entry.Key, entry => entry.Value);
                var refreshedReport = _cacheShapeDiagnosticService.Analyze(refreshedEntries);
                var nextPendingPairs = new List<(int itemId, string dataCenter)>();

                foreach (var pair in pendingPairs)
                {
                    if (!refreshedEntries.TryGetValue(pair, out var refreshedEntry))
                    {
                        lastFailureReason = $"Refresh attempt {attempt}/{SuspectCacheRefreshAttemptLimit} did not return fresh data.";
                        nextPendingPairs.Add(pair);
                        continue;
                    }

                    var refreshedIssues = GetIssuesForPair(refreshedReport, pair).ToList();
                    if (refreshedIssues.Count > 0)
                    {
                        lastFailureReason = $"Refresh attempt {attempt}/{SuspectCacheRefreshAttemptLimit} returned the same suspect cache shape.";
                        nextPendingPairs.Add(pair);
                        continue;
                    }

                    cleanRefreshedEntries[pair] = refreshedEntry;
                }

                pendingPairs = nextPendingPairs;
            }

            unresolvedIssues.AddRange(pendingPairs.SelectMany(pair =>
                CreateBlockedIssues(GetIssuesForPair(initialReport, pair), lastFailureReason)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            unresolvedIssues.AddRange(CreateBlockedIssues(
                initialReport.Issues,
                $"Refresh failed with {ex.GetType().Name}."));
        }

        var unresolvedPairs = unresolvedIssues
            .Select(issue => (issue.ItemId, issue.DataCenter))
            .ToHashSet();
        var entries = evidence.Entries
            .Where(entry => !suspectPairs.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        foreach (var (pair, entry) in cleanRefreshedEntries)
        {
            if (!unresolvedPairs.Contains(pair))
            {
                entries[pair] = entry;
            }
        }

        var repairedEvidence = new MarketEvidenceSet(
            entries,
            evidence.RequestedPairs,
            evidence.Scope,
            evidence.DataCenters,
            evidence.SelectedDataCenter,
            evidence.SelectedRegion,
            evidence.MaxAge,
            evidence.FetchedCount + forcedFetchedCount,
            DateTime.UtcNow);
        return new SuspectCacheRefreshResult(
            repairedEvidence,
            unresolvedIssues.Count == 0
                ? MarketCacheShapeReport.Empty
                : new MarketCacheShapeReport(unresolvedIssues));
    }

    private static IEnumerable<MarketCacheShapeIssue> GetIssuesForPair(
        MarketCacheShapeReport report,
        (int itemId, string dataCenter) pair)
    {
        return report.Issues.Where(issue =>
            issue.ItemId == pair.itemId &&
            string.Equals(issue.DataCenter, pair.dataCenter, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<MarketCacheShapeIssue> CreateBlockedIssues(
        IEnumerable<MarketCacheShapeIssue> issues,
        string failureReason)
    {
        return issues.Select(issue => issue with { Message = $"{issue.Message} Refresh failure: {failureReason}" });
    }

    private static List<MarketItemAnalysis> AppendBlockedCacheShapeWarnings(
        List<MarketItemAnalysis> analyses,
        MarketCacheShapeReport cacheShapeReport)
    {
        if (!cacheShapeReport.HasIssues)
        {
            return analyses;
        }

        var issuesByItem = cacheShapeReport.Issues
            .GroupBy(issue => issue.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var annotatedAnalyses = new List<MarketItemAnalysis>(analyses.Count);
        foreach (var analysis in analyses)
        {
            if (!issuesByItem.TryGetValue(analysis.ItemId, out var issues))
            {
                annotatedAnalyses.Add(analysis);
                continue;
            }

            var warning = CreateBlockedCacheShapeWarning(issues);
            annotatedAnalyses.Add(CloneWithWarning(
                analysis,
                string.IsNullOrWhiteSpace(analysis.Warning)
                    ? warning
                    : $"{analysis.Warning} {warning}"));
        }

        return annotatedAnalyses;
    }

    private static string CreateBlockedCacheShapeWarning(IReadOnlyList<MarketCacheShapeIssue> issues)
    {
        var context = string.Join(
            "; ",
            issues.Select(issue =>
                $"{issue.DataCenter}/{issue.WorldName}: {issue.RepeatedListingCount} repeated of {issue.TotalWorldListingCount} listings ({issue.Message})"));
        return $"Suspicious cached market evidence could not be refreshed ({context}). Recommendations for this item/scope are blocked.";
    }

    private static MarketItemAnalysis CloneWithWarning(MarketItemAnalysis analysis, string warning)
    {
        return new MarketItemAnalysis
        {
            ItemId = analysis.ItemId,
            Name = analysis.Name,
            QuantityNeeded = analysis.QuantityNeeded,
            Scope = analysis.Scope,
            LoadedAtUtc = analysis.LoadedAtUtc,
            AnalysisScopeBaselineUnitPrice = analysis.AnalysisScopeBaselineUnitPrice,
            AnalysisScopeAverageUnitPrice = analysis.AnalysisScopeAverageUnitPrice,
            AnalysisScopeCompetitiveAverageUnitPrice = analysis.AnalysisScopeCompetitiveAverageUnitPrice,
            AnalysisScopeMedianUnitPrice = analysis.AnalysisScopeMedianUnitPrice,
            CompetitiveThresholdUnitPrice = analysis.CompetitiveThresholdUnitPrice,
            SaneThresholdUnitPrice = analysis.SaneThresholdUnitPrice,
            RequestedDataCenters = analysis.RequestedDataCenters,
            PresentDataCenters = analysis.PresentDataCenters,
            MissingDataCenters = analysis.MissingDataCenters,
            WorstDataQualityBucket = analysis.WorstDataQualityBucket,
            PriceEvaluation = analysis.PriceEvaluation,
            Worlds = analysis.Worlds,
            Warning = warning
        };
    }

    private static void BlockCacheShapeFailures(
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        MarketCacheShapeReport cacheShapeReport)
    {
        if (!cacheShapeReport.HasIssues)
        {
            return;
        }

        var issuesByItem = cacheShapeReport.Issues
            .GroupBy(issue => issue.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var plan in shoppingPlans)
        {
            if (!issuesByItem.TryGetValue(plan.ItemId, out var issues))
            {
                continue;
            }

            var warning = CreateBlockedCacheShapeWarning(issues);
            plan.MarketDataWarning = string.IsNullOrWhiteSpace(plan.MarketDataWarning)
                ? warning
                : $"{plan.MarketDataWarning} {warning}";
            plan.Error = warning;
            plan.RecommendedWorld = null;
            plan.RecommendedSplit = null;
        }
    }

    private sealed record SuspectCacheRefreshResult(
        MarketEvidenceSet Evidence,
        MarketCacheShapeReport UnresolvedIssues);
}
