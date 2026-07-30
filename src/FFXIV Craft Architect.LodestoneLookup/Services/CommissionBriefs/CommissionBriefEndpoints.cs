using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

public static class CommissionBriefEndpoints
{
    public static void MapCommissionBriefEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/xivdata/commission-briefs");

        group.MapPost(
            "/",
            async (
                HttpContext context,
                CommissionBriefCreateRequest request,
                CommissionBriefOptions options,
                SqliteCommissionBriefStore store,
                CancellationToken ct) =>
            {
                if (!IsAvailable(context, options))
                {
                    return Results.NotFound();
                }

                if (request.Ownership != null)
                {
                    return Results.BadRequest(new
                    {
                        error = "canonical_company_ownership_required",
                        message = "Company-owned publications require the authenticated Trade Company API."
                    });
                }

                var validationError = CommissionBriefValidator.Validate(request.Brief);
                if (validationError != null)
                {
                    return Results.BadRequest(new { error = validationError });
                }

                var created = await store.CreateAsync(request.Brief, request.Ownership, ct);
                if (!options.TryBuildPublicUrl(created.Published.PublicId, out var publicUrl))
                {
                    await store.RevokeAsync(
                        created.Published.PublicId,
                        created.EditorToken,
                        ct);
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Commission links are unavailable.",
                        detail: "The canonical commission page URL is not configured safely.");
                }

                return Results.Ok(new CommissionBriefCreateResponse
                {
                    PublicId = created.Published.PublicId,
                    PublicUrl = publicUrl,
                    EditorToken = created.EditorToken,
                    Version = created.Published.Version,
                    PublishedAtUtc = created.Published.PublishedAtUtc
                });
            });

        group.MapGet(
            "/{publicId}",
            async (
                HttpContext context,
                string publicId,
                CommissionBriefOptions options,
                SqliteCommissionBriefStore store,
                CancellationToken ct) =>
            {
                if (!IsAvailable(context, options) || !IsValidPublicId(publicId))
                {
                    return Results.NotFound();
                }

                var brief = await store.LoadAsync(publicId, ct);
                return brief == null ? Results.NotFound() : Results.Ok(brief);
            });

        group.MapGet(
            "/{publicId}/link",
            async (
                HttpContext context,
                string publicId,
                CommissionBriefOptions options,
                SqliteCommissionBriefStore store,
                CancellationToken ct) =>
            {
                if (!IsAvailable(context, options) || !IsValidPublicId(publicId))
                {
                    return Results.NotFound();
                }

                var brief = await store.LoadAsync(publicId, ct);
                if (brief == null)
                {
                    return Results.NotFound();
                }

                if (!options.TryBuildPublicUrl(publicId, out var publicUrl))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Commission links are unavailable.",
                        detail: "The canonical commission page URL is not configured safely.");
                }

                return Results.Ok(new CommissionBriefLinkResponse
                {
                    PublicId = publicId,
                    PublicUrl = publicUrl,
                    Version = brief.Version,
                    PublishedAtUtc = brief.PublishedAtUtc
                });
            });

        group.MapDelete(
            "/{publicId}",
            async (
                HttpContext context,
                string publicId,
                CommissionBriefOptions options,
                SqliteCommissionBriefStore store,
                CancellationToken ct) =>
            {
                if (!IsAvailable(context, options) || !IsValidPublicId(publicId))
                {
                    return Results.NotFound();
                }

                var token = context.Request.Headers["X-Commission-Editor"].ToString();
                if (string.IsNullOrWhiteSpace(token))
                {
                    return Results.Unauthorized();
                }

                if (!await store.RevokeAsync(publicId, token, ct))
                {
                    return Results.Unauthorized();
                }

                var publications = context.RequestServices
                    .GetRequiredService<DiscordPublicationService>();
                await publications.RevokeAsync(publicId, ct);

                return Results.NoContent();
            });
    }

    public static void MapCompanyCommissionBriefEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(
            "/trade/v1/companies/{companyId}/commission-briefs");

        group.MapPost(
            "/",
            async (
                string companyId,
                CompanyCommissionBriefCreateRequest request,
                HttpRequest httpRequest,
                CommissionBriefOptions options,
                TradeCompanyAuthorization authorization,
                ProfileHostedTradeCompanyService companies,
                SqliteCommissionBriefStore store,
                CancellationToken ct) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var access = await authorization.ResolveAsync(
                    httpRequest,
                    companyId,
                    ct);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                if (request.OrderId == Guid.Empty ||
                    request.OrderRevision.Value <= 0 ||
                    string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
                    request.IdempotencyKey.Length > 120)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_publication",
                        message = "A revisioned order and bounded idempotency key are required."
                    });
                }

                var validationError = CommissionBriefValidator.Validate(request.Brief);
                if (validationError != null)
                {
                    return Results.BadRequest(new { error = validationError });
                }

                var ownership = new TradeCompanyPublicationOwnership(
                    access.CompanyId,
                    request.OrderId,
                    request.OrderRevision);
                var publicId = SqliteCommissionBriefStore.CreateCompanyPublicId(
                    ownership,
                    request.IdempotencyKey);
                if (!options.TryBuildPublicUrl(publicId, out var publicUrl))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Commission links are unavailable.",
                        detail: "The canonical commission page URL is not configured safely.");
                }

                var existing = await store.LoadAsync(publicId, ct);
                if (existing == null)
                {
                    var order = await companies.LoadRecordAsync(
                        access,
                        TradeCompanyRecordKinds.Order,
                        request.OrderId.ToString("D"),
                        ct);
                    if (order == null)
                    {
                        return Results.NotFound(new
                        {
                            error = "canonical_order_missing",
                            message = "The canonical Trade order is unavailable."
                        });
                    }
                    if (order.RecordRevision != request.OrderRevision)
                    {
                        return Results.Conflict(new
                        {
                            error = "order_revision_conflict",
                            message = "The canonical Trade order changed before publication."
                        });
                    }
                }

                PublishedCommissionBrief published;
                try
                {
                    published = await store.CreateCompanyOwnedAsync(
                        request.Brief,
                        ownership,
                        request.IdempotencyKey,
                        ct);
                }
                catch (InvalidOperationException exception)
                {
                    return Results.Conflict(new
                    {
                        error = "publication_conflict",
                        message = exception.Message
                    });
                }

                return Results.Ok(new CommissionBriefCreateResponse
                {
                    PublicId = published.PublicId,
                    PublicUrl = publicUrl,
                    EditorToken = string.Empty,
                    Version = published.Version,
                    PublishedAtUtc = published.PublishedAtUtc
                });
            });

        group.MapDelete(
            "/{publicId}",
            async (
                string companyId,
                string publicId,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                SqliteCommissionBriefStore store,
                CancellationToken ct) =>
            {
                var access = await authorization.ResolveAsync(
                    request,
                    companyId,
                    ct);
                if (access == null)
                {
                    return Results.Unauthorized();
                }
                if (!IsValidPublicId(publicId))
                {
                    return Results.NotFound();
                }

                return await store.RevokeCompanyOwnedAsync(
                    publicId,
                    access.CompanyId,
                    ct)
                    ? Results.NoContent()
                    : Results.NotFound();
            });
    }

    private static bool IsAvailable(HttpContext context, CommissionBriefOptions options) =>
        options.Enabled &&
        options.IsAllowedRequestHost(context.Request.Host.Host);

    private static bool IsValidPublicId(string value) =>
        value.Length is >= 12 and <= 32 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

}
