using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

public sealed class DiscordIdentityAuthorization(
    ProfileHostOptions profileOptions,
    ProfileAuthenticationGate authenticationGate,
    SqliteProfileHostStore profiles,
    ProfileAccessKeyHasher accessKeyHasher)
{
    private const string AccessKeyHeader = "X-Profile-Key";

    public async Task<ProfileHostProfileResponse?> ResolveAsync(
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!profileOptions.Enabled)
        {
            return null;
        }

        var key = request.Headers[AccessKeyHeader].ToString();
        return string.IsNullOrWhiteSpace(key) || key.Length > 256
            ? null
            : await authenticationGate.ExecuteAsync(
                key,
                ct => profiles.TryAuthenticateCachedAsync(
                    key,
                    accessKeyHasher,
                    ct),
                ct => profiles.AuthenticateAsync(
                    key,
                    accessKeyHasher,
                    ct),
                cancellationToken);
    }
}

internal static class DiscordOAuthAuthorization
{
    public static DiscordLinkStartResponse CreateResponse(
        DiscordIdentityOptions options,
        string callbackUri,
        string state,
        string verifier)
    {
        var challenge = DiscordIdentityValue.Base64Url(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = QueryString.Create(new KeyValuePair<string, string?>[]
        {
            new("client_id", options.ClientId),
            new("response_type", "code"),
            new("redirect_uri", callbackUri),
            new("scope", "identify"),
            new("state", state),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256")
        });
        return new DiscordLinkStartResponse(options.AuthorizationEndpoint + query);
    }

    public static string CreateSecret(int byteCount) =>
        DiscordIdentityValue.Base64Url(RandomNumberGenerator.GetBytes(byteCount));
}
