using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record CompanyHubThemeResponse(
    string Accent,
    string BannerStyle,
    string Emblem,
    string? Tagline,
    string? About);

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
    string Name,
    int Quantity,
    int CompletedQuantity,
    int ReadyQuantity,
    int AcceptedQuantity);

public sealed record CompanyHubPaymentResponse(string Schedule, string Label, decimal Total);

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
    string State);

public sealed record CompanyHubRosterMemberResponse(string DisplayName, string Role);

public sealed record CompanyHubActivityResponse(
    string CommissionId,
    string Reference,
    string Kind,
    DateTime OccurredAtUtc);

public sealed record CompanyHubResponse(
    string Kind,
    string CompanyId,
    string Slug,
    string DisplayName,
    CompanyHubThemeResponse Theme,
    CompanyHubStandingResponse Standing,
    IReadOnlyList<CompanyHubCommissionResponse> OpenCommissions,
    IReadOnlyList<CompanyHubCommissionResponse> Assignments,
    IReadOnlyList<CompanyHubRosterMemberResponse> Roster,
    IReadOnlyList<CompanyHubActivityResponse> RecentActivity,
    int? PendingMembershipRequestCount);

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
    }
}

public sealed class CompanyHubService(
    SqliteProfileHostStore profiles,
    SqliteMembershipStore memberships,
    ProfileHostChangeSignal changes,
    TimeProvider timeProvider)
{
    private const int MaximumDisplayNameLength = 120;
    private const int MaximumTaglineLength = 120;
    private const int MaximumAboutLength = 2000;
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
        var standing = ResolveStanding(company, account, membership);
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
        var assignments = account == null
            ? []
            : orders.Where(order => order.CompanyCommission?.ActiveClaim is { } claim &&
                (claim.CrafterId == account.ProfileId || claim.ProvisionalCrafterId == account.ProfileId))
                .Select(ProjectCommission)
                .ToArray();
        var activity = orders
            .SelectMany(order => (order.CompanyCommission?.Activity ?? [])
                .Where(item => item.Visibility == CompanyCommissionActivityVisibility.Shared)
                .Select(item => new CompanyHubActivityResponse(
                    order.Id.ToString("D"),
                    ClampText(order.CompanyCommission!.Reference, 120, string.Empty),
                    item.Kind.ToString().ToLowerInvariant(),
                    item.CreatedAtUtc)))
            .OrderByDescending(item => item.OccurredAtUtc)
            .Take(20)
            .ToArray();
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
            open,
            assignments,
            roster,
            activity,
            pendingCount);
    }

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
                ? new HostedCompany(company, Guid.Parse(item.ProfileId), 1)
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

    private static CompanyHubCommissionResponse ProjectCommission(TradeOrder order)
    {
        var commission = order.CompanyCommission!;
        var terms = commission.CurrentTerms;
        var progressByLine = commission.OutputProgress.ToDictionary(progress => progress.LineId);
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
            order.Status.ToString().ToLowerInvariant());
    }

    private static CompanyHubStandingResponse ResolveStanding(
        HostedCompany company,
        MembershipAccount? account,
        CompanyMembership? membership)
    {
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
            SanitizeMarkdown(theme?.About));

    private static bool IsValid<T>(T? value) where T : struct, Enum =>
        value.HasValue && Enum.IsDefined(value.Value);

    private static string ToToken<T>(T value) where T : struct, Enum =>
        Regex.Replace(value.ToString(), "(?<!^)([A-Z])", "-$1").ToLowerInvariant();

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

    private static string? SanitizeMarkdown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var source = value.Trim()[..Math.Min(value.Trim().Length, MaximumAboutLength)];
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
        return sanitized.ToString()[..Math.Min(sanitized.Length, MaximumAboutLength)];
    }

    private static string SanitizeLiteral(string value) =>
        HtmlEncoder.Default.Encode(value)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);

    private sealed record HostedCompany(TradeCompanyProfile Profile, Guid HostProfileId, int Ordinal);

    private sealed record CompanyDirectoryCache(
        IReadOnlyList<HostedCompany> Companies,
        long Generation,
        DateTimeOffset ExpiresAtUtc);
}
