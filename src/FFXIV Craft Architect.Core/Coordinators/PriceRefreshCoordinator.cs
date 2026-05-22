using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.Logging;
using PriceInfo = FFXIV_Craft_Architect.Core.Models.PriceInfo;

namespace FFXIV_Craft_Architect.Core.Coordinators;

/// <summary>
/// Coordinates price refresh operations for crafting plans.
/// Handles fetching prices from external sources and updating plan nodes.
/// </summary>
public class PriceRefreshCoordinator : IPriceRefreshCoordinator
{
    private readonly PriceCheckService _priceCheckService;
    private readonly IMarketCacheService _marketCache;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<PriceRefreshCoordinator> _logger;

    public PriceRefreshCoordinator(
        PriceCheckService priceCheckService,
        IMarketCacheService marketCache,
        ISettingsService settingsService,
        ILogger<PriceRefreshCoordinator> logger)
    {
        _priceCheckService = priceCheckService;
        _marketCache = marketCache;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PriceRefreshResult> RefreshPlanAsync(
        CraftingPlan plan,
        string worldOrDc,
        IProgress<PriceRefreshProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            _logger.LogWarning("[PriceRefresh] No plan to refresh prices for");
            return new PriceRefreshResult(
                PriceRefreshStatus.NoPlan,
                new Dictionary<int, PriceInfo>(),
                "No plan - build a plan first");
        }

        try
        {
            // Collect all unique items from the plan
            var allItems = new List<(int itemId, string name, int quantity)>();
            CollectAllItemsWithQuantity(plan.RootItems, allItems);

            _logger.LogInformation("[PriceRefresh] Starting price refresh for {Count} items on {WorldOrDc}",
                allItems.Count, worldOrDc);

            progress?.Report(new PriceRefreshProgress(0, allItems.Count, "", PriceRefreshStage.Starting,
                $"Fetching prices for {allItems.Count} items..."));

            // Fetch prices using the underlying service
            var prices = await _priceCheckService.GetBestPricesBulkAsync(
                allItems.Select(i => (i.itemId, i.name)).ToList(),
                worldOrDc,
                ct,
                CreateInternalProgress(progress, allItems.Count),
                forceRefresh: true);

            progress?.Report(new PriceRefreshProgress(allItems.Count, allItems.Count, "", PriceRefreshStage.Updating,
                "Updating plan nodes with new prices..."));

            // Update plan nodes with fetched prices
            int successCount = 0;
            int failedCount = 0;
            int cachedCount = 0;

            foreach (var kvp in prices)
            {
                var priceInfo = kvp.Value;

                switch (priceInfo.Source)
                {
                    case PriceSource.Unknown:
                        failedCount++;
                        break;
                    case PriceSource.Vendor:
                    case PriceSource.Market:
                        successCount++;
                        break;
                    default:
                        cachedCount++;
                        break;
                }

                UpdateSingleNodePrice(plan.RootItems, kvp.Key, priceInfo);
            }

            progress?.Report(new PriceRefreshProgress(allItems.Count, allItems.Count, "", PriceRefreshStage.Complete,
                "Price refresh complete"));

            var status = failedCount == 0
                ? PriceRefreshStatus.Success
                : successCount > 0
                    ? PriceRefreshStatus.PartialSuccess
                    : PriceRefreshStatus.Failed;

            var message = BuildResultMessage(successCount, failedCount, cachedCount, plan);

            _logger.LogInformation("[PriceRefresh] Completed: {Success} success, {Failed} failed, {Cached} cached",
                successCount, failedCount, cachedCount);

            return new PriceRefreshResult(status, prices, message, successCount, failedCount, cachedCount);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[PriceRefresh] Price refresh cancelled");
            return new PriceRefreshResult(
                PriceRefreshStatus.Cancelled,
                new Dictionary<int, PriceInfo>(),
                "Price refresh was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PriceRefresh] Failed to refresh prices");
            return new PriceRefreshResult(
                PriceRefreshStatus.Failed,
                new Dictionary<int, PriceInfo>(),
                $"Failed to fetch prices: {ex.Message}. Cached prices preserved.");
        }
    }

