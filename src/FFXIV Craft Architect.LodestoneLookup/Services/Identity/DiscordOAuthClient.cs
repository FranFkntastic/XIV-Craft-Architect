using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

public interface IDiscordOAuthClient
{
    Task<DiscordOAuthIdentity?> ResolveIdentityAsync(
        string code,
        string pkceVerifier,
        string callbackUri,
        CancellationToken cancellationToken = default);
}

public sealed class DiscordOAuthClient(
    HttpClient httpClient,
    DiscordIdentityOptions options) : IDiscordOAuthClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<DiscordOAuthIdentity?> ResolveIdentityAsync(
        string code,
        string pkceVerifier,
        string callbackUri,
        CancellationToken cancellationToken = default)
    {
        if (!IsBounded(code, 1, 512) ||
            !IsBounded(pkceVerifier, 43, 128) ||
            !Uri.TryCreate(callbackUri, UriKind.Absolute, out _))
        {
            return null;
        }

        using var tokenRequest = new HttpRequestMessage(
            HttpMethod.Post,
            options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = callbackUri,
                ["code_verifier"] = pkceVerifier
            })
        };
        tokenRequest.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        using var tokenResponse = await httpClient.SendAsync(
            tokenRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var tokenStream = await tokenResponse.Content.ReadAsStreamAsync(
            cancellationToken);
        var token = await JsonSerializer.DeserializeAsync<DiscordTokenResponse>(
            tokenStream,
            JsonOptions,
            cancellationToken);
        if (token == null ||
            !string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            !IsBounded(token.AccessToken, 32, 2048) ||
            !token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("identify", StringComparer.Ordinal))
        {
            return null;
        }

        using var userRequest = new HttpRequestMessage(
            HttpMethod.Get,
            options.UserEndpoint);
        userRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);
        userRequest.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        using var userResponse = await httpClient.SendAsync(
            userRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!userResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var userStream = await userResponse.Content.ReadAsStreamAsync(
            cancellationToken);
        var user = await JsonSerializer.DeserializeAsync<DiscordUserResponse>(
            userStream,
            JsonOptions,
            cancellationToken);
        if (user == null || !DiscordIdentityValue.IsSnowflake(user.Id))
        {
            return null;
        }

        var displayName = string.IsNullOrWhiteSpace(user.GlobalName)
            ? user.Username
            : user.GlobalName;
        return string.IsNullOrWhiteSpace(displayName)
            ? null
            : new DiscordOAuthIdentity(user.Id, displayName);
    }

    private static bool IsBounded(string? value, int minimum, int maximum) =>
        value is not null && value.Length >= minimum && value.Length <= maximum;

    private sealed record DiscordTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("scope")] string Scope);

    private sealed record DiscordUserResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("global_name")] string? GlobalName);
}
