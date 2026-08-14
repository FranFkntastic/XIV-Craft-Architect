namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

public static class DiscordIdentityEndpoints
{
    public static RouteGroupBuilder MapDiscordIdentityEndpoints(
        this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/identity/v1/discord");
        group.MapGet(
            "/",
            async (
                HttpRequest request,
                DiscordIdentityAuthorization authorization,
                DiscordIdentityOptions options,
                SqliteDiscordIdentityStore links,
                CancellationToken cancellationToken) =>
            {
                var profile = await authorization.ResolveAsync(
                    request,
                    cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                var profileId = Guid.TryParse(profile.ProfileId, out var parsedProfileId)
                    ? parsedProfileId
                    : Guid.Empty;
                var link = options.Enabled && profileId != Guid.Empty
                    ? await links.LoadByProfileAsync(profileId, cancellationToken)
                    : null;
                return Results.Ok(new DiscordAccountIdentityStatus(
                    options.Enabled,
                    link != null,
                    link?.DisplayNameSnapshot,
                    link?.LinkedAt));
            });
        group.MapPost(
            "/link",
            () => RetiredLinkEndpoint());
        group.MapDelete(
            "/link",
            () => RetiredLinkEndpoint());
        group.MapGet(
            "/callback",
            () => RetiredLinkEndpoint());
        group.MapPost(
            "/participant-exchanges",
            async (
                DiscordParticipantExchangeRequest request,
                IDiscordParticipantExchangeService exchange,
                CancellationToken cancellationToken) =>
            {
                var result = await exchange.ExchangeAsync(
                    request,
                    cancellationToken);
                return result == null
                    ? Results.Unauthorized()
                    : Results.Ok(result);
            });

        var signIn = routes.MapGroup("/identity/v1/signin/discord");
        signIn.MapGet(
            "/status",
            (DiscordIdentityOptions options) =>
                Results.Ok(new DiscordSignInStatus(options.Enabled)));
        signIn.MapPost(
            "/start",
            async (
                string? returnPath,
                DiscordIdentitySignInService service,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    return Results.Ok(await service.StartAsync(returnPath, cancellationToken));
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new { error = "invalid_return_path", message = exception.Message });
                }
                catch (InvalidOperationException)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Discord sign-in is unavailable.");
                }
            });
        signIn.MapGet(
            "/callback",
            async (
                string? code,
                string? state,
                DiscordIdentitySignInService service,
                DiscordIdentityOptions options,
                CancellationToken cancellationToken) =>
            {
                DiscordSignInCompletion completion;
                try
                {
                    completion = await service.CompleteAsync(
                        code,
                        state,
                        cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    completion = new DiscordSignInCompletion(
                        DiscordSignInCompletionStatus.ProviderRejected);
                }

                var location = completion.Status is
                    DiscordSignInCompletionStatus.SessionIssued or
                    DiscordSignInCompletionStatus.Provisioned
                        ? FragmentRedirect(
                            ApplicationReturnUri(options.ApplicationBaseUri, completion.ReturnPath),
                            "signin",
                            completion.PlaintextAccessKey!)
                        : FragmentRedirect(
                            ApplicationReturnUri(options.ApplicationBaseUri, completion.ReturnPath),
                            "signin-error",
                            SignInErrorCode(completion.Status));
                return Results.Redirect(location);
            });
        return group;
    }

    private static string SignInErrorCode(DiscordSignInCompletionStatus status) => status switch
    {
        DiscordSignInCompletionStatus.ExpiredState => "expired-state",
        DiscordSignInCompletionStatus.ReplayedState => "replayed-state",
        DiscordSignInCompletionStatus.IdentityInactive => "inactive-profile",
        DiscordSignInCompletionStatus.ProviderRejected => "provider-rejected",
        DiscordSignInCompletionStatus.Conflict => "link-conflict",
        _ => "invalid-state"
    };

    private static string FragmentRedirect(string applicationBaseUri, string name, string value)
    {
        var builder = new UriBuilder(applicationBaseUri)
        {
            Fragment = $"{name}={Uri.EscapeDataString(value)}"
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string ApplicationReturnUri(string applicationBaseUri, string? returnPath) =>
        returnPath == null || !DiscordIdentitySignInService.IsValidReturnPath(returnPath)
            ? applicationBaseUri
            : new Uri(new Uri(applicationBaseUri), returnPath).AbsoluteUri;

    private static IResult RetiredLinkEndpoint() => Results.Json(
        new
        {
            error = "link_endpoints_retired",
            message = "Discord linking now happens through Discord sign-in. Start at /identity/v1/signin/discord/start."
        },
        statusCode: StatusCodes.Status410Gone);
}
