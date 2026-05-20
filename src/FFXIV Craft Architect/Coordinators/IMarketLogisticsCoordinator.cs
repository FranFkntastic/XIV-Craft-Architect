using System.Windows.Controls;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.ViewModels;
using PriceInfo = FFXIV_Craft_Architect.Core.Models.PriceInfo;

namespace FFXIV_Craft_Architect.Coordinators;

/// <summary>
/// Defines the contract for coordinating market logistics calculations and related UI state.
/// </summary>
public interface IMarketLogisticsCoordinator
{
    #region Selection State (MVVM Binding)
    
    /// <summary>
    /// The ViewModel for the currently selected expanded panel, or null if nothing is selected.
    /// This property is the binding target for the ContentControl in the split-pane view.
    /// </summary>
    ExpandedPanelViewModel? SelectedExpandedPanel { get; }
    
    /// <summary>
    /// The ItemId of the currently selected card, or null if nothing is selected.
    /// Used for card highlighting in the collapsed cards grid.
    /// </summary>
    int? SelectedItemId { get; }
    
    /// <summary>
    /// Sets the available shopping plans. The coordinator needs this data to resolve
    /// item IDs to DetailedShoppingPlan objects when creating ExpandedPanelViewModels.
    /// </summary>
    /// <param name="plans">The list of available shopping plans.</param>
    void SetAvailablePlans(IReadOnlyList<DetailedShoppingPlan> plans);
    
    /// <summary>
    /// Selects an item by its ID, creating an ExpandedPanelViewModel and updating
    /// the SelectedExpandedPanel property. If the same item is already selected,
    /// this clears the selection (toggle behavior).
    /// </summary>
    /// <param name="itemId">The item ID to select.</param>
    void SelectItem(int itemId);
    
    /// <summary>
    /// Clears the current selection, setting SelectedExpandedPanel to null.
    /// This triggers the placeholder display in the split-pane view.
    /// </summary>
    void ClearSelection();

    /// <summary>
    /// Opens a detailed recommendation window for the specified shopping plan.
    /// Works for both split-world and single-world recommendations.
    /// </summary>
    /// <param name="plan">The shopping plan to display in the window.</param>
    void OpenDetailsWindow(DetailedShoppingPlan plan);

    /// <summary>
    /// Opens the split world recommendation window for the specified shopping plan.
    /// Kept for compatibility; delegates to <see cref="OpenDetailsWindow"/>.
    /// </summary>
    /// <param name="plan">The shopping plan to display in the window.</param>
    void OpenSplitWorldWindow(DetailedShoppingPlan plan);
    
    #endregion
    
    #region Market Logistics
    
    Border CreateLoadingState(string dataCenter, int itemCount, bool searchAllNA);

    Task<MarketLogisticsCoordinator.MarketLogisticsResult> CalculateMarketLogisticsAsync(
        CraftingPlan plan,
        Dictionary<int, PriceInfo> prices,
        string dataCenter,
        bool searchAllNA,
        RecommendationMode mode,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    MarketLogisticsCoordinator.MarketLogisticsResult CalculateCachedLogistics(
        CraftingPlan plan,
        Dictionary<int, PriceInfo> prices,
        List<DetailedShoppingPlan>? savedPlans);

    MarketSummaryData CalculateSummaryData(CraftingPlan plan, Dictionary<int, PriceInfo> prices);
    
    #endregion
}
