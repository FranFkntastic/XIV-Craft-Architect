using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class ProfileHostClient
{
    private const string AccessKeyHeaderName = "X-Profile-Key";
    private readonly HttpClient _httpClient;
    private readonly ProfileHostClientOptions _options;

    public ProfileHostClient(HttpClient httpClient, ProfileHostClientOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public ProfileHostClient(HttpClient httpClient)
        : this(
            httpClient,
            new ProfileHostClientOptions(
                httpClient.BaseAddress?.AbsoluteUri ?? "http://localhost/"))
    {
    }

    public string DefaultHostUrl => _options.DefaultHostUrl;

    public async Task<ProfileHostHealthResponse> GetHealthAsync(string hostUrl, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                BuildUri(hostUrl, "/profile-host/health"),
                ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.HostUnavailable,
                    "The profile host did not answer its health check.");
            }

            var health = await response.Content.ReadFromJsonAsync<ProfileHostHealthResponse>(
                cancellationToken: ct);
            if (health == null)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.IncompatibleHost,
                    "The server is not a compatible Craft Architect profile host.");
            }

            return health;
        }
        catch (ProfileHostConnectionException)
        {
            throw;
        }
        catch (UriFormatException ex)
        {
            throw InvalidAddress(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.HostUnavailable,
                "This browser could not reach the profile host. The host may be offline or may not allow this site.",
                ex);
        }
    }

    public async Task<ProfileHostProfileResponse> GetProfileAsync(string hostUrl, string accessKey, CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, hostUrl, "/profile-host/profile", accessKey);
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.AccessKeyRejected,
                    "The access key was rejected. It may be incorrect, expired, or revoked.");
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.ProfileHostingDisabled,
                    "Profile hosting is not enabled on that server.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.HostUnavailable,
                    "The profile host could not complete authentication.");
            }

            return (await response.Content.ReadFromJsonAsync<ProfileHostProfileResponse>(
                    cancellationToken: ct))
                ?? throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.IncompatibleHost,
                    "The server returned an invalid profile response.");
        }
        catch (ProfileHostConnectionException)
        {
            throw;
        }
        catch (UriFormatException ex)
        {
            throw InvalidAddress(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.HostUnavailable,
                "This browser lost contact with the profile host while authenticating.",
                ex);
        }
    }

    public async Task<ProfileHostPairingCodeResponse> CreatePairingCodeAsync(
        string hostUrl,
        string accessKey,
        CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, hostUrl, "/profile-host/pairing/create", accessKey);
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.AccessKeyRejected,
                    "This browser is no longer authorized to create a pairing code.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.HostUnavailable,
                    "The profile host could not create a pairing code.");
            }

            return (await response.Content.ReadFromJsonAsync<ProfileHostPairingCodeResponse>(
                    cancellationToken: ct))
                ?? throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.IncompatibleHost,
                    "The server returned an invalid pairing response.");
        }
        catch (ProfileHostConnectionException)
        {
            throw;
        }
        catch (UriFormatException ex)
        {
            throw InvalidAddress(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.HostUnavailable,
                "This browser could not reach the profile host to create a pairing code.",
                ex);
        }
    }

    public async Task<ProfileHostPairingRedeemResponse> RedeemPairingCodeAsync(
        string hostUrl,
        string pairingCode,
        CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                BuildUri(hostUrl, "/profile-host/pairing/redeem"),
                new ProfileHostPairingRedeemRequest { PairingCode = pairingCode },
                ct);
            if (response.StatusCode == HttpStatusCode.BadRequest ||
                response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.PairingCodeRejected,
                    "The pairing code is invalid, expired, or has already been used.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.HostUnavailable,
                    "The profile host could not redeem the pairing code.");
            }
            return (await response.Content.ReadFromJsonAsync<ProfileHostPairingRedeemResponse>(
                    cancellationToken: ct))
                ?? throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.IncompatibleHost,
                    "The server returned an invalid pairing response.");
        }
        catch (ProfileHostConnectionException)
        {
            throw;
        }
        catch (UriFormatException ex)
        {
            throw InvalidAddress(ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.HostUnavailable,
                "This browser could not reach the profile host to redeem the pairing code.",
                ex);
        }
    }

    public async Task<ProfileSyncChangesResponse> GetChangesAsync(
        string hostUrl,
        string accessKey,
        long sinceRevision,
        int limit,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            hostUrl,
            $"/profile-host/changes?sinceRevision={sinceRevision}&limit={limit}",
            accessKey);
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

    private static HttpRequestMessage CreateRequest(HttpMethod method, string hostUrl, string path, string accessKey)
    {
        var request = new HttpRequestMessage(method, BuildUri(hostUrl, path));
        request.Headers.Add(AccessKeyHeaderName, accessKey);
        return request;
    }

    public static string NormalizeHostUrl(string hostUrl)
    {
        if (string.IsNullOrWhiteSpace(hostUrl))
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.InvalidAddress,
                "Enter a profile host address.");
        }

        if (!Uri.TryCreate(hostUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.InvalidAddress,
                "Enter a complete HTTP or HTTPS profile host address.");
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = uri.AbsolutePath.TrimEnd('/') + "/"
        };
        if (builder.Path == "/")
        {
            builder.Path = "/api/";
        }

        return builder.Uri.AbsoluteUri;
    }

    private static Uri BuildUri(string hostUrl, string path) =>
        new(new Uri(NormalizeHostUrl(hostUrl)), path.TrimStart('/'));

    private static ProfileHostConnectionException InvalidAddress(Exception innerException) =>
        new(
            ProfileHostConnectionFailure.InvalidAddress,
            "Enter a complete HTTP or HTTPS profile host address.",
            innerException);

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

}
