using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCommissionOperationsClient(
    HttpClient http,
    ProfileSyncLocalStateService localState)
{
    private const string AccessKeyHeader = "X-Profile-Key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CompanyCommissionOwnerProjection> LoadOwnerProjectionAsync(
        Guid companyId,
        Guid commissionId,
        CancellationToken cancellationToken = default)
    {
        var connection = await localState.LoadConnectionSettingsAsync();
        return await LoadOwnerProjectionAsync(
            connection,
            companyId,
            commissionId,
            cancellationToken);
    }

    public async Task<CompanyCommissionOwnerProjection> LoadOwnerProjectionAsync(
        HostedProfileConnectionSettings connection,
        Guid companyId,
        Guid commissionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var response = await SendAsync(
            HttpMethod.Get,
            $"trade/v1/companies/{companyId:D}/commissions/{commissionId:D}/owner",
            content: null,
            contentType: null,
            cancellationToken,
            connection);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var problem = await ReadProblemAsync(response, cancellationToken);
            if (string.Equals(
                    problem?.Error,
                    "commission_missing",
                    StringComparison.Ordinal))
            {
                throw new MissingCompanyCommissionOwnerException(
                    companyId,
                    commissionId,
                    problem?.Message ?? problem?.ErrorMessage);
            }

            throw new InvalidOperationException(
                problem?.Message ??
                problem?.ErrorMessage ??
                $"Company commission operations failed with HTTP {(int)response.StatusCode}.");
        }
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new TradeCompanyAuthorizationException(companyId);
        }
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CompanyCommissionOwnerProjection>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The authenticated commission owner endpoint returned an empty projection.");
    }

    public async Task<TradeCommissionOwnerMutationResponse> ExecuteAsync<TCommand>(
        string route,
        TCommand command,
        CancellationToken cancellationToken = default,
        HostedProfileConnectionSettings? capturedConnection = null)
        where TCommand : ICompanyCommissionCommand
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{command.Context.CompanyId}/commissions/" +
            $"{command.Context.CommissionId:D}/commands/{route}",
            command,
            typeof(TCommand),
            cancellationToken,
            capturedConnection);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var problem = await ReadProblemAsync(response, cancellationToken);
            if (string.Equals(
                    problem?.Error,
                    "revision_conflict",
                    StringComparison.Ordinal))
            {
                throw new CompanyCommissionRevisionConflictException(
                    problem?.Message ?? problem?.ErrorMessage);
            }

            throw new InvalidOperationException(
                problem?.Message ??
                problem?.ErrorMessage ??
                $"Company commission operations failed with HTTP {(int)response.StatusCode}.");
        }
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<TradeCommissionOwnerMutationBody>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The commissioner command endpoint returned an empty mutation result.");
        return new TradeCommissionOwnerMutationResponse(
            new CompanyCommissionMutationResult(
                result.Status,
                result.Order,
                result.Activity,
                result.ErrorCode,
                result.ErrorMessage),
            result.Projection,
            result.ClaimUrl);
    }

    public async Task<TradeCommissionRecoveryResetResponse> ResetParticipantRecoveryAsync(
        ResetCompanyCommissionParticipantRecoveryCommand command,
        CancellationToken cancellationToken = default,
        HostedProfileConnectionSettings? capturedConnection = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{command.Context.CompanyId}/commissions/" +
            $"{command.Context.CommissionId:D}/commands/reset-participant-recovery",
            command,
            typeof(ResetCompanyCommissionParticipantRecoveryCommand),
            cancellationToken,
            capturedConnection);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TradeCommissionRecoveryResetResponse>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Participant recovery reset returned an empty response.");
    }

    public async Task<TradeCommissionClaimLinkResponse> IssueClaimLinkAsync(
        CompanyCommissionCommandContext context,
        CancellationToken cancellationToken = default,
        HostedProfileConnectionSettings? capturedConnection = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"trade/v1/companies/{context.CompanyId}/commissions/" +
            $"{context.CommissionId:D}/commands/issue-claim-link",
            new TradeCommissionClaimLinkRequest(context),
            typeof(TradeCommissionClaimLinkRequest),
            cancellationToken,
            capturedConnection);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TradeCommissionClaimLinkResponse>(
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Claim-link issuance returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        object? content,
        Type? contentType,
        CancellationToken cancellationToken,
        HostedProfileConnectionSettings? capturedConnection = null)
    {
        var connection = capturedConnection ??
                         await localState.LoadConnectionSettingsAsync();
        if (!connection.IsConfigured)
        {
            throw new InvalidOperationException(
                "Connect this browser in Settings before operating a company commission.");
        }

        var hostUri = new Uri(connection.HostUrl!.Trim().TrimEnd('/') + "/");
        var apiBaseUri = hostUri.AbsolutePath.TrimEnd('/').EndsWith(
            "/api",
            StringComparison.OrdinalIgnoreCase)
            ? hostUri
            : new Uri(hostUri, "api/");
        var request = new HttpRequestMessage(
            method,
            new Uri(apiBaseUri, relativePath.TrimStart('/')));
        request.Headers.Add(AccessKeyHeader, connection.AccessKey);
        if (content != null)
        {
            request.Content = JsonContent.Create(
                content,
                contentType ?? content.GetType(),
                options: JsonOptions);
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

        var problem = await ReadProblemAsync(response, cancellationToken);

        throw new InvalidOperationException(
            problem?.Message ??
            problem?.ErrorMessage ??
            $"Company commission operations failed with HTTP {(int)response.StatusCode}.");
    }

    private static async Task<CommissionOperationsProblem?> ReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<CommissionOperationsProblem>(
                JsonOptions,
                cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // An unstructured error, including an unknown host route, still fails closed.
            return null;
        }
    }

    private sealed record CommissionOperationsProblem(
        string? Error,
        string? ErrorMessage,
        string? Message);
}

internal sealed class CompanyCommissionRevisionConflictException : InvalidOperationException
{
    public CompanyCommissionRevisionConflictException(string? message = null)
        : base(message ??
            "The hosted commission or company changed before the command was applied.")
    {
    }
}

internal sealed class TradeCompanyAuthorizationException : InvalidOperationException
{
    public TradeCompanyAuthorizationException(Guid companyId)
        : base($"The connected hosted profile is not authorized for Trade company '{companyId:D}'.")
    {
        CompanyId = companyId;
    }

    public Guid CompanyId { get; }
}

public sealed record TradeCommissionRecoveryResetResponse(
    CompanyCommissionMutationResult Mutation,
    CompanyCommissionOwnerProjection Projection,
    string RecoveryUrl);

public sealed record TradeCommissionClaimLinkRequest(
    CompanyCommissionCommandContext Context);

public sealed record TradeCommissionClaimLinkResponse(string ClaimUrl);

public sealed record TradeCommissionOwnerMutationResponse(
    CompanyCommissionMutationResult Mutation,
    CompanyCommissionOwnerProjection? Projection,
    string? ClaimUrl);

internal sealed record TradeCommissionOwnerMutationBody(
    CompanyCommissionMutationStatus Status,
    TradeOrder? Order,
    CompanyCommissionActivityEvent? Activity,
    string? ErrorCode,
    string? ErrorMessage,
    CompanyCommissionOwnerProjection? Projection,
    string? ClaimUrl);
