using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// One authoritative rule for company commission templates, shared by the host
/// (LodestoneLookup) and the Web client so both reject the same payloads.
/// </summary>
public static class TradeCommissionTemplateValidator
{
    public const int MaxTemplatesPerCompany = 100;
    public const int MaxNameLength = 120;
    public const int MaxDefaultOrderTitleLength = 160;
    public const int MaxNotesLength = 2000;
    public const int MaxCustomPaymentTermsLength = 500;
    public const int MaxOutputs = 100;
    public const int MaxSupplyChoices = 200;
    public const int MaxOutputQuantity = 9999;

    public static bool IsValid(IReadOnlyList<TradeCommissionTemplate>? templates, out string? error)
    {
        error = null;
        if (templates == null)
        {
            return true;
        }

        if (templates.Count > MaxTemplatesPerCompany)
        {
            error = $"A company can store at most {MaxTemplatesPerCompany} commission templates.";
            return false;
        }

        var ids = new HashSet<Guid>();
        foreach (var template in templates)
        {
            if (!IsValid(template, out error))
            {
                return false;
            }

            if (!ids.Add(template.Id))
            {
                error = $"Commission template id {template.Id} appears more than once.";
                return false;
            }
        }

        return true;
    }

    public static bool IsValid(TradeCommissionTemplate? template, out string? error)
    {
        error = null;
        if (template == null)
        {
            error = "Commission template is missing.";
            return false;
        }

        if (template.Id == Guid.Empty)
        {
            error = "Commission template id is required.";
            return false;
        }

        var name = template.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            error = "Commission template name is required.";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = $"Commission template name must be at most {MaxNameLength} characters.";
            return false;
        }

        if (template.DefaultOrderTitle?.Trim().Length > MaxDefaultOrderTitleLength)
        {
            error = $"Template \"{name}\": default order title must be at most {MaxDefaultOrderTitleLength} characters.";
            return false;
        }

        if (template.Notes?.Trim().Length > MaxNotesLength)
        {
            error = $"Template \"{name}\": notes must be at most {MaxNotesLength} characters.";
            return false;
        }

        if (template.CustomPaymentTerms?.Trim().Length > MaxCustomPaymentTermsLength)
        {
            error = $"Template \"{name}\": custom payment terms must be at most {MaxCustomPaymentTermsLength} characters.";
            return false;
        }

        if (!Enum.IsDefined(template.PaymentSchedule))
        {
            error = $"Template \"{name}\": payment schedule is not recognized.";
            return false;
        }

        var outputs = template.Outputs ?? [];
        if (outputs.Count is < 1 or > MaxOutputs)
        {
            error = $"Template \"{name}\": needs 1 to {MaxOutputs} outputs (has {outputs.Count}).";
            return false;
        }

        foreach (var output in outputs)
        {
            if (output == null ||
                output.ItemId <= 0 ||
                string.IsNullOrWhiteSpace(output.Name) ||
                output.Quantity is < 1 or > MaxOutputQuantity)
            {
                error = $"Template \"{name}\": output {output?.Name ?? "(missing)"} needs an item, a name, and a quantity of 1 to {MaxOutputQuantity}.";
                return false;
            }
        }

        var supplyChoices = template.SupplyChoices ?? [];
        if (supplyChoices.Count > MaxSupplyChoices)
        {
            error = $"Template \"{name}\": at most {MaxSupplyChoices} supply choices (has {supplyChoices.Count}).";
            return false;
        }

        foreach (var choice in supplyChoices)
        {
            if (choice == null ||
                choice.ItemId <= 0 ||
                string.IsNullOrWhiteSpace(choice.Name) ||
                !Enum.IsDefined(choice.Responsibility) ||
                (choice.AcquisitionSource is { } source && !Enum.IsDefined(source)))
            {
                error = $"Template \"{name}\": supply choice {choice?.Name ?? "(missing)"} needs an item and a name.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Trims text, drops empty optional text, and collapses duplicate outputs/supply
    /// choices (last one wins) without changing what the template means.
    /// </summary>
    public static TradeCommissionTemplate Normalize(TradeCommissionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var outputs = (template.Outputs ?? [])
            .Where(output => output != null)
            .Select(output => output with { Name = output.Name?.Trim() ?? string.Empty })
            .GroupBy(output => (output.ItemId, output.MustBeHq))
            .Select(group => group.Last())
            .ToArray();
        var supplyChoices = (template.SupplyChoices ?? [])
            .Where(choice => choice != null)
            .Select(choice => choice with { Name = choice.Name?.Trim() ?? string.Empty })
            .GroupBy(choice => (choice.ItemId, choice.RequiresHq))
            .Select(group => group.Last())
            .ToArray();

        return template with
        {
            Id = template.Id == Guid.Empty ? Guid.NewGuid() : template.Id,
            Name = template.Name?.Trim() ?? string.Empty,
            DefaultOrderTitle = TrimToNull(template.DefaultOrderTitle),
            Notes = TrimToNull(template.Notes),
            CustomPaymentTerms = template.PaymentSchedule == CompanyCommissionPaymentSchedule.Custom
                ? TrimToNull(template.CustomPaymentTerms)
                : null,
            Outputs = outputs,
            SupplyChoices = supplyChoices
        };
    }

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
