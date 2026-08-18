using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record TradeCompanyWorkspaceProfileResponse(
    int SchemaVersion,
    Guid Id,
    string Name,
    string? Description,
    string? CommissionContact,
    TradePaymentPolicy PaymentPolicy,
    TradeMaterialPricingPolicy MaterialPricingPolicy,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record TradeCompanyWorkspaceProfileUpdateRequest(
    long ExpectedRevision,
    string Name,
    string? Description,
    string? CommissionContact,
    TradePaymentPolicy PaymentPolicy,
    TradeMaterialPricingPolicy MaterialPricingPolicy);

public sealed record TradeCompanyWorkspaceProfileUpdateResponse(long Revision);

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

                var snapshot = await companies.LoadCompanyProfileSnapshotAsync(
                    access,
                    cancellationToken);
                return snapshot == null
                    ? Results.NotFound()
                    : Results.Ok(new TradeCompanyWorkspaceProfileResponse(
                        1,
                        snapshot.Profile.Id,
                        snapshot.Profile.Name,
                        snapshot.Profile.Description,
                        snapshot.Profile.CommissionContact,
                        snapshot.Profile.PaymentPolicy,
                        snapshot.Profile.MaterialPricingPolicy,
                        snapshot.Revision.Value,
                        snapshot.Profile.CreatedAtUtc,
                        snapshot.Profile.UpdatedAtUtc));
            });

        companies.MapPut(
            "/workspace-profile",
            async (
                string companyId,
                TradeCompanyWorkspaceProfileUpdateRequest body,
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

                var access = await authorization.ResolveAsync(request, companyId, cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }
                if (body.ExpectedRevision < 0 ||
                    string.IsNullOrWhiteSpace(body.Name) ||
                    body.Name.Trim().Length > 160 ||
                    body.Description?.Length > 4_000 ||
                    body.CommissionContact?.Length > 240)
                {
                    return Results.BadRequest(new { error = "invalid_company_name" });
                }

                var result = await companies.UpdateCompanyProfileAsync(
                    access,
                    new CompanyRecordRevision(body.ExpectedRevision),
                    body.Name,
                    body.Description,
                    body.CommissionContact,
                    body.PaymentPolicy,
                    body.MaterialPricingPolicy,
                    cancellationToken);
                return result.Status switch
                {
                    CompanyHubMutationStatus.Applied => Results.Ok(
                        new TradeCompanyWorkspaceProfileUpdateResponse(result.ProfileRevision!.Value)),
                    CompanyHubMutationStatus.Conflict => Results.Conflict(new
                    {
                        error = result.ErrorCode ?? "company_profile_revision_conflict",
                        message = result.ErrorMessage ?? "The company policy changed before this save completed."
                    }),
                    CompanyHubMutationStatus.NotFound => Results.NotFound(),
                    _ => Results.BadRequest(new
                    {
                        error = result.ErrorCode ?? "company_profile_update_failed",
                        message = result.ErrorMessage ?? "The company policy could not be saved."
                    })
                };
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