    /// <inheritdoc />
    public async Task<PriceRefreshResult> RefreshItemAsync(
        int itemId,
        string itemName,
        string worldOrDc,
        CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("[PriceRefresh] Refreshing single item: {ItemName} ({ItemId})",
                itemName, itemId);

            var priceInfo = await _priceCheckService.GetBestPriceAsync(itemId, itemName, worldOrDc, ct);

            var prices = new Dictionary<int, PriceInfo> { [itemId] = priceInfo };

            var status = priceInfo.Source == PriceSource.Unknown
                ? PriceRefreshStatus.Failed
                : PriceRefreshStatus.Success;

            var message = priceInfo.Source == PriceSource.Unknown
                ? $"Failed to fetch price for {itemName}"
                : $"Updated price for {itemName}: {priceInfo.UnitPrice:N0}g";

            return new PriceRefreshResult(
                status,
                prices,
                message,
                status == PriceRefreshStatus.Success ? 1 : 0,
                status == PriceRefreshStatus.Failed ? 1 : 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PriceRefresh] Failed to refresh price for {ItemName}", itemName);
            return new PriceRefreshResult(
                PriceRefreshStatus.Failed,
                new Dictionary<int, PriceInfo>(),
                $"Failed to fetch price for {itemName}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<PlanPriceRefreshContext> FetchPlanPricesAsync(
        CraftingPlan plan,
        string dataCenter,
        string worldOrDc,
        bool searchAllNa,
        bool forceRefresh = false,
        IProgress<PriceRefreshProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            return new PlanPriceRefreshContext(
                new List<(int itemId, string name, int quantity)>(),
                new Dictionary<int, PriceInfo>(),
                new HashSet<int>(),
                false,
                new HashSet<(int itemId, string dataCenter)>(),
                new Dictionary<int, (int CachedDataCenterCount, int CachedWorldCount)>(),
                Array.Empty<string>());
        }

        var allItems = new List<(int itemId, string name, int quantity)>();
        CollectAllItemsWithQuantity(plan.RootItems, allItems);

        progress?.Report(new PriceRefreshProgress(
            0,
            allItems.Count,
            string.Empty,
            PriceRefreshStage.Starting,
            $"Fetching prices for {allItems.Count} items..."));

        var allMaterials = plan.AggregatedMaterials ?? new List<MaterialAggregate>();
        var warmCacheForCraftedItems = _settingsService.Get("market.warm_cache_for_crafted_items", false);
        var cacheCandidateItemIds = warmCacheForCraftedItems
            ? allItems.Select(i => i.itemId).Distinct().ToHashSet()
            : allMaterials.Where(m => m.TotalQuantity > 0).Select(m => m.ItemId).ToHashSet();

        var scopeDataCenters = searchAllNa
            ? new[] { "Aether", "Primal", "Crystal", "Dynamis" }
            : new[] { dataCenter };

        var fetchedThisRunKeys = new HashSet<(int itemId, string dataCenter)>();
        var dataScopeByItemId = new Dictionary<int, (int CachedDataCenterCount, int CachedWorldCount)>();

        if (cacheCandidateItemIds.Count > 0)
        {
            var cacheProgress = new Progress<string>(msg =>
            {
                progress?.Report(new PriceRefreshProgress(
                    0,
                    cacheCandidateItemIds.Count,
                    string.Empty,
                    PriceRefreshStage.Fetching,
                    $"Fetching market data: {msg}"));
            });

            var cacheRequests = new List<(int itemId, string dataCenter)>();
            if (searchAllNa)
            {
                var naDataCenters = new[] { "Aether", "Primal", "Crystal", "Dynamis" };
                foreach (var itemId in cacheCandidateItemIds)
                {
                    foreach (var itemDc in naDataCenters)
                    {
                        cacheRequests.Add((itemId, itemDc));
                    }
                }
            }
            else
            {
                cacheRequests = cacheCandidateItemIds.Select(itemId => (itemId, dataCenter)).ToList();
            }

            var effectiveCacheTtl = forceRefresh
                ? TimeSpan.Zero
                : TimeSpan.FromHours(_settingsService.Get("market.cache_ttl_hours", 3.0));
            var missingBeforePopulate = await _marketCache.GetMissingAsync(cacheRequests, effectiveCacheTtl);
            fetchedThisRunKeys = missingBeforePopulate.ToHashSet();
            await _marketCache.EnsurePopulatedAsync(cacheRequests, effectiveCacheTtl, cacheProgress, ct);

            foreach (var itemId in cacheCandidateItemIds)
            {
                int cachedDataCenterCount = 0;
                int cachedWorldCount = 0;

                foreach (var itemDc in scopeDataCenters)
                {
                    var (cachedData, _) = await _marketCache.GetWithStaleAsync(itemId, itemDc, effectiveCacheTtl);
                    if (cachedData == null)
                    {
                        continue;
                    }

                    cachedDataCenterCount++;
                    cachedWorldCount += cachedData.Worlds.Count;
                }

                dataScopeByItemId[itemId] = (cachedDataCenterCount, cachedWorldCount);
            }
        }

        Dictionary<int, PriceInfo> prices;
        if (searchAllNa)
        {
            prices = await _priceCheckService.GetBestPricesMultiDCAsync(
                allItems.Select(i => (i.itemId, i.name)).ToList(),
                ct,
                CreateInternalProgress(progress, allItems.Count),
                forceRefresh: false);
        }
        else
        {
            prices = await _priceCheckService.GetBestPricesBulkAsync(
                allItems.Select(i => (i.itemId, i.name)).ToList(),
                worldOrDc,
                ct,
                CreateInternalProgress(progress, allItems.Count),
                forceRefresh: false);
        }

        progress?.Report(new PriceRefreshProgress(
            allItems.Count,
            allItems.Count,
            string.Empty,
            PriceRefreshStage.Complete,
            "Price refresh complete"));

        return new PlanPriceRefreshContext(
            allItems,
            prices,
            cacheCandidateItemIds,
            warmCacheForCraftedItems,
            fetchedThisRunKeys,
            dataScopeByItemId,
            scopeDataCenters);
    }

