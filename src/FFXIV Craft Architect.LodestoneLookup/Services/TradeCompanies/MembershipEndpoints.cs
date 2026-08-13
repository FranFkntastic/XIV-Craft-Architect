using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record MembershipRequestBody(string? RequestNote);
public sealed record MembershipTransitionBody(string? Reason);
public sealed record LegacyCrafterBindingBody(Guid AccountProfileId);

public sealed record MembershipResponse(
    string CompanyId,
    Guid AccountProfileId,
    string Role,
    string State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    Guid? DecidedByProfileId,
    string? RequestNote,
    bool HasMembership);

public sealed record MembershipErrorResponse(string Error, string Message);
public sealed record MembershipNotificationPreferenceBody(
    bool ActionRequired,
    bool CommissionerMessages,
    bool ProgressAndStatus);
public sealed record MembershipNotificationPreferenceResponse(
    string CompanyId,
    bool ActionRequired,
    bool CommissionerMessages,
    bool ProgressAndStatus);
public sealed record MembershipNotificationTestReadinessResponse(
    bool Ready,
    string? DestinationDisplayName,
    string? Reason);
public sealed record MembershipNotificationTestResponse(
    Guid TestId,
    string State,
    string DestinationDisplayName,
    int AttemptCount,
    string? MessageId,
    string? Error);
public sealed record CompanyMemberResponse(
    Guid AccountProfileId,
    string DisplayName,
    string Role,
    string State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    bool DiscordLinked);
public sealed record LegacyCrafterCandidateResponse(
    Guid LegacyCrafterId,
    string DisplayName,
    string? WorldName,
    string? LodestoneCharacterId);
public sealed record LegacyCrafterBindingResponse(
    Guid LegacyCrafterId,
    Guid AccountProfileId,
    string Evidence,
    DateTimeOffset CreatedAtUtc);
