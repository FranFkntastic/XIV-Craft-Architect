using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
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
    DateTime UpdatedAtUtc,
    TradeCompanyPricingScope? CommissionPricingScope = null,
    IReadOnlyList<TradeMarginItem>? MarginItems = null,
    bool FreelancerOpenCommissions = true,
    IReadOnlyList<TradeCommissionTemplate>? CommissionTemplates = null,
    IReadOnlyList<TradeCompanySupplyDefault>? DefaultCompanySuppliedItems = null,
    IReadOnlyList<TradeMaterialSource>? MaterialSources = null,
    IReadOnlyList<string>? MarginsMarketRegions = null);

public sealed record TradeCompanyWorkspaceProfileUpdateRequest(
    long ExpectedRevision,
    string Name,
    string? Description,
    string? CommissionContact,
    TradePaymentPolicy? PaymentPolicy,
    TradeMaterialPricingPolicy? MaterialPricingPolicy = null,
    TradeCompanyPricingScope? CommissionPricingScope = null,
    bool UpdateCommissionPricingScope = false,
    IReadOnlyList<TradeMarginItem>? MarginItems = null,
    bool UpdateMarginItems = false,
    bool? FreelancerOpenCommissions = null,
    IReadOnlyList<TradeCommissionTemplate>? CommissionTemplates = null,
    bool UpdateCommissionTemplates = false,
    IReadOnlyList<TradeCompanySupplyDefault>? DefaultCompanySuppliedItems = null,
    bool UpdateDefaultCompanySuppliedItems = false,
    IReadOnlyList<TradeMaterialSource>? MaterialSources = null,
    bool UpdateMaterialSources = false,
    IReadOnlyList<string>? MarginsMarketRegions = null,
    bool UpdateMarginsMarketRegions = false);

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
                        snapshot.Profile.UpdatedAtUtc,
                        snapshot.Profile.CommissionPricingScope,
                        snapshot.Profile.MarginItems,
                        snapshot.Profile.FreelancerOpenCommissions,
                        snapshot.Profile.CommissionTemplates,
                        snapshot.Profile.DefaultCompanySuppliedItems,
                        snapshot.Profile.MaterialSources,
                        snapshot.Profile.MarginsMarketRegions));
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
                    body.CommissionContact?.Length > 240 ||
                    body.CommissionPricingScope is { IsValid: false })
                {
                    return Results.BadRequest(new { error = "invalid_company_name" });
                }
                if (body.MarginItems is { } items &&
                    (items.Count > 500 || items.Any(item => !TradeMarginItem.IsValid(item)) ||
                     items.Select(item => item.ItemId).Distinct().Count() != items.Count))
                {
                    return Results.BadRequest(new { error = "invalid_margin_items" });
                }
                if ((body.UpdateCommissionTemplates || body.CommissionTemplates != null) &&
                    !TradeCommissionTemplateValidator.IsValid(body.CommissionTemplates, out var templateError))
                {
                    return Results.BadRequest(new { error = "invalid_commission_templates", message = templateError });
                }
                if ((body.UpdateDefaultCompanySuppliedItems || body.DefaultCompanySuppliedItems != null) &&
                    !TradeCompanySupplyDefaultValidator.IsValid(
                        body.DefaultCompanySuppliedItems, out var supplyDefaultsError))
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_default_company_supplied_items",
                        message = supplyDefaultsError
                    });
                }

                if ((body.UpdateMaterialSources || body.MaterialSources != null) &&
                    !TradeMaterialSourceValidator.IsValid(body.MaterialSources, out var materialSourcesError))
                {
                    return Results.BadRequest(new
                    {
                        error = TradeMaterialSourceValidator.ErrorCode,
                        message = materialSourcesError
                    });
                }

                if ((body.UpdateMarginsMarketRegions || body.MarginsMarketRegions != null) &&
                    !TradeMarginMarketScope.IsValid(body.MarginsMarketRegions, out var marginsScopeError))
                {
                    return Results.BadRequest(new
                    {
                        error = TradeMarginMarketScope.ErrorCode,
                        message = marginsScopeError
                    });
                }

                var result = await companies.UpdateCompanyProfileAsync(
                    access,
                    new CompanyRecordRevision(body.ExpectedRevision),
                    body.Name,
                    body.Description,
                    body.CommissionContact,
                    body.PaymentPolicy,
                    body.MaterialPricingPolicy,
                    cancellationToken,
                    body.CommissionPricingScope,
                    body.UpdateCommissionPricingScope,
                    body.MarginItems,
                    body.UpdateMarginItems,
                    body.FreelancerOpenCommissions,
                    body.CommissionTemplates,
                    body.UpdateCommissionTemplates,
                    body.DefaultCompanySuppliedItems,
                    body.UpdateDefaultCompanySuppliedItems,
                    body.MaterialSources,
                    body.UpdateMaterialSources,
                    body.MarginsMarketRegions,
                    body.UpdateMarginsMarketRegions);
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

                // A conflicting company record is returned to the company operator, so a
                // browser can convert an open company draft from the authoritative copy.
                var problem = new
                {
                    error = result.ErrorCode ?? "order_adoption_failed",
                    message = result.ErrorMessage ?? "The synchronized draft could not be adopted.",
                    currentRecord = result.Status == TradeCompanyMutationStatus.Conflict
                        ? result.CurrentRecord
                        : null
                };
                return result.Status == TradeCompanyMutationStatus.Conflict
                    ? Results.Conflict(problem)
                    : Results.BadRequest(problem);
            });
    }
}
