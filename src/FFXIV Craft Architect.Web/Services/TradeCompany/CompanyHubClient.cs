using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class CompanyHubClient(
    HttpClient http,
    ProfileSyncLocalStateService localState,
    ProfileHostClient profileHostClient)
{
    private const string AccessKeyHeader = "X-Profile-Key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CompanyHubProjection?> LoadAsync(string slug, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"trade/v1/companies/{Uri.EscapeDataString(slug)}/hub", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CompanyHubProjection>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Company hub returned an empty response.");
    }

    public async Task RequestMembershipAsync(string companyId, string? note, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/membership-requests",
            new { RequestNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim() },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ClaimAsync(string companyId, string commissionId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/commissions/{Uri.EscapeDataString(commissionId)}/claim",
            null,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<CompanyMembership>> LoadMembershipsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "trade/v1/memberships", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<CompanyMembership>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<bool> LoadNotificationOptOutAsync(string companyId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/membership-notifications", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CompanyMembershipNotifications>(JsonOptions, cancellationToken))!.OptedOut;
    }

    public async Task SetNotificationOptOutAsync(string companyId, bool optedOut, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Put, $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/membership-notifications", new { OptedOut = optedOut }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ReportProgressAsync(CompanyHubCommission commission, CancellationToken cancellationToken = default)
    {
        var outputs = commission.Outputs.Select(output => new
        {
            output.LineId,
            output.ItemId,
            CompletedQuantity = output.Quantity,
            ReadyQuantity = output.Quantity
        });
        await SendParticipantCommandAsync(commission, "report-progress", new { Outputs = outputs, Comment = (string?)null }, cancellationToken);
    }

    public async Task DeclareReadinessAsync(CompanyHubCommission commission, CancellationToken cancellationToken = default) =>
        await SendParticipantCommandAsync(commission, "declare-readiness", new { Comment = (string?)null }, cancellationToken);

    public async Task<IReadOnlyList<CompanyMember>> LoadCompanyMembersAsync(string companyId, CancellationToken cancellationToken = default) =>
        await GetListAsync<CompanyMember>($"trade/v1/companies/{Uri.EscapeDataString(companyId)}/memberships", cancellationToken);

    public async Task<IReadOnlyList<CompanyMembership>> LoadPendingMembershipsAsync(string companyId, CancellationToken cancellationToken = default) =>
        await GetListAsync<CompanyMembership>($"trade/v1/companies/{Uri.EscapeDataString(companyId)}/membership-requests", cancellationToken);

    public async Task TransitionMembershipAsync(string companyId, Guid accountProfileId, string action, string? reason = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/memberships/{accountProfileId:D}/{action}", reason == null ? null : new { Reason = reason }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<T>>(JsonOptions, cancellationToken) ?? [];
    }

    private async Task SendParticipantCommandAsync(CompanyHubCommission commission, string route, object command, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, $"xivdata/commission-briefs/{Uri.EscapeDataString(commission.PublicBriefId!)}/commands/{route}", new
        {
            ProtocolVersion = 1,
            commission.PublicBriefId,
            ExpectedProjectionRevision = commission.ProjectionRevision,
            CommandId = Guid.NewGuid(),
            Command = command
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var connection = await localState.LoadConnectionSettingsAsync();
        var hostUrl = connection.IsConfigured ? connection.HostUrl! : profileHostClient.DefaultHostUrl;
        var request = new HttpRequestMessage(method, new Uri(new Uri(ProfileHostClient.NormalizeHostUrl(hostUrl)), path));
        if (connection.IsConfigured)
        {
            request.Headers.Add(AccessKeyHeader, connection.AccessKey);
        }
        if (body != null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        try
        {
            return await http.SendAsync(request, cancellationToken);
        }
        finally
        {
            request.Dispose();
        }
    }
}

public sealed record CompanyHubProjection(
    string Kind,
    string CompanyId,
    string Slug,
    string DisplayName,
    CompanyHubTheme Theme,
    CompanyHubStanding Standing,
    int? OpenCommissionCount,
    IReadOnlyList<CompanyHubCommission>? OpenCommissions,
    IReadOnlyList<CompanyHubCommission>? Assignments,
    IReadOnlyList<CompanyHubRosterMember>? Roster,
    IReadOnlyList<CompanyHubActivity>? RecentActivity,
    int? PendingMembershipRequestCount);

public sealed record CompanyHubTheme(string Accent, string BannerStyle, string Emblem, string? Tagline, string? About);
public sealed record CompanyHubStanding(string State, string? Role);
public sealed record CompanyHubOutput(Guid LineId, int ItemId, string Name, int Quantity, int CompletedQuantity, int ReadyQuantity, int AcceptedQuantity);
public sealed record CompanyHubPayment(string Schedule, string Label, decimal Total);
public sealed record CompanyHubCommission(string CommissionId, string Title, string Reference, int TermsVersion, string DeliveryInstructions, string? PublicBriefId, long ProjectionRevision, IReadOnlyList<CompanyHubOutput> Outputs, CompanyHubPayment Payment, string SettlementState, string State);
public sealed record CompanyHubRosterMember(string DisplayName, string Role);
public sealed record CompanyHubActivity(string CommissionId, string Reference, string Kind, DateTime OccurredAtUtc);
public sealed record CompanyMembership(string CompanyId, Guid AccountProfileId, string Role, string State, DateTimeOffset RequestedAtUtc, DateTimeOffset? DecidedAtUtc, Guid? DecidedByProfileId, string? RequestNote);
public sealed record CompanyMembershipNotifications(string CompanyId, bool OptedOut);
public sealed record CompanyMember(Guid AccountProfileId, string DisplayName, string Role, string State, DateTimeOffset RequestedAtUtc, DateTimeOffset? DecidedAtUtc, bool DiscordLinked);
