using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class TradePaymentCalculator
{
    public TradePaymentComparisonSummary Calculate(TradePaymentCalculationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Policy);

        var policy = TradePaymentPolicyNormalizer.Normalize(request.Policy);
        var materials = request.Materials?.ToArray() ?? [];
        var laborInputs = request.CraftLabor ?? [];
        ValidateInputs(materials, laborInputs, policy);
        var estimatedProcurementTotal = RoundGil(materials.Sum(material => material.UnitCost * material.Quantity));
        var materialReimbursementTotal = RoundGil(materials
            .Where(material => material.Responsibility == CommissionMaterialResponsibility.Crafter)
            .Sum(material => material.UnitCost * material.Quantity));
        var providedMaterialTotal = estimatedProcurementTotal - materialReimbursementTotal;

        var legacy = BuildLegacy(
            policy,
            materialReimbursementTotal,
            estimatedProcurementTotal);
        var labor = BuildLaborStandard(
            policy,
            laborInputs,
            materialReimbursementTotal);
        var active = policy.ActiveContract == TradePaymentContractMode.LaborStandard
            ? labor
            : legacy;
        var includeLaborWarnings = policy.ActiveContract == TradePaymentContractMode.LaborStandard;
        var warnings = (request.Warnings ?? [])
            .Concat(materials.SelectMany(material => material.Warnings ?? []))
            .Concat(legacy.Warnings)
            .Concat(includeLaborWarnings ? labor.Warnings : [])
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new TradePaymentComparisonSummary(
            materials,
            estimatedProcurementTotal,
            materialReimbursementTotal,
            providedMaterialTotal,
            legacy,
            labor,
            active,
            active.IsAvailable ? active.Total : 0m,
            warnings);
    }

    private static void ValidateInputs(
        IReadOnlyList<TradePaymentMaterialInput> materials,
        IReadOnlyList<TradeCraftLaborInput> laborInputs,
        TradePaymentPolicy policy)
    {
        if (policy.LegacyCommissionPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Legacy commission percent must be between 0 and 100.");
        }

        if (policy.LaborGilPerSynth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Labor gil per synth must be greater than zero.");
        }

        if (materials.Any(material => material.Quantity < 0 || material.UnitCost < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(materials), "Material quantity and unit cost cannot be negative.");
        }

        if (laborInputs.Any(input => input.RequestedQuantity < 0 || input.CraftCount < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(laborInputs), "Requested quantity and craft count cannot be negative.");
        }
    }

    private static TradePaymentContractBreakdown BuildLegacy(
        TradePaymentPolicy policy,
        decimal materialReimbursementTotal,
        decimal estimatedProcurementTotal)
    {
        var percent = policy.LegacyCommissionPercent;
        var commission = RoundGil(estimatedProcurementTotal * percent / 100m);

        return new TradePaymentContractBreakdown(
            TradePaymentContractMode.LegacyCommission,
            IsAvailable: true,
            materialReimbursementTotal,
            percent,
            commission,
            CraftLaborTotal: 0m,
            CraftSynthCount: 0,
            GilPerSynth: 0m,
            materialReimbursementTotal + commission,
            CraftLaborLines: [],
            Warnings: []);
    }

    private static TradePaymentContractBreakdown BuildLaborStandard(
        TradePaymentPolicy policy,
        IReadOnlyList<TradeCraftLaborInput> laborInputs,
        decimal materialReimbursementTotal)
    {
        var activeInputs = laborInputs.Where(input => input.CraftCount > 0).ToArray();
        if (activeInputs.Length == 0)
        {
            return UnavailableLabor(materialReimbursementTotal, "Labor-standard evidence is unavailable. Reprice the order from its linked craft plan before using synth-labor payment.");
        }

        var gilPerSynth = policy.LaborGilPerSynth;
        var lines = activeInputs
            .Select(input =>
            {
                var total = RoundGil(input.CraftCount * gilPerSynth);
                return new TradeCraftLaborLine(
                    input.NodeId,
                    input.ItemId,
                    input.Name,
                    input.RequestedQuantity,
                    input.CraftCount,
                    gilPerSynth,
                    total,
                    input.Warnings?.ToArray() ?? []);
            })
            .ToArray();
        var craftLaborTotal = RoundGil(lines.Sum(line => line.LaborTotal));
        var synthCount = lines.Sum(line => line.CraftCount);
        var warnings = lines
            .SelectMany(line => line.Warnings)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new TradePaymentContractBreakdown(
            TradePaymentContractMode.LaborStandard,
            IsAvailable: true,
            materialReimbursementTotal,
            CommissionPercent: 0m,
            CommissionAmount: 0m,
            craftLaborTotal,
            synthCount,
            gilPerSynth,
            materialReimbursementTotal + craftLaborTotal,
            lines,
            warnings);
    }

    private static TradePaymentContractBreakdown UnavailableLabor(decimal materialReimbursementTotal, string warning)
    {
        return new TradePaymentContractBreakdown(
            TradePaymentContractMode.LaborStandard,
            IsAvailable: false,
            materialReimbursementTotal,
            CommissionPercent: 0m,
            CommissionAmount: 0m,
            CraftLaborTotal: 0m,
            CraftSynthCount: 0,
            GilPerSynth: 0m,
            Total: 0m,
            CraftLaborLines: [],
            Warnings: [warning]);
    }

    private static decimal RoundGil(decimal value)
    {
        return Math.Round(value, 0, MidpointRounding.AwayFromZero);
    }
}
