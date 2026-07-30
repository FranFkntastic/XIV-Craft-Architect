using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed record DiscordPublishExistingBriefRequest(string IdempotencyKey);

public sealed record DiscordCreatePublicationRequest(
    Guid OrderId,
    CompanyRecordRevision OrderRevision,
    CommissionBriefDocument Brief,
    string IdempotencyKey);

public sealed record DiscordCompanyPublicationResponse(
    Guid OrderId,
    string PublicId,
    int Version,
    DateTime PublishedAtUtc,
    string State,
    string DestinationLabel,
    string? Message);

public sealed record DiscordAcceptInterestRequest(
    Guid CrafterId,
    string IdempotencyKey);

public sealed record DiscordDeclineInterestRequest(string IdempotencyKey);

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
                TradeCompanyAuthorization authorization,
                DiscordClaimService claims,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    authorization,
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
            "/publications",
            async (
                string companyId,
                DiscordCreatePublicationRequest body,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                DiscordPublicationService publications,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    authorization,
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
                    body.OrderId == Guid.Empty ||
                    body.OrderRevision.Value <= 0 ||
                    body.Brief == null ||
                    string.IsNullOrWhiteSpace(body.IdempotencyKey))
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_publication",
                        message = "A revisioned order, commission brief, and idempotency key are required."
                    });
                }

                var result = await publications.PublishNewBriefAsync(
                    access,
                    body.OrderId,
                    body.OrderRevision,
                    body.Brief,
                    body.IdempotencyKey,
                    cancellationToken);
                if (!result.Success ||
                    result.Delivery.Publication is not { } publication ||
                    result.Brief is not { } brief)
                {
                    if (result.OrderCommitted && result.Brief is { } committedBrief)
                    {
                        return Results.Accepted(
                            value: new DiscordCompanyPublicationResponse(
                                body.OrderId,
                                committedBrief.PublicId,
                                committedBrief.Version,
                                committedBrief.PublishedAtUtc,
                                "Failed",
                                "company channel",
                                result.Delivery.Error ??
                                    "The commission terms are committed, but Discord delivery needs reconciliation."));
                    }

                    return Results.Conflict(new
                    {
                        error = "publication_conflict",
                        message = result.Delivery.Error ??
                            "The Discord publication could not be created."
                    });
                }

                return Results.Ok(ToPublicationResponse(
                    publication,
                    brief.PublishedAtUtc));
            });

        group.MapGet(
            "/publications",
            async (
                string companyId,
                Guid orderId,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                SqliteDiscordCollaborationStore collaboration,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    authorization,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                if (orderId == Guid.Empty)
                {
                    return Results.BadRequest();
                }

                var publication = await collaboration.LoadPublicationByOrderAsync(
                    access.CompanyId,
                    orderId,
                    cancellationToken);
                return publication == null
                    ? Results.NotFound()
                    : Results.Ok(ToPublicationResponse(
                        publication,
                        publication.CreatedAt.UtcDateTime));
            });

        group.MapDelete(
            "/publications/{publicId}",
            async (
                string companyId,
                string publicId,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                SqliteCommissionBriefStore briefs,
                DiscordPublicationService publications,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    authorization,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                if (access.Role == TradeCompanyRole.ReadOnly)
                {
                    return Results.Forbid();
                }

                if (!await briefs.RevokeCompanyOwnedAsync(
                        publicId,
                        access.CompanyId,
                        cancellationToken))
                {
                    return Results.NotFound();
                }

                await publications.RevokeAsync(publicId, cancellationToken);
                return Results.NoContent();
            });

        group.MapPost(
            "/publications/{publicId}/post",
            async (
                string companyId,
                string publicId,
                DiscordPublishExistingBriefRequest body,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                DiscordPublicationService publications,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    authorization,
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
                TradeCompanyAuthorization authorization,
                DiscordClaimService claims,
                DiscordPublicationService publications,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    authorization,
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
                if (result.Success && result.Claim != null)
                {
                    await publications.RefreshOrderAsync(
                        access,
                        result.Claim.OrderId,
                        cancellationToken);
                }
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
                DiscordDeclineInterestRequest body,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                DiscordClaimService claims,
                CancellationToken cancellationToken) =>
            {
                var access = await ResolveAccessAsync(
                    companyId,
                    request,
                    authorization,
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
                        error = "invalid_decline",
                        message = "A decline idempotency key is required."
                    });
                }

                var result = await claims.DeclineAsync(
                    access,
                    claimId,
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

        return group;
    }

    private static async Task<TradeCompanyAccessContext?> ResolveAccessAsync(
        string rawCompanyId,
        HttpRequest request,
        TradeCompanyAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var access = await authorization.ResolveAsync(
            request,
            rawCompanyId,
            cancellationToken);
        return access != null &&
            access.GrantId != Guid.Empty
                ? access
                : null;
    }

    private static DiscordCompanyPublicationResponse ToPublicationResponse(
        DiscordPublicationRecord publication,
        DateTime publishedAtUtc) =>
        new(
            publication.OrderId,
            publication.PublicId,
            publication.BriefVersion,
            publishedAtUtc,
            publication.State switch
            {
                DiscordPublicationState.Failed => "Failed",
                DiscordPublicationState.Revoked => "Revoked",
                DiscordPublicationState.ReconciliationRequired => "Failed",
                _ when publication.MessageId == null => "Pending",
                _ => "Published"
            },
            publication.ChannelId,
            publication.State == DiscordPublicationState.ReconciliationRequired
                ? "Discord delivery needs reconciliation before retrying."
                : publication.MessageId == null
                    ? "Discord delivery is queued."
                    : "Discord delivery is current.");
}
