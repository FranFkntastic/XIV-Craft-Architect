using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCompanyCollaborationClient(
    HttpClient http,
    ProfileSyncLocalStateService localState)
{
    private const string AccessKeyHeader = "X-Profile-Key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<TradeCommissionInterest>> LoadPendingInterestsAsync(
        Guid companyProfileId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"trade/v1/companies/{companyProfileId:D}/discord/claims?orderId={orderId:D}",
            content: null,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var claims = await response.Content.ReadFromJsonAsync<DiscordInterestClaimDto[]>(
            JsonOptions,
            cancellationToken) ?? [];
        return claims.Select(ToInterest).ToArray();
    }

    public async Task<TradeCommissionPublicationProjection?> LoadPublicationAsync(
        Guid companyProfileId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"trade/v1/companies/{companyProfileId:D}/discord/publications?orderId={orderId:D}",
            content: null,
            cancellationToken);
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

    public async Task<TradeDiscordNotificationRoute?> LoadNotificationRouteAsync(
        Guid companyProfileId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"trade/v1/companies/{companyProfileId:D}/discord/notifications/route",
            content: null,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        var route = await response.Content.ReadFromJsonAsync<DiscordNotificationRouteDto>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The Discord notification route endpoint returned an empty response.");
        return ToNotificationRoute(route);
    }

    public async Task<TradeDiscordNotificationRouteSaveResult> SaveNotificationRouteAsync(
        Guid companyProfileId,
        TradeDiscordNotificationRouteUpdate update,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            $"trade/v1/companies/{companyProfileId:D}/discord/notifications/route",
            update,
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var route = await response.Content.ReadFromJsonAsync<DiscordNotificationRouteDto>(
                JsonOptions,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The Discord notification route endpoint returned an empty response.");
            return new TradeDiscordNotificationRouteSaveResult(
                TradeDiscordNotificationRouteSaveStatus.Saved,
                ToNotificationRoute(route));
        }

        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
        {
            var problem =
                await response.Content.ReadFromJsonAsync<DiscordNotificationRouteProblemDto>(
                    JsonOptions,
                    cancellationToken);
            return new TradeDiscordNotificationRouteSaveResult(
                response.StatusCode == HttpStatusCode.Conflict
                    ? TradeDiscordNotificationRouteSaveStatus.Conflict
                    : TradeDiscordNotificationRouteSaveStatus.Invalid,
                problem?.Current == null
                    ? null
                    : ToNotificationRoute(problem.Current),
                problem?.Message);
        }

        await EnsureSuccessAsync(response, cancellationToken);
        throw new InvalidOperationException(
            "Discord notification route save returned an unexpected response.");
    }

    public async Task<TradeCommissionPublicationProjection> PublishAsync(
        Guid companyProfileId,
        Guid orderId,
        long orderRevision,
        CommissionBriefDocument brief,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{companyProfileId:D}/discord/publications",
            new DiscordCreatePublicationBody(
                orderId,
                new CompanyRecordRevision(orderRevision),
                brief,
                idempotencyKey),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var publication = await response.Content.ReadFromJsonAsync<DiscordPublicationDto>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The Discord publication endpoint returned an empty response.");
        return ToPublication(publication);
    }

    public async Task<TradeCommissionPublicationProjection> RetryPublicationAsync(
        Guid companyProfileId,
        string publicId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{companyProfileId:D}/discord/publications/{Uri.EscapeDataString(publicId)}/retry",
            content: null,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var publication = await response.Content.ReadFromJsonAsync<DiscordPublicationDto>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The Discord retry endpoint returned an empty response.");
        return ToPublication(publication);
    }

    public async Task<TradeCompanyPortablePublication> PublishPortableLinkAsync(
        Guid companyProfileId,
        Guid orderId,
        long orderRevision,
        CommissionBriefDocument brief,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{companyProfileId:D}/commission-briefs",
            new CompanyCommissionBriefCreateRequest
            {
                OrderId = orderId,
                OrderRevision = new CompanyRecordRevision(orderRevision),
                Brief = brief,
                IdempotencyKey = idempotencyKey
            },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var published = await response.Content.ReadFromJsonAsync<CommissionBriefCreateResponse>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Portable commission publication returned an empty response.");
        return new TradeCompanyPortablePublication(
            CommissionBriefClient.CreatePortableLink(published),
            published.OrderRecord ??
            throw new InvalidOperationException(
                "Portable commission publication did not return its authoritative Trade order."));
    }

    public async Task<PortableCommissionLink> ResolvePortableLinkAsync(
        string publicId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        using var response = await SendAsync(
            HttpMethod.Get,
            $"xivdata/commission-briefs/{Uri.EscapeDataString(publicId)}/link",
            content: null,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var link = await response.Content.ReadFromJsonAsync<CommissionBriefLinkResponse>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Commission link resolution returned an empty response.");
        return CommissionBriefClient.CreatePortableLink(
            link.PublicId,
            link.PublicUrl,
            link.Version,
            link.PublishedAtUtc);
    }

    public async Task RevokePortableLinkAsync(
        Guid companyProfileId,
        string publicId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Delete,
            $"trade/v1/companies/{companyProfileId:D}/commission-briefs/" +
            Uri.EscapeDataString(publicId),
            content: null,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<TradeCommissionInterestResolutionReceipt> AcceptAsync(
        Guid companyProfileId,
        string claimId,
        Guid crafterId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var parsedClaimId = ParseClaimId(claimId);
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{companyProfileId:D}/discord/claims/{parsedClaimId:D}/accept",
            new DiscordAcceptInterestBody(crafterId, idempotencyKey),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadReceiptAsync(response, accepted: true, cancellationToken);
    }

    public async Task<TradeCommissionInterestResolutionReceipt> DeclineAsync(
        Guid companyProfileId,
        string claimId,
        CancellationToken cancellationToken = default)
    {
        var parsedClaimId = ParseClaimId(claimId);
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{companyProfileId:D}/discord/claims/{parsedClaimId:D}/decline",
            new DiscordDeclineInterestBody($"discord-decline:{parsedClaimId:D}"),
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadReceiptAsync(response, accepted: false, cancellationToken);
    }

    public async Task RevokeAsync(
        Guid companyProfileId,
        string publicId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Delete,
            $"trade/v1/companies/{companyProfileId:D}/discord/publications/" +
            Uri.EscapeDataString(publicId),
            content: null,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        object? content,
        CancellationToken cancellationToken)
    {
        var connection = await localState.LoadConnectionSettingsAsync();
        if (!connection.IsConfigured)
        {
            throw new InvalidOperationException(
                "Connect Profile Hosting in Options before using Discord collaboration.");
        }

        var baseUri = new Uri(connection.HostUrl!.Trim().TrimEnd('/') + "/");
        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        request.Headers.Add(AccessKeyHeader, connection.AccessKey);
        if (content != null)
        {
            request.Content = JsonContent.Create(content, options: JsonOptions);
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

    private static async Task EnsureSuccessAsync(
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

    private static async Task<TradeCommissionInterestResolutionReceipt> ReadReceiptAsync(
        HttpResponseMessage response,
        bool accepted,
        CancellationToken cancellationToken)
    {
        var result = await response.Content.ReadFromJsonAsync<DiscordClaimResultDto>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The Discord claim endpoint returned an empty response.");
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
            result.OrderMutation?.Record?.RecordRevision.Value,
            result.Error);
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

    private static TradeDiscordNotificationRoute ToNotificationRoute(
        DiscordNotificationRouteDto route) =>
        new(
            route.CommissionerDiscordUserId,
            route.DestinationMode,
            route.UpdateChannelId,
            route.DirectMessageFallback,
            route.RoutineBehavior,
            route.ActionRequiredBehavior,
            route.CriticalExceptionBehavior,
            route.Revision);

    private sealed record DiscordCreatePublicationBody(
        Guid OrderId,
        CompanyRecordRevision OrderRevision,
        CommissionBriefDocument Brief,
        string IdempotencyKey);

    private sealed record DiscordAcceptInterestBody(Guid CrafterId, string IdempotencyKey);
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

    private sealed record DiscordProblemDto(string? Error, string? Message);
    private sealed record DiscordNotificationRouteDto(
        CompanyId CompanyId,
        string CommissionerDiscordUserId,
        TradeDiscordNotificationDestinationMode DestinationMode,
        string? UpdateChannelId,
        TradeDiscordDirectMessageFallback DirectMessageFallback,
        TradeDiscordNotificationBehavior RoutineBehavior,
        TradeDiscordNotificationBehavior ActionRequiredBehavior,
        TradeDiscordNotificationBehavior CriticalExceptionBehavior,
        long Revision,
        DateTimeOffset UpdatedAt);

    private sealed record DiscordNotificationRouteProblemDto(
        string? Error,
        string? Message,
        DiscordNotificationRouteDto? Current);
}

public sealed record TradeCompanyPortablePublication(
    PortableCommissionLink Link,
    TradeCompanyRecordEnvelope OrderRecord);