    /// <inheritdoc />
    public async Task<PlanCacheInspectionContext> InspectPlanCacheAsync(
        CraftingPlan plan,
        string dataCenter,
        bool searchAllNa,
        CancellationToken ct = default)
    {
        if (plan == null || plan.RootItems.Count == 0)
        {
            return new PlanCacheInspectionContext(
                new List<(int itemId, string name, int quantity)>(),
                new HashSet<int>(),
                new Dictionary<int, ItemCacheInspectionResult>(),
                Array.Empty<string>());
        }

        var allItems = new List<(int itemId, string name, int quantity)>();
        CollectAllItemsWithQuantity(plan.RootItems, allItems);

        var allMaterials = plan.AggregatedMaterials ?? new List<MaterialAggregate>();
        var warmCacheForCraftedItems = _settingsService.Get("market.warm_cache_for_crafted_items", false);
        var cacheCandidateItemIds = warmCacheForCraftedItems
            ? allItems.Select(i => i.itemId).Distinct().ToHashSet()
            : allMaterials.Where(m => m.TotalQuantity > 0).Select(m => m.ItemId).ToHashSet();

        var scopeDataCenters = searchAllNa
            ? new[] { "Aether", "Primal", "Crystal", "Dynamis" }
            : new[] { dataCenter };

        var cacheTtl = TimeSpan.FromHours(_settingsService.Get("market.cache_ttl_hours", 3.0));
        var itemCacheByItemId = new Dictionary<int, ItemCacheInspectionResult>();

        foreach (var itemId in cacheCandidateItemIds)
        {
            ct.ThrowIfCancellationRequested();

            int cachedDataCenterCount = 0;
            int cachedWorldCount = 0;
            bool hasFreshCache = false;
            DateTime? latestFetchedAtUtc = null;
            decimal cachedUnitPrice = 0;

            foreach (var itemDc in scopeDataCenters)
            {
                ct.ThrowIfCancellationRequested();

                var (cachedData, isStale) = await _marketCache.GetWithStaleAsync(itemId, itemDc, cacheTtl);
                if (cachedData == null)
                {
                    continue;
                }

                cachedDataCenterCount++;
                cachedWorldCount += cachedData.Worlds.Count;

                if (!isStale)
                {
                    hasFreshCache = true;
                }

                if (!latestFetchedAtUtc.HasValue || cachedData.FetchedAt > latestFetchedAtUtc.Value)
                {
                    latestFetchedAtUtc = cachedData.FetchedAt;
                    cachedUnitPrice = cachedData.DCAveragePrice;
                }
            }

            itemCacheByItemId[itemId] = new ItemCacheInspectionResult(
                HasCache: cachedDataCenterCount > 0,
                HasFreshCache: hasFreshCache,
                LatestFetchedAtUtc: latestFetchedAtUtc,
                CachedUnitPrice: cachedUnitPrice,
                CachedDataCenterCount: cachedDataCenterCount,
                CachedWorldCount: cachedWorldCount);
        }

        return new PlanCacheInspectionContext(
            allItems,
            cacheCandidateItemIds,
            itemCacheByItemId,
            scopeDataCenters);
    }

