using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class TradeRequestedOrderWorkflow
{
    public static string CreateSuggestedTitle(IReadOnlyList<TradeRequestedOrderOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        var output = outputs
            .Where(output => output.Quantity > 0)
            .OrderByDescending(output => output.EstimatedSaleValue)
            .ThenByDescending(output => output.Quantity)
            .ThenBy(output => output.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return output == null ? string.Empty : $"{output.Name} Commission";
    }

    public static IReadOnlyList<TradeOrderMaterialSnapshot> BuildMaterialSnapshots(
        IReadOnlyList<MaterialAggregate> activeProcurementItems,
        IReadOnlyList<TradeRequestedOrderOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(activeProcurementItems);
        ArgumentNullException.ThrowIfNull(outputs);

        var requestedOutputIds = outputs
            .Select(output => output.ItemId)
            .ToHashSet();

        return activeProcurementItems
            .Where(item => item.TotalQuantity > 0 && !requestedOutputIds.Contains(item.ItemId))
            .Select(item => new TradeOrderMaterialSnapshot(
                item.ItemId,
                item.Name,
                item.TotalQuantity,
                item.RequiresHq,
                item.UnitPrice,
                item.UnitPrice * item.TotalQuantity))
            .ToArray();
    }
}
