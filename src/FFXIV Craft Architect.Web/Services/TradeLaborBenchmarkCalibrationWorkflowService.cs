using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record TradeLaborBenchmarkCalibrationRequest(
    string SelectedDataCenter,
    string SelectedRegion,
    decimal LegacyCommissionPercent,
    int BenchmarkSynthCount,
    TimeSpan? FreshnessWindow = null,
    DateTime? CalibratedAtUtc = null);

public sealed record TradeLaborBenchmarkCalibrationResult(
    TradeLaborBenchmarkCalibrationStatus Status,
    TradeLaborStandard? LaborStandard,
    string Message);

public sealed record TradeLaborBenchmarkPlanPreview(
    string Title,
    string DataCenter,
    IReadOnlyList<TradeLaborBenchmarkPlanPreviewItem> Items);

public sealed record TradeLaborBenchmarkPlanPreviewItem(
    string Name,
    int Quantity,
    int Depth,
    AcquisitionSource Source,
    bool IsActiveProcurement);

public enum TradeLaborBenchmarkCalibrationStatus
{
    ReusedFreshEvidence,
    RefreshedEvidence,
    MissingEvidence,
    RefreshFailed
}

public interface ITradeLaborBenchmarkPlanBuilder
{
    Task<CraftingPlan> BuildManagedCobaltRivetsPlanAsync(
        string dataCenter,
        CancellationToken ct = default);
}

public sealed class TradeLaborBenchmarkPlanBuilder : ITradeLaborBenchmarkPlanBuilder
{
    private readonly RecipeCalculationService _recipeCalculationService;

    public TradeLaborBenchmarkPlanBuilder(RecipeCalculationService recipeCalculationService)
    {
        _recipeCalculationService = recipeCalculationService;
    }

    public Task<CraftingPlan> BuildManagedCobaltRivetsPlanAsync(
        string dataCenter,
        CancellationToken ct = default)
    {
        return _recipeCalculationService.BuildPlanAsync(
            [
                (
                    TradeLaborStandardCalibrationService.CobaltRivetsItemId,
                    TradeLaborStandardCalibrationService.CobaltRivetsItemName,
                    TradeLaborStandardCalibrationService.CobaltRivetsBenchmarkQuantity,
                    TradeLaborStandardCalibrationService.CobaltRivetsBenchmarkRequiresHq)
            ],
            dataCenter,
            dataCenter,
            ct);
    }
}

public sealed class TradeLaborBenchmarkCalibrationWorkflowService
{
    private static readonly TimeSpan DefaultFreshnessWindow = TimeSpan.FromHours(1);

    private readonly IMarketCacheService _marketCache;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly TradeLaborStandardCalibrationService _laborCalibration;
    private readonly ITradeLaborBenchmarkPlanBuilder _benchmarkPlanBuilder;

    public TradeLaborBenchmarkCalibrationWorkflowService(
        IMarketCacheService marketCache,
        MarketShoppingService marketShoppingService,
        TradeLaborStandardCalibrationService laborCalibration,
        ITradeLaborBenchmarkPlanBuilder benchmarkPlanBuilder)
    {
        _marketCache = marketCache;
        _marketShoppingService = marketShoppingService;
        _laborCalibration = laborCalibration;
        _benchmarkPlanBuilder = benchmarkPlanBuilder;
    }

