using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class CommissionBriefClient
{
    private readonly HttpClient _httpClient;

    public CommissionBriefClient(LodestoneLookupClientOptions options)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = options.BaseAddress,
            Timeout = TimeSpan.FromSeconds(20)
        };
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
        return ToPortableLink(
            published.PublicId,
            published.PublicUrl,
            published.Version,
            published.PublishedAtUtc,
            published.EditorToken);
    }

    public static PortableCommissionLink CreatePortableLink(
        CommissionBriefCreateResponse published) =>
        CreatePortableLink(
            published.PublicId,
            published.PublicUrl,
            published.Version,
            published.PublishedAtUtc,
            string.IsNullOrWhiteSpace(published.EditorToken)
                ? null
                : published.EditorToken);

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
            editorToken);

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
            editorToken: null);
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
        string? editorToken)
    {
        if (string.IsNullOrWhiteSpace(publicId) ||
            !Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            uri.Scheme == "http" && !uri.IsLoopback ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(
                uri.Query,
                $"?id={Uri.EscapeDataString(publicId)}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Commission publication returned an unsafe public URL.");
        }

        return new PortableCommissionLink(
            publicId,
            uri.AbsoluteUri,
            version,
            publishedAtUtc,
            editorToken);
    }
}

public sealed record PortableCommissionLink(
    string PublicId,
    string Url,
    int Version,
    DateTime PublishedAtUtc,
    string? EditorToken);
