using System.Text;

using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record TradeOrderPaymentOutput(
    string Name,
    int Quantity,
    bool MustBeHq);

public sealed record TradeOrderPaymentProvenance(
    string SourcePlanName,
    CommissionCostBasis? CostBasis,
    MarketFetchScope? MarketFetchScope,
    string? SelectedDataCenter,
    string? SelectedRegion,
    IReadOnlyList<string> RequestedDataCenters,
    DateTime PricingSnapshotAtUtc);

public sealed record TradeOrderPaymentCopyContext(
    string OrderTitle,
    string AssignedCrafter,
    IReadOnlyList<TradeOrderPaymentOutput> Outputs,
    TradeOrderPaymentProvenance Provenance,
    TradeCommissionPaymentSummary Payment);

public static class TradeOrderPaymentCopyFormatter
{
    public static string BuildReceipt(TradeOrderPaymentCopyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var summary = context.Payment;
        var active = summary.Active;
        var builder = CreateHeader("Payment receipt", context);
        builder.AppendLine($"Active basis: {FormatPaymentContract(active.Contract)}");
        builder.AppendLine($"Payment amount: {FormatGil(summary.TotalPayment)}");
        builder.AppendLine($"Crafter-procured reimbursement: {FormatGil(active.MaterialReimbursementTotal)}");
        builder.AppendLine($"{FormatMaterialAdjustmentLabel(active.Contract)} ({active.CommissionPercent:N0}%): {FormatGil(active.CommissionAmount)}");
        if (active.Contract == TradePaymentContractMode.LaborStandard)
        {
            builder.AppendLine($"Craft labor: {active.CraftSynthCount:N0} synths x {active.GilPerSynth:N2} gil = {FormatGil(active.CraftLaborTotal)}");
        }

        AppendActiveWarnings(builder, summary);
        return builder.ToString();
    }

    public static string BuildSummary(TradeOrderPaymentCopyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var summary = context.Payment;
        var builder = CreateHeader("Payment summary", context);
        AppendMaterialSection(builder, "Crafter procures", summary.Materials.Where(material => material.Responsibility == CommissionMaterialResponsibility.Crafter));
        builder.AppendLine();
        AppendMaterialSection(builder, "Provided by commissioner", summary.Materials.Where(material => material.Responsibility == CommissionMaterialResponsibility.Provided));
        builder.AppendLine();
        builder.AppendLine($"Active basis: {FormatPaymentContract(summary.Active.Contract)}");
        builder.AppendLine($"Payment amount: {FormatGil(summary.TotalPayment)}");
        builder.AppendLine($"Crafter-procured reimbursement: {FormatGil(summary.MaterialReimbursementTotal)}");
        builder.AppendLine($"Provided material value: {FormatGil(summary.ProvidedMaterialTotal)}");
        builder.AppendLine($"Legacy comparison: {FormatPaymentBreakdown(summary.Legacy)}");
        builder.AppendLine($"Labor-standard comparison: {FormatPaymentBreakdown(summary.LaborStandard)}");
        if (summary.LaborStandard.IsAvailable)
        {
            builder.AppendLine($"Labor material bonus ({summary.LaborStandard.CommissionPercent:N0}%): {FormatGil(summary.LaborStandard.CommissionAmount)}");
            builder.AppendLine($"Craft labor: {summary.LaborStandard.CraftSynthCount:N0} synths x {summary.LaborStandard.GilPerSynth:N2} gil = {FormatGil(summary.LaborStandard.CraftLaborTotal)}");
            builder.AppendLine($"Difference vs legacy: {FormatPaymentDifference(summary.LaborStandard, summary.Legacy)}");
        }

        builder.AppendLine($"Total estimated procurement: {FormatGil(summary.EstimatedProcurementTotal)}");
        AppendAllWarnings(builder, summary);
        return builder.ToString();
    }

    public static string FormatPaymentBreakdown(TradePaymentContractBreakdown breakdown)
    {
        if (breakdown.IsAvailable)
        {
            return FormatGil(breakdown.Total);
        }

        return breakdown.Warnings.Any(warning => warning.Contains("policy", StringComparison.OrdinalIgnoreCase))
            ? "Needs labor standard"
            : "Needs reprice";
    }

    public static string FormatPaymentDifference(
        TradePaymentContractBreakdown laborStandard,
        TradePaymentContractBreakdown legacy)
    {
        if (!laborStandard.IsAvailable)
        {
            return "Needs reprice";
        }

        var value = laborStandard.Total - legacy.Total;
        if (value == 0)
        {
            return "0 gil";
        }

        return value > 0
            ? $"+{value:N0} gil"
            : $"{value:N0} gil";
    }

    public static string FormatPaymentContract(TradePaymentContractMode mode)
    {
        return mode == TradePaymentContractMode.LaborStandard
            ? "labor standard"
            : "legacy";
    }

