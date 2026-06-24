namespace FFXIV_Craft_Architect.Core.Helpers;

/// <summary>
/// Helper class for FFXIV job/crafter name conversions.
/// </summary>
public static class JobHelper
{
    /// <summary>
    /// Gets the crafter name from a Garland recipe job ID.
    /// </summary>
    public static string GetJobName(int jobId) => jobId switch
    {
        8 => "Carpenter",
        9 => "Blacksmith",
        10 => "Armorer",
        11 => "Goldsmith",
        12 => "Leatherworker",
        13 => "Weaver",
        14 => "Alchemist",
        15 => "Culinarian",
        _ => "Unknown"
    };
}
