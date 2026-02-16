namespace FFXIVCraftArchitect.Core.Models;

/// <summary>
/// Helper class for zone/location name resolution.
/// Provides centralized zone ID to name mappings used across the application.
/// </summary>
public static class ZoneMappingHelper
{
    /// <summary>
    /// Maps Garland location IDs to readable location names.
    /// Hard-coded for common zones where vendors exist.
    /// </summary>
    public static readonly Dictionary<int, string> ZoneNameMappings = new()
    {
        // Housing Districts - Material Suppliers
        [425] = "Mist",
        [427] = "The Goblet",
        [426] = "The Lavender Beds",
        [2412] = "Shirogane",
        [4139] = "Empyreum",

        // Major Cities - A Realm Reborn
        [128] = "Limsa Lominsa",
        [130] = "Gridania",
        [131] = "Ul'dah",
        
        // City Areas
        [28] = "Limsa Lominsa Upper Decks",
        [29] = "Limsa Lominsa Lower Decks",
        [52] = "Limsa Lominsa Lower Decks",
        [53] = "Old Gridania",
        [54] = "New Gridania",
        [40] = "Ul'dah - Steps of Nald",
        [41] = "Ul'dah - Steps of Thal",
        
        // City Inns/Aetheryte Plazas
        [129] = "The Drowning Wench (Limsa)",
        [137] = "The Quicksand (Ul'dah)",
        [138] = "The Roost (Gridania)",

        // Heavensward (3.0)
        [132] = "Ishgard",
        [218] = "Foundation",
        [2301] = "The Pillars",
        [139] = "The Jeweled Crozier (Ishgard)",
        [2082] = "Idyllshire",

        // Stormblood (4.0)
        [133] = "Kugane",
        [2403] = "Rhalgr's Reach",
        [140] = "The Shiokaze Hostelry (Kugane)",
        [2411] = "The Azim Steppe",

        // Shadowbringers (5.0)
        [134] = "The Crystarium",
        [141] = "The Pendants (Crystarium)",
        [51] = "Eulmore",

        // Endwalker (6.0)
        [135] = "Old Sharlayan",
        [142] = "The Baldesion Annex (Sharlayan)",
        [3706] = "Old Sharlayan",
        [3707] = "Radz-at-Han",
        [3710] = "Garlemald",
        [3711] = "Mare Lamentorum",
        [3712] = "Ultima Thule",

        // Dawntrail (7.0)
        [136] = "Tuliyollal",
        [2500] = "Solution Nine",
        [5301] = "Urqopacha",
        [5406] = "Shaaloani",
        [4505] = "Urqopacha",
        [4506] = "Kozama'uka",
        [4507] = "Yak T'el",
        [4508] = "Shaaloani",
        [4509] = "Heritage Found",
        [4510] = "Living Memory",

        // A Realm Reborn Zones
        [24] = "Mor Dhona",
        [57] = "North Shroud",
        [42] = "Western Thanalan",
        [43] = "Central Thanalan",
        [44] = "Eastern Thanalan",
        [45] = "Southern Thanalan",
        [46] = "Northern Thanalan",
        [30] = "Middle La Noscea",
        [31] = "Lower La Noscea",
        [32] = "Eastern La Noscea",
        [33] = "Western La Noscea",
        [34] = "Upper La Noscea",
        [35] = "Outer La Noscea",
        [148] = "Central Shroud",
        [55] = "East Shroud",
        [56] = "South Shroud",
    };

    /// <summary>
    /// Maps a location ID to a readable zone name.
    /// Uses hard-coded mappings for common zones, falls back to "Zone {id}" for unknown zones.
    /// </summary>
    /// <param name="locationId">The numeric location ID from Garland API</param>
    /// <returns>Human-readable zone name or "Zone {id}" if not found</returns>
    public static string LocationIdToName(int locationId)
    {
        return ZoneNameMappings.TryGetValue(locationId, out var name) 
            ? name 
            : $"Zone {locationId}";
    }

    /// <summary>
    /// Attempts to convert a location string (which may be an ID) to a zone name.
    /// Handles both numeric IDs and already-resolved names.
    /// </summary>
    /// <param name="location">Location string which may be "28" or "Limsa Lominsa"</param>
    /// <returns>Resolved zone name or original string if not a known ID</returns>
    public static string ResolveLocationName(string? location)
    {
        if (string.IsNullOrEmpty(location))
            return "Unknown Location";

        // If it's already a descriptive name (contains letters), return as-is
        if (location.Any(char.IsLetter))
            return location;

        // Try to parse as ID
        if (int.TryParse(location, out var locationId))
            return LocationIdToName(locationId);

        return location;
    }

    /// <summary>
    /// Debug method to check if a zone ID is in the mapping.
    /// </summary>
    public static bool HasZoneMapping(int locationId)
    {
        return ZoneNameMappings.ContainsKey(locationId);
    }

    /// <summary>
    /// Returns count of zone mappings for debugging.
    /// </summary>
    public static int GetMappingCount()
    {
        return ZoneNameMappings.Count;
    }
}
