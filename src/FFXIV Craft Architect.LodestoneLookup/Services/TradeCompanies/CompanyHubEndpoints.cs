using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record CompanyHubThemeResponse(
    string Accent,
    string BannerStyle,
    string Emblem,
    string? Tagline,
    string? About,
    bool ShowOpenCommissionCount);

public sealed record CompanyHubStandingResponse(string State, string? Role);

public sealed record CompanyHubTeaserResponse(
    string Kind,
    string CompanyId,
    string Slug,
    string DisplayName,
    CompanyHubThemeResponse Theme,
    CompanyHubStandingResponse Standing,
    int? OpenCommissionCount);

public sealed record CompanyHubOutputResponse(
    Guid LineId,
    int ItemId,
    string Name,
    int Quantity,
    int CompletedQuantity,
    int ReadyQuantity,
    int AcceptedQuantity);

public sealed record CompanyHubPaymentResponse(string Schedule, string Label, decimal Total);

public sealed record CompanyHubCommissionAttentionResponse(
    Guid EventId,
    long Revision,
    string Text,
    DateTime CreatedAtUtc);

public sealed record CompanyHubUpdateResponse(
    Guid Id,
    string Title,
    string Body,
    string AuthorDisplayName,
    DateTime PublishedAtUtc,
    DateTime? EditedAtUtc,
    bool IsPinned);

public sealed record CompanyHubCommissionResponse(
    string CommissionId,
    string Title,
    string Reference,
    int TermsVersion,
    string DeliveryInstructions,
    string? PublicBriefId,
    long ProjectionRevision,
    IReadOnlyList<CompanyHubOutputResponse> Outputs,
    CompanyHubPaymentResponse Payment,
    string SettlementState,
    string State,
    bool CanWork,
    bool CanReportProgress,
    bool CanDeclareReadiness,
    string? WorkBlockedReason,
    CompanyHubCommissionAttentionResponse? UnreadCommissionerUpdate = null);

public sealed record CompanyHubRosterMemberResponse(string DisplayName, string Role);

public sealed record CompanyHubResponse(
    string Kind,
    string CompanyId,
    string Slug,
    string DisplayName,
    CompanyHubThemeResponse Theme,
    CompanyHubStandingResponse Standing,
    long ProfileRevision,
    IReadOnlyList<CompanyHubUpdateResponse> Updates,
    IReadOnlyList<CompanyHubCommissionResponse> OpenCommissions,
    IReadOnlyList<CompanyHubCommissionResponse> Assignments,
    IReadOnlyList<CompanyHubRosterMemberResponse> Roster,
    int? PendingMembershipRequestCount);

public sealed record CompanyHubThemeUpdateRequest(
    long ExpectedProfileRevision,
    string Accent,
    string BannerStyle,
    string Emblem,
    string? Tagline,
    string? About,
    bool ShowOpenCommissionCount);

public sealed record CompanyHubPostUpdateRequest(
    long ExpectedProfileRevision,
    string Title,
    string Body,
    bool IsPinned);

public sealed record CompanyHubMutationResponse(long ProfileRevision);
public sealed record CompanyHubAttentionReadRequest(long OpenedRevision);
public sealed record CompanyHubAttentionReadResponse(long ReadRevision);

public enum CompanyHubMutationStatus
{
    Applied,
    NotFound,
    Unauthorized,
    Forbidden,
    Conflict,
    Invalid
}

