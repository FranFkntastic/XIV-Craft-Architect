using System.Net.Http.Json;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed record DiscordIdentityWebStatus(
    bool Enabled,
    bool Linked,
    string? DisplayName,
    DateTimeOffset? LinkedAt);

public sealed record DiscordSignInWebStatus(bool Enabled);

public sealed class DiscordIdentityClient(HttpClient httpClient)
{
    private const string AccessKeyHeader = "X-Profile-Key";

    public async Task<DiscordIdentityWebStatus> GetStatusAsync(
        string hostUrl,
        string accessKey,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            hostUrl,
            "identity/v1/discord/",
            accessKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DiscordIdentityWebStatus>(
                cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "The Discord identity service returned an empty status.");
    }

    public async Task<Uri> StartLinkAsync(
        string hostUrl,
        string accessKey,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            hostUrl,
            "identity/v1/discord/link",
            accessKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<DiscordLinkStartDto>(
            cancellationToken: cancellationToken);
        return result != null &&
            Uri.TryCreate(result.AuthorizationUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps
                ? uri
                : throw new InvalidOperationException(
                    "The Discord identity service returned an invalid authorization address.");
    }

    public async Task<DiscordSignInWebStatus> GetSignInStatusAsync(
        string hostUrl,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            hostUrl,
            "identity/v1/signin/discord/status");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DiscordSignInWebStatus>(
                cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException(
                "The Discord sign-in service returned an empty status.");
    }

    public async Task<Uri> StartSignInAsync(
        string hostUrl,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            hostUrl,
            "identity/v1/signin/discord/start");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<DiscordLinkStartDto>(
            cancellationToken: cancellationToken);
        return result != null &&
            Uri.TryCreate(result.AuthorizationUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps
                ? uri
                : throw new InvalidOperationException(
                    "The Discord sign-in service returned an invalid authorization address.");
    }

    public async Task UnlinkAsync(
        string hostUrl,
        string accessKey,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(
            HttpMethod.Delete,
            hostUrl,
            "identity/v1/discord/link",
            accessKey);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string hostUrl,
        string path,
        string accessKey)
    {
        var request = new HttpRequestMessage(
            method,
            new Uri(new Uri(ProfileHostClient.NormalizeHostUrl(hostUrl)), path));
        request.Headers.Add(AccessKeyHeader, accessKey);
        return request;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string hostUrl,
        string path) =>
        new(
            method,
            new Uri(new Uri(ProfileHostClient.NormalizeHostUrl(hostUrl)), path));

    private sealed record DiscordLinkStartDto(string AuthorizationUrl);
}
