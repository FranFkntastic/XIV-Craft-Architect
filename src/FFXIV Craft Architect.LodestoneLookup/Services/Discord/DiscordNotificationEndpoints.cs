using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public static class DiscordNotificationEndpoints
{
    public static RouteGroupBuilder MapDiscordNotificationEndpoints(
        this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(
            "/trade/v1/companies/{companyId}/discord/notifications");

        group.MapGet(
            "/route",
            async (
                string companyId,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                CompanyCommissionDiscordDeliveryService delivery,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveOperatorAsync(
                    companyId,
                    request,
                    authorization,
                    cancellationToken);
                if (access.Result != null)
                {
                    return access.Result;
                }

                var route = await delivery.LoadRouteAsync(
                    access.Context!.CompanyId,
                    cancellationToken);
                return route == null ? Results.NotFound() : Results.Ok(route);
            });

        group.MapPut(
            "/route",
            async (
                string companyId,
                DiscordNotificationRouteUpdate body,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                CompanyCommissionDiscordDeliveryService delivery,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveOperatorAsync(
                    companyId,
                    request,
                    authorization,
                    cancellationToken);
                if (access.Result != null)
                {
                    return access.Result;
                }

                if (body == null)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_notification_route",
                        message = "A complete revisioned notification route is required."
                    });
                }

                var result = await delivery.PutRouteAsync(
                    access.Context!.CompanyId,
                    body,
                    cancellationToken);
                return result.Status switch
                {
                    DiscordNotificationRouteUpdateStatus.Applied or
                    DiscordNotificationRouteUpdateStatus.Replayed =>
                        Results.Ok(result.Configuration),
                    DiscordNotificationRouteUpdateStatus.Invalid =>
                        Results.BadRequest(new
                        {
                            error = "invalid_notification_route",
                            message = result.Error
                        }),
                    _ => Results.Conflict(new
                    {
                        error = "notification_route_conflict",
                        message = result.Error,
                        current = result.Configuration
                    })
                };
            });

        group.MapGet(
            "/diagnostics",
            async (
                string companyId,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                CompanyCommissionDiscordDeliveryService delivery,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveOperatorAsync(
                    companyId,
                    request,
                    authorization,
                    cancellationToken);
                if (access.Result != null)
                {
                    return access.Result;
                }

                return Results.Ok(await delivery.LoadDiagnosticsAsync(
                    access.Context!.CompanyId,
                    cancellationToken));
            });

        group.MapPost(
            "/diagnostics/{diagnosticId:guid}/retry",
            async (
                string companyId,
                Guid diagnosticId,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                CompanyCommissionDiscordDeliveryService delivery,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveOperatorAsync(
                    companyId,
                    request,
                    authorization,
                    cancellationToken);
                if (access.Result != null)
                {
                    return access.Result;
                }

                return await delivery.RetryDiagnosticAsync(
                    access.Context!.CompanyId,
                    diagnosticId,
                    cancellationToken)
                        ? Results.Accepted()
                        : Results.Conflict(new
                        {
                            error = "notification_not_retryable",
                            message =
                                "The diagnostic is missing, already resolved, or requires reconciliation instead of a blind retry."
                        });
            });

        return group;
    }

    private static async Task<(
        TradeCompanyAccessContext? Context,
        IResult? Result)> ResolveOperatorAsync(
        string rawCompanyId,
        HttpRequest request,
        TradeCompanyAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var access = await authorization.ResolveAsync(
            request,
            rawCompanyId,
            cancellationToken);
        if (access == null || access.GrantId == Guid.Empty)
        {
            return (null, Results.Unauthorized());
        }

        return access.Role is TradeCompanyRole.Operator or TradeCompanyRole.Owner
            ? (access, null)
            : (null, Results.Forbid());
    }
}
