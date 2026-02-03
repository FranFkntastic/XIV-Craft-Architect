namespace FFXIVCraftArchitect.Core.Helpers;

/// <summary>
/// Helper class for FFXIV job/crafter name conversions.
/// </summary>
public static class JobHelper
{
    /// <summary>
    /// Gets the job name from a job ID.
    /// </summary>
    public static string GetJobName(int jobId) => jobId switch
    {
        1 => "Carpenter",
        2 => "Blacksmith",
        3 => "Armorer",
        4 => "Goldsmith",
        5 => "Leatherworker",
        6 => "Weaver",
        7 => "Alchemist",
        8 => "Culinarian",
        _ => "Unknown"
    };
}
