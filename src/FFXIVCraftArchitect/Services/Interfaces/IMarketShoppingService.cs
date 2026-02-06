using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Models;

namespace FFXIVCraftArchitect.Services.Interfaces;

/// <summary>
/// Service for calculating optimal market board shopping plans.
/// Groups listings by world and applies intelligent filtering.
/// </summary>
public interface IMarketShoppingService
{
    /// <summary>
    /// Sets the world name to ID mapping for travel prohibition checks.
    /// </summary>
    /// <param name="mapping">Dictionary mapping world names to IDs</param>
    void SetWorldNameToIdMapping(Dictionary<string, int> mapping);
    
    /// <summary>
    /// Calculate detailed shopping plans for market board items.
    /// Uses bulk API to fetch all items at once for efficiency, with caching.
    /// </summary>
    /// <param name="marketItems">Items to purchase from market</param>
    /// <param name="dataCenter">Data center name</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="mode">Recommendation mode</param>
    Task<List<DetailedShoppingPlan>> CalculateDetailedShoppingPlansAsync(
        List<MaterialAggregate> marketItems,
        string dataCenter,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost);
    
    /// <summary>
    /// Calculate shopping plans searching across all NA Data Centers for potential savings.
    /// </summary>
    /// <param name="marketItems">Items to purchase from market</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="ct">Cancellation token</param>
    /// <param name="mode">Recommendation mode</param>
    Task<List<DetailedShoppingPlan>> CalculateDetailedShoppingPlansMultiDCAsync(
        List<MaterialAggregate> marketItems,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        RecommendationMode mode = RecommendationMode.MinimizeTotalCost);
    
    /// <summary>
    /// Calculate craft-vs-buy analysis for all craftable items in a plan.
    /// Compares cost of buying finished product vs crafting from components.
    /// </summary>
    /// <param name="plan">Crafting plan</param>
    /// <param name="marketPrices">Current market prices</param>
    List<CraftVsBuyAnalysis> AnalyzeCraftVsBuy(CraftingPlan plan, Dictionary<int, PriceInfo> marketPrices);
}
