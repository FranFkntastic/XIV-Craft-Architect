using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
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

    public async Task<TradeCompanyWorkspaceProfile> LoadWorkspaceProfileAsync(
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"trade/v1/companies/{companyId:D}/workspace-profile",
            null,
            cancellationToken);
        await EnsureHubSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TradeCompanyWorkspaceProfile>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The selected company workspace returned an empty profile.");
    }

    public async Task RequestMembershipAsync(string companyId, string? note, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/membership-requests",
            new { RequestNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim() },
            cancellationToken);
        await EnsureHubSuccessAsync(response, cancellationToken);
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

    public async Task UpdateThemeAsync(
        string companyId,
        long expectedProfileRevision,
        CompanyHubTheme theme,
        bool showOpenCommissionCount,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/hub/theme",
            new
            {
                ExpectedProfileRevision = expectedProfileRevision,
                theme.Accent,
                theme.BannerStyle,
                theme.Emblem,
                theme.Tagline,
                theme.About,
                ShowOpenCommissionCount = showOpenCommissionCount
            },
            cancellationToken);
        await EnsureHubSuccessAsync(response, cancellationToken);
    }

    public async Task PostUpdateAsync(
        string companyId,
        long expectedProfileRevision,
        string title,
        string body,
        bool isPinned,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/hub/updates",
            new
            {
                ExpectedProfileRevision = expectedProfileRevision,
                Title = title,
                Body = body,
                IsPinned = isPinned
            },
            cancellationToken);
        await EnsureHubSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<CompanyMembership>> LoadMembershipsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "trade/v1/memberships", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<CompanyMembership>>(JsonOptions, cancellationToken) ?? [];
    }

    public async Task<CompanyMembershipNotifications> LoadNotificationPreferencesAsync(string companyId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/membership-notifications", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CompanyMembershipNotifications>(JsonOptions, cancellationToken))!;
    }

    public async Task SetNotificationPreferencesAsync(
        string companyId,
        CompanyMembershipNotifications preferences,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/membership-notifications",
            new
            {
                preferences.ActionRequired,
                preferences.CommissionerMessages,
                preferences.ProgressAndStatus
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<MemberNotificationTestReadiness> LoadNotificationTestReadinessAsync(
        string companyId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/membership-notifications/test-readiness",
            null,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MemberNotificationTestReadiness>(
            JsonOptions,
            cancellationToken))!;
    }

    public async Task<MemberNotificationTestDelivery> SendNotificationTestAsync(
        string companyId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/membership-notifications/test",
            null,
            cancellationToken);
        await EnsureHubSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<MemberNotificationTestDelivery>(
            JsonOptions,
            cancellationToken))!;
    }

    public async Task<MemberNotificationTestDelivery> LoadNotificationTestAsync(
        string companyId,
        Guid testId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/membership-notifications/test/{testId:D}",
            null,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MemberNotificationTestDelivery>(
            JsonOptions,
            cancellationToken))!;
    }

    public async Task<long> MarkCommissionReadAsync(
        string companyId,
        string commissionId,
        long openedRevision,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/hub/commissions/{Uri.EscapeDataString(commissionId)}/attention/read",
            new { OpenedRevision = openedRevision },
            cancellationToken);
        await EnsureHubSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<CompanyHubAttentionRead>(
            JsonOptions,
            cancellationToken))!.ReadRevision;
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

    public async Task<LegacyCrafterMigration> LoadLegacyCrafterMigrationAsync(
        string companyId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/legacy-crafter-migration",
            null,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LegacyCrafterMigration>(
                   JsonOptions,
                   cancellationToken)
               ?? new LegacyCrafterMigration([], []);
    }

    public async Task ConnectLegacyCrafterAsync(
        string companyId,
        Guid legacyCrafterId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/legacy-crafter-bindings/{legacyCrafterId:D}",
            new { AccountProfileId = accountProfileId },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DisconnectLegacyCrafterAsync(
        string companyId,
        Guid legacyCrafterId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Delete,
            $"trade/v1/companies/{Uri.EscapeDataString(companyId)}/legacy-crafter-bindings/{legacyCrafterId:D}",
            null,
            cancellationToken);
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
        await EnsureHubSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureHubSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        CompanyHubError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<CompanyHubError>(
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
        }

        throw new CompanyHubRequestException(
            response.StatusCode,
            error?.Error,
            error?.Message ?? $"The company hub request failed with status {(int)response.StatusCode}.");
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
    long ProfileRevision,
    IReadOnlyList<CompanyHubUpdate>? Updates,
    int? OpenCommissionCount,
    IReadOnlyList<CompanyHubCommission>? OpenCommissions,
    IReadOnlyList<CompanyHubCommission>? Assignments,
    IReadOnlyList<CompanyHubRosterMember>? Roster,
    int? PendingMembershipRequestCount);

