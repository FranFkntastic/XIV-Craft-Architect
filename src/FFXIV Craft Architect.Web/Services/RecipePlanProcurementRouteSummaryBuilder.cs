using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public static class RecipePlanProcurementRouteSummaryBuilder
{
    public static IReadOnlyDictionary<int, RecipePlanProcurementRouteSummary> Build(
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        string fallbackDataCenter)
    {
        ArgumentNullException.ThrowIfNull(shoppingPlans);

        var cards = ProcurementWorldCardBuilder.BuildWorldCards(shoppingPlans, fallbackDataCenter);
        return cards
            .SelectMany(card => card.Items.Select(item => (item.ItemId, Card: card)))
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => CreateSummary(group.Key, group.Select(entry => entry.Card).DistinctBy(ProcurementWorldCardBuilder.GetWorldKey)));
    }

    private static RecipePlanProcurementRouteSummary CreateSummary(
        int itemId,
        IEnumerable<WorldProcurementCardModel> destinations)
    {
        var cards = destinations.ToList();
        var dataCenterCount = cards
            .Where(card => !card.IsVendor)
            .Select(card => card.DataCenter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var label = cards.Count switch
        {
            1 => cards[0].IsVendor ? "Vendor" : cards[0].WorldName,
            _ => $"{cards.Count:N0} stops · {dataCenterCount:N0} DC"
        };

        return new RecipePlanProcurementRouteSummary(itemId, cards.Count, dataCenterCount, label);
    }
}

public sealed record RecipePlanProcurementRouteSummary(
    int ItemId,
    int DestinationCount,
    int DataCenterCount,
    string Label);