    private static string FormatMaterialAdjustmentLabel(TradePaymentContractMode mode)
    {
        return mode == TradePaymentContractMode.LaborStandard
            ? "Labor material bonus"
            : "Legacy material commission";
    }

    private static StringBuilder CreateHeader(string title, TradeOrderPaymentCopyContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"FFXIV Trade Architect {title}");
        builder.AppendLine($"Order: {context.OrderTitle}");
        builder.AppendLine($"Assigned crafter: {context.AssignedCrafter}");
        builder.AppendLine($"Plan: {FormatRecordedValue(context.Provenance.SourcePlanName)}");
        builder.AppendLine($"Material cost basis: {FormatCostBasis(context.Provenance.CostBasis)}");
        builder.AppendLine($"Evidence scope: {FormatEvidenceScope(context.Provenance)}");
        builder.AppendLine($"Evidence snapshot: {FormatTimestamp(context.Provenance.PricingSnapshotAtUtc)}");
        builder.AppendLine();
        builder.AppendLine("Outputs:");
        foreach (var item in context.Outputs.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var hqSuffix = item.MustBeHq ? " HQ" : string.Empty;
            builder.AppendLine($"- {item.Name}{hqSuffix} x{item.Quantity:N0}");
        }

        builder.AppendLine();
        return builder;
    }

    private static void AppendMaterialSection(
        StringBuilder builder,
        string heading,
        IEnumerable<TradeCommissionPaymentMaterial> materials)
    {
        builder.AppendLine($"{heading}:");
        var lines = materials.OrderBy(material => material.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (lines.Length == 0)
        {
            builder.AppendLine("- None");
            return;
        }

        foreach (var material in lines)
        {
            var hqSuffix = material.RequiresHq ? " HQ" : string.Empty;
            builder.AppendLine($"- {material.Name}{hqSuffix} x{material.Quantity:N0}: {FormatGil(material.TotalCost)}");
        }
    }

    private static string FormatCostBasis(CommissionCostBasis? costBasis)
    {
        return costBasis switch
        {
            CommissionCostBasis.MarketRecommendation => "Market recommendation",
            CommissionCostBasis.SelectedAcquisitionSources => "Selected acquisition sources",
            _ => "Not recorded (legacy order snapshot)"
        };
    }

    private static string FormatEvidenceScope(TradeOrderPaymentProvenance provenance)
    {
        var dataCenters = (provenance.RequestedDataCenters ?? Array.Empty<string>())
            .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(dataCenter => dataCenter, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!provenance.MarketFetchScope.HasValue)
        {
            return string.IsNullOrWhiteSpace(provenance.SelectedDataCenter)
                ? "Not recorded (legacy order snapshot)"
                : $"{provenance.SelectedDataCenter} data center; full scope not recorded";
        }

        if (provenance.MarketFetchScope == MarketFetchScope.SelectedDataCenter)
        {
            var dataCenter = !string.IsNullOrWhiteSpace(provenance.SelectedDataCenter)
                ? provenance.SelectedDataCenter.Trim()
                : dataCenters.FirstOrDefault();
            var region = FormatOptionalParenthetical(provenance.SelectedRegion);
            return string.IsNullOrWhiteSpace(dataCenter)
                ? $"Selected data center not recorded{region}"
                : $"{dataCenter} data center{region}";
        }

        if (dataCenters.Length == 0)
        {
            return string.IsNullOrWhiteSpace(provenance.SelectedRegion)
                ? "Entire region; region not recorded"
                : $"{provenance.SelectedRegion.Trim()} region";
        }

        var regions = MarketFetchScopeResolver.ResolveRegionsForDataCenters(
            dataCenters,
            provenance.SelectedRegion ?? string.Empty);
        var regionLabel = regions.Count == 1
            ? $"{regions[0]} region"
            : $"{string.Join(" + ", regions)} regions";
        return $"{regionLabel} ({string.Join(", ", dataCenters)})";
    }

    private static string FormatTimestamp(DateTime timestamp)
    {
        return timestamp == default
            ? "Not recorded"
            : timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");
    }

    private static string FormatRecordedValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "Not recorded"
            : value.Trim();
    }

    private static string FormatOptionalParenthetical(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : $" ({value.Trim()})";
    }

    private static void AppendActiveWarnings(StringBuilder builder, TradeCommissionPaymentSummary summary)
    {
        var warnings = summary.Active.Warnings
            .Concat(summary.Warnings)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(warning => warning, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AppendWarnings(builder, "Payment warnings", warnings);
    }

    private static void AppendAllWarnings(StringBuilder builder, TradeCommissionPaymentSummary summary)
    {
        AppendWarnings(builder, "Evidence warnings", summary.Warnings);
    }

    private static void AppendWarnings(StringBuilder builder, string heading, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"{heading}:");
        foreach (var warning in warnings)
        {
            builder.AppendLine($"- {warning}");
        }
    }

    private static string FormatGil(decimal value)
    {
        return value > 0 ? $"{value:N0} gil" : "Not priced";
    }
}
