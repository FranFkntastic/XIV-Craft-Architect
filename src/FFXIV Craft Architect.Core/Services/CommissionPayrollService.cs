using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CommissionPayrollService
{
    public CommissionPayrollRun Calculate(
        IEnumerable<CommissionPayrollInputLine> inputLines,
        CommissionPayoutPolicy policy,
        CommissionCostBasis costBasis = CommissionCostBasis.MarketRecommendation)
    {
        ArgumentNullException.ThrowIfNull(inputLines);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.CommissionPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Commission percent must be between 0 and 100.");
        }

        var lines = inputLines
            .Select(ToPayrollLine)
            .ToArray();

        var estimatedMaterialTotal = lines.Sum(line => line.EstimatedMaterialCost);
        var materialBasisTotal = lines.Sum(line => line.MaterialBasis);
        var commissionAmount = RoundGil(estimatedMaterialTotal * policy.CommissionPercent / 100m);
        var totalPay = materialBasisTotal + commissionAmount;
        var warnings = lines
            .SelectMany(line => line.Warnings)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CommissionPayrollRun(
            costBasis,
            policy,
            lines,
            estimatedMaterialTotal,
            materialBasisTotal,
            commissionAmount,
            totalPay,
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
        var materialBasis = input.Responsibility == CommissionMaterialResponsibility.Crafter
            ? estimatedMaterialCost
            : 0m;

        return new CommissionPayrollLine(
            input.ItemId,
            input.Name,
            input.Quantity,
            input.UnitCost,
            input.RequiresHq,
            input.Responsibility,
            estimatedMaterialCost,
            materialBasis,
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
