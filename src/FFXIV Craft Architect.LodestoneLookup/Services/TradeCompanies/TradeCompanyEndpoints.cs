using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public static class TradeCompanyEndpoints
{
    private const string AccessKeyHeader = "X-Trade-Company-Key";
    private const string ProvisioningKeyHeader = "X-Trade-Company-Provisioning-Key";

    public static RouteGroupBuilder MapTradeCompanyEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/trade-company/v1");

        group.MapGet(
            "/meta",
            async (
                TradeCompanyOptions options,
                SqliteTradeCompanyStore store,
                CancellationToken cancellationToken) =>
            {
                var schemaVersion = options.IsReady
                    ? await store.GetSchemaVersionAsync(cancellationToken)
                    : 0;
                return Results.Ok(new TradeCompanyMetaResponse(
                    "FFXIV Craft Architect Trade Company",
                    options.EnvironmentId,
                    options.IsReady,
                    TradeCompanyProtocol.MinimumSupportedVersion,
                    TradeCompanyProtocol.CurrentVersion,
                    schemaVersion));
            });

        group.MapPost(
            "/companies",
            async (
                TradeCompanyCreateRequest createRequest,
                HttpRequest request,
                TradeCompanyOptions options,
                SqliteTradeCompanyStore store,
                TradeCompanyAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                if (!options.CanProvision)
                {
                    return Results.NotFound();
                }

                if (!ProvisioningKeyMatches(request, options.ProvisioningKey))
                {
                    return Results.Unauthorized();
                }

                if (!IsSupportedProtocol(createRequest.ProtocolVersion))
                {
                    return UpgradeRequired();
                }

                var displayName = createRequest.DisplayName?.Trim() ?? string.Empty;
                if (displayName.Length is < 1 or > 120)
                {
                    return Results.BadRequest(new TradeCompanyProblem(
                        "invalid_company_name",
                        "Company name must contain between 1 and 120 characters."));
                }

                var key = hasher.CreateAccessKey();
                var provisioned = await store.CreateCompanyAsync(
                    displayName,
                    key.StoredHash,
                    cancellationToken);
                return Results.Created(
                    $"/trade-company/v1/companies/{provisioned.Company.CompanyId}",
                    new TradeCompanyProvisionResponse(
                        provisioned.Company,
                        provisioned.OwnerGrant,
                        key.PlaintextKey));
            });

        group.MapGet(
            "/companies/{companyId}",
            async (
                string companyId,
                HttpRequest request,
                TradeCompanyOptions options,
                SqliteTradeCompanyStore store,
                ITradeCompanyService service,
                CancellationToken cancellationToken) =>
            {
                var authorization = await AuthorizeAsync(
                    companyId,
                    request,
                    options,
                    store,
                    cancellationToken);
                if (authorization.Error != null)
                {
                    return authorization.Error;
                }

                var company = await service.GetCompanyAsync(authorization.Access!, cancellationToken);
                return company == null ? Results.NotFound() : Results.Ok(company);
            });

        group.MapGet(
            "/companies/{companyId}/session",
            async (
                string companyId,
                HttpRequest request,
                TradeCompanyOptions options,
                SqliteTradeCompanyStore store,
                ITradeCompanyService service,
                CancellationToken cancellationToken) =>
            {
                var authorization = await AuthorizeAsync(
                    companyId,
                    request,
                    options,
                    store,
                    cancellationToken);
                if (authorization.Error != null)
                {
                    return authorization.Error;
                }

                var company = await service.GetCompanyAsync(
                    authorization.Access!,
                    cancellationToken);
                return company == null
                    ? Results.NotFound()
                    : Results.Ok(new TradeCompanySessionResponse(
                        company,
                        authorization.Access!));
            });

        group.MapGet(
            "/companies/{companyId}/changes",
            async (
                string companyId,
                long? afterRevision,
                HttpRequest request,
                TradeCompanyOptions options,
                SqliteTradeCompanyStore store,
                ITradeCompanyService service,
                CancellationToken cancellationToken) =>
            {
                var authorization = await AuthorizeAsync(
                    companyId,
                    request,
                    options,
                    store,
                    cancellationToken);
                if (authorization.Error != null)
                {
                    return authorization.Error;
                }

                if (afterRevision is < 0)
                {
                    return Results.BadRequest(new TradeCompanyProblem(
                        "invalid_revision",
                        "The company revision cannot be negative."));
                }

                var changes = await service.GetChangesAsync(
                    authorization.Access!,
                    new CompanyRevision(afterRevision ?? 0),
                    cancellationToken);
                return Results.Ok(changes);
            });

        group.MapPut(
            "/companies/{companyId}/records/{recordKind}/{recordId}",
            async (
                string companyId,
                string recordKind,
                string recordId,
                TradeCompanyRecordPutRequest putRequest,
                HttpRequest request,
                TradeCompanyOptions options,
                SqliteTradeCompanyStore store,
                ITradeCompanyService service,
                CancellationToken cancellationToken) =>
            {
                var authorization = await AuthorizeAsync(
                    companyId,
                    request,
                    options,
                    store,
                    cancellationToken);
                if (authorization.Error != null)
                {
                    return authorization.Error;
                }

                var mutation = new TradeCompanyMutationRequest(
                    authorization.Access!.CompanyId,
                    recordKind,
                    recordId,
                    putRequest.PayloadJson,
                    putRequest.ExpectedRecordRevision,
                    putRequest.ExpectedCompanyRevision,
                    putRequest.IdempotencyKey,
                    putRequest.ProtocolVersion);
                var result = await service.MutateAsync(
                    authorization.Access,
                    mutation,
                    cancellationToken);
                return result.Status switch
                {
                    TradeCompanyMutationStatus.Applied or TradeCompanyMutationStatus.Replayed =>
                        Results.Ok(result),
                    TradeCompanyMutationStatus.Conflict =>
                        Results.Conflict(result),
                    _ when result.ErrorCode == "unsupported_client_protocol" =>
                        UpgradeRequired(),
                    _ when result.ErrorCode == "company_role_forbidden" =>
                        Results.StatusCode(StatusCodes.Status403Forbidden),
                    _ => Results.BadRequest(result)
                };
            });

        group.MapGet(
            "/companies/{companyId}/grants",
            async (
                string companyId,
                HttpRequest request,
                TradeCompanyOptions options,
                SqliteTradeCompanyStore store,
                CancellationToken cancellationToken) =>
            {
                var authorization = await AuthorizeOwnerAsync(
                    companyId,
                    request,
                    options,
                    store,
                    cancellationToken);
                if (authorization.Error != null)
                {
                    return authorization.Error;
                }

                return Results.Ok(await store.LoadGrantsAsync(
                    authorization.Access!.CompanyId,
                    cancellationToken));
            });

        group.MapPost(
            "/companies/{companyId}/grants",
            async (
                string companyId,
                TradeCompanyGrantCreateRequest createRequest,
                HttpRequest request,
                TradeCompanyOptions options,
                SqliteTradeCompanyStore store,
                TradeCompanyAccessKeyHasher hasher,
                CancellationToken cancellationToken) =>
            {
                var authorization = await AuthorizeOwnerAsync(
                    companyId,
                    request,
                    options,
                    store,
                    cancellationToken);
                if (authorization.Error != null)
                {
                    return authorization.Error;
                }

                if (!IsSupportedProtocol(createRequest.ProtocolVersion))
                {
                    return UpgradeRequired();
                }

                if (!Enum.IsDefined(createRequest.Role))
                {
                    return Results.BadRequest(new TradeCompanyProblem(
                        "invalid_company_role",
                        "The requested company role is invalid."));
                }

                var key = hasher.CreateAccessKey();
                var grant = await store.CreateGrantAsync(
                    authorization.Access!.CompanyId,
                    createRequest.Role,
                    key.StoredHash,
                    cancellationToken);
                return Results.Ok(new TradeCompanyGrantCreateResponse(grant, key.PlaintextKey));
            });

        group.MapDelete(
            "/companies/{companyId}/grants/{grantId:guid}",
            async (
                string companyId,
                Guid grantId,
                HttpRequest request,
                TradeCompanyOptions options,
                SqliteTradeCompanyStore store,
                CancellationToken cancellationToken) =>
            {
                var authorization = await AuthorizeOwnerAsync(
                    companyId,
                    request,
                    options,
                    store,
                    cancellationToken);
                if (authorization.Error != null)
                {
                    return authorization.Error;
                }

                var status = await store.RevokeGrantAsync(
                    authorization.Access!.CompanyId,
                    grantId,
                    cancellationToken);
                return status switch
                {
                    TradeCompanyGrantRevokeStatus.Revoked => Results.NoContent(),
                    TradeCompanyGrantRevokeStatus.LastOwner => Results.Conflict(new TradeCompanyProblem(
                        "last_owner_grant",
                        "The final active owner grant cannot be revoked.")),
                    _ => Results.NotFound()
                };
            });

        return group;
    }

    private static async Task<(TradeCompanyAccessContext? Access, IResult? Error)> AuthorizeOwnerAsync(
        string companyId,
        HttpRequest request,
        TradeCompanyOptions options,
        SqliteTradeCompanyStore store,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(
            companyId,
            request,
            options,
            store,
            cancellationToken);
        if (authorization.Error != null)
        {
            return authorization;
        }

        return authorization.Access!.Role == TradeCompanyRole.Owner
            ? authorization
            : (null, Results.StatusCode(StatusCodes.Status403Forbidden));
    }

    private static async Task<(TradeCompanyAccessContext? Access, IResult? Error)> AuthorizeAsync(
        string companyId,
        HttpRequest request,
        TradeCompanyOptions options,
        SqliteTradeCompanyStore store,
        CancellationToken cancellationToken)
    {
        if (!options.IsReady)
        {
            return (null, Results.NotFound());
        }

        if (!CompanyId.TryParse(companyId, out var parsedCompanyId))
        {
            return (null, Results.BadRequest(new TradeCompanyProblem(
                "invalid_company_id",
                "The company ID is invalid.")));
        }

        var key = request.Headers[AccessKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return (null, Results.Unauthorized());
        }

        var access = await store.AuthenticateAsync(key, cancellationToken);
        if (access == null)
        {
            return (null, Results.Unauthorized());
        }

        return access.CompanyId == parsedCompanyId
            ? (access, null)
            : (null, Results.NotFound());
    }

    private static bool ProvisioningKeyMatches(HttpRequest request, string expectedKey)
    {
        var suppliedKey = request.Headers[ProvisioningKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(suppliedKey) || string.IsNullOrWhiteSpace(expectedKey))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey)));
    }

    private static bool IsSupportedProtocol(int version) =>
        version is >= TradeCompanyProtocol.MinimumSupportedVersion and <= TradeCompanyProtocol.CurrentVersion;

    private static IResult UpgradeRequired() =>
        Results.Json(
            new TradeCompanyProblem(
                "unsupported_client_protocol",
                "The client protocol is not supported by this service."),
            statusCode: StatusCodes.Status426UpgradeRequired);
}
