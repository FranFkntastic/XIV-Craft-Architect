using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// One authoritative rule for company material sources, shared by the host
/// (LodestoneLookup) and the Web client so both reject the same payloads.
/// </summary>
public static class TradeMaterialSourceValidator
{
    public const int MaxSources = 2000;
    public const string ErrorCode = "invalid_material_sources";

    /// <summary>Drops a unit price the route does not use.</summary>
    public static TradeMaterialSource Normalize(TradeMaterialSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Route == TradeMaterialRoute.SetPrice ? source : source with { UnitPrice = null };
    }

    public static bool IsValid(IReadOnlyList<TradeMaterialSource>? sources, out string? error)
    {
        error = null;
        if (sources == null || sources.Count == 0)
        {
            return true;
        }

        if (sources.Count > MaxSources)
        {
            error = $"A company can store at most {MaxSources} material sources.";
            return false;
        }

        var ids = new HashSet<int>();
        foreach (var source in sources)
        {
            if (source == null)
            {
                error = "Material source is missing.";
                return false;
            }

            if (source.ItemId <= 0)
            {
                error = "Material source item id must be positive.";
                return false;
            }

            if (!Enum.IsDefined(source.Route))
            {
                error = $"Material source {source.ItemId} has an unknown route.";
                return false;
            }

            if (source.UnitPrice < 0)
            {
                error = $"Material source {source.ItemId} has a negative unit price.";
                return false;
            }

            if (source.Route == TradeMaterialRoute.SetPrice && source.UnitPrice == null)
            {
                error = $"Material source {source.ItemId} needs a unit price.";
                return false;
            }

            if (!ids.Add(source.ItemId))
            {
                error = $"Material source {source.ItemId} appears more than once.";
                return false;
            }
        }

        return true;
    }
}