public sealed record CompanyHubMutationResult(
    CompanyHubMutationStatus Status,
    long? ProfileRevision = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public static class CompanyHubEndpoints
{
    public static void MapCompanyHubEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/trade/v1/companies/{slugOrGuid}/hub",
            async (
                string slugOrGuid,
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
                var projection = await hubs.LoadAsync(slugOrGuid, account, cancellationToken);
                return projection == null ? Results.NotFound() : Results.Ok(projection);
            });

        app.MapPut(
            "/trade/v1/companies/{slugOrGuid}/hub/theme",
            async (
                string slugOrGuid,
                CompanyHubThemeUpdateRequest body,
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
                var result = await hubs.UpdateThemeAsync(slugOrGuid, account, body, cancellationToken);
                return ToMutationResult(result);
            });

        app.MapPost(
            "/trade/v1/companies/{slugOrGuid}/hub/updates",
            async (
                string slugOrGuid,
                CompanyHubPostUpdateRequest body,
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
                var result = await hubs.PostUpdateAsync(slugOrGuid, account, body, cancellationToken);
                return ToMutationResult(result);
            });

        app.MapPost(
            "/trade/v1/companies/{slugOrGuid}/hub/commissions/{commissionId:guid}/attention/read",
            async (
                string slugOrGuid,
                Guid commissionId,
                CompanyHubAttentionReadRequest body,
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
                if (account == null)
                {
                    return Results.Unauthorized();
                }
                var result = await hubs.MarkCommissionReadAsync(
                    slugOrGuid,
                    commissionId,
                    account,
                    body.OpenedRevision,
                    cancellationToken);
                return result.Status switch
                {
                    CompanyHubAttentionReadStatus.Applied => Results.Ok(
                        new CompanyHubAttentionReadResponse(result.ReadRevision!.Value)),
                    CompanyHubAttentionReadStatus.NotFound => Results.NotFound(),
                    CompanyHubAttentionReadStatus.Forbidden =>
                        Results.StatusCode(StatusCodes.Status403Forbidden),
                    _ => Results.Conflict(new
                    {
                        error = "company_hub_attention_revision_conflict",
                        message = "The commission changed before it could be marked read."
                    })
                };
            });
    }

    private static IResult ToMutationResult(CompanyHubMutationResult result) =>
        result.Status switch
        {
            CompanyHubMutationStatus.Applied => Results.Ok(
                new CompanyHubMutationResponse(result.ProfileRevision!.Value)),
            CompanyHubMutationStatus.NotFound => Results.NotFound(),
            CompanyHubMutationStatus.Unauthorized => Results.Unauthorized(),
            CompanyHubMutationStatus.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
            CompanyHubMutationStatus.Conflict => Results.Conflict(new
            {
                error = result.ErrorCode ?? "company_hub_conflict",
                message = result.ErrorMessage ?? "The company hub changed before the update completed."
            }),
            _ => Results.BadRequest(new
            {
                error = result.ErrorCode ?? "company_hub_invalid",
                message = result.ErrorMessage ?? "The company hub update is invalid."
            })
        };
}

public enum CompanyHubAttentionReadStatus
{
    Applied,
    NotFound,
    Forbidden,
    Conflict
}

public sealed record CompanyHubAttentionReadResult(
    CompanyHubAttentionReadStatus Status,
    long? ReadRevision = null);

