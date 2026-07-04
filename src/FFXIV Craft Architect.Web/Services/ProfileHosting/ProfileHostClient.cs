using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class ProfileHostClient
{
    private const string AccessKeyHeaderName = "X-Profile-Key";
    private readonly HttpClient _httpClient;

    public ProfileHostClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ProfileHostHealthResponse> GetHealthAsync(CancellationToken ct)
    {
        var response = await _httpClient.GetFromJsonAsync<ProfileHostHealthResponse>("/profile-host/health", ct);
        return response ?? new ProfileHostHealthResponse
        {
            Status = "unavailable",
            ProfileHostEnabled = false
        };
    }

    public async Task<ProfileHostProfileResponse> GetProfileAsync(string accessKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "/profile-host/profile", accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileHostProfileResponse>(cancellationToken: ct))!;
    }

    public async Task<ProfileSyncChangesResponse> GetChangesAsync(
        string accessKey,
        long sinceRevision,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, $"/profile-host/changes?sinceRevision={sinceRevision}", accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileSyncChangesResponse>(cancellationToken: ct))!;
    }

    public async Task<ProfileSyncPutResponse> PutObjectAsync(
        string accessKey,
        string collection,
        string objectId,
        ProfileSyncPutRequest putRequest,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Put, $"/profile-host/objects/{collection}/{Uri.EscapeDataString(objectId)}", accessKey);
        request.Content = JsonContent.Create(putRequest);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileSyncPutResponse>(cancellationToken: ct))!;
    }

    public async Task<ProfileSyncPutResponse> DeleteObjectAsync(
        string accessKey,
        string collection,
        string objectId,
        long expectedRevision,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Delete,
            $"/profile-host/objects/{collection}/{Uri.EscapeDataString(objectId)}?expectedRevision={expectedRevision}",
            accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileSyncPutResponse>(cancellationToken: ct))!;
    }

    public async Task<ProfileSyncChangesResponse> UploadBootstrapAsync(
        string accessKey,
        ProfileHostBootstrapPayload payload,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, "/profile-host/bootstrap/upload", accessKey);
        request.Content = JsonContent.Create(payload);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileSyncChangesResponse>(cancellationToken: ct))!;
    }

    public async Task<ProfileHostBootstrapPayload> ExportBootstrapAsync(string accessKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, "/profile-host/bootstrap/export", accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileHostBootstrapPayload>(cancellationToken: ct))!;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, string accessKey)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(AccessKeyHeaderName, accessKey);
        return request;
    }
}
