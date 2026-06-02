namespace FFXIV_Craft_Architect.Core.Models;

public enum MarketFetchScope
{
    SelectedDataCenter = 0,
    EntireRegion = 1
}

public static class MarketFetchScopeResolver
{
    private static readonly IReadOnlyDictionary<string, string[]> RegionDataCenters =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["North America"] = ["Aether", "Primal", "Crystal", "Dynamis"],
            ["Europe"] = ["Chaos", "Light"],
            ["Japan"] = ["Elemental", "Gaia", "Mana", "Meteor"],
            ["Oceania"] = ["Materia"]
        };

    public static IReadOnlyList<string> GetDataCenters(
        MarketFetchScope scope,
        string selectedDataCenter,
        string selectedRegion)
    {
        if (scope == MarketFetchScope.SelectedDataCenter ||
            !RegionDataCenters.TryGetValue(selectedRegion, out var regionDataCenters))
        {
            return [selectedDataCenter];
        }

        return regionDataCenters;
    }

    public static string ResolveRegionForDataCenter(string selectedDataCenter, string fallbackRegion)
    {
        foreach (var (region, dataCenters) in RegionDataCenters)
        {
            if (dataCenters.Contains(selectedDataCenter, StringComparer.OrdinalIgnoreCase))
            {
                return region;
            }
        }

        return fallbackRegion;
    }
}
