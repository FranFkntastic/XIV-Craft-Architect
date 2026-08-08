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
            new { Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() },
            cancellationToken);
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
public sealed record CompanyHubOutput(string Name, int Quantity);
public sealed record CompanyHubPayment(string Schedule, string Label, decimal Total);
public sealed record CompanyHubCommission(string CommissionId, string Title, string Reference, IReadOnlyList<CompanyHubOutput> Outputs, CompanyHubPayment Payment, string State);
public sealed record CompanyHubRosterMember(string DisplayName, string Role);
public sealed record CompanyHubActivity(string CommissionId, string Reference, string Kind, DateTime OccurredAtUtc);