public sealed record LegacyCrafterMigrationResponse(
    IReadOnlyList<LegacyCrafterCandidateResponse> LegacyCrafters,
    IReadOnlyList<LegacyCrafterBindingResponse> Bindings);

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
                LegacyCrafterAccountResolver crafterAccounts,
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
                    await crafterAccounts.DiscoverCommittedDiscordBindingsAsync(
                        parsedCompanyId,
                        account.ProfileId,
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
                return Results.Ok(pending.Select(item => ToResponse(item)).ToArray());
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

        companies.MapGet(
            "/{companyId}/legacy-crafter-migration",
            async (
                string companyId,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                ProfileHostedTradeCompanyService companyService,
                SqliteMembershipStore memberships,
                LegacyCrafterAccountResolver crafterAccounts,
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

                var companyMemberships = await memberships.LoadForCompanyAsync(
                    authorization.CompanyId,
                    cancellationToken);
                foreach (var membership in companyMemberships.Where(item =>
                             item.State is MembershipState.Pending or MembershipState.Active))
                {
                    await crafterAccounts.DiscoverCommittedDiscordBindingsAsync(
                        authorization.CompanyId,
                        membership.AccountProfileId,
                        cancellationToken);
                }

                var candidates = await crafterAccounts.LoadCandidatesAsync(
                    authorization.CompanyId,
                    cancellationToken);
                var bindings = await memberships.LoadCrafterBindingsAsync(
                    authorization.CompanyId,
                    cancellationToken);
                return Results.Ok(new LegacyCrafterMigrationResponse(
                    candidates.Select(item => new LegacyCrafterCandidateResponse(
                        item.LegacyCrafterId,
                        item.DisplayName,
                        item.WorldName,
                        item.LodestoneCharacterId)).ToArray(),
                    bindings.Select(item => new LegacyCrafterBindingResponse(
                        item.LegacyCrafterId,
                        item.AccountProfileId,
                        item.Evidence.ToString(),
                        item.CreatedAtUtc)).ToArray()));
            });

        companies.MapPut(
            "/{companyId}/legacy-crafter-bindings/{legacyCrafterId:guid}",
            async (
                string companyId,
                Guid legacyCrafterId,
                LegacyCrafterBindingBody body,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                ProfileHostedTradeCompanyService companyService,
                SqliteMembershipStore memberships,
                LegacyCrafterAccountResolver crafterAccounts,
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
                var targetMembership = body.AccountProfileId == Guid.Empty
                    ? null
                    : await memberships.LoadAsync(
                        authorization.CompanyId,
                        body.AccountProfileId,
                        cancellationToken);
                if (targetMembership is not
                        { State: MembershipState.Pending or MembershipState.Active } ||
                    !await crafterAccounts.IsCompanyCrafterAsync(
                        authorization.CompanyId,
                        legacyCrafterId,
                        cancellationToken))
                {
                    return Results.BadRequest(new MembershipErrorResponse(
                        "invalid_crafter_binding",
                        "Choose a company member and a legacy crafter from this company."));
                }

                var result = await memberships.BindCrafterAsync(
                    authorization.CompanyId,
                    legacyCrafterId,
                    body.AccountProfileId,
                    CrafterAccountBindingEvidence.OperatorConfirmed,
                    authorization.Account!.ProfileId,
                    cancellationToken);
                return result.Status switch
                {
                    CrafterAccountBindingMutationStatus.Applied or
                        CrafterAccountBindingMutationStatus.Replayed => Results.Ok(
                            new LegacyCrafterBindingResponse(
                                result.Binding!.LegacyCrafterId,
                                result.Binding.AccountProfileId,
                                result.Binding.Evidence.ToString(),
                                result.Binding.CreatedAtUtc)),
                    _ => Results.Conflict(new MembershipErrorResponse(
                        "crafter_already_connected",
                        "That legacy crafter history is already connected to another account."))
                };
            });

        companies.MapDelete(
            "/{companyId}/legacy-crafter-bindings/{legacyCrafterId:guid}",
            async (
                string companyId,
                Guid legacyCrafterId,
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
                var result = await memberships.UnbindCrafterAsync(
                    authorization.CompanyId,
                    legacyCrafterId,
                    authorization.Account!.ProfileId,
                    cancellationToken);
                return result.Status == CrafterAccountBindingMutationStatus.NotFound
                    ? Results.NotFound()
                    : Results.NoContent();
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
                SqliteDiscordIdentityStore identities,
                SqliteDiscordNotificationStore notifications,
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
                var response = new Dictionary<CompanyId, MembershipResponse>();
                foreach (var membership in current)
                {
                    var access = await accessResolver.ResolveCompanyAccessAsync(
                        account,
                        membership.CompanyId,
                        cancellationToken);
                    var effectiveRole = access?.Role switch
                    {
                        TradeCompanyRole.Owner => MembershipRole.Owner,
                        TradeCompanyRole.Operator => MembershipRole.Operator,
                        _ => membership.Role
                    };
                    response[membership.CompanyId] = ToResponse(membership, effectiveRole);
                }

                var identity = await identities.LoadByProfileAsync(
                    account.ProfileId,
                    cancellationToken);
                if (identity != null)
                {
                    var routes = await notifications.LoadRoutesForCommissionerAsync(
                        identity.DiscordUserId,
                        cancellationToken);
                    foreach (var route in routes)
                    {
                        var access = await accessResolver.ResolveCompanyAccessAsync(
                            account,
                            route.CompanyId,
                            cancellationToken);
                        if (access is not
                            { Role: TradeCompanyRole.Owner or TradeCompanyRole.Operator })
                        {
                            continue;
                        }

                        if (response.TryGetValue(route.CompanyId, out var existing) &&
                            existing is { HasMembership: true, State: "active" })
                        {
                            response[route.CompanyId] = existing with
                            {
                                Role = access.Role.ToString().ToLowerInvariant()
                            };
                            continue;
                        }

                        response[route.CompanyId] = new MembershipResponse(
                            route.CompanyId.ToString(),
                            account.ProfileId,
                            access.Role.ToString().ToLowerInvariant(),
                            "active",
                            route.UpdatedAt,
                            route.UpdatedAt,
                            null,
                            null,
                            false);
                    }
                }

                return Results.Ok(response.Values
                    .OrderBy(item => item.CompanyId, StringComparer.Ordinal)
                    .ToArray());
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
                return membership is not { State: MembershipState.Active }
                    ? Results.NotFound()
                    : Results.Ok(new MembershipNotificationPreferenceResponse(
                        parsed.ToString(),
                        membership.NotifyActionRequired,
                        membership.NotifyCommissionerMessages,
                        membership.NotifyProgressAndStatus));
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
                var membership = await memberships.SetNotificationPreferencesAsync(
                    parsed,
                    account.ProfileId,
                    new MemberNotificationPreferences(
                        body.ActionRequired,
                        body.CommissionerMessages,
                        body.ProgressAndStatus),
                    cancellationToken);
                return membership == null
                    ? Results.NotFound()
                    : Results.Ok(new MembershipNotificationPreferenceResponse(
                        parsed.ToString(),
                        membership.NotifyActionRequired,
                        membership.NotifyCommissionerMessages,
                        membership.NotifyProgressAndStatus));
            });

        companies.MapGet(
            "/{companyId}/membership-notifications/test-readiness",
            async (
                string companyId,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                SqliteMembershipStore memberships,
                SqliteDiscordIdentityStore identities,
                SqliteDiscordNotificationStore notifications,
                DiscordCommissionOptions discordOptions,
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
                if (membership is not { State: MembershipState.Active })
                {
                    return Results.NotFound();
                }
                var identity = await identities.LoadByProfileAsync(
                    account.ProfileId,
                    cancellationToken);
                if (identity == null)
                {
                    return Results.Ok(new MembershipNotificationTestReadinessResponse(
                        false,
                        null,
                        "Link Discord in Profile before testing private delivery."));
                }
                var route = await notifications.LoadRouteAsync(parsed, cancellationToken);
                return route == null || !discordOptions.CanPublishDirectly
                    ? Results.Ok(new MembershipNotificationTestReadinessResponse(
                        false,
                        identity.DisplayNameSnapshot,
                        route == null
                            ? "This company does not have a Discord notification route yet."
                            : "Private Discord delivery is not available on this deployment."))
                    : Results.Ok(new MembershipNotificationTestReadinessResponse(
                        true,
                        identity.DisplayNameSnapshot,
                        null));
            });

        companies.MapPost(
            "/{companyId}/membership-notifications/test",
            async (
                string companyId,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                SqliteMembershipStore memberships,
                SqliteDiscordIdentityStore identities,
                SqliteDiscordNotificationStore notifications,
                DiscordCommissionOptions discordOptions,
                ProfileHostedTradeCompanyService companyService,
                TimeProvider timeProvider,
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
                var identity = await identities.LoadByProfileAsync(
                    account.ProfileId,
                    cancellationToken);
                var route = await notifications.LoadRouteAsync(parsed, cancellationToken);
                var company = await companyService.LoadPublicCompanyProfileAsync(
                    parsed,
                    cancellationToken);
                if (membership is not { State: MembershipState.Active } || company == null)
                {
                    return Results.NotFound();
                }
                if (identity == null || route == null || !discordOptions.CanPublishDirectly)
                {
                    return Results.Conflict(new MembershipErrorResponse(
                        "member_notification_test_unavailable",
                        identity == null
                            ? "Link Discord in Profile before testing private delivery."
                            : route == null
                                ? "This company does not have a Discord notification route yet."
                                : "Private Discord delivery is not available on this deployment."));
                }

                var testId = Guid.NewGuid();
                var payload = JsonSerializer.Serialize(new
                {
                    embeds = new[]
                    {
                        new
                        {
                            title = "Craft Architect notification test",
                            description = $"Private commission updates from {company.Name} can reach this Discord account.",
                            color = 0x4CA073,
                            footer = new { text = "No commission was changed by this test." }
                        }
                    },
                    allowed_mentions = new { parse = Array.Empty<string>() }
                });
                var result = await notifications.EnqueueMemberAsync(
                    parsed,
                    testId,
                    testId,
                    0,
                    DiscordNotificationAttentionClass.Routine,
                    route.Revision,
                    payload,
                    [identity.DiscordUserId],
                    timeProvider.GetUtcNow(),
                    cancellationToken,
                    isMemberTest: true);
                if (!result.Success || result.WorkItemIds.Count != 1)
                {
                    return Results.Conflict(new MembershipErrorResponse(
                        "member_notification_test_not_queued",
                        result.Error ?? "The private test notification could not be queued."));
                }
                return Results.Accepted(value: new MembershipNotificationTestResponse(
                    result.WorkItemIds[0],
                    "pending",
                    identity.DisplayNameSnapshot,
                    0,
                    null,
                    null));
            });

        companies.MapGet(
            "/{companyId}/membership-notifications/test/{testId:guid}",
            async (
                string companyId,
                Guid testId,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                SqliteMembershipStore memberships,
                SqliteDiscordIdentityStore identities,
                SqliteDiscordNotificationStore notifications,
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
                var identity = await identities.LoadByProfileAsync(
                    account.ProfileId,
                    cancellationToken);
                if (membership is not { State: MembershipState.Active } || identity == null)
                {
                    return Results.NotFound();
                }
                var delivery = await notifications.LoadMemberTestDeliveryAsync(
                    parsed,
                    testId,
                    identity.DiscordUserId,
                    cancellationToken);
                return delivery == null
                    ? Results.NotFound()
                    : Results.Ok(new MembershipNotificationTestResponse(
                        delivery.WorkItemId,
                        delivery.State.ToString().ToLowerInvariant(),
                        identity.DisplayNameSnapshot,
                        delivery.AttemptCount,
                        delivery.MessageId,
                        delivery.Error));
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

    private static MembershipResponse ToResponse(
        CompanyMembership membership,
        MembershipRole? effectiveRole = null) =>
        new(
            membership.CompanyId.ToString(),
            membership.AccountProfileId,
            (effectiveRole ?? membership.Role).ToString().ToLowerInvariant(),
            membership.State.ToString().ToLowerInvariant(),
            membership.RequestedAtUtc,
            membership.DecidedAtUtc,
            membership.DecidedByProfileId,
            membership.RequestNote,
            true);

    private sealed record CompanyAuthorizationResult(
        CompanyId CompanyId,
        MembershipAccount? Account,
        TradeCompanyRole? Role,
        IResult? Error);
}
