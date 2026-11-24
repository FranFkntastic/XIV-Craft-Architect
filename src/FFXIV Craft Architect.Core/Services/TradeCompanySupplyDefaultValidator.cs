using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// One authoritative rule for company-wide "always company-supplied" defaults, shared by
/// the host (LodestoneLookup) and the Web client so both reject the same payloads.
/// </summary>
public static class TradeCompanySupplyDefaultValidator
{
    public const int MaxDefaults = 200;
    public const int MaxNameLength = 120;

    public static TradeCompanySupplyDefault Normalize(TradeCompanySupplyDefault item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item with { Name = item.Name?.Trim() ?? string.Empty };
    }

    public static bool IsValid(IReadOnlyList<TradeCompanySupplyDefault>? items, out string? error)
    {
        error = null;
        if (items == null || items.Count == 0)
        {
            return true;
        }

        if (items.Count > MaxDefaults)
        {
            error = $"A company can store at most {MaxDefaults} company-supplied defaults.";
            return false;
        }

        var keys = new HashSet<(int ItemId, bool RequiresHq)>();
        foreach (var item in items)
        {
            if (item == null)
            {
                error = "Company-supplied default is missing.";
                return false;
            }

            if (item.ItemId <= 0)
            {
                error = "Company-supplied default item id must be positive.";
                return false;
            }

            var name = item.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                error = $"Company-supplied default {item.ItemId} needs a name.";
                return false;
            }

            if (name.Length > MaxNameLength)
            {
                error = $"Company-supplied default name must be at most {MaxNameLength} characters.";
                return false;
            }

            if (!keys.Add((item.ItemId, item.RequiresHq)))
            {
                error = $"Company-supplied default {name}{(item.RequiresHq ? " (HQ)" : string.Empty)} appears more than once.";
                return false;
            }
        }

        return true;
    }
}
