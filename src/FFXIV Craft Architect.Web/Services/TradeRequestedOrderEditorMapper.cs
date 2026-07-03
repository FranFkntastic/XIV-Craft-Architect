using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record TradeRequestedOrderOutputEditorRow(
    int ItemId,
    string Name,
    int Quantity,
    bool MustBeHq,
    decimal EstimatedSaleValue);

public static class TradeRequestedOrderEditorMapper
{
    private const int MaxQuantity = 9999;

    public static List<TradeRequestedOrderOutputEditorRow> FromOrder(TradeOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return (order.SourceSnapshot?.RootItems ?? Array.Empty<TradeOrderRootItemSnapshot>())
            .Where(item => item.ItemId > 0 && item.Quantity > 0 && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new TradeRequestedOrderOutputEditorRow(
                item.ItemId,
                item.Name,
                ClampQuantity(item.Quantity),
                item.MustBeHq,
                item.EstimatedSaleValue))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.MustBeHq)
            .ToList();
    }

    public static IReadOnlyList<TradeRequestedOrderOutput> ToOutputs(
        IEnumerable<TradeRequestedOrderOutputEditorRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows
            .Where(row => row.ItemId > 0 && !string.IsNullOrWhiteSpace(row.Name))
            .GroupBy(row => (row.ItemId, row.MustBeHq))
            .Select(group =>
            {
                var first = group.First();
                return new TradeRequestedOrderOutput(
                    first.ItemId,
                    first.Name.Trim(),
                    ClampQuantity(group.Sum(row => row.Quantity)),
                    first.MustBeHq,
                    group.Sum(row => row.EstimatedSaleValue));
            })
            .Where(output => output.Quantity > 0)
            .OrderBy(output => output.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(output => output.MustBeHq)
            .ToArray();
    }

    public static bool HasChanges(
        TradeOrder order,
        IEnumerable<TradeRequestedOrderOutputEditorRow> rows)
    {
        var original = FromOrder(order)
            .Select(Normalize)
            .ToArray();
        var current = ToOutputs(rows)
            .Select(output => Normalize(new TradeRequestedOrderOutputEditorRow(
                output.ItemId,
                output.Name,
                output.Quantity,
                output.MustBeHq,
                output.EstimatedSaleValue)))
            .ToArray();

        return !original.SequenceEqual(current);
    }

    public static int ClampQuantity(int quantity)
    {
        return Math.Clamp(quantity, 1, MaxQuantity);
    }

    private static (int ItemId, string Name, int Quantity, bool MustBeHq) Normalize(
        TradeRequestedOrderOutputEditorRow row)
    {
        return (row.ItemId, row.Name.Trim(), ClampQuantity(row.Quantity), row.MustBeHq);
    }
}
