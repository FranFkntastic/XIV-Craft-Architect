using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

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

public static class DiscordCollaborationEndpoints
{
    public static RouteGroupBuilder MapDiscordCollaborationEndpoints(
        this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/trade/v1/companies/{companyId}/discord");

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

                if (!CanManagePublications(access))
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

        group.MapPost(
            "/publications/{publicId}/retry",
            async (
                string companyId,
                string publicId,
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

                if (!CanManagePublications(access))
                {
                    return Results.Forbid();
                }

                if (string.IsNullOrWhiteSpace(publicId))
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_publication",
                        message = "An existing failed publication identity is required."
                    });
                }

                var result = await publications.RetryFailedAsync(
                    access,
                    publicId,
                    cancellationToken);
                return result.Status switch
                {
                    DiscordPublicationRetryStatus.Queued
                        when result.Publication is { } publication =>
                        Results.Ok(new DiscordCompanyPublicationResponse(
                            publication.OrderId,
                            publication.PublicId,
                            publication.BriefVersion,
                            publication.CreatedAt.UtcDateTime,
                            "Pending",
                            publication.ChannelId,
                            "Discord retry queued.")),
                    DiscordPublicationRetryStatus.Missing =>
                        Results.NotFound(new
                        {
                            error = "publication_not_found",
                            message = result.Error
                        }),
                    _ => Results.Conflict(new
                    {
                        error = "publication_not_retryable",
                        message = result.Error ??
                            "The Discord publication is not safe to retry."
                    })
                };
            });

        group.MapPost(
            "/publications/{publicId}/reconcile",
            async (
                string companyId,
                string publicId,
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

                if (!CanManagePublications(access))
                {
                    return Results.Forbid();
                }

                if (string.IsNullOrWhiteSpace(publicId))
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_publication",
                        message = "An existing publication identity is required."
                    });
                }

                var result = await publications.ReconcileAsync(
                    access,
                    publicId,
                    cancellationToken);
                return result.Status switch
                {
                    DiscordPublicationReconcileStatus.Queued
                        when result.Publication is { } publication =>
                        Results.Ok(ToPublicationResponse(
                            publication,
                            publication.CreatedAt.UtcDateTime)),
                    DiscordPublicationReconcileStatus.Missing =>
                        Results.NotFound(new
                        {
                            error = "publication_not_found",
                            message = result.Error
                        }),
                    _ => Results.Conflict(new
                    {
                        error = "publication_not_reconcilable",
                        message = result.Error ??
                            "The Discord publication could not be reconciled."
                    })
                };
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

                if (!CanManagePublications(access))
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

    internal static bool CanManagePublications(TradeCompanyAccessContext access) =>
        access.GrantId != Guid.Empty &&
        access.Role is TradeCompanyRole.Operator or TradeCompanyRole.Owner;

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
                DiscordPublicationState.Suppressed => "Suppressed",
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
