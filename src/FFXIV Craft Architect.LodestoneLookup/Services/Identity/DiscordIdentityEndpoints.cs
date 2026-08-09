using System.Text;
using System.Text.Encodings.Web;

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
                DiscordIdentityLinkService service,
                CancellationToken cancellationToken) =>
            {
                var profile = await authorization.ResolveAsync(
                    request,
                    cancellationToken);
                return profile == null
                    ? Results.Unauthorized()
                    : Results.Ok(await service.GetStatusAsync(
                        profile,
                        cancellationToken));
            });
        group.MapPost(
            "/link",
            async (
                HttpRequest request,
                DiscordIdentityAuthorization authorization,
                DiscordIdentityLinkService service,
                CancellationToken cancellationToken) =>
            {
                var profile = await authorization.ResolveAsync(
                    request,
                    cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    return Results.Ok(await service.StartAsync(
                        profile,
                        cancellationToken));
                }
                catch (InvalidOperationException)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Discord linking is unavailable.");
                }
            });
        group.MapDelete(
            "/link",
            async (
                HttpRequest request,
                DiscordIdentityAuthorization authorization,
                DiscordIdentityLinkService service,
                CancellationToken cancellationToken) =>
            {
                var profile = await authorization.ResolveAsync(
                    request,
                    cancellationToken);
                if (profile == null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    await service.UnlinkAsync(profile, cancellationToken);
                    return Results.NoContent();
                }
                catch (InvalidOperationException)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Discord linking is unavailable.");
                }
            });
        group.MapGet(
            "/callback",
            async (
                string? code,
                string? state,
                DiscordIdentityLinkService service,
                CancellationToken cancellationToken) =>
            {
                DiscordLinkCompletion result;
                try
                {
                    result = await service.CompleteAsync(
                        code,
                        state,
                        cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    result = new DiscordLinkCompletion(
                        DiscordLinkCompletionStatus.ProviderRejected);
                }

                var success = result.Status is
                    DiscordLinkCompletionStatus.Linked or
                    DiscordLinkCompletionStatus.Refreshed;
                var title = success ? "Discord linked" : "Discord link not changed";
                var message = result.Status switch
                {
                    DiscordLinkCompletionStatus.Linked or
                    DiscordLinkCompletionStatus.Refreshed =>
                        $"{result.DisplayName} is now linked to this Craft Architect profile.",
                    DiscordLinkCompletionStatus.ExpiredState =>
                        "This linking attempt expired. Start a new link from Craft Architect.",
                    DiscordLinkCompletionStatus.ReplayedState =>
                        "This linking callback was already used. No account was changed.",
                    DiscordLinkCompletionStatus.Conflict =>
                        "That Craft Architect profile or Discord account already has another active link.",
                    DiscordLinkCompletionStatus.IdentityInactive =>
                        "The Craft Architect profile is no longer active.",
                    _ => "Discord could not verify this linking attempt. No account was changed."
                };
                return HtmlResult(
                    title,
                    message,
                    success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
            });
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
                DiscordIdentitySignInService service,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    return Results.Ok(await service.StartAsync(cancellationToken));
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
                            options.ApplicationBaseUri,
                            "signin",
                            completion.PlaintextAccessKey!)
                        : FragmentRedirect(
                            options.ApplicationBaseUri,
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

    private static IResult HtmlResult(string title, string message, int statusCode)
    {
        var encodedTitle = HtmlEncoder.Default.Encode(title);
        var encodedMessage = HtmlEncoder.Default.Encode(message);
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
              <title>{{encodedTitle}}</title>
            </head>
            <body style="font:16px system-ui;background:#171717;color:#f3f3f3;max-width:42rem;margin:5rem auto;padding:1.5rem">
              <h1>{{encodedTitle}}</h1>
              <p>{{encodedMessage}}</p>
              <p>You can close this tab and return to Craft Architect.</p>
            </body>
            </html>
            """;
        return Results.Content(
            html,
            "text/html; charset=utf-8",
            Encoding.UTF8,
            statusCode);
    }
}
