using System.Windows.Controls;
using FFXIV_Craft_Architect.Core.Models;
using PriceInfo = FFXIV_Craft_Architect.Core.Models.PriceInfo;

namespace FFXIV_Craft_Architect.Coordinators;

/// <summary>
/// Defines the contract for coordinating market logistics calculations and related UI state.
/// </summary>
public interface IMarketLogisticsCoordinator
{
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

    Border CreatePlaceholderCard();

    Border CreateErrorCard(string errorMessage);
}
