using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CommissionPayrollService
{
    public CommissionPayrollRun Calculate(
        IEnumerable<CommissionPayrollInputLine> inputLines,
        CommissionCostBasis costBasis = CommissionCostBasis.MarketRecommendation)
    {
        ArgumentNullException.ThrowIfNull(inputLines);
        var lines = inputLines
            .Select(ToPayrollLine)
            .ToArray();

        var warnings = lines
            .SelectMany(line => line.Warnings)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CommissionPayrollRun(
            costBasis,
            lines,
            warnings);
    }

    private static CommissionPayrollLine ToPayrollLine(CommissionPayrollInputLine input)
    {
        if (input.Quantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Material quantity cannot be negative.");
        }

        if (input.UnitCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Material unit cost cannot be negative.");
        }

        var estimatedMaterialCost = RoundGil(input.Quantity * input.UnitCost);

        return new CommissionPayrollLine(
            input.ItemId,
            input.Name,
            input.Quantity,
            input.UnitCost,
            input.RequiresHq,
            input.Responsibility,
            estimatedMaterialCost,
            input.EvidenceSource,
            input.UnitCostExplanation,
            input.EvidenceTimestampUtc,
            input.Warnings.ToArray());
    }

    private static decimal RoundGil(decimal value)
    {
        return Math.Round(value, 0, MidpointRounding.AwayFromZero);
    }
}