public sealed class CompanyHubService(
    SqliteProfileHostStore profiles,
    SqliteMembershipStore memberships,
    MembershipAccessResolver accessResolver,
    LegacyCrafterAccountResolver crafterAccounts,
    ProfileHostChangeSignal changes,
    TimeProvider timeProvider)
{
    private const int MaximumDisplayNameLength = 120;
    private const int MaximumTaglineLength = 120;
    private const int MaximumAboutLength = 2000;
    private const int MaximumUpdateTitleLength = 160;
    private const int MaximumUpdateBodyLength = 2000;
    private const int MaximumUpdates = 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex MarkdownToken = new(
        @"\[(?<linkText>[^\]\r\n]{1,240})\]\((?<linkUrl>[^\s)]{1,2048})\)|\*\*(?<bold>[^*\r\n]{1,500})\*\*|\*(?<italic>[^*\r\n]{1,500})\*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim directoryGate = new(1, 1);
    private CompanyDirectoryCache? directory;

    public async Task<object?> LoadAsync(
        string slugOrGuid,
        MembershipAccount? account,
        CancellationToken cancellationToken = default)
    {
        var company = await ResolveCompanyAsync(slugOrGuid, cancellationToken);
        if (company == null)
        {
            return null;
        }

        var membership = account == null
            ? null
            : await memberships.LoadForAccountAsync(
                new CompanyId(company.Profile.Id),
                account.ProfileId,
                cancellationToken);
        var access = account == null
            ? null
            : await accessResolver.ResolveCompanyAccessAsync(
                account,
                new CompanyId(company.Profile.Id),
                cancellationToken);
        var standing = ResolveStanding(company, account, membership, access);
        var theme = ProjectTheme(company.Profile.Landing);
        var slug = BuildSlug(company.Profile.Name, company.Ordinal);
        var orders = await LoadOrdersAsync(company, cancellationToken);
        var open = orders.Where(IsOpen).Select(ProjectCommission).ToArray();
        var teaser = new CompanyHubTeaserResponse(
            "teaser",
            company.Profile.Id.ToString("D"),
            slug,
            ClampText(company.Profile.Name, MaximumDisplayNameLength, "Trade company"),
            theme,
            standing,
            company.Profile.Landing?.ShowOpenCommissionCount == true ? open.Length : (int?)null);
        if (standing.State != "active")
        {
            return teaser;
        }

        var active = await memberships.LoadActiveAsync(
            new CompanyId(company.Profile.Id),
            cancellationToken);
        var roster = await ProjectRosterAsync(company, active, cancellationToken);
        var assignments = new List<CompanyHubCommissionResponse>();
        if (account != null)
        {
            var companyId = new CompanyId(company.Profile.Id);
            var crafterScope = await crafterAccounts.ResolveScopeAsync(
                companyId,
                account.ProfileId,
                cancellationToken);
            foreach (var order in orders)
            {
                if (crafterScope.Owns(order.CompanyCommission?.ActiveClaim))
                {
                    assignments.Add(await ProjectAssignmentAsync(
                        order,
                        companyId,
                        account.ProfileId,
                        company.Profile.Name,
                        cancellationToken));
                }
            }
        }
        var pendingCount = standing.Role is "owner" or "operator"
            ? (await memberships.LoadPendingAsync(new CompanyId(company.Profile.Id), cancellationToken)).Count
            : (int?)null;
        return new CompanyHubResponse(
            "hub",
            company.Profile.Id.ToString("D"),
            slug,
            ClampText(company.Profile.Name, MaximumDisplayNameLength, "Trade company"),
            theme,
            standing,
            company.ObjectRevision,
            ProjectUpdates(company.Profile.Updates),
            open,
            assignments,
            roster,
            pendingCount);
    }

    public async Task<CompanyHubAttentionReadResult> MarkCommissionReadAsync(
        string slugOrGuid,
        Guid commissionId,
        MembershipAccount account,
        long openedRevision,
        CancellationToken cancellationToken = default)
    {
        var company = await ResolveCompanyAsync(slugOrGuid, cancellationToken);
        if (company == null)
        {
            return new(CompanyHubAttentionReadStatus.NotFound);
        }
        var companyId = new CompanyId(company.Profile.Id);
        var membership = await memberships.LoadForAccountAsync(
            companyId,
            account.ProfileId,
            cancellationToken);
        if (membership is not { State: MembershipState.Active } &&
            account.ProfileId != company.HostProfileId)
        {
            return new(CompanyHubAttentionReadStatus.Forbidden);
        }
        var order = (await LoadOrdersAsync(company, cancellationToken))
            .SingleOrDefault(candidate => candidate.Id == commissionId);
        if (order == null ||
            !await crafterAccounts.IsClaimOwnedByAccountAsync(
                companyId,
                order.CompanyCommission?.ActiveClaim,
                account.ProfileId,
                cancellationToken))
        {
            return new(CompanyHubAttentionReadStatus.NotFound);
        }
        var currentRevision = order.CompanyCommission!.Activity.LastOrDefault()?.CommissionRevision ?? 0;
        if (openedRevision < 0 || openedRevision > currentRevision)
        {
            return new(CompanyHubAttentionReadStatus.Conflict);
        }
        var readRevision = await memberships.AdvanceCommissionReadRevisionAsync(
            companyId,
            account.ProfileId,
            commissionId,
            openedRevision,
            cancellationToken);
        return new(CompanyHubAttentionReadStatus.Applied, readRevision);
    }

    public async Task<CompanyHubMutationResult> UpdateThemeAsync(
        string slugOrGuid,
        MembershipAccount? account,
        CompanyHubThemeUpdateRequest body,
        CancellationToken cancellationToken = default)
    {
        var company = await ResolveCompanyAsync(slugOrGuid, cancellationToken);
        var authorization = await AuthorizeAdministratorAsync(company, account, cancellationToken);
        if (authorization != null)
        {
            return authorization;
        }

        if (body.ExpectedProfileRevision != company!.ObjectRevision)
        {
            return RevisionConflict();
        }

        if (!TryParseToken<CompanyLandingAccent>(body.Accent, out var accent) ||
            !TryParseToken<CompanyLandingBannerStyle>(body.BannerStyle, out var banner) ||
            !TryParseToken<CompanyLandingEmblem>(body.Emblem, out var emblem) ||
            body.ExpectedProfileRevision <= 0 ||
            (body.Tagline?.Length ?? 0) > MaximumTaglineLength ||
            (body.About?.Length ?? 0) > MaximumAboutLength)
        {
            return new CompanyHubMutationResult(
                CompanyHubMutationStatus.Invalid,
                ErrorCode: "company_hub_theme_invalid",
                ErrorMessage: "The company theme contains an unsupported token or exceeds its text limit.");
        }

        var updated = CloneProfile(company!.Profile);
        updated.Landing = new CompanyLandingTheme
        {
            Accent = accent,
            BannerStyle = banner,
            Emblem = emblem,
            Tagline = NormalizeOptionalText(body.Tagline, MaximumTaglineLength),
            About = string.IsNullOrWhiteSpace(body.About) ? null : body.About.Trim(),
            ShowOpenCommissionCount = body.ShowOpenCommissionCount
        };
        updated.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        updated.SyncState = TradeSyncState.Synced;
        return await SaveProfileAsync(company, updated, body.ExpectedProfileRevision, cancellationToken);
    }

    public async Task<CompanyHubMutationResult> PostUpdateAsync(
        string slugOrGuid,
        MembershipAccount? account,
        CompanyHubPostUpdateRequest body,
        CancellationToken cancellationToken = default)
    {
        var company = await ResolveCompanyAsync(slugOrGuid, cancellationToken);
        var authorization = await AuthorizeAdministratorAsync(company, account, cancellationToken);
        if (authorization != null)
        {
            return authorization;
        }

        if (body.ExpectedProfileRevision != company!.ObjectRevision)
        {
            return RevisionConflict();
        }

        if (body.ExpectedProfileRevision <= 0 ||
            string.IsNullOrWhiteSpace(body.Title) ||
            body.Title.Length > MaximumUpdateTitleLength ||
            string.IsNullOrWhiteSpace(body.Body) ||
            body.Body.Length > MaximumUpdateBodyLength)
        {
            return new CompanyHubMutationResult(
                CompanyHubMutationStatus.Invalid,
                ErrorCode: "company_hub_update_invalid",
                ErrorMessage: "Company updates require a title and body within the supported limits.");
        }

        var updated = CloneProfile(company!.Profile);
        var existing = (updated.Updates ?? [])
            .Where(item => item.Id != Guid.Empty)
            .Select(item => body.IsPinned && item.IsPinned ? item with { IsPinned = false } : item)
            .OrderByDescending(item => item.PublishedAtUtc)
            .Take(MaximumUpdates - 1);
        var companyUpdate = new TradeCompanyUpdate
        {
            Id = Guid.NewGuid(),
            Title = body.Title.Trim(),
            Body = body.Body.Trim(),
            AuthorDisplayName = ClampText(
                account!.Profile.DisplayName,
                MaximumDisplayNameLength,
                "Company member"),
            PublishedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            IsPinned = body.IsPinned
        };
        updated.Updates = existing.Prepend(companyUpdate).ToArray();
        updated.UpdatedAtUtc = companyUpdate.PublishedAtUtc;
        updated.SyncState = TradeSyncState.Synced;
        return await SaveProfileAsync(company, updated, body.ExpectedProfileRevision, cancellationToken);
    }

    private async Task<CompanyHubMutationResult?> AuthorizeAdministratorAsync(
        HostedCompany? company,
        MembershipAccount? account,
        CancellationToken cancellationToken)
    {
        if (company == null)
        {
            return new CompanyHubMutationResult(CompanyHubMutationStatus.NotFound);
        }
        if (account == null)
        {
            return new CompanyHubMutationResult(CompanyHubMutationStatus.Unauthorized);
        }
        var access = await accessResolver.ResolveCompanyAccessAsync(
            account,
            new CompanyId(company.Profile.Id),
            cancellationToken);
        return access is { Role: TradeCompanyRole.Owner or TradeCompanyRole.Operator }
                ? null
                : new CompanyHubMutationResult(CompanyHubMutationStatus.Forbidden);
    }

    private async Task<CompanyHubMutationResult> SaveProfileAsync(
        HostedCompany company,
        TradeCompanyProfile updated,
        long expectedProfileRevision,
        CancellationToken cancellationToken)
    {
        var result = await profiles.PutObjectAsync(
            company.HostProfileId.ToString("D"),
            ProfileSyncCollections.TradeCompanyProfiles,
            company.Profile.Id.ToString("D"),
            JsonSerializer.Serialize(updated, JsonOptions),
            expectedProfileRevision,
            cancellationToken,
            allowCompanyCollection: true);
        return result.Success
            ? new CompanyHubMutationResult(
                CompanyHubMutationStatus.Applied,
                result.Object?.Revision ?? expectedProfileRevision)
            : new CompanyHubMutationResult(
                result.Conflict ? CompanyHubMutationStatus.Conflict : CompanyHubMutationStatus.Invalid,
                ErrorCode: result.ErrorCode,
                ErrorMessage: result.ErrorMessage);
    }

    private static TradeCompanyProfile CloneProfile(TradeCompanyProfile source) =>
        JsonSerializer.Deserialize<TradeCompanyProfile>(
            JsonSerializer.Serialize(source, JsonOptions),
            JsonOptions)
        ?? throw new InvalidOperationException("The company profile could not be copied.");

    private static CompanyHubMutationResult RevisionConflict() =>
        new(
            CompanyHubMutationStatus.Conflict,
            ErrorCode: "company_hub_revision_conflict",
            ErrorMessage: "The company hub changed before the update completed.");

    private async Task<HostedCompany?> ResolveCompanyAsync(
        string slugOrGuid,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slugOrGuid) || slugOrGuid.Length > 160)
        {
            return null;
        }

        var companies = await LoadCompaniesAsync(cancellationToken);
        if (CompanyId.TryParse(slugOrGuid, out var companyId))
        {
            var match = companies.SingleOrDefault(item => item.Profile.Id == companyId.Value);
            if (match == null)
            {
                return null;
            }

            var ordinal = companies
                .Where(item => string.Equals(
                    Slugify(item.Profile.Name),
                    Slugify(match.Profile.Name),
                    StringComparison.Ordinal))
                .OrderBy(item => item.Profile.CreatedAtUtc)
                .ThenBy(item => item.Profile.Id)
                .Select((item, index) => new { item.Profile.Id, Ordinal = index + 1 })
                .Single(item => item.Id == match.Profile.Id)
                .Ordinal;
            return match with { Ordinal = ordinal };
        }

        return companies
            .GroupBy(item => Slugify(item.Profile.Name), StringComparer.Ordinal)
            .SelectMany(group => group
                .OrderBy(item => item.Profile.CreatedAtUtc)
                .ThenBy(item => item.Profile.Id)
                .Select((item, index) => item with { Ordinal = index + 1 }))
            .Where(item => string.Equals(
                BuildSlug(item.Profile.Name, item.Ordinal),
                slugOrGuid,
                StringComparison.Ordinal))
            .OrderBy(item => item.Profile.CreatedAtUtc)
            .ThenBy(item => item.Profile.Id)
            .FirstOrDefault();
    }

    private async Task<IReadOnlyList<HostedCompany>> LoadCompaniesAsync(
        CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref directory);
        if (current is { } cached && DirectoryIsCurrent(cached))
        {
            return cached.Companies;
        }

        await directoryGate.WaitAsync(cancellationToken);
        try
        {
            current = Volatile.Read(ref directory);
            if (current is { } refreshed && DirectoryIsCurrent(refreshed))
            {
                return refreshed.Companies;
            }

            IReadOnlyList<HostedCompany> companies;
            ProfileHostChangeObservation observation;
            do
            {
                observation = changes.ObserveAll();
                companies = await BuildCompanyDirectoryAsync(cancellationToken);
            }
            while (changes.ObserveAll().Generation != observation.Generation);
            Volatile.Write(
                ref directory,
                new CompanyDirectoryCache(
                    companies,
                    observation.Generation,
                    timeProvider.GetUtcNow().AddMinutes(1)));
            return companies;
        }
        finally
        {
            directoryGate.Release();
        }
    }

    private bool DirectoryIsCurrent(CompanyDirectoryCache? current) =>
        current != null &&
        current.ExpiresAtUtc > timeProvider.GetUtcNow() &&
        changes.ObserveAll().Generation == current.Generation;

    private async Task<IReadOnlyList<HostedCompany>> BuildCompanyDirectoryAsync(
        CancellationToken cancellationToken)
    {
        var hosted = await profiles.LoadObjectsAsync(
            ProfileSyncCollections.TradeCompanyProfiles,
            cancellationToken);
        return hosted
            .Select(item => TryReadCompany(item, out var company)
                ? new HostedCompany(
                    company,
                    Guid.Parse(item.ProfileId),
                    item.Object.Revision,
                    1)
                : null)
            .Where(item => item != null)
            .Cast<HostedCompany>()
            .GroupBy(item => item.Profile.Id)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();
    }

    private async Task<IReadOnlyList<TradeOrder>> LoadOrdersAsync(
        HostedCompany company,
        CancellationToken cancellationToken)
    {
        var hosted = await profiles.LoadProfileObjectsAsync(
            company.HostProfileId.ToString("D"),
            ProfileSyncCollections.TradeOrders,
            cancellationToken);
        var orders = new List<TradeOrder>();
        foreach (var item in hosted)
        {
            try
            {
                var order = JsonSerializer.Deserialize<TradeOrder>(item.Object.PayloadJson, JsonOptions);
                if (order?.CompanyCommission?.CompanyId.Value == company.Profile.Id &&
                    order.CompanyCommission.CommissionId == order.Id &&
                    order.CompanyCommission.TermsVersions.Any(terms =>
                        terms.Version == order.CompanyCommission.CurrentTermsVersion))
                {
                    orders.Add(order);
                }
            }
            catch (JsonException)
            {
            }
        }

        return orders;
    }

    private async Task<IReadOnlyList<CompanyHubRosterMemberResponse>> ProjectRosterAsync(
        HostedCompany company,
        IReadOnlyList<CompanyMembership> active,
        CancellationToken cancellationToken)
    {
        var membershipsByAccount = active
            .GroupBy(item => item.AccountProfileId)
            .Select(group => group.OrderByDescending(item => item.Role).First())
            .ToDictionary(item => item.AccountProfileId);
        if (!membershipsByAccount.ContainsKey(company.HostProfileId))
        {
            membershipsByAccount[company.HostProfileId] = new CompanyMembership(
                new CompanyId(company.Profile.Id),
                company.HostProfileId,
                MembershipRole.Owner,
                MembershipState.Active,
                DateTimeOffset.MinValue,
                null,
                null,
                null);
        }

        var roster = new List<CompanyHubRosterMemberResponse>();
        foreach (var membership in membershipsByAccount.Values.OrderBy(item => item.Role).ThenBy(item => item.AccountProfileId))
        {
            var profile = await profiles.LoadProfileAsync(
                membership.AccountProfileId.ToString("D"),
                cancellationToken);
            if (profile != null)
            {
                roster.Add(new CompanyHubRosterMemberResponse(
                    ClampText(profile.DisplayName, MaximumDisplayNameLength, "Member"),
                    membership.Role.ToString().ToLowerInvariant()));
            }
        }

        return roster;
    }

    private static bool TryReadCompany(HostedProfileObject hosted, out TradeCompanyProfile company)
    {
        company = null!;
        if (!Guid.TryParse(hosted.ProfileId, out var profileId) || profileId == Guid.Empty ||
            !Guid.TryParse(hosted.Object.ObjectId, out var objectId) || objectId == Guid.Empty)
        {
            return false;
        }

        try
        {
            company = JsonSerializer.Deserialize<TradeCompanyProfile>(hosted.Object.PayloadJson, JsonOptions)!;
            return company?.Id == objectId && company.Id != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsOpen(TradeOrder order) =>
        order.Status == TradeOrderStatus.ReadyToAssign &&
        order.CompanyCommission is { ActiveClaim: null } commission &&
        commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Published;

    private async Task<CompanyHubCommissionResponse> ProjectAssignmentAsync(
        TradeOrder order,
        CompanyId companyId,
        Guid accountProfileId,
        string companyDisplayName,
        CancellationToken cancellationToken)
    {
        var commission = order.CompanyCommission!;
        var claim = commission.ActiveClaim!;
        var readRevision = await memberships.LoadCommissionReadRevisionAsync(
            companyId,
            accountProfileId,
            order.Id,
            cancellationToken);
        var latest = commission.Activity
            .Where(activity =>
                activity.Actor.Kind == CompanyCommissionActorKind.Commissioner &&
                activity.Visibility == CompanyCommissionActivityVisibility.Shared &&
                activity.Kind != CompanyCommissionActivityKind.DraftUpdated &&
                activity.CreatedAtUtc >= claim.ClaimedAtUtc &&
                activity.CommissionRevision > (readRevision ?? 0))
            .OrderByDescending(activity => activity.CommissionRevision)
            .FirstOrDefault();
        CompanyHubCommissionAttentionResponse? attention = null;
        if (latest != null)
        {
            var brief = CompanyCommissionProjectionService.CreatePublicBrief(
                order,
                companyDisplayName);
            attention = new CompanyHubCommissionAttentionResponse(
                latest.EventId,
                latest.CommissionRevision,
                ClampText(
                    DiscordCompanyCommissionPostCommitSink.BuildSummary(latest, brief),
                    800,
                    "The commissioner updated this commission."),
                latest.CreatedAtUtc);
        }
        return ProjectCommission(order) with { UnreadCommissionerUpdate = attention };
    }

    private static CompanyHubCommissionResponse ProjectCommission(TradeOrder order)
    {
        var commission = order.CompanyCommission!;
        var terms = commission.CurrentTerms;
        var progressByLine = commission.OutputProgress.ToDictionary(progress => progress.LineId);
        var canWork = commission.ActiveClaim != null &&
            commission.ClearedToWork &&
            commission.ParticipantAcknowledgedTermsVersion == commission.CurrentTermsVersion;
        var allOutputsReady = terms.Outputs.All(output =>
            progressByLine.TryGetValue(output.LineId, out var progress) &&
            progress.CompletedQuantity >= output.RequiredQuantity &&
            progress.ReadyQuantity >= output.RequiredQuantity);
        return new CompanyHubCommissionResponse(
            order.Id.ToString("D"),
            ClampText(order.Title, 240, "Untitled commission"),
            ClampText(commission.Reference, 120, string.Empty),
            commission.CurrentTermsVersion,
            terms.DeliveryInstructions,
            commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Published
                ? commission.PublicMetadata.PublicBriefId
                : null,
            commission.Activity.LastOrDefault()?.CommissionRevision ?? 0,
            terms.Outputs.Select(output =>
            {
                progressByLine.TryGetValue(output.LineId, out var progress);
                return new CompanyHubOutputResponse(
                    output.LineId,
                    output.ItemId,
                    ClampText(output.Name, 240, "Unknown item"),
                    Math.Max(0, output.RequiredQuantity),
                    Math.Max(0, progress?.CompletedQuantity ?? 0),
                    Math.Max(0, progress?.ReadyQuantity ?? 0),
                    Math.Max(0, progress?.AcceptedQuantity ?? 0));
            }).ToArray(),
            new CompanyHubPaymentResponse(
                terms.Payment.Schedule.ToString().ToLowerInvariant(),
                ClampText(terms.Payment.ContractLabel, 240, "Commission"),
                terms.Payment.Total),
            commission.SettlementState.ToString().ToLowerInvariant(),
            order.Status.ToString().ToLowerInvariant(),
            canWork,
            canWork && !commission.DeliveryReadiness.IsReady && !allOutputsReady,
            canWork && !commission.DeliveryReadiness.IsReady && allOutputsReady,
            WorkBlockedReason(commission),
            null);
    }

    private static string? WorkBlockedReason(TradeCompanyCommission commission)
    {
        if (commission.ActiveClaim == null)
        {
            return null;
        }
        if (!GateSatisfied(commission.Gates.Identity.State))
        {
            return "Identity review is still required.";
        }
        if (!GateSatisfied(commission.Gates.Payment.State))
        {
            return "Payment confirmation is still required.";
        }
        if (!GateSatisfied(commission.Gates.CompanyMaterials.State))
        {
            return "Company materials have not been received.";
        }
        return commission.ParticipantAcknowledgedTermsVersion != commission.CurrentTermsVersion
            ? "Review the current terms before starting work."
            : null;
    }

    private static bool GateSatisfied(CompanyCommissionClearanceState state) =>
        state is CompanyCommissionClearanceState.NotRequired or CompanyCommissionClearanceState.Satisfied;

    private static IReadOnlyList<CompanyHubUpdateResponse> ProjectUpdates(
        IReadOnlyList<TradeCompanyUpdate>? updates) =>
        (updates ?? [])
            .Where(item => item.Id != Guid.Empty &&
                !string.IsNullOrWhiteSpace(item.Title) &&
                !string.IsNullOrWhiteSpace(item.Body))
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.PublishedAtUtc)
            .Take(MaximumUpdates)
            .Select(item => new CompanyHubUpdateResponse(
                item.Id,
                ClampText(item.Title, MaximumUpdateTitleLength, "Company update"),
                SanitizeMarkdown(item.Body, MaximumUpdateBodyLength) ?? string.Empty,
                ClampText(item.AuthorDisplayName, MaximumDisplayNameLength, "Company member"),
                item.PublishedAtUtc,
                item.EditedAtUtc,
                item.IsPinned))
            .ToArray();

    private static CompanyHubStandingResponse ResolveStanding(
        HostedCompany company,
        MembershipAccount? account,
        CompanyMembership? membership,
        TradeCompanyAccessContext? access)
    {
        if (access is { Role: TradeCompanyRole.Owner or TradeCompanyRole.Operator })
        {
            return new CompanyHubStandingResponse(
                "active",
                access.Role.ToString().ToLowerInvariant());
        }
        if (membership?.State is MembershipState.Denied or MembershipState.Revoked)
        {
            return new CompanyHubStandingResponse("none", null);
        }
        if (account?.ProfileId == company.HostProfileId)
        {
            return new CompanyHubStandingResponse("active", "owner");
        }

        return membership?.State switch
        {
            MembershipState.Pending => new CompanyHubStandingResponse("pending", null),
            MembershipState.Active => new CompanyHubStandingResponse(
                "active",
                membership.Role.ToString().ToLowerInvariant()),
            _ => new CompanyHubStandingResponse("none", null)
        };
    }

    private static CompanyHubThemeResponse ProjectTheme(CompanyLandingTheme? theme) =>
        new(
            IsValid(theme?.Accent) ? ToToken(theme!.Accent) : ToToken(CompanyLandingAccent.DeepBlue),
            IsValid(theme?.BannerStyle) ? ToToken(theme!.BannerStyle) : ToToken(CompanyLandingBannerStyle.Gradient),
            IsValid(theme?.Emblem) ? ToToken(theme!.Emblem) : ToToken(CompanyLandingEmblem.Star),
            NormalizeOptionalText(theme?.Tagline, MaximumTaglineLength),
            SanitizeMarkdown(theme?.About),
            theme?.ShowOpenCommissionCount == true);

    private static bool IsValid<T>(T? value) where T : struct, Enum =>
        value.HasValue && Enum.IsDefined(value.Value);

    private static string ToToken<T>(T value) where T : struct, Enum =>
        Regex.Replace(value.ToString(), "(?<!^)([A-Z])", "-$1").ToLowerInvariant();

    private static bool TryParseToken<T>(string? value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value?.Replace("-", string.Empty, StringComparison.Ordinal), true, out parsed) &&
        Enum.IsDefined(parsed);

    private static string BuildSlug(string? name, int ordinal) =>
        ordinal <= 1 ? Slugify(name) : $"{Slugify(name)}-{ordinal}";

    private static string Slugify(string? value)
    {
        var source = value ?? string.Empty;
        var slug = new System.Text.StringBuilder(source.Length);
        var hyphenPending = false;
        foreach (var character in source)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (hyphenPending && slug.Length > 0)
                {
                    slug.Append('-');
                }

                slug.Append(char.ToLowerInvariant(character));
                hyphenPending = false;
            }
            else
            {
                hyphenPending = slug.Length > 0;
            }
        }

        return slug.Length == 0 ? "company" : slug.ToString();
    }

    private static string? NormalizeOptionalText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ClampText(value, maximumLength, string.Empty);
    }

    private static string ClampText(string? value, int maximumLength, string fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim()[..Math.Min(value.Trim().Length, maximumLength)];

    private static string? SanitizeMarkdown(string? value, int maximumLength = MaximumAboutLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var source = value.Trim()[..Math.Min(value.Trim().Length, maximumLength)];
        var sanitized = new System.Text.StringBuilder(source.Length);
        var position = 0;
        foreach (Match match in MarkdownToken.Matches(source))
        {
            sanitized.Append(SanitizeLiteral(source[position..match.Index]));
            position = match.Index + match.Length;
            if (match.Groups["linkText"].Success)
            {
                var url = match.Groups["linkUrl"].Value;
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    sanitized.Append('[')
                        .Append(SanitizeLiteral(match.Groups["linkText"].Value))
                        .Append("](")
                        .Append(uri.AbsoluteUri)
                        .Append(')');
                }
            }
            else if (match.Groups["bold"].Success)
            {
                sanitized.Append("**").Append(SanitizeLiteral(match.Groups["bold"].Value)).Append("**");
            }
            else
            {
                sanitized.Append('*').Append(SanitizeLiteral(match.Groups["italic"].Value)).Append('*');
            }
        }

        sanitized.Append(SanitizeLiteral(source[position..]));
        return sanitized.ToString()[..Math.Min(sanitized.Length, maximumLength)];
    }

    private static string SanitizeLiteral(string value) =>
        HtmlEncoder.Default.Encode(value)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private sealed record HostedCompany(
        TradeCompanyProfile Profile,
        Guid HostProfileId,
        long ObjectRevision,
        int Ordinal);

    private sealed record CompanyDirectoryCache(
        IReadOnlyList<HostedCompany> Companies,
        long Generation,
        DateTimeOffset ExpiresAtUtc);
}
