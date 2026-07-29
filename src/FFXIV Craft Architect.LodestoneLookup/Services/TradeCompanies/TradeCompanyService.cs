using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class TradeCompanyService(ITradeCompanyStore store) : ITradeCompanyService
{
    private const int MaximumPayloadBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<TradeCompanyIdentity?> GetCompanyAsync(
        TradeCompanyAccessContext access,
        CancellationToken cancellationToken = default)
    {
        return store.LoadCompanyAsync(access.CompanyId, cancellationToken);
    }

    public Task<TradeCompanyChangeSet> GetChangesAsync(
        TradeCompanyAccessContext access,
        CompanyRevision afterRevision,
        CancellationToken cancellationToken = default)
    {
        return store.LoadChangesAsync(access.CompanyId, afterRevision, cancellationToken);
    }

    public Task<TradeCompanyMutationResult> MutateAsync(
        TradeCompanyAccessContext access,
        TradeCompanyMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        var rejection = ValidateMutation(access, request);
        return rejection == null
            ? store.ApplyMutationAsync(request, cancellationToken)
            : Task.FromResult(rejection);
    }

    public Task<TradeCompanyPublicationOwnership?> ResolvePublicationOwnershipAsync(
        string publicId,
        CancellationToken cancellationToken = default)
    {
        return store.LoadPublicationOwnershipAsync(publicId, cancellationToken);
    }

    private static TradeCompanyMutationResult? ValidateMutation(
        TradeCompanyAccessContext access,
        TradeCompanyMutationRequest request)
    {
        if (request.CompanyId != access.CompanyId)
        {
            return Rejected("company_scope_mismatch", "The authenticated grant does not own this company.");
        }

        if (access.Role == TradeCompanyRole.ReadOnly)
        {
            return Rejected("company_role_forbidden", "The authenticated grant cannot mutate company records.");
        }

        if (request.ProtocolVersion is < TradeCompanyProtocol.MinimumSupportedVersion or > TradeCompanyProtocol.CurrentVersion)
        {
            return Rejected("unsupported_client_protocol", "The client protocol is not supported by this service.");
        }

        if (!TradeCompanyRecordKinds.All.Contains(request.RecordKind))
        {
            return Rejected("unsupported_record_kind", "The company record kind is not supported.");
        }

        if (!IsSafeIdentifier(request.RecordId, 160))
        {
            return Rejected("invalid_record_id", "The company record ID is invalid.");
        }

        if (!IsSafeIdentifier(request.IdempotencyKey, 160) || request.IdempotencyKey.Length < 12)
        {
            return Rejected("invalid_idempotency_key", "A stable idempotency key is required.");
        }

        if (string.IsNullOrWhiteSpace(request.PayloadJson) ||
            System.Text.Encoding.UTF8.GetByteCount(request.PayloadJson) > MaximumPayloadBytes)
        {
            return Rejected("invalid_payload", "The company record payload is empty or too large.");
        }

        try
        {
            using var payload = JsonDocument.Parse(request.PayloadJson);
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Rejected("invalid_payload", "Company record payloads must be JSON objects.");
            }
        }
        catch (JsonException)
        {
            return Rejected("invalid_payload", "The company record payload is not valid JSON.");
        }

        if (request.RecordKind == TradeCompanyRecordKinds.Publication)
        {
            TradeCompanyPublicationOwnership? ownership;
            try
            {
                ownership = JsonSerializer.Deserialize<TradeCompanyPublicationOwnership>(
                    request.PayloadJson,
                    JsonOptions);
            }
            catch (JsonException)
            {
                ownership = null;
            }

            if (ownership == null ||
                ownership.CompanyId != request.CompanyId ||
                ownership.OrderId == Guid.Empty ||
                ownership.OrderRevision.Value <= 0)
            {
                return Rejected(
                    "invalid_publication_ownership",
                    "Publication ownership must name this company and a revisioned Trade order.");
            }
        }

        return null;
    }

    private static bool IsSafeIdentifier(string value, int maximumLength)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Length <= maximumLength &&
            value.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_' or '.' or ':');
    }

    private static TradeCompanyMutationResult Rejected(string code, string message) =>
        new(TradeCompanyMutationStatus.Rejected, null, ErrorCode: code, ErrorMessage: message);
}
