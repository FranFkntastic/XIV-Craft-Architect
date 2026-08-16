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

    public async Task<ProfileHostProfileResponse> UpdateProfileDisplayNameAsync(
        string hostUrl,
        string accessKey,
        long expectedMetadataRevision,
        string displayName,
        CancellationToken ct)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Put, hostUrl, "/profile-host/profile", accessKey);
            request.Content = JsonContent.Create(new ProfileHostDisplayNameUpdateRequest
            {
                ExpectedMetadataRevision = expectedMetadataRevision,
                DisplayName = displayName
            });
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.AccessKeyRejected,
                    "This browser session is no longer authorized.");
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.ProfileHostingDisabled,
                    "This hosted profile is no longer available.");
            }
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.InvalidProfileName,
                    "Account names must contain 1 to 120 visible characters.");
            }
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.ProfileNameConflict,
                    "The account name changed in another browser.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.HostUnavailable,
                    "The profile host could not update the account name.");
            }

            var result = await response.Content.ReadFromJsonAsync<ProfileHostDisplayNameUpdateResponse>(
                cancellationToken: ct);
            return result?.Profile
                ?? throw new ProfileHostConnectionException(
                    ProfileHostConnectionFailure.IncompatibleHost,
                    "The server returned an invalid account-name response.");
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
                "This browser lost contact with the profile host while updating the account name.",
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

    public async Task<IReadOnlyList<ProfileHostAccessKeyMetadata>> GetAccessKeysAsync(
        string hostUrl,
        string accessKey,
        CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, hostUrl, "/profile-host/keys", accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        EnsureKeyManagementResponse(response, "load active browser sessions");
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ProfileHostAccessKeyMetadata>>(
                cancellationToken: ct)
            ?? throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.IncompatibleHost,
                "The profile host returned an invalid access-key list.");
    }

    public async Task RevokeCurrentAccessKeyAsync(
        string hostUrl,
        string accessKey,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Delete,
            hostUrl,
            "/profile-host/keys/current",
            accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        EnsureKeyManagementResponse(response, "sign out this browser");
    }

    public async Task RevokeAccessKeyAsync(
        string hostUrl,
        string accessKey,
        string keyId,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Delete,
            hostUrl,
            $"/profile-host/keys/{Uri.EscapeDataString(keyId)}",
            accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        EnsureKeyManagementResponse(response, "revoke that browser session");
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
        CancellationToken ct,
        IReadOnlyCollection<string>? collections = null)
    {
        var path = $"/profile-host/changes?sinceRevision={sinceRevision}&limit={limit}";
        if (collections is { Count: > 0 })
        {
            path += $"&collections={Uri.EscapeDataString(string.Join(",", collections))}";
        }

        using var request = CreateRequest(
            HttpMethod.Get,
            hostUrl,
            path,
            accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.AccessKeyRejected,
                "The profile access key was rejected or revoked.");
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.IncompatibleHost,
                "The saved profile revision is incompatible with the hosted profile.");
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.ProfileHostingDisabled,
                "The configured host no longer exposes this hosted profile.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.HostUnavailable,
                $"The profile host returned HTTP {(int)response.StatusCode} while loading changes.");
        }

        return (await response.Content.ReadFromJsonAsync<ProfileSyncChangesResponse>(cancellationToken: ct))
            ?? throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.IncompatibleHost,
                "The profile host returned an invalid changes response.");
    }

    public async Task<ProfileSyncObjectEnvelope?> GetObjectAsync(
        string hostUrl,
        string accessKey,
        string collection,
        string objectId,
        CancellationToken ct)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            hostUrl,
            $"/profile-host/objects/{collection}/{Uri.EscapeDataString(objectId)}",
            accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.AccessKeyRejected,
                "The profile access key was rejected or revoked.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.HostUnavailable,
                $"The profile host returned HTTP {(int)response.StatusCode} while loading a hosted object.");
        }

        return await response.Content.ReadFromJsonAsync<ProfileSyncObjectEnvelope>(
            cancellationToken: ct);
    }

    public async Task<TradeOrderDeepArchivePage> SearchDeepArchivedOrdersAsync(
        string hostUrl,
        string accessKey,
        string query,
        int offset,
        int limit,
        CancellationToken ct)
    {
        var path = $"/profile-host/archive/orders?query={Uri.EscapeDataString(query)}&offset={offset}&limit={limit}";
        using var request = CreateRequest(HttpMethod.Get, hostUrl, path, accessKey);
        using var response = await _httpClient.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.AccessKeyRejected,
                "The profile access key was rejected or revoked.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.HostUnavailable,
                $"The profile host returned HTTP {(int)response.StatusCode} while searching older orders.");
        }
        return (await response.Content.ReadFromJsonAsync<TradeOrderDeepArchivePage>(
                cancellationToken: ct))
            ?? throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.IncompatibleHost,
                "The profile host returned an invalid deep archive response.");
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

    private static void EnsureKeyManagementResponse(HttpResponseMessage response, string operation)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.AccessKeyRejected,
                "This browser session is no longer authorized.");
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ProfileHostConnectionException(
                ProfileHostConnectionFailure.ProfileHostingDisabled,
                $"The profile host could not {operation}.");
        }

        response.EnsureSuccessStatusCode();
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

}
