using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

public enum DiscordSignInCompletionStatus
{
    SessionIssued,
    Provisioned,
    InvalidState,
    ExpiredState,
    ReplayedState,
    IdentityInactive,
    ProviderRejected,
    Conflict
}

public sealed record DiscordSignInCompletion(
    DiscordSignInCompletionStatus Status,
    string? PlaintextAccessKey = null);

public sealed class DiscordIdentitySignInService(
    DiscordIdentityOptions options,
    SqliteDiscordIdentityStore links,
    SqliteProfileHostStore profiles,
    ProfileAccessKeyHasher accessKeyHasher,
    IDiscordOAuthClient discord,
    TimeProvider timeProvider)
{
    public async Task<DiscordLinkStartResponse> StartAsync(
        CancellationToken cancellationToken = default)
    {
        RequireEnabled();
        var state = DiscordOAuthAuthorization.CreateSecret(32);
        var verifier = DiscordOAuthAuthorization.CreateSecret(48);
        var now = timeProvider.GetUtcNow();
        await links.CreateSignInOAuthStateAsync(
            state,
            verifier,
            now,
            now + options.StateLifetime,
            cancellationToken);
        return DiscordOAuthAuthorization.CreateResponse(
            options,
            options.SignInCallbackUri,
            state,
            verifier);
    }

    public async Task<DiscordSignInCompletion> CompleteAsync(
        string? code,
        string? state,
        CancellationToken cancellationToken = default)
    {
        RequireEnabled();
        if (string.IsNullOrWhiteSpace(state) || state.Length > 256)
        {
            return new DiscordSignInCompletion(DiscordSignInCompletionStatus.InvalidState);
        }

        var consumed = await links.ConsumeOAuthStateAsync(
            state,
            timeProvider.GetUtcNow(),
            cancellationToken);
        if (consumed.Status != DiscordOAuthStateStatus.Consumed ||
            consumed.Purpose != DiscordOAuthPurpose.SignIn ||
            consumed.ProfileId != null ||
            string.IsNullOrWhiteSpace(consumed.PkceVerifier))
        {
            return new DiscordSignInCompletion(consumed.Status switch
            {
                DiscordOAuthStateStatus.Expired => DiscordSignInCompletionStatus.ExpiredState,
                DiscordOAuthStateStatus.Replayed => DiscordSignInCompletionStatus.ReplayedState,
                _ => DiscordSignInCompletionStatus.InvalidState
            });
        }

        if (string.IsNullOrWhiteSpace(code) || code.Length > 512)
        {
            return new DiscordSignInCompletion(DiscordSignInCompletionStatus.ProviderRejected);
        }

        var identity = await discord.ResolveIdentityAsync(
            code,
            consumed.PkceVerifier,
            options.SignInCallbackUri,
            cancellationToken);
        if (identity == null)
        {
            return new DiscordSignInCompletion(DiscordSignInCompletionStatus.ProviderRejected);
        }

        var now = timeProvider.GetUtcNow();
        await links.RecordSignInAuditAsync(
            profileId: null,
            "signin_started",
            identity.DiscordUserId,
            now,
            cancellationToken);
        var link = await links.LoadByDiscordUserAsync(
            identity.DiscordUserId,
            cancellationToken);
        if (link != null)
        {
            if (await profiles.LoadProfileAsync(
                    link.ProfileId.ToString("D"),
                    cancellationToken) == null)
            {
                return new DiscordSignInCompletion(DiscordSignInCompletionStatus.IdentityInactive);
            }

            var key = accessKeyHasher.CreateAccessKey();
            await profiles.AddAccessKeyAsync(
                link.ProfileId.ToString("D"),
                key,
                cancellationToken);
            await links.RecordSignInAuditAsync(
                profileId: null,
                "signin_session_issued",
                identity.DiscordUserId,
                now,
                cancellationToken);
            return new DiscordSignInCompletion(
                DiscordSignInCompletionStatus.SessionIssued,
                key.PlaintextKey);
        }

        var profile = await profiles.CreateProfileAsync(
            NormalizeProfileName(identity.DisplayName),
            cancellationToken);
        var profileId = Guid.Parse(profile.ProfileId);
        var linked = await links.LinkAsync(
            profileId,
            identity.DiscordUserId,
            identity.DisplayName,
            now,
            cancellationToken);
        if (linked.Status != DiscordIdentityLinkResultStatus.Linked)
        {
            await profiles.DisableProfileAsync(profile.ProfileId, cancellationToken);
            var winner = await links.LoadByDiscordUserAsync(
                identity.DiscordUserId,
                cancellationToken);
            if (winner != null &&
                await profiles.LoadProfileAsync(
                    winner.ProfileId.ToString("D"),
                    cancellationToken) != null)
            {
                var winnerKey = accessKeyHasher.CreateAccessKey();
                await profiles.AddAccessKeyAsync(
                    winner.ProfileId.ToString("D"),
                    winnerKey,
                    cancellationToken);
                await links.RecordSignInAuditAsync(
                    profileId: null,
                    "signin_session_issued",
                    identity.DiscordUserId,
                    now,
                    cancellationToken);
                return new DiscordSignInCompletion(
                    DiscordSignInCompletionStatus.SessionIssued,
                    winnerKey.PlaintextKey);
            }

            return new DiscordSignInCompletion(DiscordSignInCompletionStatus.Conflict);
        }

        await links.RecordSignInAuditAsync(
            profileId: null,
            "signin_provisioned",
            identity.DiscordUserId,
            now,
            cancellationToken);
        var accessKey = accessKeyHasher.CreateAccessKey();
        await profiles.AddAccessKeyAsync(
            profile.ProfileId,
            accessKey,
            cancellationToken);
        await links.RecordSignInAuditAsync(
            profileId: null,
            "signin_session_issued",
            identity.DiscordUserId,
            now,
            cancellationToken);
        return new DiscordSignInCompletion(
            DiscordSignInCompletionStatus.Provisioned,
            accessKey.PlaintextKey);
    }

    private static string NormalizeProfileName(string value)
    {
        var normalized = string.Join(' ', (value ?? string.Empty).Trim().Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Crafter";
        }

        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private void RequireEnabled()
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Discord sign-in is unavailable.");
        }
    }
}
