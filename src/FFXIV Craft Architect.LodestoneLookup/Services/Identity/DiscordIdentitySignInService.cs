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
    string? PlaintextAccessKey = null,
    string? ReturnPath = null);

public sealed class DiscordIdentitySignInService(
    DiscordIdentityOptions options,
    SqliteDiscordIdentityStore links,
    SqliteProfileHostStore profiles,
    ProfileAccessKeyHasher accessKeyHasher,
    IDiscordOAuthClient discord,
    TimeProvider timeProvider)
{
    public async Task<DiscordLinkStartResponse> StartAsync(
        string? returnPath = null,
        CancellationToken cancellationToken = default)
    {
        RequireEnabled();
        if (returnPath != null && !IsValidReturnPath(returnPath))
        {
            throw new ArgumentException("Return path must be an application-relative path.", nameof(returnPath));
        }
        var state = DiscordOAuthAuthorization.CreateSecret(32);
        var verifier = DiscordOAuthAuthorization.CreateSecret(48);
        var now = timeProvider.GetUtcNow();
        await links.CreateSignInOAuthStateAsync(
            state,
            verifier,
            now,
            now + options.StateLifetime,
            returnPath,
            cancellationToken);
        return DiscordOAuthAuthorization.CreateResponse(
            options,
            options.SignInCallbackUri,
            state,
            verifier);
    }

    internal static bool IsValidReturnPath(string returnPath) =>
        returnPath.StartsWith("/", StringComparison.Ordinal) &&
        !returnPath.StartsWith("//", StringComparison.Ordinal) &&
        !returnPath.Contains('\\') &&
        Uri.IsWellFormedUriString(returnPath, UriKind.Relative);

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
            }, ReturnPath: consumed.ReturnPath);
        }

        if (string.IsNullOrWhiteSpace(code) || code.Length > 512)
        {
            return new DiscordSignInCompletion(
                DiscordSignInCompletionStatus.ProviderRejected,
                ReturnPath: consumed.ReturnPath);
        }

        DiscordOAuthIdentity? identity;
        try
        {
            identity = await discord.ResolveIdentityAsync(
                code,
                consumed.PkceVerifier,
                options.SignInCallbackUri,
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiscordSignInCompletion(
                DiscordSignInCompletionStatus.ProviderRejected,
                ReturnPath: consumed.ReturnPath);
        }
        if (identity == null)
        {
            return new DiscordSignInCompletion(
                DiscordSignInCompletionStatus.ProviderRejected,
                ReturnPath: consumed.ReturnPath);
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
                return new DiscordSignInCompletion(
                    DiscordSignInCompletionStatus.IdentityInactive,
                    ReturnPath: consumed.ReturnPath);
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
                key.PlaintextKey,
                consumed.ReturnPath);
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
                    winnerKey.PlaintextKey,
                    consumed.ReturnPath);
            }

            return new DiscordSignInCompletion(
                DiscordSignInCompletionStatus.Conflict,
                ReturnPath: consumed.ReturnPath);
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
            accessKey.PlaintextKey,
            consumed.ReturnPath);
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