    public async Task<TradeLaborBenchmarkPlanPreview> BuildManagedCobaltRivetsPlanPreviewAsync(
        string dataCenter,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dataCenter))
        {
            throw new ArgumentException("A selected data center is required to build the benchmark plan preview.", nameof(dataCenter));
        }

        var benchmarkPlan = await _benchmarkPlanBuilder.BuildManagedCobaltRivetsPlanAsync(dataCenter, ct);
        var benchmarkRoot = benchmarkPlan.RootItems.FirstOrDefault(item =>
            item.ItemId == TradeLaborStandardCalibrationService.CobaltRivetsItemId);
        if (benchmarkRoot != null)
        {
            AcquisitionPlanningService.SetAcquisitionSource(
                benchmarkRoot,
                AcquisitionSource.Craft,
                AcquisitionSourceReason.SystemDefault);
        }

        var activeProcurementItemIds = AcquisitionPlanningService.GetActiveProcurementItems(benchmarkPlan)
            .Where(item => item.TotalQuantity > 0)
            .Select(item => item.ItemId)
            .ToHashSet();
        var items = benchmarkPlan.RootItems
            .SelectMany(root => FlattenPreviewItems(root, 0, activeProcurementItemIds))
            .ToArray();

        return new TradeLaborBenchmarkPlanPreview(
            "Cobalt Rivets benchmark craft plan",
            dataCenter,
            items);
    }

    public async Task<TradeLaborBenchmarkCalibrationResult> RecalculateManagedCobaltRivetsAsync(
        TradeLaborBenchmarkCalibrationRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.SelectedDataCenter))
        {
            return MissingEvidence("A selected data center is required to calibrate the Cobalt Rivets benchmark.");
        }

        var freshnessWindow = request.FreshnessWindow ?? DefaultFreshnessWindow;
        CraftingPlan benchmarkPlan;
        try
        {
            benchmarkPlan = await _benchmarkPlanBuilder.BuildManagedCobaltRivetsPlanAsync(
                request.SelectedDataCenter,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TradeLaborBenchmarkCalibrationResult(
                TradeLaborBenchmarkCalibrationStatus.RefreshFailed,
                null,
                $"Could not build Cobalt Rivets craft benchmark: {ex.Message}");
        }

        var benchmarkRoot = benchmarkPlan.RootItems.FirstOrDefault(item =>
            item.ItemId == TradeLaborStandardCalibrationService.CobaltRivetsItemId);
        if (benchmarkRoot == null || !benchmarkRoot.CanCraft || !benchmarkRoot.Children.Any())
        {
            return MissingEvidence("Cobalt Rivets benchmark recalculation needs a craftable Cobalt Rivets recipe.");
        }

        AcquisitionPlanningService.SetAcquisitionSource(
            benchmarkRoot,
            AcquisitionSource.Craft,
            AcquisitionSourceReason.SystemDefault);
        var activeItems = AcquisitionPlanningService.GetActiveProcurementItems(benchmarkPlan)
            .Where(item => item.TotalQuantity > 0)
            .ToArray();
        if (activeItems.Length == 0)
        {
            return MissingEvidence("Cobalt Rivets benchmark recalculation needs craft ingredient demand.");
        }

        MarketEvidenceSet evidence;
        try
        {
            evidence = await MarketEvidenceLoader.LoadAsync(
                _marketCache,
                activeItems.Select(item => item.ItemId).Distinct().ToArray(),
                MarketFetchScope.SelectedDataCenter,
                request.SelectedDataCenter,
                request.SelectedRegion,
                maxAge: freshnessWindow,
                forceRefreshData: false,
                progress: progress,
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TradeLaborBenchmarkCalibrationResult(
                TradeLaborBenchmarkCalibrationStatus.RefreshFailed,
                null,
                $"Could not refresh Cobalt Rivets market evidence: {ex.Message}");
        }

        var plans = await _marketShoppingService.CalculateDetailedShoppingPlansAsync(
            new MarketAnalysisRequest
            {
                Items = activeItems,
                Evidence = evidence,
                RecommendationMode = RecommendationMode.MinimizeTotalCost,
                AnalysisConfig = new MarketAnalysisConfig
                {
                    EnableSplitWorld = false,
                    TravelTolerance = 0
                }
            },
            progress,
            ct,
            MarketAnalysisExecutionOptions.Synchronous);

        AcquisitionPlanningService.ApplyCheapestAcquisitionDefaults(benchmarkPlan, plans);
        AcquisitionPlanningService.SetAcquisitionSource(
            benchmarkRoot,
            AcquisitionSource.Craft,
            AcquisitionSourceReason.SystemDefault);
        var benchmarkCost = AcquisitionPlanningService.CalculateCraftCost(benchmarkRoot, plans);

        if (benchmarkCost <= 0)
        {
            return MissingEvidence("Cobalt Rivets benchmark recalculation needs supported craft ingredient market evidence.");
        }

        var legacyCommissionAmount = benchmarkCost * request.LegacyCommissionPercent / 100m;
        if (legacyCommissionAmount <= 0)
        {
            return MissingEvidence("Cobalt Rivets benchmark recalculation needs a positive legacy commission amount.");
        }

        var status = evidence.FetchedCount > 0
            ? TradeLaborBenchmarkCalibrationStatus.RefreshedEvidence
            : TradeLaborBenchmarkCalibrationStatus.ReusedFreshEvidence;
        var evidenceAction = status == TradeLaborBenchmarkCalibrationStatus.RefreshedEvidence
            ? "refreshed"
            : "reused";
        var evidenceText = $"Cobalt Rivets market evidence {evidenceAction} from {request.SelectedDataCenter}.";
        var calibratedAtUtc = request.CalibratedAtUtc ?? DateTime.UtcNow;
        var standard = _laborCalibration.CreateManagedCobaltRivetsBenchmark(
            legacyCommissionAmount,
            request.BenchmarkSynthCount,
            calibratedAtUtc,
            evidenceText);

        return new TradeLaborBenchmarkCalibrationResult(
            status,
            standard,
            $"{evidenceText} Benchmark labor payout is {standard.BenchmarkLaborPayout:N0} gil.");
    }

    private static TradeLaborBenchmarkCalibrationResult MissingEvidence(string message)
    {
        return new TradeLaborBenchmarkCalibrationResult(
            TradeLaborBenchmarkCalibrationStatus.MissingEvidence,
            null,
            message);
    }

    private static IEnumerable<TradeLaborBenchmarkPlanPreviewItem> FlattenPreviewItems(
        PlanNode node,
        int depth,
        IReadOnlySet<int> activeProcurementItemIds)
    {
        yield return new TradeLaborBenchmarkPlanPreviewItem(
            node.Name,
            node.Quantity,
            depth,
            node.Source,
            activeProcurementItemIds.Contains(node.ItemId));

        foreach (var child in node.Children)
        {
            foreach (var item in FlattenPreviewItems(child, depth + 1, activeProcurementItemIds))
            {
                yield return item;
            }
        }
    }
}
