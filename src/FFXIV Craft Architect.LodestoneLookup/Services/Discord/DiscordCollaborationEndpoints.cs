using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed record DiscordPublishExistingBriefRequest(string IdempotencyKey);

public sealed record DiscordAcceptInterestRequest(
    Guid CrafterId,
    string IdempotencyKey);

public static class DiscordCollaborationEndpoints
{
    public static RouteGroupBuilder MapDiscordCollaborationEndpoints(
        this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/trade/v1/companies/{companyId}/discord");

        group.MapGet(
            "/claims",
            async (
                string companyId,
                Guid? orderId,
                HttpRequest request,
                IDiscordCompanyAccessResolver accessResolver,
                DiscordClaimService claims,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    accessResolver,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                var pending = await claims.LoadPendingAsync(
                    access,
                    orderId,
                    cancellationToken);
                return Results.Ok(pending);
            });

        group.MapPost(
            "/publications/{publicId}/post",
            async (
                string companyId,
                string publicId,
                DiscordPublishExistingBriefRequest body,
                HttpRequest request,
                IDiscordCompanyAccessResolver accessResolver,
                DiscordPublicationService publications,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    accessResolver,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                if (access.Role == TradeCompanyRole.ReadOnly)
                {
                    return Results.Forbid();
                }

                if (body == null || string.IsNullOrWhiteSpace(body.IdempotencyKey))
                {
                    return Results.BadRequest(new
                    {
                        error = "missing_idempotency_key",
                        message = "A publication idempotency key is required."
                    });
                }

                var result = await publications.PublishExistingBriefAsync(
                    access,
                    publicId,
                    body.IdempotencyKey,
                    cancellationToken);
                return result.Success
                    ? Results.Ok(result)
                    : Results.Conflict(result);
            });

        group.MapPost(
            "/claims/{claimId:guid}/accept",
            async (
                string companyId,
                Guid claimId,
                DiscordAcceptInterestRequest body,
                HttpRequest request,
                IDiscordCompanyAccessResolver accessResolver,
                DiscordClaimService claims,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    accessResolver,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                if (access.Role == TradeCompanyRole.ReadOnly)
                {
                    return Results.Forbid();
                }

                if (body == null ||
                    body.CrafterId == Guid.Empty ||
                    string.IsNullOrWhiteSpace(body.IdempotencyKey))
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_acceptance",
                        message = "An explicit crafter and idempotency key are required."
                    });
                }

                var result = await claims.AcceptAsync(
                    access,
                    claimId,
                    body.CrafterId,
                    body.IdempotencyKey,
                    cancellationToken);
                return result.Status switch
                {
                    DiscordOperatorClaimStatus.Applied or
                    DiscordOperatorClaimStatus.Replayed => Results.Ok(result),
                    DiscordOperatorClaimStatus.Missing => Results.NotFound(result),
                    _ => Results.Conflict(result)
                };
            });

        group.MapPost(
            "/claims/{claimId:guid}/decline",
            async (
                string companyId,
                Guid claimId,
                HttpRequest request,
                IDiscordCompanyAccessResolver accessResolver,
                DiscordClaimService claims,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    accessResolver,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                if (access.Role == TradeCompanyRole.ReadOnly)
                {
                    return Results.Forbid();
                }

                var result = await claims.DeclineAsync(
                    access,
                    claimId,
                    cancellationToken);
                return result.Status switch
                {
                    DiscordOperatorClaimStatus.Applied or
                    DiscordOperatorClaimStatus.Replayed => Results.Ok(result),
                    DiscordOperatorClaimStatus.Missing => Results.NotFound(result),
                    _ => Results.Conflict(result)
                };
            });

        group.MapPost(
            "/reconcile",
            async (
                string companyId,
                HttpRequest request,
                IDiscordCompanyAccessResolver accessResolver,
                DiscordProjectionService projections,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    accessResolver,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                if (access.Role == TradeCompanyRole.ReadOnly)
                {
                    return Results.Forbid();
                }

                return Results.Ok(await projections.ReconcileAsync(
                    access,
                    cancellationToken));
            });

        return group;
    }

    private static async Task<TradeCompanyAccessContext?> ResolveAccessAsync(
        string rawCompanyId,
        HttpRequest request,
        IDiscordCompanyAccessResolver resolver,
        CancellationToken cancellationToken)
    {
        if (!CompanyId.TryParse(rawCompanyId, out var companyId))
        {
            return null;
        }

        var access = await resolver.ResolveAsync(
            request,
            companyId,
            cancellationToken);
        return access?.CompanyId == companyId &&
            access.GrantId != Guid.Empty
                ? access
                : null;
    }
}
