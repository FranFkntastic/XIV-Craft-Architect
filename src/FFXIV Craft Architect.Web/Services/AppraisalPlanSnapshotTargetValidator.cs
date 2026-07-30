namespace FFXIV_Craft_Architect.Web.Services;

public static class AppraisalPlanSnapshotTargetValidator
{
    private const string Marker = "/api/craft/plans/";
    private const int Sha256HexLength = 64;

    public static bool IsValid(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        string path;
        if (target.StartsWith("/", StringComparison.Ordinal))
        {
            if (target.Contains('?', StringComparison.Ordinal) ||
                target.Contains('#', StringComparison.Ordinal))
            {
                return false;
            }

            path = target;
        }
        else
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var absolute) ||
                absolute.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(absolute.UserInfo) ||
                !string.IsNullOrEmpty(absolute.Query) ||
                !string.IsNullOrEmpty(absolute.Fragment))
            {
                return false;
            }

            path = absolute.AbsolutePath;
        }

        var markerIndex = path.LastIndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var token = path[(markerIndex + Marker.Length)..];
        return token.Length == Sha256HexLength && token.All(Uri.IsHexDigit);
    }
}
