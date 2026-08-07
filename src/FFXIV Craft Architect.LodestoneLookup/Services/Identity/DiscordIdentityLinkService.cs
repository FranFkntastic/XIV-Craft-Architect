using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

public enum DiscordLinkCompletionStatus
{
    Linked,
    Refreshed,
    InvalidState,
    ExpiredState,
    ReplayedState,
    IdentityInactive,
    ProviderRejected,
    Conflict
}

public sealed record DiscordLinkCompletion(
    DiscordLinkCompletionStatus Status,
    string? DisplayName = null);

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
                ct => profiles.AuthenticateAsync(
                    key,
                    accessKeyHasher,
                    ct),
                cancellationToken);
    }
}

public sealed class DiscordIdentityLinkService(
    DiscordIdentityOptions options,
    SqliteDiscordIdentityStore links,
    SqliteProfileHostStore profiles,
    IDiscordOAuthClient discord,
    TimeProvider timeProvider)
{
    public async Task<DiscordLinkStartResponse> StartAsync(
        ProfileHostProfileResponse profile,
        CancellationToken cancellationToken = default)
    {
        RequireEnabled();
        if (!Guid.TryParse(profile.ProfileId, out var profileId) || profileId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("The hosted profile identity is invalid.");
        }

        var state = DiscordOAuthAuthorization.CreateSecret(32);
        var verifier = DiscordOAuthAuthorization.CreateSecret(48);
        var now = timeProvider.GetUtcNow();
        await links.CreateOAuthStateAsync(
            profileId,
            state,
            verifier,
            now,
            now + options.StateLifetime,
            cancellationToken);
        return DiscordOAuthAuthorization.CreateResponse(
            options,
            options.CallbackUri,
            state,
            verifier);
    }

    public async Task<DiscordLinkCompletion> CompleteAsync(
        string? code,
        string? state,
        CancellationToken cancellationToken = default)
    {
        RequireEnabled();
        if (string.IsNullOrWhiteSpace(state) || state.Length > 256)
        {
            return new DiscordLinkCompletion(DiscordLinkCompletionStatus.InvalidState);
        }

        var consumed = await links.ConsumeOAuthStateAsync(
            state,
            timeProvider.GetUtcNow(),
            cancellationToken);
        if (consumed.Status != DiscordOAuthStateStatus.Consumed ||
            consumed.Purpose != DiscordOAuthPurpose.Link ||
            consumed.ProfileId is not { } profileId ||
            string.IsNullOrWhiteSpace(consumed.PkceVerifier))
        {
            return new DiscordLinkCompletion(consumed.Status switch
            {
                DiscordOAuthStateStatus.Expired => DiscordLinkCompletionStatus.ExpiredState,
                DiscordOAuthStateStatus.Replayed => DiscordLinkCompletionStatus.ReplayedState,
                _ => DiscordLinkCompletionStatus.InvalidState
            });
        }

        if (string.IsNullOrWhiteSpace(code) || code.Length > 512)
        {
            return new DiscordLinkCompletion(DiscordLinkCompletionStatus.ProviderRejected);
        }

        if (await profiles.LoadProfileAsync(
                profileId.ToString("D"),
                cancellationToken) == null)
        {
            return new DiscordLinkCompletion(DiscordLinkCompletionStatus.IdentityInactive);
        }

        var identity = await discord.ResolveIdentityAsync(
            code,
            consumed.PkceVerifier,
            options.CallbackUri,
            cancellationToken);
        if (identity == null)
        {
            return new DiscordLinkCompletion(DiscordLinkCompletionStatus.ProviderRejected);
        }

        if (await profiles.LoadProfileAsync(
                profileId.ToString("D"),
                cancellationToken) == null)
        {
            return new DiscordLinkCompletion(DiscordLinkCompletionStatus.IdentityInactive);
        }

        var result = await links.LinkAsync(
            profileId,
            identity.DiscordUserId,
            identity.DisplayName,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return result.Status switch
        {
            DiscordIdentityLinkResultStatus.Linked =>
                new DiscordLinkCompletion(
                    DiscordLinkCompletionStatus.Linked,
                    result.Link!.DisplayNameSnapshot),
            DiscordIdentityLinkResultStatus.Refreshed =>
                new DiscordLinkCompletion(
                    DiscordLinkCompletionStatus.Refreshed,
                    result.Link!.DisplayNameSnapshot),
            _ => new DiscordLinkCompletion(DiscordLinkCompletionStatus.Conflict)
        };
    }

    public async Task<DiscordIdentityLinkStatus> GetStatusAsync(
        ProfileHostProfileResponse profile,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled ||
            !Guid.TryParse(profile.ProfileId, out var profileId) ||
            profileId == Guid.Empty)
        {
            return new DiscordIdentityLinkStatus(
                options.Enabled,
                Linked: false,
                DisplayName: null,
                LinkedAt: null);
        }

        var link = await links.LoadByProfileAsync(profileId, cancellationToken);
        return new DiscordIdentityLinkStatus(
            Enabled: true,
            Linked: link != null,
            link?.DisplayNameSnapshot,
            link?.LinkedAt);
    }

    public async Task<bool> UnlinkAsync(
        ProfileHostProfileResponse profile,
        CancellationToken cancellationToken = default)
    {
        RequireEnabled();
        return Guid.TryParse(profile.ProfileId, out var profileId) &&
            profileId != Guid.Empty &&
            await links.UnlinkAsync(
                profileId,
                timeProvider.GetUtcNow(),
                cancellationToken);
    }

    private void RequireEnabled()
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Discord identity linking is unavailable.");
        }
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
