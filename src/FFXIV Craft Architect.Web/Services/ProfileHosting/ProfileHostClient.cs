using System.Net;
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

    public async Task<ProfileHostHealthResponse> GetHealthAsync(string hostUrl, CancellationToken ct)
    {
        var response = await _httpClient.GetFromJsonAsync<ProfileHostHealthResponse>(
            BuildUri(hostUrl, "/profile-host/health"),
            ct);
        return response ?? new ProfileHostHealthResponse
        {
            Status = "unavailable",
            ProfileHostEnabled = false
        };
    }

    public async Task<ProfileHostProfileResponse> GetProfileAsync(string hostUrl, string accessKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, hostUrl, "/profile-host/profile", accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileHostProfileResponse>(cancellationToken: ct))!;
    }

    public async Task<ProfileSyncChangesResponse> GetChangesAsync(
        string hostUrl,
        string accessKey,
        long sinceRevision,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, hostUrl, $"/profile-host/changes?sinceRevision={sinceRevision}", accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileSyncChangesResponse>(cancellationToken: ct))!;
    }

    public async Task<ProfileSyncPutResponse> PutObjectAsync(
        string hostUrl,
        string accessKey,
        string collection,
        string objectId,
        ProfileSyncPutRequest putRequest,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Put, hostUrl, $"/profile-host/objects/{collection}/{Uri.EscapeDataString(objectId)}", accessKey);
        request.Content = JsonContent.Create(putRequest);
        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadProfileSyncPutResponseAsync(response, ct);
    }

    public async Task<ProfileSyncPutResponse> DeleteObjectAsync(
        string hostUrl,
        string accessKey,
        string collection,
        string objectId,
        long expectedRevision,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Delete,
            hostUrl,
            $"/profile-host/objects/{collection}/{Uri.EscapeDataString(objectId)}?expectedRevision={expectedRevision}",
            accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        return await ReadProfileSyncPutResponseAsync(response, ct);
    }

    public async Task<ProfileSyncChangesResponse> UploadBootstrapAsync(
        string hostUrl,
        string accessKey,
        ProfileHostBootstrapPayload payload,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Post, hostUrl, "/profile-host/bootstrap/upload", accessKey);
        request.Content = JsonContent.Create(payload);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileSyncChangesResponse>(cancellationToken: ct))!;
    }

    public async Task<ProfileHostBootstrapPayload> ExportBootstrapAsync(string hostUrl, string accessKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, hostUrl, "/profile-host/bootstrap/export", accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProfileHostBootstrapPayload>(cancellationToken: ct))!;
    }

    public async Task<ProfileHostMigrationPreflightResponse> PreflightMigrationAsync(
        string hostUrl,
        string accessKey,
        ProfileHostMigrationPreflightRequest payload,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            hostUrl,
            "/profile-host/migrations/preflight",
            accessKey);
        request.Content = JsonContent.Create(payload);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await ReadRequiredJsonAsync<ProfileHostMigrationPreflightResponse>(
            response,
            "Migration preflight",
            ct);
    }

    public async Task<ProfileHostMigrationCommitClientResult> CommitMigrationAsync(
        string hostUrl,
        string accessKey,
        ProfileHostMigrationCommitRequest payload,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            hostUrl,
            "/profile-host/migrations/commit",
            accessKey);
        request.Content = JsonContent.Create(payload);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await ReadRequiredJsonAsync<ProfileHostMigrationPreflightResponse>(
                response,
                "Migration commit conflict",
                ct);
            return ProfileHostMigrationCommitClientResult.FromConflict(conflict);
        }

        response.EnsureSuccessStatusCode();
        var committed = await ReadRequiredJsonAsync<ProfileHostMigrationCommitResponse>(
            response,
            "Migration commit",
            ct);
        return ProfileHostMigrationCommitClientResult.FromCommit(committed);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string hostUrl, string path, string accessKey)
    {
        var request = new HttpRequestMessage(method, BuildUri(hostUrl, path));
        request.Headers.Add(AccessKeyHeaderName, accessKey);
        return request;
    }

    private static Uri BuildUri(string hostUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(hostUrl))
        {
            throw new InvalidOperationException("A profile host URL is required.");
        }

        var hostUri = new Uri(hostUrl.Trim().TrimEnd('/') + "/");
        var apiBaseUri = hostUri.AbsolutePath.TrimEnd('/').EndsWith(
            "/api",
            StringComparison.OrdinalIgnoreCase)
            ? hostUri
            : new Uri(hostUri, "api/");
        return new Uri(apiBaseUri, path.TrimStart('/'));
    }

    private static async Task<ProfileSyncPutResponse> ReadProfileSyncPutResponseAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
        }

        return (await response.Content.ReadFromJsonAsync<ProfileSyncPutResponse>(cancellationToken: ct))!;
    }

    private static async Task<T> ReadRequiredJsonAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
        where T : class
    {
        var payload = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        return payload ??
               throw new InvalidOperationException(
                   $"{operation} returned an empty response.");
    }
}

public sealed record ProfileHostMigrationCommitClientResult
{
    private ProfileHostMigrationCommitClientResult()
    {
    }

    public ProfileHostMigrationCommitResponse? Commit { get; private init; }
    public ProfileHostMigrationPreflightResponse? Conflict { get; private init; }
    public bool Succeeded => Commit is not null;

    public static ProfileHostMigrationCommitClientResult FromCommit(
        ProfileHostMigrationCommitResponse commit) =>
        new()
        {
            Commit = commit
        };

    public static ProfileHostMigrationCommitClientResult FromConflict(
        ProfileHostMigrationPreflightResponse conflict) =>
        new()
        {
            Conflict = conflict
        };
}
