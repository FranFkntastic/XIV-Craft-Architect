using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class ProfileHostedTradeCompanyService(
    SqliteProfileHostStore profiles,
    ProfileAccessKeyHasher accessKeyHasher)
{
    private const string CompanyCollectionPrefix = "tradeCompany.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<TradeCompanyAccessContext?> AuthenticateAsync(
        string plaintextKey,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        var host = await profiles.AuthenticateAsync(
            plaintextKey,
            accessKeyHasher,
            cancellationToken);
        if (host == null ||
            !Guid.TryParse(host.ProfileId, out var hostProfileId) ||
            await LoadCompanyProfileAsync(host.ProfileId, companyId, cancellationToken) == null)
        {
            return null;
        }

        return new TradeCompanyAccessContext(
            companyId,
            hostProfileId,
            TradeCompanyRole.Owner,
            hostProfileId);
    }

    public async Task<TradeCompanyRecordEnvelope?> LoadRecordAsync(
        TradeCompanyAccessContext access,
        string recordKind,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        var hostProfileId = RequireHostProfile(access);
        var stored = await profiles.LoadObjectAsync(
            hostProfileId,
            ToCollection(recordKind),
            ToStoredObjectId(access.CompanyId, recordKind, recordId),
            cancellationToken);
        return stored is { Deleted: false } &&
            PayloadBelongsToCompany(access.CompanyId, recordKind, stored.PayloadJson)
                ? ToRecord(access.CompanyId, recordKind, recordId, stored)
                : null;
    }

    public async Task<TradeCompanyMutationResult> PutRecordAsync(
        TradeCompanyAccessContext access,
        string recordKind,
        string recordId,
        string payloadJson,
        CompanyRecordRevision expectedRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default,
        CompanyRecordRevision? expectedCompanyRevision = null)
    {
        var hostProfileId = RequireHostProfile(access);
        if (!TradeCompanyRecordKinds.All.Contains(recordKind) ||
            string.IsNullOrWhiteSpace(idempotencyKey) ||
            !PayloadBelongsToCompany(access.CompanyId, recordKind, payloadJson))
        {
            return Rejected(
                "company_scope_mismatch",
                "The record is invalid or does not belong to the authenticated company.");
        }

        var collection = ToCollection(recordKind);
        var put = await profiles.PutObjectAsync(
            hostProfileId,
            collection,
            ToStoredObjectId(access.CompanyId, recordKind, recordId),
            payloadJson,
            expectedRevision.Value,
            cancellationToken,
            allowCompanyCollection: collection.StartsWith(
                CompanyCollectionPrefix,
                StringComparison.Ordinal),
            expectedServerRevision: expectedCompanyRevision?.Value);
        var current = put.Object ?? put.RemoteObject;
        if (put.Success && current != null)
        {
            return new TradeCompanyMutationResult(
                TradeCompanyMutationStatus.Applied,
                ToRecord(access.CompanyId, recordKind, recordId, current));
        }

        if (current != null &&
            string.Equals(current.PayloadJson, payloadJson, StringComparison.Ordinal))
        {
            return new TradeCompanyMutationResult(
                TradeCompanyMutationStatus.Replayed,
                ToRecord(access.CompanyId, recordKind, recordId, current));
        }

        return new TradeCompanyMutationResult(
            TradeCompanyMutationStatus.Conflict,
            null,
            current == null
                ? null
                : ToRecord(access.CompanyId, recordKind, recordId, current),
            put.ErrorCode ?? "revision_conflict",
            put.ErrorMessage ?? "The hosted Trade record changed.");
    }

    public async Task<TradeCompanyPublicationOwnership?> ResolvePublicationOwnershipAsync(
        string publicId,
        CancellationToken cancellationToken = default)
    {
        var found = await profiles.FindObjectAsync(
            ToCollection(TradeCompanyRecordKinds.Publication),
            publicId,
            cancellationToken);
        return found == null ? null : DeserializeOwnership(found.Object.PayloadJson);
    }

    public async Task<CompanyRecordRevision> LoadCompanyRevisionAsync(
        TradeCompanyAccessContext access,
        CancellationToken cancellationToken = default)
    {
        var profileId = RequireHostProfile(access);
        var profile = await profiles.LoadProfileAsync(profileId, cancellationToken)
            ?? throw new UnauthorizedAccessException(
                "The hosted company profile is unavailable.");
        return new CompanyRecordRevision(profile.ServerRevision);
    }

    public async Task<TradeCompanyProfile?> LoadCompanyProfileAsync(
        TradeCompanyAccessContext access,
        CancellationToken cancellationToken = default) =>
        await LoadCompanyProfileAsync(
            RequireHostProfile(access),
            access.CompanyId,
            cancellationToken);

    public async Task<(TradeCompanyRecordEnvelope Envelope, TradeOrder Order)?> LoadPublicOrderAsync(
        TradeCompanyPublicationOwnership ownership,
        CancellationToken cancellationToken = default)
    {
        var found = await profiles.FindObjectAsync(
            ProfileSyncCollections.TradeOrders,
            ownership.OrderId.ToString("D"),
            cancellationToken);
        if (found is not { Object.Deleted: false })
        {
            return null;
        }

        try
        {
            var order = JsonSerializer.Deserialize<TradeOrder>(
                found.Object.PayloadJson,
                JsonOptions);
            if (!OrderBelongsToCompany(order, ownership.CompanyId) ||
                order!.CommissionPublication?.Ownership != ownership)
            {
                return null;
            }

            return (
                ToRecord(
                    ownership.CompanyId,
                    TradeCompanyRecordKinds.Order,
                    ownership.OrderId.ToString("D"),
                    found.Object),
                order);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<TradeCompanyProfile?> LoadPublicCompanyProfileAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        var found = await profiles.FindObjectAsync(
            ProfileSyncCollections.TradeCompanyProfiles,
            companyId.ToString(),
            cancellationToken);
        if (found is not { Object.Deleted: false })
        {
            return null;
        }

        try
        {
            var profile = JsonSerializer.Deserialize<TradeCompanyProfile>(
                found.Object.PayloadJson,
                JsonOptions);
            return profile?.Id == companyId.Value ? profile : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<TradeCompanyAccessContext?> ResolvePublicAccessAsync(
        TradeCompanyPublicationOwnership ownership,
        CancellationToken cancellationToken = default)
    {
        var found = await profiles.FindObjectAsync(
            ProfileSyncCollections.TradeOrders,
            ownership.OrderId.ToString("D"),
            cancellationToken);
        if (found is not { Object.Deleted: false } ||
            !Guid.TryParse(found.ProfileId, out var hostProfileId) ||
            hostProfileId == Guid.Empty)
        {
            return null;
        }

        try
        {
            var order = JsonSerializer.Deserialize<TradeOrder>(
                found.Object.PayloadJson,
                JsonOptions);
            if (!OrderBelongsToCompany(order, ownership.CompanyId) ||
                order!.CommissionPublication?.Ownership != ownership)
            {
                return null;
            }

            var access = new TradeCompanyAccessContext(
                ownership.CompanyId,
                hostProfileId,
                TradeCompanyRole.Owner,
                hostProfileId);
            return await LoadCompanyProfileAsync(
                found.ProfileId,
                ownership.CompanyId,
                cancellationToken) == null
                ? null
                : access;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<TradeCompanyProfile?> LoadCompanyProfileAsync(
        string hostProfileId,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var stored = await profiles.LoadObjectAsync(
            hostProfileId,
            ProfileSyncCollections.TradeCompanyProfiles,
            companyId.ToString(),
            cancellationToken);
        if (stored is not { Deleted: false })
        {
            return null;
        }

        try
        {
            var company = JsonSerializer.Deserialize<TradeCompanyProfile>(
                stored.PayloadJson,
                JsonOptions);
            return company?.Id == companyId.Value ? company : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool PayloadBelongsToCompany(
        CompanyId companyId,
        string recordKind,
        string payloadJson)
    {
        try
        {
            return recordKind switch
            {
                TradeCompanyRecordKinds.Crafter =>
                    JsonSerializer.Deserialize<TradeCrafterProfile>(payloadJson, JsonOptions)?
                        .CompanyProfileId == companyId.Value,
                TradeCompanyRecordKinds.Order =>
                    OrderBelongsToCompany(
                        JsonSerializer.Deserialize<TradeOrder>(payloadJson, JsonOptions),
                        companyId),
                TradeCompanyRecordKinds.Publication =>
                    DeserializeOwnership(payloadJson)?.CompanyId == companyId,
                _ => true
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool OrderBelongsToCompany(TradeOrder? order, CompanyId companyId) =>
        order != null &&
        (order.CompanyCommission?.CompanyId == companyId ||
         order.CompanyCommission == null &&
         order.CompanyProfileId == companyId.Value) &&
        (order.History ?? [])
            .All(item =>
                order.CompanyCommission != null ||
                item.CompanyProfileId == companyId.Value);

    private static TradeCompanyPublicationOwnership? DeserializeOwnership(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<TradeCompanyPublicationOwnership>(
                payloadJson,
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ToCollection(string recordKind) =>
        recordKind switch
        {
            TradeCompanyRecordKinds.Crafter => ProfileSyncCollections.TradeCrafters,
            TradeCompanyRecordKinds.Order => ProfileSyncCollections.TradeOrders,
            _ => CompanyCollectionPrefix + recordKind
        };

    private static string ToStoredObjectId(
        CompanyId companyId,
        string recordKind,
        string recordId) =>
        recordKind is
            TradeCompanyRecordKinds.Crafter or
            TradeCompanyRecordKinds.Order or
            TradeCompanyRecordKinds.Publication
                ? recordId
                : $"{companyId}:{recordId}";

    private static TradeCompanyRecordEnvelope ToRecord(
        CompanyId companyId,
        string recordKind,
        string recordId,
        ProfileSyncObjectEnvelope item) =>
        new(
            companyId,
            recordKind,
            recordId,
            item.PayloadJson,
            new CompanyRecordRevision(item.Revision),
            item.UpdatedAtUtc,
            item.Deleted,
            item.DeletedAtUtc);

    private static string RequireHostProfile(TradeCompanyAccessContext access)
    {
        if (access.Role != TradeCompanyRole.Owner ||
            access.HostProfileId is not { } hostProfileId ||
            hostProfileId == Guid.Empty)
        {
            throw new UnauthorizedAccessException(
                "A hosted-profile owner capability is required.");
        }

        return hostProfileId.ToString("D");
    }

    private static TradeCompanyMutationResult Rejected(string code, string message) =>
        new(TradeCompanyMutationStatus.Rejected, null, ErrorCode: code, ErrorMessage: message);
}
