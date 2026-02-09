using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Core.Services;
using Microsoft.Extensions.Logging;
using PriceInfo = FFXIVCraftArchitect.Core.Models.PriceInfo;
using PriceCheckService = FFXIVCraftArchitect.Core.Services.PriceCheckService;

namespace FFXIVCraftArchitect.Coordinators;

/// <summary>
/// Coordinates price refresh operations for crafting plans.
/// Handles fetching prices from external sources and updating plan nodes.
/// </summary>
public class PriceRefreshCoordinator : IPriceRefreshCoordinator
{
    private readonly PriceCheckService _priceCheckService;
    private readonly ILogger<PriceRefreshCoordinator> _logger;

    public PriceRefreshCoordinator(
        PriceCheckService priceCheckService,
        ILogger<PriceRefreshCoordinator> logger)
    {
        _priceCheckService = priceCheckService;
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

    /// <summary>
    /// Creates an adapter to convert internal PriceCheckService progress to PriceRefreshProgress.
    /// </summary>
    private IProgress<(int current, int total, string itemName, PriceFetchStage stage, string? message)>?
        CreateInternalProgress(IProgress<PriceRefreshProgress>? progress, int totalItems)
    {
        if (progress == null) return null;

        return new Progress<(int current, int total, string itemName, PriceFetchStage stage, string? message)>(p =>
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

            progress.Report(new PriceRefreshProgress(p.current, p.total, p.itemName, stage, p.message));
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
