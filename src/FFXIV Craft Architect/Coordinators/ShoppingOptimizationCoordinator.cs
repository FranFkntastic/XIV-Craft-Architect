using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Coordinators;

/// <summary>
/// Coordinates shopping optimization operations for crafting plans.
/// Calculates optimal purchase strategies from market board data.
/// </summary>
public class ShoppingOptimizationCoordinator : IShoppingOptimizationCoordinator
{
    private readonly MarketShoppingService _marketShoppingService;
    private readonly ILogger<ShoppingOptimizationCoordinator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShoppingOptimizationCoordinator"/> class.
    /// </summary>
    /// <param name="marketShoppingService">Service for calculating detailed shopping plans.</param>
    /// <param name="logger">Logger instance.</param>
    public ShoppingOptimizationCoordinator(
        MarketShoppingService marketShoppingService,
        ILogger<ShoppingOptimizationCoordinator> logger)
    {
        _marketShoppingService = marketShoppingService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OptimizationResult> OptimizeAsync(
        CraftingPlan plan,
        RecommendationMode mode,
        string worldOrDc,
        bool searchAllNa,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // Step 1: Validate inputs
        if (plan == null || plan.RootItems.Count == 0)
        {
            _logger.LogWarning("[ShoppingOptimization] No plan to optimize");
            return new OptimizationResult(
                OptimizationStatus.NoPlan,
                new List<DetailedShoppingPlan>(),
                0, 0, 0,
                "No plan - build a plan first");
        }

        if (plan.AggregatedMaterials.Count == 0)
        {
            _logger.LogWarning("[ShoppingOptimization] Plan has no materials");
            return new OptimizationResult(
                OptimizationStatus.NoPlan,
                new List<DetailedShoppingPlan>(),
                0, 0, 0,
                "Plan has no materials to purchase");
        }

        try
        {
            // Step 2: Categorize materials
            var (vendorItems, marketItems, untradeableItems) = CategorizeMaterials(plan);

            _logger.LogInformation(
                "[ShoppingOptimization] Starting optimization for {MarketItems} market items, {VendorItems} vendor items, {UntradeableItems} untradeable on {Location}",
                marketItems.Count, vendorItems.Count, untradeableItems.Count,
                searchAllNa ? "all NA DCs" : worldOrDc);

            progress?.Report($"Analyzing {marketItems.Count} items for optimal purchases...");

            // Step 3: Fetch market data for market items only
            List<DetailedShoppingPlan> shoppingPlans = new();

            if (marketItems.Count > 0)
            {
                if (searchAllNa)
                {
                    shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansMultiDCAsync(
                        marketItems, progress, ct, mode);
                }
                else
                {
                    shoppingPlans = await _marketShoppingService.CalculateDetailedShoppingPlansAsync(
                        marketItems, worldOrDc, progress, ct, mode);
                }
            }

            // Step 4: Process results
            var itemsWithOptions = shoppingPlans.Count(p => p.HasOptions && string.IsNullOrEmpty(p.Error));
            var itemsWithoutOptions = shoppingPlans.Count - itemsWithOptions;
            var totalCost = shoppingPlans
                .Where(p => p.RecommendedWorld != null)
                .Sum(p => p.RecommendedWorld!.TotalCost);

            // Step 5: Determine status and build message
            var status = DetermineStatus(itemsWithOptions, itemsWithoutOptions, marketItems.Count);
            var message = BuildResultMessage(
                status, itemsWithOptions, itemsWithoutOptions, 
                vendorItems.Count, untradeableItems.Count, totalCost);

            _logger.LogInformation(
                "[ShoppingOptimization] Completed with status {Status}: {WithOptions} with options, {WithoutOptions} without, total cost {TotalCost:N0}g",
                status, itemsWithOptions, itemsWithoutOptions, totalCost);

            return new OptimizationResult(
                status,
                shoppingPlans,
                totalCost,
                itemsWithOptions,
                itemsWithoutOptions,
                message);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[ShoppingOptimization] Optimization cancelled");
            return new OptimizationResult(
                OptimizationStatus.Cancelled,
                new List<DetailedShoppingPlan>(),
                0, 0, 0,
                "Shopping optimization was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ShoppingOptimization] Optimization failed");
            return new OptimizationResult(
                OptimizationStatus.Failed,
                new List<DetailedShoppingPlan>(),
                0, 0, 0,
                $"Failed to calculate shopping plans: {ex.Message}");
        }
    }

    /// <summary>
    /// Categorizes materials by their price source (vendor, market, or untradeable).
    /// </summary>
    private (List<MaterialAggregate> VendorItems, List<MaterialAggregate> MarketItems, List<MaterialAggregate> UntradeableItems)
        CategorizeMaterials(CraftingPlan plan)
    {
        var vendorItems = new List<MaterialAggregate>();
        var marketItems = new List<MaterialAggregate>();
        var untradeableItems = new List<MaterialAggregate>();

        foreach (var material in plan.AggregatedMaterials)
        {
            // All materials default to market for optimization
            // The actual price source (vendor/market/untradeable) is determined 
            // during price fetching, not during optimization
            marketItems.Add(material);
        }

        return (vendorItems, marketItems, untradeableItems);
    }

    /// <summary>
    /// Determines the optimization status based on results.
    /// </summary>
    private static OptimizationStatus DetermineStatus(int itemsWithOptions, int itemsWithoutOptions, int totalMarketItems)
    {
        if (totalMarketItems == 0)
        {
            // No market items to optimize (all vendor/untradeable)
            return OptimizationStatus.Success;
        }

        if (itemsWithOptions == 0)
        {
            return OptimizationStatus.Failed;
        }

        if (itemsWithoutOptions > 0)
        {
            return OptimizationStatus.PartialSuccess;
        }

        return OptimizationStatus.Success;
    }

    /// <summary>
    /// Builds a human-readable result message.
    /// </summary>
    private static string BuildResultMessage(
        OptimizationStatus status,
        int itemsWithOptions,
        int itemsWithoutOptions,
        int vendorItemCount,
        int untradeableItemCount,
        decimal totalCost)
    {
        var parts = new List<string>();

        switch (status)
        {
            case OptimizationStatus.Success:
                parts.Add($"Shopping optimization complete. {itemsWithOptions} items analyzed.");
                break;
            case OptimizationStatus.PartialSuccess:
                parts.Add($"Partial results: {itemsWithOptions} items with options, {itemsWithoutOptions} failed.");
                break;
            case OptimizationStatus.Failed:
                parts.Add("Failed to find purchase options for any items.");
                break;
            case OptimizationStatus.Cancelled:
                parts.Add("Shopping optimization was cancelled.");
                break;
            case OptimizationStatus.NoPlan:
                parts.Add("No plan available for optimization.");
                break;
        }

        if (totalCost > 0)
        {
            parts.Add($"Total cost: {totalCost:N0}g");
        }

        if (vendorItemCount > 0)
        {
            parts.Add($"({vendorItemCount} vendor items)");
        }

        if (untradeableItemCount > 0)
        {
            parts.Add($"({untradeableItemCount} untradeable)");
        }

        return string.Join(" ", parts);
    }
}
