using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class DesktopMarketRefreshQueueService
{
    public const string CheckedWarning = "Desktop market evidence refreshed from Universalis.";
    public const string NoListingsWarning = "No Universalis listings returned for this item.";
    public const string FetchFailedWarningPrefix = "Market evidence refresh failed:";

    private readonly IMarketCacheService _marketCache;

    public DesktopMarketRefreshQueueService(IMarketCacheService marketCache)
    {
        _marketCache = marketCache ?? throw new ArgumentNullException(nameof(marketCache));
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
        return await RefreshMaterialsAsync(session, [material], selectedDataCenter, "selected item market evidence refreshed", ct);
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

        return await RefreshMaterialsAsync(session, materials, selectedDataCenter, "active plan market evidence refreshed", ct);
    }

    private async Task<DesktopMarketRefreshQueueResult> RefreshMaterialsAsync(
        CraftSessionState session,
        IReadOnlyList<MaterialAggregate> materials,
        string? selectedDataCenter,
        string reason,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var dataCenter = string.IsNullOrWhiteSpace(selectedDataCenter)
            ? "Aether"
            : selectedDataCenter;
        IReadOnlyDictionary<(int itemId, string dataCenter), CachedMarketData> cachedData;
        string? fetchError = null;

        try
        {
            var requests = materials.Select(item => (item.ItemId, dataCenter)).ToList();
            await _marketCache.RefreshRequestedAsync(requests, ct: ct);
            cachedData = await _marketCache.GetManyAsync(requests);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cachedData = new Dictionary<(int itemId, string dataCenter), CachedMarketData>();
            fetchError = $"{FetchFailedWarningPrefix} {ex.Message}";
        }

        var analyses = materials
            .Select(item => CreateAnalysis(item, cachedData.GetValueOrDefault((item.ItemId, dataCenter)), dataCenter, now, fetchError))
            .ToArray();

        session.PublishMarketAnalysis(
            analyses,
            session.MarketEvidence.UnavailableMarketItemIds,
            reason,
            session.MarketEvidence.RecommendationMode,
            session.MarketEvidence.Lens,
            session.MarketEvidence.RecipeBasis);

        var status = fetchError != null
            ? DesktopMarketRefreshQueueStatus.Failed
            : analyses.Any(analysis => analysis.WorstDataQualityBucket == MarketDataQualityBucket.Current)
                ? DesktopMarketRefreshQueueStatus.Processed
                : DesktopMarketRefreshQueueStatus.NoData;
        return new DesktopMarketRefreshQueueResult(status, analyses.Length, analyses.FirstOrDefault()?.Name, fetchError);
    }

    private static MarketItemAnalysis CreateAnalysis(
        MaterialAggregate item,
        CachedMarketData? cachedData,
        string dataCenter,
        DateTime loadedAtUtc,
        string? fetchError)
    {
        var listings = cachedData?.Worlds
            .SelectMany(world => world.Listings)
            .Where(listing => !item.RequiresHq || listing.IsHq)
            .Where(listing => listing.PricePerUnit > 0 && listing.Quantity > 0)
            .OrderBy(listing => listing.PricePerUnit)
            .ToList() ?? [];
        var averageUnitPrice = listings.Count > 0
            ? listings.Take(10).Average(listing => listing.PricePerUnit)
            : 0;
        var medianUnitPrice = listings.Count > 0
            ? listings[listings.Count / 2].PricePerUnit
            : 0;
        var warning = fetchError ?? (listings.Count == 0 ? NoListingsWarning : null);

        return new MarketItemAnalysis
        {
            ItemId = item.ItemId,
            Name = item.Name,
            QuantityNeeded = item.TotalQuantity,
            Scope = MarketFetchScope.SelectedDataCenter,
            LoadedAtUtc = loadedAtUtc,
            AnalysisScopeBaselineUnitPrice = (decimal)averageUnitPrice,
            AnalysisScopeAverageUnitPrice = (decimal)averageUnitPrice,
            AnalysisCompetitiveAverageUnitPrice = (decimal)averageUnitPrice,
            AnalysisScopeMedianUnitPrice = medianUnitPrice,
            CompetitiveThresholdUnitPrice = (decimal)averageUnitPrice,
            SaneThresholdUnitPrice = (decimal)averageUnitPrice,
            RequestedDataCenters = [dataCenter],
            PresentDataCenters = listings.Count > 0 ? [dataCenter] : [],
            MissingDataCenters = listings.Count > 0 ? [] : [dataCenter],
            WorstDataQualityBucket = listings.Count > 0 ? MarketDataQualityBucket.Current : MarketDataQualityBucket.Missing,
            Warning = warning
        };
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
