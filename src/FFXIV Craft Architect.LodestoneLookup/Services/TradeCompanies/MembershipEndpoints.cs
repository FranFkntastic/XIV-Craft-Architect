using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record MembershipRequestBody(string? RequestNote);

public sealed record MembershipResponse(
    string CompanyId,
    Guid AccountProfileId,
    string Role,
    string State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    Guid? DecidedByProfileId,
    string? RequestNote);

public sealed record MembershipErrorResponse(string Error, string Message);

public static class MembershipEndpoints
{
    public static void MapMembershipEndpoints(this WebApplication app)
    {
        var companies = app.MapGroup("/trade/v1/companies");
        companies.MapPost(
            "/{companyId}/membership-requests",
            async (
                string companyId,
                MembershipRequestBody body,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                ProfileHostedTradeCompanyService companyService,
                SqliteMembershipStore memberships,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }
                var account = await accessResolver.ResolveAccountAsync(request, cancellationToken);
                if (account == null)
                {
                    return Results.Unauthorized();
                }
                if (!CompanyId.TryParse(companyId, out var parsedCompanyId) ||
                    await companyService.LoadPublicCompanyProfileAsync(
                        parsedCompanyId,
                        cancellationToken) == null)
                {
                    return Results.NotFound();
                }

                try
                {
                    var result = await memberships.RequestAsync(
                        parsedCompanyId,
                        account.ProfileId,
                        body.RequestNote,
                        cancellationToken);
                    return Results.Ok(ToResponse(result.Membership!));
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new MembershipErrorResponse(
                        "invalid_membership_request",
                        exception.Message));
                }
            });

        companies.MapGet(
            "/{companyId}/membership-requests",
            async (
                string companyId,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                ProfileHostedTradeCompanyService companyService,
                SqliteMembershipStore memberships,
                CancellationToken cancellationToken) =>
            {
                var authorization = await AuthorizeCompanyAdministratorAsync(
                    companyId,
                    request,
                    options,
                    accessResolver,
                    companyService,
                    cancellationToken);
                if (authorization.Error != null)
                {
                    return authorization.Error;
                }

                var pending = await memberships.LoadPendingAsync(
                    authorization.CompanyId,
                    cancellationToken);
                return Results.Ok(pending.Select(ToResponse).ToArray());
            });

        MapTransition(companies, "approve", static (store, companyId, accountId, actorId, ct) =>
            store.ApproveAsync(companyId, accountId, actorId, ct));
        MapTransition(companies, "deny", static (store, companyId, accountId, actorId, ct) =>
            store.DenyAsync(companyId, accountId, actorId, ct));
        MapTransition(companies, "revoke", static (store, companyId, accountId, actorId, ct) =>
            store.RevokeAsync(companyId, accountId, actorId, ct));

        app.MapGet(
            "/trade/v1/memberships",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                SqliteMembershipStore memberships,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }
                var account = await accessResolver.ResolveAccountAsync(request, cancellationToken);
                if (account == null)
                {
                    return Results.Unauthorized();
                }

                var current = await memberships.LoadCurrentForAccountAsync(
                    account.ProfileId,
                    cancellationToken);
                return Results.Ok(current.Select(ToResponse).ToArray());
            });
    }

    private static void MapTransition(
        RouteGroupBuilder companies,
        string action,
        Func<SqliteMembershipStore, CompanyId, Guid, Guid, CancellationToken,
            Task<MembershipMutationResult>> transition)
    {
        companies.MapPost(
            $"/{{companyId}}/memberships/{{accountProfileId:guid}}/{action}",
            async (
                string companyId,
                Guid accountProfileId,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                ProfileHostedTradeCompanyService companyService,
                SqliteMembershipStore memberships,
                CancellationToken cancellationToken) =>
            {
                var authorization = await AuthorizeCompanyAdministratorAsync(
                    companyId,
                    request,
                    options,
                    accessResolver,
                    companyService,
                    cancellationToken);
                if (authorization.Error != null)
                {
                    return authorization.Error;
                }

                var result = await transition(
                    memberships,
                    authorization.CompanyId,
                    accountProfileId,
                    authorization.Account!.ProfileId,
                    cancellationToken);
                return result.Status switch
                {
                    MembershipMutationStatus.Applied => Results.Ok(ToResponse(result.Membership!)),
                    MembershipMutationStatus.NotFound => Results.NotFound(),
                    MembershipMutationStatus.LastOwner => Results.Conflict(
                        new MembershipErrorResponse(
                            "last_owner",
                            "The only active owner cannot be revoked.")),
                    _ => Results.Conflict(new MembershipErrorResponse(
                        "invalid_membership_state",
                        "The membership is not in the required state."))
                };
            });
    }

    private static async Task<CompanyAuthorizationResult> AuthorizeCompanyAdministratorAsync(
        string rawCompanyId,
        HttpRequest request,
        ProfileHostOptions options,
        MembershipAccessResolver accessResolver,
        ProfileHostedTradeCompanyService companyService,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return new(default, null, Results.NotFound());
        }
        var account = await accessResolver.ResolveAccountAsync(request, cancellationToken);
        if (account == null)
        {
            return new(default, null, Results.Unauthorized());
        }
        if (!CompanyId.TryParse(rawCompanyId, out var companyId) ||
            await companyService.LoadPublicCompanyProfileAsync(companyId, cancellationToken) == null)
        {
            return new(default, account, Results.NotFound());
        }

        var access = await accessResolver.ResolveCompanyAccessAsync(
            account,
            companyId,
            cancellationToken);
        return access is { Role: TradeCompanyRole.Owner or TradeCompanyRole.Operator }
            ? new CompanyAuthorizationResult(companyId, account, null)
            : new CompanyAuthorizationResult(
                companyId,
                account,
                Results.StatusCode(StatusCodes.Status403Forbidden));
    }

    private static MembershipResponse ToResponse(CompanyMembership membership) =>
        new(
            membership.CompanyId.ToString(),
            membership.AccountProfileId,
            membership.Role.ToString().ToLowerInvariant(),
            membership.State.ToString().ToLowerInvariant(),
            membership.RequestedAtUtc,
            membership.DecidedAtUtc,
            membership.DecidedByProfileId,
            membership.RequestNote);

    private sealed record CompanyAuthorizationResult(
        CompanyId CompanyId,
        MembershipAccount? Account,
        IResult? Error);
}
