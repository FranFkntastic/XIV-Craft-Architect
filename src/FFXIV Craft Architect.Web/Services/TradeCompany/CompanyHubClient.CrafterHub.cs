using System.Net;
using System.Net.Http.Json;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed partial class CompanyHubClient
{
    public async Task<CrafterHubProjection?> LoadCrafterHubAsync(
        string? companyId = null,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(companyId)
            ? "trade/v1/crafter-hub"
            : $"trade/v1/crafter-hub?company={Uri.EscapeDataString(companyId)}";
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            return null;
        }
        await EnsureHubSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CrafterHubProjection>(JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Whether the signed-in account operates any hosted company; null when the host
    /// cannot say (signed out or unreachable).
    /// </summary>
    public async Task<bool?> LoadCrafterHubAccessAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "trade/v1/crafter-hub/access", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        return (await response.Content.ReadFromJsonAsync<CrafterHubAccess>(JsonOptions, cancellationToken))?.CanOperate;
    }

    public async Task AcceptCrafterHubInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/crafter-hub/invitations/{invitationId:D}/accept",
            null,
            cancellationToken);
        await EnsureHubSuccessAsync(response, cancellationToken);
    }

    public async Task DeclineCrafterHubInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/crafter-hub/invitations/{invitationId:D}/decline",
            null,
            cancellationToken);
        await EnsureHubSuccessAsync(response, cancellationToken);
    }
}

public sealed record CrafterHubProjection(
    bool CanOperate,
    IReadOnlyList<CrafterHubCompany> Companies,
    IReadOnlyList<CrafterHubWork> MyWork,
    IReadOnlyList<CrafterHubOpenCommission> OpenCommissions,
    IReadOnlyList<CrafterHubUpdate> Updates);

public sealed record CrafterHubCompany(
    string CompanyId,
    string Slug,
    string DisplayName,
    string Accent,
    string? Role,
    bool IsFreelancer,
    bool MembershipRequested,
    bool CanRequestMembership,
    Guid? InvitationId);

public sealed record CrafterHubWork(
    string CompanyId,
    string CompanyName,
    string Status,
    bool IsFreelance,
    string? WorkspaceUrl,
    CompanyHubCommission Commission);

public sealed record CrafterHubOpenCommission(
    string CompanyId,
    string CompanyName,
    string? MaterialsLabel,
    string? PublicUrl,
    CompanyHubCommission Commission);

public sealed record CrafterHubUpdate(
    string CompanyId,
    string CompanyName,
    string Kind,
    string? CommissionId,
    string? CommissionTitle,
    string Text,
    DateTime CreatedAtUtc,
    bool IsNew);

public sealed record CrafterHubAccess(bool CanOperate);
