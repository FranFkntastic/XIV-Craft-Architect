using System.Net;
using System.Text.Json;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

internal static class InvitationLandingPage
{
    public static async Task<IResult> GetAsync(
        string code,
        HttpContext context,
        ProfileHostOptions options,
        SqliteMembershipStore memberships,
        ProfileHostedTradeCompanyService companies,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var title = "This invitation is no longer available";
        var description = "This trade company invitation cannot be used.";
        var destination = "/invitation-unavailable";
        var invitation = options.Enabled ? await memberships.LoadInvitationAsync(code, cancellationToken) : null;
        if (invitation is { State: MembershipInvitationState.Active } &&
            await companies.LoadPublicCompanyProfileAsync(invitation.CompanyId, cancellationToken) is { } company)
        {
            title = $"Join {WebUtility.HtmlEncode(company.Name)} on FFXIV Craft Architect";
            description = "You are invited to join this trade company on FFXIV Craft Architect.";
            destination = $"/companies/{invitation.CompanyId.Value:D}?invite={Uri.EscapeDataString(code)}";
        }

        var imageOrigin = context.Request.Host.Host.Equals("dev.xivcraftarchitect.com", StringComparison.OrdinalIgnoreCase)
            ? "https://dev.xivcraftarchitect.com"
            : "https://xivcraftarchitect.com";
        var href = WebUtility.HtmlEncode(destination);
        return Results.Content($"""
            <!doctype html>
            <html lang="en"><head>
            <meta charset="utf-8">
            <meta name="robots" content="noindex">
            <meta name="referrer" content="no-referrer">
            <meta name="theme-color" content="#d4a73a">
            <title>{title}</title>
            <meta property="og:type" content="website">
            <meta property="og:site_name" content="FFXIV Craft Architect">
            <meta property="og:title" content="{title}">
            <meta property="og:description" content="{description}">
            <meta property="og:image" content="{imageOrigin}/invitation-letter.png">
            <meta name="twitter:card" content="summary">
            <meta name="twitter:title" content="{title}">
            <meta name="twitter:description" content="{description}">
            <meta name="twitter:image" content="{imageOrigin}/invitation-letter.png">
            <script>location.replace({JsonSerializer.Serialize(destination)});</script>
            <noscript><meta http-equiv="refresh" content="0;url={href}"></noscript>
            </head><body><a href="{href}">Continue to FFXIV Craft Architect</a></body></html>
            """, "text/html; charset=utf-8");
    }
}