    /// <summary>
    /// Creates an adapter to convert internal PriceCheckService progress to PriceRefreshProgress.
    /// </summary>
    private IProgress<(int completed, int total, string currentItem, PriceFetchStage stage, string message)>?
        CreateInternalProgress(IProgress<PriceRefreshProgress>? progress, int totalItems)
    {
        if (progress == null) return null;

        return new Progress<(int completed, int total, string currentItem, PriceFetchStage stage, string message)>(p =>
        {
            var stage = p.stage switch
            {
                PriceFetchStage.CheckingCache => PriceRefreshStage.Starting,
                PriceFetchStage.FetchingGarlandData => PriceRefreshStage.Fetching,
                PriceFetchStage.FetchingMarketData => PriceRefreshStage.Fetching,
                PriceFetchStage.ProcessingResults => PriceRefreshStage.Updating,
                PriceFetchStage.Complete => PriceRefreshStage.Complete,
                _ => PriceRefreshStage.Fetching
            };

            progress.Report(new PriceRefreshProgress(p.completed, p.total, p.currentItem, stage, p.message));
        });
    }

    /// <summary>
    /// Collects all unique items with their quantities from plan nodes.
    /// </summary>
    private void CollectAllItemsWithQuantity(List<PlanNode> nodes, List<(int itemId, string name, int quantity)> items)
    {
        foreach (var node in nodes)
        {
            if (!items.Any(i => i.itemId == node.ItemId))
            {
                items.Add((node.ItemId, node.Name, node.Quantity));
            }

            // Only recurse into children if this item is being crafted
            // If it's being bought (VendorBuy/MarketBuy), its children aren't needed
            if (node.Children?.Any() == true && node.Source == AcquisitionSource.Craft)
            {
                CollectAllItemsWithQuantity(node.Children, items);
            }
        }
    }

    /// <summary>
    /// Updates a single node's price information in the plan tree.
    /// </summary>
    private void UpdateSingleNodePrice(List<PlanNode> nodes, int itemId, PriceInfo priceInfo)
    {
        foreach (var node in nodes)
        {
            if (node.ItemId == itemId)
            {
                node.MarketPrice = priceInfo.UnitPrice;
                if (node.CanBeHq)
                {
                    node.HqMarketPrice = priceInfo.HqUnitPrice > 0 ? priceInfo.HqUnitPrice : 0;
                }
                node.PriceSource = priceInfo.Source;
                node.PriceSourceDetails = priceInfo.SourceDetails;
            }

            if (node.Children?.Any() == true)
            {
                UpdateSingleNodePrice(node.Children, itemId, priceInfo);
            }
        }
    }

    /// <summary>
    /// Builds a human-readable result message based on the refresh outcome.
    /// </summary>
    private string BuildResultMessage(int successCount, int failedCount, int cachedCount, CraftingPlan plan)
    {
        var totalCost = plan.AggregatedMaterials.Sum(m => m.TotalCost);

        if (failedCount > 0 && successCount == 0)
        {
            return $"Price fetch failed! Using cached prices. Total: {totalCost:N0}g";
        }
        else if (failedCount > 0)
        {
            return $"Prices updated! Total: {totalCost:N0}g ({successCount} success, {failedCount} failed, {cachedCount} cached)";
        }
        else
        {
            return $"Prices fetched! Total: {totalCost:N0}g ({successCount} items)";
        }
    }
}
