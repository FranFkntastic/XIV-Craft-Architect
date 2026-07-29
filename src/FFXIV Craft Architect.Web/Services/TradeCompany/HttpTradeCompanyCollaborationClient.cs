using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.BrowserPersistence;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public interface ITradeCompanyCollaborationClient
{
    Task<IReadOnlyList<TradeCommissionInterest>> LoadPendingInterestsAsync(
        CompanyId companyId,
        Guid orderId,
        CancellationToken cancellationToken = default);

    Task<TradeCommissionPublicationProjection?> LoadPublicationAsync(
        CompanyId companyId,
        Guid orderId,
        CancellationToken cancellationToken = default);

    Task<TradeCommissionPublicationProjection> PublishAsync(
        CompanyId companyId,
        Guid orderId,
        CompanyRecordRevision orderRevision,
        CommissionBriefDocument brief,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<TradeCommissionInterestResolutionReceipt> AcceptAsync(
        CompanyId companyId,
        string claimId,
        Guid crafterId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<TradeCommissionInterestResolutionReceipt> DeclineAsync(
        CompanyId companyId,
        string claimId,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(
        CompanyId companyId,
        string publicId,
        CancellationToken cancellationToken = default);
}

internal sealed class HttpTradeCompanyCollaborationClient(
    HttpClient http,
    TradeCompanyConnectionStore connections) : ITradeCompanyCollaborationClient
{
    private const string AccessKeyHeader = "X-Trade-Company-Key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<TradeCommissionInterest>> LoadPendingInterestsAsync(
        CompanyId companyId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Get,
            companyId,
            $"trade/v1/companies/{companyId}/discord/claims?orderId={orderId:D}",
            cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var claims = await response.Content.ReadFromJsonAsync<DiscordInterestClaimDto[]>(
            JsonOptions,
            cancellationToken) ?? [];
        return claims.Select(ToInterest).ToArray();
    }

    public async Task<TradeCommissionPublicationProjection?> LoadPublicationAsync(
        CompanyId companyId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Get,
            companyId,
            $"trade/v1/companies/{companyId}/discord/publications?orderId={orderId:D}",
            cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var publication = await response.Content.ReadFromJsonAsync<DiscordPublicationDto>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The Discord publication endpoint returned an empty response.");
        return ToPublication(publication);
    }

    public async Task<TradeCommissionPublicationProjection> PublishAsync(
        CompanyId companyId,
        Guid orderId,
        CompanyRecordRevision orderRevision,
        CommissionBriefDocument brief,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            companyId,
            $"trade/v1/companies/{companyId}/discord/publications",
            cancellationToken);
        request.Content = JsonContent.Create(
            new DiscordCreatePublicationBody(
                orderId,
                orderRevision,
                brief,
                idempotencyKey),
            options: JsonOptions);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureCollaborationSuccessAsync(response, cancellationToken);
        var publication = await response.Content.ReadFromJsonAsync<DiscordPublicationDto>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The Discord publication endpoint returned an empty response.");
        return ToPublication(publication);
    }

    public async Task<TradeCommissionInterestResolutionReceipt> AcceptAsync(
        CompanyId companyId,
        string claimId,
        Guid crafterId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var parsedClaimId = ParseClaimId(claimId);
        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            companyId,
            $"trade/v1/companies/{companyId}/discord/claims/{parsedClaimId:D}/accept",
            cancellationToken);
        request.Content = JsonContent.Create(
            new DiscordAcceptInterestBody(crafterId, idempotencyKey),
            options: JsonOptions);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureCollaborationSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<DiscordClaimResultDto>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The Discord claim endpoint returned an empty response.");
        return ToReceipt(result, accepted: true);
    }

    public async Task<TradeCommissionInterestResolutionReceipt> DeclineAsync(
        CompanyId companyId,
        string claimId,
        CancellationToken cancellationToken = default)
    {
        var parsedClaimId = ParseClaimId(claimId);
        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            companyId,
            $"trade/v1/companies/{companyId}/discord/claims/{parsedClaimId:D}/decline",
            cancellationToken);
        request.Content = JsonContent.Create(
            new DiscordDeclineInterestBody($"discord-decline:{parsedClaimId:D}"),
            options: JsonOptions);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureCollaborationSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<DiscordClaimResultDto>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The Discord claim endpoint returned an empty response.");
        return ToReceipt(result, accepted: false);
    }

    public async Task RevokeAsync(
        CompanyId companyId,
        string publicId,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Delete,
            companyId,
            $"trade/v1/companies/{companyId}/discord/publications/" +
            Uri.EscapeDataString(publicId),
            cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureCollaborationSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        CompanyId companyId,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var connection = await connections.LoadAsync(companyId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Connect this browser to the Trade Company before using Discord collaboration.");
        var baseUri = new Uri(connection.ServiceUrl.Trim().TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        request.Headers.Add(AccessKeyHeader, connection.AccessKey);
        return request;
    }

    private static async Task EnsureCollaborationSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var problem = await response.Content.ReadFromJsonAsync<DiscordProblemDto>(
            JsonOptions,
            cancellationToken);
        throw new InvalidOperationException(
            problem?.Message ??
            $"Discord collaboration failed with HTTP {(int)response.StatusCode}.");
    }

    private static Guid ParseClaimId(string claimId) =>
        Guid.TryParse(claimId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidOperationException("The Discord claim ID is invalid.");

    private static TradeCommissionInterest ToInterest(DiscordInterestClaimDto claim) =>
        new(
            claim.ClaimId.ToString("D"),
            claim.OrderId,
            claim.DiscordUserId,
            claim.DiscordDisplayName,
            claim.State switch
            {
                DiscordInterestStateDto.Accepted => TradeCommissionInterestState.Accepted,
                DiscordInterestStateDto.Declined => TradeCommissionInterestState.Declined,
                DiscordInterestStateDto.Withdrawn => TradeCommissionInterestState.Withdrawn,
                DiscordInterestStateDto.Superseded => TradeCommissionInterestState.Superseded,
                _ => TradeCommissionInterestState.Pending
            },
            claim.ResolvedCrafterId,
            claim.CreatedAt.UtcDateTime);

    private static TradeCommissionPublicationProjection ToPublication(
        DiscordPublicationDto publication) =>
        new(
            publication.OrderId,
            TradeCommissionDestination.DiscordChannel,
            Enum.TryParse<TradeCommissionDeliveryState>(
                publication.State,
                ignoreCase: true,
                out var state)
                ? state
                : TradeCommissionDeliveryState.Failed,
            publication.PublicId,
            publication.DestinationLabel,
            publication.PublishedAtUtc,
            publication.Message);

    private static TradeCommissionInterestResolutionReceipt ToReceipt(
        DiscordClaimResultDto result,
        bool accepted)
    {
        var claim = result.Claim
            ?? throw new InvalidOperationException(
                result.Error ?? "The Discord claim response did not include a claim.");
        TradeOrder? order = null;
        if (!string.IsNullOrWhiteSpace(result.OrderMutation?.Record?.PayloadJson))
        {
            order = JsonSerializer.Deserialize<TradeOrder>(
                result.OrderMutation.Record.PayloadJson,
                JsonOptions);
        }

        return new TradeCommissionInterestResolutionReceipt(
            ToInterest(claim) with
            {
                State = accepted
                    ? TradeCommissionInterestState.Accepted
                    : TradeCommissionInterestState.Declined
            },
            order,
            result.Error);
    }

    private sealed record DiscordCreatePublicationBody(
        Guid OrderId,
        CompanyRecordRevision OrderRevision,
        CommissionBriefDocument Brief,
        string IdempotencyKey);

    private sealed record DiscordAcceptInterestBody(
        Guid CrafterId,
        string IdempotencyKey);

    private sealed record DiscordDeclineInterestBody(string IdempotencyKey);

    private sealed record DiscordPublicationDto(
        Guid OrderId,
        string PublicId,
        int Version,
        DateTime PublishedAtUtc,
        string State,
        string DestinationLabel,
        string? Message);

    private enum DiscordInterestStateDto
    {
        Pending,
        AssignmentPending,
        Accepted,
        Declined,
        Withdrawn,
        Superseded
    }

    private sealed record DiscordInterestClaimDto(
        Guid ClaimId,
        Guid PublicationId,
        CompanyId CompanyId,
        Guid OrderId,
        string DiscordUserId,
        string DiscordDisplayName,
        DiscordInterestStateDto State,
        Guid? ResolvedCrafterId,
        CompanyRecordRevision? AcceptedOrderRevision,
        string? ResolutionIdempotencyKey,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ResolvedAt);

    private sealed record DiscordClaimResultDto(
        int Status,
        DiscordInterestClaimDto? Claim,
        TradeCompanyMutationResult? OrderMutation,
        string? Error);

    private sealed record DiscordProblemDto(
        string? Error,
        string? Message);
}
