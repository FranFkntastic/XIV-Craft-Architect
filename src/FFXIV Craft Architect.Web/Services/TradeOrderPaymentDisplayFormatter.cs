using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public static class TradeOrderPaymentDisplayFormatter
{
    public static TradeCommissionPaymentSummary ApplyCanonicalPayment(
        TradeCommissionPaymentSummary summary,
        CompanyCommissionPaymentTerms payment)
    {
        var breakdown = summary.Active with
        {
            IsAvailable = true,
            MaterialReimbursementTotal = payment.MaterialReimbursement,
            CraftLaborTotal = payment.CraftLabor,
            CraftSynthCount = payment.CraftSynthCount,
            GilPerSynth = payment.GilPerSynth,
            Total = payment.Total,
            Warnings = []
        };
        return summary with
        {
            MaterialReimbursementTotal = payment.MaterialReimbursement,
            TotalPayment = payment.Total,
            Active = breakdown
        };
    }

    public static string FormatMaterialPaymentImpact(
        TradeCommissionPaymentMaterial material,
        TradeMaterialQuote? quote = null,
        bool requireQuotedCash = false)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (material.Responsibility == CommissionMaterialResponsibility.Provided || material.IsOnHand)
        {
            return FormatGilAllowZero(0);
        }

        if (quote != null)
        {
            var lines = quote.Lines.Where(line => line.ItemId == material.ItemId &&
                line.RequiresHq == material.RequiresHq).ToArray();
            return lines.Length switch
            {
                1 when lines[0].RequiredQuantity == material.Quantity => FormatGilAllowZero(lines[0].CashRequired),
                // Moved to the crafter after the route was quoted: last known value until repriced.
                0 when !requireQuotedCash => FormatGil(material.TotalCost),
                _ => "Impact unavailable"
            };
        }

        if (requireQuotedCash)
        {
            return "Not itemized in terms";
        }

        if (material.TotalCost <= 0)
        {
            return "Not priced";
        }

        return FormatGil(material.TotalCost);
    }

    public static string FormatCraftLaborBasis(TradePaymentContractBreakdown breakdown)
    {
        if (!breakdown.IsAvailable)
        {
            return "Unavailable";
        }

        if (breakdown.CraftSynthCount <= 0)
        {
            return "No active synths";
        }

        return $"{breakdown.CraftSynthCount:N0} synths x {breakdown.GilPerSynth:N2} gil";
    }

    private static string FormatGil(decimal value)
    {
        return value > 0 ? $"{value:N0} gil" : "Not priced";
    }

    private static string FormatGilAllowZero(decimal value)
    {
        return value >= 0 ? $"{value:N0} gil" : "Not priced";
    }
}
