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

    public static IReadOnlyList<string> GetRegions() =>
        RegionDataCenters.Keys.ToArray();

    public static IReadOnlyList<string> GetDataCentersForRegion(string selectedRegion) =>
        RegionDataCenters.TryGetValue(selectedRegion, out var dataCenters)
            ? dataCenters
            : Array.Empty<string>();

    public static bool IsDataCenterInRegion(
        string selectedDataCenter,
        string selectedRegion) =>
        RegionDataCenters.TryGetValue(selectedRegion, out var dataCenters) &&
        dataCenters.Contains(selectedDataCenter, StringComparer.OrdinalIgnoreCase);

    public static string ResolveValidDataCenter(
        string selectedRegion,
        string? preferredDataCenter = null)
    {
        if (!RegionDataCenters.TryGetValue(selectedRegion, out var dataCenters) ||
            dataCenters.Length == 0)
        {
            return string.IsNullOrWhiteSpace(preferredDataCenter)
                ? "Aether"
                : preferredDataCenter;
        }

        return !string.IsNullOrWhiteSpace(preferredDataCenter) &&
               dataCenters.Contains(preferredDataCenter, StringComparer.OrdinalIgnoreCase)
            ? dataCenters.First(dataCenter =>
                string.Equals(
                    dataCenter,
                    preferredDataCenter,
                    StringComparison.OrdinalIgnoreCase))
            : dataCenters[0];
    }

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

    public static IReadOnlyList<string> GetDataCenters(
        MarketFetchScope scope,
        string selectedDataCenter,
        string selectedRegion,
        IReadOnlyCollection<string>? selectedRegions)
    {
        if (scope == MarketFetchScope.SelectedDataCenter)
        {
            return [ResolveValidDataCenter(selectedRegion, selectedDataCenter)];
        }

        var regions = NormalizeSelectedRegions(selectedRegion, selectedRegions);
        return regions
            .SelectMany(GetDataCentersForRegion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> NormalizeSelectedRegions(
        string selectedRegion,
        IReadOnlyCollection<string>? selectedRegions,
        int maximumRegions = 2)
    {
        maximumRegions = Math.Max(1, maximumRegions);
        var normalized = new List<string>(maximumRegions);

        void AddIfKnown(string? region)
        {
            if (string.IsNullOrWhiteSpace(region) ||
                !RegionDataCenters.ContainsKey(region) ||
                normalized.Contains(region, StringComparer.OrdinalIgnoreCase) ||
                normalized.Count >= maximumRegions)
            {
                return;
            }

            normalized.Add(RegionDataCenters.Keys.First(candidate =>
                string.Equals(candidate, region, StringComparison.OrdinalIgnoreCase)));
        }

        AddIfKnown(selectedRegion);
        foreach (var region in selectedRegions ?? Array.Empty<string>())
        {
            AddIfKnown(region);
        }

        if (normalized.Count == 0)
        {
            normalized.Add("North America");
        }

        return normalized;
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

    public static IReadOnlyList<string> ResolveRegionsForDataCenters(
        IEnumerable<string> dataCenters,
        string fallbackRegion)
    {
        var regions = dataCenters
            .Select(dataCenter => ResolveRegionForDataCenter(dataCenter, string.Empty))
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return regions.Length > 0
            ? regions
            : [fallbackRegion];
    }
}
