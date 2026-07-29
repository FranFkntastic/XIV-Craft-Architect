using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

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
                return Results.Ok(new CommissionBriefCreateResponse
                {
                    PublicId = created.Published.PublicId,
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

                var revocations = context.RequestServices
                    .GetService<IDiscordPublicationRevocationSink>();
                if (revocations != null)
                {
                    await revocations.RevokeAsync(publicId, ct);
                }

                return Results.NoContent();
            });
    }

    private static bool IsAvailable(HttpContext context, CommissionBriefOptions options) =>
        options.Enabled && options.AllowedHosts.Contains(context.Request.Host.Host);

    private static bool IsValidPublicId(string value) =>
        value.Length is >= 12 and <= 32 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

}
