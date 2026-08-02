namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

public sealed class DiscordIdentityOptions
{
    public bool Enabled { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string BootstrapSecret { get; init; } = string.Empty;
    public string CallbackUri { get; init; } = string.Empty;
    public string ApplicationBaseUri { get; init; } = string.Empty;
    public string DatabasePath { get; init; } = string.Empty;
    public string AuthorizationEndpoint { get; init; } =
        "https://discord.com/oauth2/authorize";
    public string TokenEndpoint { get; init; } =
        "https://discord.com/api/v10/oauth2/token";
    public string UserEndpoint { get; init; } =
        "https://discord.com/api/v10/users/@me";
    public TimeSpan StateLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ParticipantBootstrapLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!DiscordIdentityValue.IsSnowflake(ClientId) ||
            ClientSecret.Length < 32 ||
            BootstrapSecret.Length < 32 ||
            string.IsNullOrWhiteSpace(DatabasePath) ||
            StateLifetime is { TotalSeconds: < 60 or > 900 } ||
            ParticipantBootstrapLifetime is { TotalSeconds: < 30 or > 900 })
        {
            throw new InvalidOperationException(
                "Discord identity linking requires a stable application ID, separate server secrets, a database path, and bounded lifetimes.");
        }

        RequireSecureAbsoluteUri(CallbackUri, nameof(CallbackUri));
        RequireSecureAbsoluteUri(ApplicationBaseUri, nameof(ApplicationBaseUri));
        RequireSecureAbsoluteUri(AuthorizationEndpoint, nameof(AuthorizationEndpoint));
        RequireSecureAbsoluteUri(TokenEndpoint, nameof(TokenEndpoint));
        RequireSecureAbsoluteUri(UserEndpoint, nameof(UserEndpoint));
        var callback = new Uri(CallbackUri);
        if (!callback.AbsolutePath.EndsWith(
                "/identity/v1/discord/callback",
                StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(callback.Query) ||
            !string.IsNullOrEmpty(callback.Fragment))
        {
            throw new InvalidOperationException(
                "Discord identity CallbackUri must exactly target the fixed identity callback endpoint without query or fragment data.");
        }
    }

    private static void RequireSecureAbsoluteUri(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps &&
            !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw new InvalidOperationException(
                $"Discord identity {name} must be an absolute HTTPS URI (HTTP is allowed only for loopback development).");
        }
    }
}

internal static class DiscordIdentityValue
{
    public static bool IsSnowflake(string? value) =>
        value is { Length: >= 17 and <= 20 } && value.All(char.IsAsciiDigit);

    public static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
