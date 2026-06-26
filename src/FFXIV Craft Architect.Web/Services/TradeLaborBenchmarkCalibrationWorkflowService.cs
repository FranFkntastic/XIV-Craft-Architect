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

public enum TradeLaborBenchmarkCalibrationStatus
{
    ReusedFreshEvidence,
    RefreshedEvidence,
    MissingEvidence,
    RefreshFailed
}

public sealed class TradeLaborBenchmarkCalibrationWorkflowService
{
    private static readonly TimeSpan DefaultFreshnessWindow = TimeSpan.FromHours(1);

    private readonly IMarketCacheService _marketCache;
    private readonly MarketShoppingService _marketShoppingService;
    private readonly TradeLaborStandardCalibrationService _laborCalibration;

    public TradeLaborBenchmarkCalibrationWorkflowService(
        IMarketCacheService marketCache,
        MarketShoppingService marketShoppingService,
        TradeLaborStandardCalibrationService laborCalibration)
    {
        _marketCache = marketCache;
        _marketShoppingService = marketShoppingService;
        _laborCalibration = laborCalibration;
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
        MarketEvidenceSet evidence;
        try
        {
            evidence = await MarketEvidenceLoader.LoadAsync(
                _marketCache,
                [TradeLaborStandardCalibrationService.CobaltRivetsItemId],
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

        var item = new MaterialAggregate
        {
            ItemId = TradeLaborStandardCalibrationService.CobaltRivetsItemId,
            Name = TradeLaborStandardCalibrationService.CobaltRivetsItemName,
            TotalQuantity = TradeLaborStandardCalibrationService.CobaltRivetsBenchmarkQuantity,
            RequiresHq = TradeLaborStandardCalibrationService.CobaltRivetsBenchmarkRequiresHq
        };

        var plans = await _marketShoppingService.CalculateDetailedShoppingPlansAsync(
            new MarketAnalysisRequest
            {
                Items = [item],
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

        var shoppingPlan = plans.SingleOrDefault();
        var purchaseEstimate = MarketPurchaseCostProjectionService.Estimate(
            shoppingPlan,
            TradeLaborStandardCalibrationService.CobaltRivetsBenchmarkQuantity,
            hqOnly: TradeLaborStandardCalibrationService.CobaltRivetsBenchmarkRequiresHq,
            includeVendor: false);

        if (!purchaseEstimate.IsDefaultEligible || purchaseEstimate.Cost <= 0)
        {
            return MissingEvidence("Cobalt Rivets benchmark recalculation needs supported market evidence.");
        }

        var legacyCommissionAmount = purchaseEstimate.Cost * request.LegacyCommissionPercent / 100m;
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
}
