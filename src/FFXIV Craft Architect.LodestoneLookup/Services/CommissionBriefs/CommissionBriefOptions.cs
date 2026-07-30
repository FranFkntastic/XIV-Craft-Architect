namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

public sealed class CommissionBriefOptions
{
    public bool Enabled { get; set; } = true;
    public string DatabasePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "commission-briefs.db");
    public string PublicPageUrl { get; set; } = "http://localhost:5000/commission.html";
    public IReadOnlySet<string> AllowedHosts { get; set; } = new HashSet<string>(
        ["dev.xivcraftarchitect.com", "localhost", "127.0.0.1"],
        StringComparer.OrdinalIgnoreCase);

    public bool IsAllowedRequestHost(string requestHost) =>
        AllowedHosts.Contains(requestHost) ||
        Uri.TryCreate(PublicPageUrl, UriKind.Absolute, out var pageUri) &&
        string.Equals(
            requestHost,
            pageUri.Host,
            StringComparison.OrdinalIgnoreCase);

    public bool TryBuildPublicUrl(string publicId, out string publicUrl)
    {
        publicUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(publicId) ||
            !Uri.TryCreate(PublicPageUrl, UriKind.Absolute, out var pageUri) ||
            pageUri.Scheme is not ("https" or "http") ||
            pageUri.Scheme == "http" && !pageUri.IsLoopback ||
            !string.IsNullOrEmpty(pageUri.UserInfo) ||
            !string.IsNullOrEmpty(pageUri.Query) ||
            !string.IsNullOrEmpty(pageUri.Fragment))
        {
            return false;
        }

        publicUrl = new UriBuilder(pageUri)
        {
            Query = $"id={Uri.EscapeDataString(publicId)}"
        }.Uri.AbsoluteUri;
        return true;
    }
}
