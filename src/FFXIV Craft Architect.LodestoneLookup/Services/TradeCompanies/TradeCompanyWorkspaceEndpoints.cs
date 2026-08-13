using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record TradeCompanyWorkspaceProfileResponse(
    Guid Id,
    string Name,
    string? CommissionContact,
    TradePaymentPolicy PaymentPolicy,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record TradeCompanyOrderAdoptionRequest(
    CompanyRecordRevision SourceRevision,
    string IdempotencyKey);

public sealed record TradeCompanyOrderAdoptionResponse(
    TradeCompanyRecordEnvelope OrderRecord,
    CompanyRecordRevision? CompanyRevision);

public static class TradeCompanyWorkspaceEndpoints
{
    public static void MapTradeCompanyWorkspaceEndpoints(this WebApplication app)
    {
        var companies = app.MapGroup("/trade/v1/companies/{companyId}");

        companies.MapGet(
            "/workspace-profile",
            async (
                string companyId,
                HttpRequest request,
                ProfileHostOptions options,
                TradeCompanyAuthorization authorization,
                ProfileHostedTradeCompanyService companies,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var access = await authorization.ResolveAsync(
                    request,
                    companyId,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                var profile = await companies.LoadCompanyProfileAsync(
                    access,
                    cancellationToken);
                return profile == null
                    ? Results.NotFound()
                    : Results.Ok(new TradeCompanyWorkspaceProfileResponse(
                        profile.Id,
                        profile.Name,
                        profile.CommissionContact,
                        profile.PaymentPolicy,
                        profile.CreatedAtUtc,
                        profile.UpdatedAtUtc));
            });

        companies.MapPost(
            "/orders/{orderId:guid}/adopt",
            async (
                string companyId,
                Guid orderId,
                TradeCompanyOrderAdoptionRequest body,
                HttpRequest request,
                ProfileHostOptions options,
                TradeCompanyAuthorization authorization,
                ProfileHostedTradeCompanyService companies,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var access = await authorization.ResolveAsync(
                    request,
                    companyId,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                var result = await companies.AdoptSynchronizedOrderAsync(
                    access,
                    orderId,
                    body.SourceRevision,
                    body.IdempotencyKey,
                    cancellationToken);
                if (result.Success && result.Record != null)
                {
                    return Results.Ok(new TradeCompanyOrderAdoptionResponse(
                        result.Record,
                        result.CompanyRevision));
                }

                var problem = new
                {
                    error = result.ErrorCode ?? "order_adoption_failed",
                    message = result.ErrorMessage ?? "The synchronized draft could not be adopted."
                };
                return result.Status == TradeCompanyMutationStatus.Conflict
                    ? Results.Conflict(problem)
                    : Results.BadRequest(problem);
            });
    }
}
