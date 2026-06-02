using System.Globalization;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

namespace FFXIV_Craft_Architect.Web.Services;

public static class AcquisitionDecisionLedgerOrdering
{
    private static readonly IReadOnlyList<WebTableSortRule<DecisionRow, AcquisitionDecisionLedgerSortColumn>> SortRules =
    [
        WebTableSortRule<DecisionRow, AcquisitionDecisionLedgerSortColumn>.Create(
            AcquisitionDecisionLedgerSortColumn.Item,
            row => row.ItemName,
            StringComparer.OrdinalIgnoreCase),
        WebTableSortRule<DecisionRow, AcquisitionDecisionLedgerSortColumn>.Create(
            AcquisitionDecisionLedgerSortColumn.Source,
            row => RecipePlanDisplayHelpers.GetSourceDisplayName(row.Source),
            StringComparer.OrdinalIgnoreCase),
        WebTableSortRule<DecisionRow, AcquisitionDecisionLedgerSortColumn>.Create(
            AcquisitionDecisionLedgerSortColumn.UsedIn,
            row => row.UsedIn,
            StringComparer.OrdinalIgnoreCase),
        WebTableSortRule<DecisionRow, AcquisitionDecisionLedgerSortColumn>.Create(
            AcquisitionDecisionLedgerSortColumn.Role,
            GetRoleSortValue,
            StringComparer.OrdinalIgnoreCase),
        WebTableSortRule<DecisionRow, AcquisitionDecisionLedgerSortColumn>.Create(
            AcquisitionDecisionLedgerSortColumn.MarketEvidence,
            row => row.MarketEvidence,
            StringComparer.OrdinalIgnoreCase),
        WebTableSortRule<DecisionRow, AcquisitionDecisionLedgerSortColumn>.CreateCustom(
            AcquisitionDecisionLedgerSortColumn.CalculatedTotal,
            (rows, descending) => descending
                ? rows.OrderByDescending(GetCalculatedTotalSortValue)
                : rows.OrderBy(GetCalculatedTotalSortValue))
    ];

    public static IReadOnlyList<DecisionRow> GetOrderedRows(
        IEnumerable<DecisionRow> rows,
        AcquisitionDecisionLedgerSortColumn sortColumn,
        bool sortDescending,
        bool hasActiveSort = true)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (!hasActiveSort)
        {
            return rows.ToList();
        }

        var sortState = hasActiveSort
            ? new WebTableSortState<AcquisitionDecisionLedgerSortColumn>(sortColumn, sortDescending)
            : WebTableSortState<AcquisitionDecisionLedgerSortColumn>.Unsorted;

        return WebTableOrdering.Apply(
            rows,
            sortState,
            SortRules,
            items => items.OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase),
            ordered => ordered
                .ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.NodeId, StringComparer.OrdinalIgnoreCase));
    }

    public static WebTableSortState<AcquisitionDecisionLedgerSortColumn> ToggleSort(
        AcquisitionDecisionLedgerSortColumn? currentColumn,
        bool currentDescending,
        AcquisitionDecisionLedgerSortColumn clickedColumn)
    {
        var current = currentColumn.HasValue
            ? new WebTableSortState<AcquisitionDecisionLedgerSortColumn>(currentColumn.Value, currentDescending)
            : WebTableSortState<AcquisitionDecisionLedgerSortColumn>.Unsorted;

        return current.Toggle(clickedColumn);
    }

    private static string GetRoleSortValue(DecisionRow row)
    {
        if (row.IsFullySuppressed)
        {
            return "4 Skipped";
        }

        if (row.IsActiveProcurement && row.CanBuyFromMarket && row.MarketEvidence == "Not analyzed")
        {
            return "1 Needs analysis";
        }

        if (row.IsActiveProcurement && row.CanBuyFromMarket && row.MarketEvidence == "Needs data")
        {
            return "1 Missing market data";
        }

        if (row.IsActiveProcurement)
        {
            return row.HasSuppressedOccurrences
                ? "2 Partially active"
                : "0 Ready for procurement";
        }

        return row.Source == AcquisitionSource.Craft
            ? "3 Craft path"
            : "3 Reference";
    }

    private static decimal GetCalculatedTotalSortValue(DecisionRow row)
    {
        var digits = new string(row.EstimatedCost.Where(candidate =>
            char.IsDigit(candidate) ||
            candidate == '.' ||
            candidate == '-').ToArray());

        return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : decimal.MaxValue;
    }

}

public enum AcquisitionDecisionLedgerSortColumn
{
    Item,
    Source,
    UsedIn,
    Role,
    MarketEvidence,
    CalculatedTotal
}
