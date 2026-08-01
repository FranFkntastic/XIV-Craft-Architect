using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

public static class CommissionBriefValidator
{
    public static string? Validate(CommissionBriefDocument brief)
    {
        if (brief == null ||
            string.IsNullOrWhiteSpace(brief.Title) ||
            string.IsNullOrWhiteSpace(brief.CompanyName) ||
            brief.Outputs.Count == 0)
        {
            return "Company, title, and at least one requested output are required.";
        }

        if (brief.Title.Length > 160 ||
            brief.CompanyName.Length > 120 ||
            (brief.Contact?.Length ?? 0) > 240 ||
            brief.DeliveryInstructions.Length > 1000 ||
            brief.Outputs.Count > 100 ||
            brief.CrafterMaterials.Count + brief.CompanyMaterials.Count > 500)
        {
            return "The commission brief exceeds the publication limits.";
        }

        if (brief.Outputs.Any(output =>
                output.ItemId <= 0 ||
                output.Quantity <= 0 ||
                output.Name.Length is 0 or > 160) ||
            brief.CrafterMaterials.Concat(brief.CompanyMaterials).Any(material =>
                material.ItemId <= 0 ||
                material.Quantity <= 0 ||
                material.Name.Length is 0 or > 160 ||
                material.UnitCost < 0 ||
                material.TotalCost < 0) ||
            brief.Payment.MaterialReimbursement < 0 ||
            brief.Payment.MaterialBonus < 0 ||
            brief.Payment.CraftLabor < 0 ||
            brief.Payment.Total <= 0 ||
            brief.Payment.MaterialAdjustmentPercent is < 0 or > 100 ||
            brief.Payment.CraftSynthCount < 0 ||
            brief.Payment.GilPerSynth < 0 ||
            brief.Payment.Schedule == CompanyCommissionPaymentSchedule.Custom &&
                string.IsNullOrWhiteSpace(brief.Payment.CustomTerms) ||
            brief.Payment.MaterialReimbursement +
                brief.Payment.MaterialBonus +
                brief.Payment.CraftLabor != brief.Payment.Total)
        {
            return "The commission brief contains invalid delivery or payment values.";
        }

        return null;
    }
}
