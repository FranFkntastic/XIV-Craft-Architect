using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public static class TradeOrderPaymentDisplayFormatter
{
    public static string FormatMaterialPaymentImpact(TradeCommissionPaymentMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        if (material.TotalCost <= 0)
        {
            return "Not priced";
        }

        return material.Responsibility == CommissionMaterialResponsibility.Crafter
            ? FormatGil(material.TotalCost)
            : FormatGilAllowZero(0);
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
