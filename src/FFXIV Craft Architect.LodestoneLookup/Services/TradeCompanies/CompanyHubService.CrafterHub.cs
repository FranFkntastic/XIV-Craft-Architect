using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record CrafterHubCompanyResponse(
    string CompanyId,
    string Slug,
    string DisplayName,
    string Accent,
    string? Role,
    bool IsFreelancer,
    bool MembershipRequested,
    bool CanRequestMembership,
    Guid? InvitationId);

public sealed record CrafterHubWorkResponse(
    string CompanyId,
    string CompanyName,
    string Status,
    bool IsFreelance,
    string? WorkspaceUrl,
    CompanyHubCommissionResponse Commission);

public sealed record CrafterHubOpenCommissionResponse(
    string CompanyId,
    string CompanyName,
    string? MaterialsLabel,
    string? PublicUrl,
    CompanyHubCommissionResponse Commission);

public sealed record CrafterHubUpdateResponse(
    string CompanyId,
    string CompanyName,
    string Kind,
    string? CommissionId,
    string? CommissionTitle,
    string Text,
    DateTime CreatedAtUtc,
    bool IsNew);

public sealed record CrafterHubResponse(
    bool CanOperate,
    IReadOnlyList<CrafterHubCompanyResponse> Companies,
    IReadOnlyList<CrafterHubWorkResponse> MyWork,
    IReadOnlyList<CrafterHubOpenCommissionResponse> OpenCommissions,
    IReadOnlyList<CrafterHubUpdateResponse> Updates);

public sealed record CrafterHubAccessResponse(bool CanOperate);

public sealed record CompanyCommissionDirectoryEntry(
    CompanyId CompanyId,
    string Name,
    IReadOnlyList<TradeOrder> Orders);

public static class CrafterHubWorkStatus
{
    public const string Claimed = "claimed";
    public const string InProgress = "in_progress";
    public const string AwaitingReview = "awaiting_review";
    public const string NeedsAttention = "needs_attention";
    public const string AwaitingPayment = "awaiting_payment";
}

public sealed partial class CompanyHubService
{
    private const int MaximumCompanyPostsPerCompany = 3;
    private const int MaximumHubUpdates = 12;

