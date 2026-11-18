namespace FFXIV_Craft_Architect.Core.Services;

public static class CompanyCommissionPaymentDisplayFormatter
{
    public static string FormatContractLabel(string? storedLabel)
    {
        if (string.IsNullOrWhiteSpace(storedLabel))
        {
            return "Payment terms";
        }

        return storedLabel.Contains("legacy", StringComparison.OrdinalIgnoreCase)
            ? "Agreed payment terms"
            : storedLabel.Trim();
    }
}
