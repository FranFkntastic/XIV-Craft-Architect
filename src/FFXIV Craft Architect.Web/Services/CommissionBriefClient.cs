using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class CommissionBriefClient
{
    private readonly HttpClient _httpClient;

    public CommissionBriefClient(ProfileHostClientOptions options)
        : this(options, new HttpClient())
    {
    }

    public CommissionBriefClient(ProfileHostClientOptions options, HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(
            ProfileHostClient.NormalizeHostUrl(options.DefaultHostUrl),
            UriKind.Absolute);
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<CommissionBriefCreateResponse> PublishAsync(
        CommissionBriefDocument brief,
        TradeCompanyPublicationOwnership? ownership = null,
        CancellationToken ct = default)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "xivdata/commission-briefs",
            new CommissionBriefCreateRequest
            {
                Brief = brief,
                Ownership = ownership
            },
            ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CommissionBriefCreateResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Commission publication returned an empty response.");
    }

    public async Task<PortableCommissionLink> PublishPortableLinkAsync(
        CommissionBriefDocument brief,
        CancellationToken ct = default)
    {
        var published = await PublishAsync(brief, ownership: null, ct);
        return CreatePortableLink(published);
    }

    public static PortableCommissionLink CreatePortableLink(
        CommissionBriefCreateResponse published) =>
        ToPortableLink(
            published.PublicId,
            published.PublicUrl,
            published.Version,
            published.PublishedAtUtc,
            string.IsNullOrWhiteSpace(published.EditorToken)
                ? null
                : published.EditorToken,
            published.ClaimUrl);

    public static PortableCommissionLink CreatePortableLink(
        string publicId,
        string publicUrl,
        int version,
        DateTime publishedAtUtc,
        string? editorToken = null) =>
        ToPortableLink(
            publicId,
            publicUrl,
            version,
            publishedAtUtc,
            editorToken,
            claimUrl: null);

    public async Task<PortableCommissionLink> ResolvePortableLinkAsync(
        string publicId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        using var response = await _httpClient.GetAsync(
            $"xivdata/commission-briefs/{Uri.EscapeDataString(publicId)}/link",
            ct);
        response.EnsureSuccessStatusCode();
        var link = await response.Content.ReadFromJsonAsync<CommissionBriefLinkResponse>(
            cancellationToken: ct)
            ?? throw new InvalidOperationException(
                "Commission link resolution returned an empty response.");
        return ToPortableLink(
            link.PublicId,
            link.PublicUrl,
            link.Version,
            link.PublishedAtUtc,
            editorToken: null,
            claimUrl: null);
    }

    public async Task<bool> RevokeAsync(
        string publicId,
        string editorToken,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"xivdata/commission-briefs/{Uri.EscapeDataString(publicId)}");
        request.Headers.Add("X-Commission-Editor", editorToken);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return true;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private static PortableCommissionLink ToPortableLink(
        string publicId,
        string publicUrl,
        int version,
        DateTime publishedAtUtc,
        string? editorToken,
        string? claimUrl)
    {
        if (string.IsNullOrWhiteSpace(publicId) ||
            !Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri) ||
            publicUri.Scheme is not ("https" or "http") ||
            publicUri.Scheme == "http" && !publicUri.IsLoopback ||
            !string.IsNullOrEmpty(publicUri.UserInfo) ||
            !string.IsNullOrEmpty(publicUri.Fragment) ||
            !string.Equals(
                publicUri.Query,
                $"?id={Uri.EscapeDataString(publicId)}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Commission publication returned an unsafe public URL.");
        }

        var selectedUri = publicUri;
        if (claimUrl is not null)
        {
            if (!Uri.TryCreate(claimUrl, UriKind.Absolute, out var claimUri) ||
                !string.IsNullOrEmpty(claimUri.UserInfo) ||
                !HasSamePublicLocation(publicUri, claimUri) ||
                !HasValidClaimFragment(claimUri.Fragment))
            {
                throw new InvalidOperationException(
                    "Commission publication returned an unsafe claim URL.");
            }

            selectedUri = claimUri;
        }

        return new PortableCommissionLink(
            publicId,
            selectedUri.AbsoluteUri,
            publicUri.AbsoluteUri,
            version,
            publishedAtUtc,
            editorToken);
    }

    private static bool HasSamePublicLocation(Uri publicUri, Uri claimUri) =>
        string.Equals(publicUri.Scheme, claimUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(publicUri.IdnHost, claimUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        publicUri.Port == claimUri.Port &&
        string.Equals(publicUri.AbsolutePath, claimUri.AbsolutePath, StringComparison.Ordinal) &&
        string.Equals(publicUri.Query, claimUri.Query, StringComparison.Ordinal);

    private static bool HasValidClaimFragment(string fragment)
    {
        const string prefix = "#claim=";
        const int minimumCapabilityLength = 32;
        const int maximumCapabilityLength = 512;

        if (!fragment.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var capability = fragment.AsSpan(prefix.Length);
        if (capability.Length is < minimumCapabilityLength or > maximumCapabilityLength)
        {
            return false;
        }

        foreach (var character in capability)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record PortableCommissionLink(
    string PublicId,
    string Url,
    string PublicUrl,
    int Version,
    DateTime PublishedAtUtc,
    string? EditorToken);
