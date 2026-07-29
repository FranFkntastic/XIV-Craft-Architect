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

                var validationError = Validate(request.Brief);
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

    private static string? Validate(CommissionBriefDocument brief)
    {
        if (brief == null ||
            string.IsNullOrWhiteSpace(brief.Title) ||
            string.IsNullOrWhiteSpace(brief.CompanyName) ||
            brief.Outputs.Count == 0)
        {
            return "Company, title, and at least one requested output are required.";
        }

        if (brief.Title.Length > 160 ||
            brief.CompanyName.Length > 120 ||
            (brief.Contact?.Length ?? 0) > 240 ||
            brief.DeliveryInstructions.Length > 1000 ||
            brief.Outputs.Count > 100 ||
            brief.CrafterMaterials.Count + brief.CompanyMaterials.Count > 500)
        {
            return "The commission brief exceeds the prototype publication limits.";
        }

        if (brief.Outputs.Any(output =>
                output.ItemId <= 0 ||
                output.Quantity <= 0 ||
                output.Name.Length is 0 or > 160) ||
            brief.CrafterMaterials.Concat(brief.CompanyMaterials).Any(material =>
                material.ItemId <= 0 ||
                material.Quantity <= 0 ||
                material.Name.Length is 0 or > 160 ||
                material.UnitCost < 0 ||
                material.TotalCost < 0) ||
            brief.Payment.MaterialReimbursement < 0 ||
            brief.Payment.MaterialBonus < 0 ||
            brief.Payment.CraftLabor < 0 ||
            brief.Payment.Total < 0 ||
            brief.Payment.MaterialAdjustmentPercent is < 0 or > 100 ||
            brief.Payment.CraftSynthCount < 0 ||
            brief.Payment.GilPerSynth < 0 ||
            brief.Payment.MaterialReimbursement +
                brief.Payment.MaterialBonus +
                brief.Payment.CraftLabor != brief.Payment.Total)
        {
            return "The commission brief contains invalid delivery or payment values.";
        }

        return null;
    }
}
