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
}
