using System.Text.RegularExpressions;

namespace FFXIV_Craft_Architect.Web.Services;

public static partial class RecipePlanDiagnosticFileName
{
    public static string Create(string? planName, DateTime exportedAtUtc)
    {
        var safeName = string.IsNullOrWhiteSpace(planName)
            ? "recipe-plan"
            : planName.Trim();
        safeName = InvalidFileNameCharacterPattern().Replace(safeName, "_");
        if (safeName.Length > 48)
        {
            safeName = safeName[..48];
        }

        return $"recipe-plan-{safeName}-{exportedAtUtc:yyyyMMdd-HHmmss}.json";
    }

    [GeneratedRegex(@"[\\/:*?""<>|]")]
    private static partial Regex InvalidFileNameCharacterPattern();
}
