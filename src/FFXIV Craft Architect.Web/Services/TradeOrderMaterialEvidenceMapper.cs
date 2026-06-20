using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public static class TradeOrderMaterialEvidenceMapper
{
    public static IReadOnlyList<TradeOrderMaterialSnapshot> ToMaterialSnapshots(
        IEnumerable<CommissionPayrollInputLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return lines.Select(ToMaterialSnapshot).ToArray();
    }

    public static TradeOrderMaterialSnapshot ToMaterialSnapshot(CommissionPayrollInputLine material)
    {
        ArgumentNullException.ThrowIfNull(material);

        var totalCost = Math.Round(
            material.UnitCost * material.Quantity,
            0,
            MidpointRounding.AwayFromZero);

        return new TradeOrderMaterialSnapshot(
            material.ItemId,
            material.Name,
            material.Quantity,
            material.RequiresHq,
            material.UnitCost,
            totalCost,
            material.EvidenceSource,
            material.UnitCostExplanation,
            material.EvidenceTimestampUtc,
            material.Warnings.ToArray());
    }
}
