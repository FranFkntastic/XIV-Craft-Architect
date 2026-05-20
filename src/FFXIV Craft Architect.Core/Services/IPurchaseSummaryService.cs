using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Centralized service for creating consistent purchase summaries.
/// Ensures display values show actual purchase quantities (from listings)
/// rather than idealized quantities needed.
/// </summary>
public interface IPurchaseSummaryService
{
    /// <summary>
    /// Creates a purchase summary from a detailed shopping plan.
    /// </summary>
    PurchaseSummary CreateSummary(DetailedShoppingPlan plan);
    
    /// <summary>
    /// Creates purchase summaries from multiple shopping plans.
    /// </summary>
    List<PurchaseSummary> CreateSummaries(IEnumerable<DetailedShoppingPlan> plans);
    
    /// <summary>
    /// Creates a purchase summary for a split-world purchase portion.
    /// </summary>
    PurchaseSummary CreateSplitSummary(
        SplitWorldPurchase split,
        int quantityNeeded,
        string itemName,
        int itemId,
        int iconId);
}
