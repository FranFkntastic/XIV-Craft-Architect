using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record MembershipRequestBody(string? RequestNote);
public sealed record MembershipTransitionBody(string? Reason);

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
public sealed record MembershipNotificationPreferenceBody(bool OptedOut);
public sealed record MembershipNotificationPreferenceResponse(string CompanyId, bool OptedOut);
public sealed record CompanyMemberResponse(
    Guid AccountProfileId,
    string DisplayName,
    string Role,
    string State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    bool DiscordLinked);

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
                SqliteDiscordIdentityStore identities,
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
                if (await identities.LoadByProfileAsync(account.ProfileId, cancellationToken) == null)
                {
                    return Results.Json(
                        new MembershipErrorResponse(
                            "account_sign_in_required",
                            "Sign in with Discord before requesting membership."),
                        statusCode: StatusCodes.Status403Forbidden);
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

        companies.MapGet(
            "/{companyId}/memberships",
            async (
                string companyId,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                ProfileHostedTradeCompanyService companyService,
                SqliteMembershipStore memberships,
                SqliteProfileHostStore profiles,
                SqliteDiscordIdentityStore identities,
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

                var current = await memberships.LoadForCompanyAsync(
                    authorization.CompanyId,
                    cancellationToken);
                var response = new List<CompanyMemberResponse>(current.Count);
                foreach (var membership in current)
                {
                    var profile = await profiles.LoadProfileAsync(
                        membership.AccountProfileId.ToString("D"),
                        cancellationToken);
                    if (profile != null)
                    {
                        response.Add(new CompanyMemberResponse(
                            membership.AccountProfileId,
                            profile.DisplayName,
                            membership.Role.ToString().ToLowerInvariant(),
                            membership.State.ToString().ToLowerInvariant(),
                            membership.RequestedAtUtc,
                            membership.DecidedAtUtc,
                            await identities.LoadByProfileAsync(
                                membership.AccountProfileId,
                                cancellationToken) != null));
                    }
                }
                return Results.Ok(response);
            });

        MapTransition(companies, "approve", static (store, companyId, accountId, actorId, _, ct) =>
            store.ApproveAsync(companyId, accountId, actorId, ct));
        MapTransition(companies, "deny", static (store, companyId, accountId, actorId, _, ct) =>
            store.DenyAsync(companyId, accountId, actorId, ct));
        MapTransition(companies, "revoke", static (store, companyId, accountId, actorId, reason, ct) =>
            store.RevokeAsync(companyId, accountId, actorId, reason, ct));

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

        companies.MapGet(
            "/{companyId}/membership-notifications",
            async (
                string companyId,
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
                if (account == null || !CompanyId.TryParse(companyId, out var parsed))
                {
                    return account == null ? Results.Unauthorized() : Results.NotFound();
                }
                var membership = await memberships.LoadForAccountAsync(
                    parsed,
                    account.ProfileId,
                    cancellationToken);
                return membership == null
                    ? Results.NotFound()
                    : Results.Ok(new MembershipNotificationPreferenceResponse(
                        parsed.ToString(),
                        membership.NotificationsOptedOut));
            });

        companies.MapPut(
            "/{companyId}/membership-notifications",
            async (
                string companyId,
                MembershipNotificationPreferenceBody body,
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
                if (!CompanyId.TryParse(companyId, out var parsed))
                {
                    return Results.NotFound();
                }
                var membership = await memberships.SetNotificationsOptedOutAsync(
                    parsed,
                    account.ProfileId,
                    body.OptedOut,
                    cancellationToken);
                return membership == null
                    ? Results.NotFound()
                    : Results.Ok(new MembershipNotificationPreferenceResponse(
                        parsed.ToString(),
                        membership.NotificationsOptedOut));
            });
    }

    private static void MapTransition(
        RouteGroupBuilder companies,
        string action,
        Func<SqliteMembershipStore, CompanyId, Guid, Guid, string?, CancellationToken,
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

                var target = await memberships.LoadAsync(
                    authorization.CompanyId,
                    accountProfileId,
                    cancellationToken);
                if (action == "revoke" &&
                    target?.Role == MembershipRole.Owner &&
                    authorization.Role != TradeCompanyRole.Owner)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                string? reason = null;
                if (action == "revoke" && request.ContentLength > 0)
                {
                    var body = await request.ReadFromJsonAsync<MembershipTransitionBody>(cancellationToken);
                    reason = body?.Reason;
                }

                MembershipMutationResult result;
                try
                {
                    result = await transition(
                        memberships,
                        authorization.CompanyId,
                        accountProfileId,
                        authorization.Account!.ProfileId,
                        reason,
                        cancellationToken);
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new MembershipErrorResponse("invalid_membership_transition", exception.Message));
                }
                return result.Status switch
                {
                    MembershipMutationStatus.Applied or MembershipMutationStatus.Replayed =>
                        Results.Ok(ToResponse(result.Membership!)),
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
            return new(default, null, null, Results.NotFound());
        }
        var account = await accessResolver.ResolveAccountAsync(request, cancellationToken);
        if (account == null)
        {
            return new(default, null, null, Results.Unauthorized());
        }
        if (!CompanyId.TryParse(rawCompanyId, out var companyId) ||
            await companyService.LoadPublicCompanyProfileAsync(companyId, cancellationToken) == null)
        {
            return new(default, account, null, Results.NotFound());
        }

        var access = await accessResolver.ResolveCompanyAccessAsync(
            account,
            companyId,
            cancellationToken);
        return access is { Role: TradeCompanyRole.Owner or TradeCompanyRole.Operator }
            ? new CompanyAuthorizationResult(companyId, account, access.Role, null)
            : new CompanyAuthorizationResult(
                companyId,
                account,
                null,
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
        TradeCompanyRole? Role,
        IResult? Error);
}