public sealed record TradeCompanyWorkspaceProfile(
    Guid Id,
    string Name,
    string? CommissionContact,
    TradePaymentPolicy PaymentPolicy,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public TradeCompanyProfile ToTransientProfile() => new()
    {
        Id = Id,
        Name = Name,
        CommissionContact = CommissionContact,
        PaymentPolicy = PaymentPolicy,
        RemoteId = Id.ToString("D"),
        SyncState = TradeSyncState.Synced,
        CreatedAtUtc = CreatedAtUtc,
        UpdatedAtUtc = UpdatedAtUtc
    };
}

public sealed record CompanyHubTheme(
    string Accent,
    string BannerStyle,
    string Emblem,
    string? Tagline,
    string? About,
    bool ShowOpenCommissionCount);
public sealed record CompanyHubStanding(string State, string? Role);
public sealed record CompanyHubOutput(Guid LineId, int ItemId, string Name, int Quantity, int CompletedQuantity, int ReadyQuantity, int AcceptedQuantity);
public sealed record CompanyHubPayment(string Schedule, string Label, decimal Total);
public sealed record CompanyHubUpdate(Guid Id, string Title, string Body, string AuthorDisplayName, DateTime PublishedAtUtc, DateTime? EditedAtUtc, bool IsPinned);
public sealed record CompanyHubCommission(string CommissionId, string Title, string Reference, int TermsVersion, string DeliveryInstructions, string? PublicBriefId, long ProjectionRevision, IReadOnlyList<CompanyHubOutput> Outputs, CompanyHubPayment Payment, string SettlementState, string State, bool CanWork, bool CanReportProgress, bool CanDeclareReadiness, string? WorkBlockedReason, CompanyHubCommissionAttention? UnreadCommissionerUpdate);
public sealed record CompanyHubCommissionAttention(Guid EventId, long Revision, string Text, DateTime CreatedAtUtc);
public sealed record CompanyHubAttentionRead(long ReadRevision);
public sealed record CompanyHubRosterMember(string DisplayName, string Role);
public sealed record CompanyMembership(string CompanyId, Guid AccountProfileId, string Role, string State, DateTimeOffset RequestedAtUtc, DateTimeOffset? DecidedAtUtc, Guid? DecidedByProfileId, string? RequestNote, bool HasMembership);
public sealed record CompanyMembershipNotifications(
    string CompanyId,
    bool ActionRequired,
    bool CommissionerMessages,
    bool ProgressAndStatus);
public sealed record MemberNotificationTestReadiness(
    bool Ready,
    string? DestinationDisplayName,
    string? Reason);
public sealed record MemberNotificationTestDelivery(
    Guid TestId,
    string State,
    string DestinationDisplayName,
    int AttemptCount,
    string? MessageId,
    string? Error);
public sealed record CompanyMember(Guid AccountProfileId, string DisplayName, string Role, string State, DateTimeOffset RequestedAtUtc, DateTimeOffset? DecidedAtUtc, bool DiscordLinked);
public sealed record LegacyCrafterMigration(
    IReadOnlyList<LegacyCrafterCandidate> LegacyCrafters,
    IReadOnlyList<LegacyCrafterBinding> Bindings);
public sealed record LegacyCrafterCandidate(
    Guid LegacyCrafterId,
    string DisplayName,
    string? WorldName,
    string? LodestoneCharacterId);
public sealed record LegacyCrafterBinding(
    Guid LegacyCrafterId,
    Guid AccountProfileId,
    string Evidence,
    DateTimeOffset CreatedAtUtc);

public sealed class CompanyHubRequestException(
    HttpStatusCode statusCode,
    string? errorCode,
    string message) : HttpRequestException(message, null, statusCode)
{
    public string? ErrorCode { get; } = errorCode;
}

file sealed record CompanyHubError(string? Error, string? Message);