    /// <summary>
    /// Every hosted company with its canonical commission orders. Used to rebuild the
    /// account→claims lookup from canonical state.
    /// </summary>
    public async Task<IReadOnlyList<CompanyCommissionDirectoryEntry>> LoadCommissionDirectoryAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = new List<CompanyCommissionDirectoryEntry>();
        foreach (var company in await LoadCompaniesAsync(cancellationToken))
        {
            entries.Add(new CompanyCommissionDirectoryEntry(
                new CompanyId(company.Profile.Id),
                company.Profile.Name,
                await LoadOrdersAsync(company, cancellationToken)));
        }
        return entries;
    }

    /// <summary>
    /// Whether the account holds an operating role (owner or operator) in any company.
    /// </summary>
    public async Task<bool> CanOperateAnyCompanyAsync(
        MembershipAccount account,
        CancellationToken cancellationToken = default)
    {
        var companies = await LoadCompaniesAsync(cancellationToken);
        if (companies.Any(company => company.HostProfileId == account.ProfileId))
        {
            return true;
        }

        var known = companies.Select(company => company.Profile.Id).ToHashSet();
        foreach (var membership in await memberships.LoadCurrentForAccountAsync(
                     account.ProfileId,
                     cancellationToken))
        {
            if (membership.State != MembershipState.Active ||
                !known.Contains(membership.CompanyId.Value))
            {
                continue;
            }
            var access = await accessResolver.ResolveCompanyAccessAsync(
                account,
                membership.CompanyId,
                cancellationToken);
            if (access is { Role: TradeCompanyRole.Owner or TradeCompanyRole.Operator })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The account-scoped Crafter hub: claims the account owns in every company, open
    /// commissions it may take, its company relationships, and updates for it. Claims are
    /// located through the account→claims lookup and the account's relationships, but
    /// each is listed only when the canonical claim is owned by this account (K1/K2).
    /// Freelancers see a company's open commissions only when bound to a crafter there
    /// and the company allows it (K3).
    /// </summary>
    public async Task<CrafterHubResponse> LoadCrafterHubAsync(
        MembershipAccount account,
        string? companyFilter,
        CancellationToken cancellationToken = default)
    {
        var companies = await LoadCompaniesAsync(cancellationToken);
        var slugs = BuildSlugs(companies);
        var current = (await memberships.LoadCurrentForAccountAsync(account.ProfileId, cancellationToken))
            .GroupBy(item => item.CompanyId.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var boundCompanies = (await memberships.LoadCrafterBindingsForAccountAsync(
                account.ProfileId,
                cancellationToken))
            .Select(binding => binding.CompanyId.Value)
            .ToHashSet();
        var projectedCompanies = (await memberships.LoadAccountCommissionClaimsAsync(
                account.ProfileId,
                cancellationToken))
            .Select(claim => claim.CompanyId.Value)
            .ToHashSet();
        var identity = await identities.LoadByProfileAsync(account.ProfileId, cancellationToken);
        var invitations = identity == null
            ? new Dictionary<Guid, CompanyMembershipInvitation>()
            : (await memberships.LoadDiscordRecipientInvitationsAsync(
                    identity.DiscordUserId,
                    cancellationToken))
                .GroupBy(item => item.CompanyId.Value)
                .ToDictionary(group => group.Key, group => group.First());
        // A legacy claim committed through this account's Discord user before the account
        // existed has no lookup row and no binding yet. Its company stays in scope so
        // committed Discord discovery in ResolveScopeAsync can bind and find it.
        var contactCompanies = identity == null
            ? new HashSet<Guid>()
            : (await notifications.LoadClaimContactCompaniesAsync(
                    identity.DiscordUserId,
                    cancellationToken))
                .Select(item => item.Value)
                .ToHashSet();

        var canOperate = false;
        var companyRows = new List<(int Rank, CrafterHubCompanyResponse Row)>();
        var work = new List<(DateTime ClaimedAtUtc, CrafterHubWorkResponse Item)>();
        var open = new List<(DateTime CreatedAtUtc, CrafterHubOpenCommissionResponse Item)>();
        var updates = new List<CrafterHubUpdateResponse>();
        foreach (var company in companies)
        {
            var companyGuid = company.Profile.Id;
            var isHost = company.HostProfileId == account.ProfileId;
            current.TryGetValue(companyGuid, out var membership);
            var bound = boundCompanies.Contains(companyGuid);
            var projected = projectedCompanies.Contains(companyGuid) ||
                contactCompanies.Contains(companyGuid);
            invitations.TryGetValue(companyGuid, out var invitation);
            if (!isHost && membership == null && !bound && !projected && invitation == null)
            {
                continue;
            }

            var companyId = new CompanyId(companyGuid);
            var name = ClampText(company.Profile.Name, MaximumDisplayNameLength, "Trade company");
            var companyKey = companyGuid.ToString("D");
            string? role = null;
            if (isHost || membership is { State: MembershipState.Active })
            {
                var access = await accessResolver.ResolveCompanyAccessAsync(
                    account,
                    companyId,
                    cancellationToken);
                role = access is { Role: TradeCompanyRole.Owner or TradeCompanyRole.Operator }
                    ? access.Role.ToString().ToLowerInvariant()
                    : isHost
                        ? "owner"
                        : "member";
            }
            canOperate |= role is "owner" or "operator";
            var isMember = role != null;

            var orders = isMember || bound || projected
                ? await LoadOrdersAsync(company, cancellationToken)
                : [];
            var ownsAny = false;
            if (orders.Count > 0)
            {
                var scope = await crafterAccounts.ResolveScopeAsync(
                    companyId,
                    account.ProfileId,
                    cancellationToken);
                foreach (var order in orders)
                {
                    var claim = order.CompanyCommission?.ActiveClaim;
                    if (!scope.Owns(claim))
                    {
                        continue;
                    }
                    ownsAny = true;
                    var projection = await ProjectAssignmentAsync(
                        order,
                        companyId,
                        account.ProfileId,
                        company.Profile.Name,
                        cancellationToken);
                    var status = ResolveWorkStatus(order, projection);
                    if (status == null)
                    {
                        continue;
                    }
                    work.Add((claim!.ClaimedAtUtc, new CrafterHubWorkResponse(
                        companyKey,
                        name,
                        status,
                        !isMember,
                        BuildPublicUrl(projection.PublicBriefId),
                        projection)));
                    if (projection.UnreadCommissionerUpdate is { } unread)
                    {
                        updates.Add(new CrafterHubUpdateResponse(
                            companyKey,
                            name,
                            "commissioner",
                            projection.CommissionId,
                            projection.Title,
                            unread.Text,
                            unread.CreatedAtUtc,
                            true));
                    }
                }
            }

            // A denied or revoked relationship is closed: no open commissions and no
            // Freelancer label, though the account's own active claims stay in My work.
            var requested = !isMember && membership is { State: MembershipState.Pending };
            var closed = !isMember && !requested && await IsMembershipClosedAsync(
                companyId,
                account.ProfileId,
                cancellationToken);
            var isFreelancer = !isMember && !closed && (bound || ownsAny);
            if (isMember || (isFreelancer && bound && company.Profile.FreelancerOpenCommissions))
            {
                foreach (var order in orders.Where(IsOpen))
                {
                    var projection = ProjectCommission(order);
                    open.Add((order.CreatedAtUtc, new CrafterHubOpenCommissionResponse(
                        companyKey,
                        name,
                        MaterialsLabel(order.CompanyCommission!.CurrentTerms),
                        BuildPublicUrl(projection.PublicBriefId),
                        projection)));
                }
            }
            if (isMember)
            {
                updates.AddRange(ProjectUpdates(company.Profile.Updates)
                    .OrderByDescending(item => item.PublishedAtUtc)
                    .Take(MaximumCompanyPostsPerCompany)
                    .Select(item => new CrafterHubUpdateResponse(
                        companyKey,
                        name,
                        "company",
                        null,
                        null,
                        item.Title,
                        item.PublishedAtUtc,
                        false)));
            }

            var pendingInvitation = isMember ? null : invitation;
            if (!isMember && !isFreelancer && !requested && pendingInvitation == null &&
                !(closed && ownsAny))
            {
                continue;
            }
            var rank = role switch
            {
                "owner" or "operator" => 0,
                "member" => 1,
                _ => isFreelancer ? 2 : 3
            };
            companyRows.Add((rank, new CrafterHubCompanyResponse(
                companyKey,
                slugs[companyGuid],
                name,
                ProjectTheme(company.Profile.Landing).Accent,
                role,
                isFreelancer,
                requested,
                !isMember && !requested && !closed && pendingInvitation == null,
                pendingInvitation?.InvitationId)));
        }

        bool Included(string companyId) =>
            string.IsNullOrWhiteSpace(companyFilter) ||
            string.Equals(companyId, companyFilter, StringComparison.OrdinalIgnoreCase);

        return new CrafterHubResponse(
            canOperate,
            companyRows
                .OrderBy(item => item.Rank)
                .ThenBy(item => item.Row.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Row)
                .ToArray(),
            work
                .Where(item => Included(item.Item.CompanyId))
                .OrderByDescending(item => item.Item.Commission.UnreadCommissionerUpdate != null)
                .ThenByDescending(item => item.ClaimedAtUtc)
                .Select(item => item.Item)
                .ToArray(),
            open
                .Where(item => Included(item.Item.CompanyId))
                .OrderByDescending(item => item.CreatedAtUtc)
                .Select(item => item.Item)
                .ToArray(),
            updates
                .Where(item => Included(item.CompanyId))
                .OrderByDescending(item => item.CreatedAtUtc)
                .Take(MaximumHubUpdates)
                .ToArray());
    }

    // K6: finished work stays as "Awaiting payment" until the company settles it, then
    // leaves the list. Cancelled commissions never appear.
    private static string? ResolveWorkStatus(TradeOrder order, CompanyHubCommissionResponse projection)
    {
        if (order.Status == TradeOrderStatus.Canceled)
        {
            return null;
        }
        var finished = order.Status == TradeOrderStatus.Completed ||
            (projection.Outputs.Count > 0 &&
             projection.Outputs.All(output => output.AcceptedQuantity >= output.Quantity));
        if (finished)
        {
            return order.CompanyCommission!.SettlementState == CompanyCommissionSettlementState.Satisfied
                ? null
                : CrafterHubWorkStatus.AwaitingPayment;
        }
        return order.Status switch
        {
            TradeOrderStatus.AwaitingDelivery => CrafterHubWorkStatus.AwaitingReview,
            TradeOrderStatus.ResolutionRequired => CrafterHubWorkStatus.NeedsAttention,
            TradeOrderStatus.InProgress => CrafterHubWorkStatus.InProgress,
            _ => projection.Outputs.Any(output => output.CompletedQuantity > 0)
                ? CrafterHubWorkStatus.InProgress
                : CrafterHubWorkStatus.Claimed
        };
    }

    private static string? MaterialsLabel(CompanyCommissionTermsVersion terms)
    {
        if (terms.Materials.Count == 0)
        {
            return null;
        }
        var provided = terms.Materials.Count(item =>
            item.Responsibility == CommissionMaterialResponsibility.Provided);
        return provided == terms.Materials.Count
            ? "Company provides materials"
            : provided == 0
                ? "Crafter provides materials"
                : "Company provides some materials";
    }

    private string? BuildPublicUrl(string? publicBriefId) =>
        !string.IsNullOrWhiteSpace(publicBriefId) &&
        briefOptions.TryBuildPublicUrl(publicBriefId, out var url)
            ? url
            : null;

    private async Task<bool> IsMembershipClosedAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken) =>
        await memberships.LoadForAccountAsync(companyId, accountProfileId, cancellationToken) is
        { State: MembershipState.Denied or MembershipState.Revoked };

    private static IReadOnlyDictionary<Guid, string> BuildSlugs(IReadOnlyList<HostedCompany> companies) =>
        companies
            .GroupBy(item => Slugify(item.Profile.Name), StringComparer.Ordinal)
            .SelectMany(group => group
                .OrderBy(item => item.Profile.CreatedAtUtc)
                .ThenBy(item => item.Profile.Id)
                .Select((item, index) => (item.Profile.Id, Slug: BuildSlug(item.Profile.Name, index + 1))))
            .ToDictionary(item => item.Id, item => item.Slug);
}

public static class CrafterHubEndpoints
{
    public static void MapCrafterHubEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/trade/v1/crafter-hub",
            async (
                string? company,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                CompanyHubService hubs,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }
                var account = await accessResolver.ResolveAccountAsync(request, cancellationToken);
                return account == null
                    ? Results.Unauthorized()
                    : Results.Ok(await hubs.LoadCrafterHubAsync(account, company, cancellationToken));
            });

        app.MapGet(
            "/trade/v1/crafter-hub/access",
            async (
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                CompanyHubService hubs,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }
                var account = await accessResolver.ResolveAccountAsync(request, cancellationToken);
                return account == null
                    ? Results.Unauthorized()
                    : Results.Ok(new CrafterHubAccessResponse(
                        await hubs.CanOperateAnyCompanyAsync(account, cancellationToken)));
            });

        // Invitations delivered to the account's Discord identity can be answered from the
        // hub without the invite link; the linked Discord identity proves the recipient.
        app.MapPost(
            "/trade/v1/crafter-hub/invitations/{invitationId:guid}/accept",
            async (
                Guid invitationId,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                SqliteDiscordIdentityStore identities,
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
                var identity = await identities.LoadByProfileAsync(account.ProfileId, cancellationToken);
                if (identity == null)
                {
                    return Results.Json(
                        new MembershipErrorResponse(
                            "account_sign_in_required",
                            "Sign in with Discord before accepting this invitation."),
                        statusCode: StatusCodes.Status403Forbidden);
                }
                var result = await memberships.ConsumeInvitationForRecipientAsync(
                    invitationId,
                    account.ProfileId,
                    identity.DiscordUserId,
                    cancellationToken);
                return result.Status switch
                {
                    MembershipInvitationConsumptionStatus.Applied or
                        MembershipInvitationConsumptionStatus.Replayed => Results.NoContent(),
                    MembershipInvitationConsumptionStatus.MembershipDecisionConflict => Results.Json(
                        new MembershipErrorResponse(
                            "invitation_predates_membership_decision",
                            "Your membership was denied or revoked after this invitation was issued. Ask a company administrator for a new invitation."),
                        statusCode: StatusCodes.Status403Forbidden),
                    MembershipInvitationConsumptionStatus.BindingConflict => Results.Conflict(
                        new MembershipErrorResponse(
                            "invitation_history_conflict",
                            "This crafter history was connected to another account before the invitation was accepted.")),
                    _ => Results.Json(
                        new MembershipErrorResponse(
                            "invitation_unavailable",
                            "This invitation is expired, revoked, or has already been used."),
                        statusCode: StatusCodes.Status410Gone)
                };
            });

        app.MapPost(
            "/trade/v1/crafter-hub/invitations/{invitationId:guid}/decline",
            async (
                Guid invitationId,
                HttpRequest request,
                ProfileHostOptions options,
                MembershipAccessResolver accessResolver,
                SqliteDiscordIdentityStore identities,
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
                var identity = await identities.LoadByProfileAsync(account.ProfileId, cancellationToken);
                if (identity == null)
                {
                    return Results.Unauthorized();
                }
                return await memberships.DeclineInvitationForRecipientAsync(
                        invitationId,
                        account.ProfileId,
                        identity.DiscordUserId,
                        cancellationToken)
                    ? Results.NoContent()
                    : Results.Json(
                        new MembershipErrorResponse(
                            "invitation_unavailable",
                            "This invitation is expired, revoked, or has already been used."),
                        statusCode: StatusCodes.Status410Gone);
            });
    }
}
