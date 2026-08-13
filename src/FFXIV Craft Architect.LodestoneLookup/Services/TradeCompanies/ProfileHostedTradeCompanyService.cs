using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class ProfileHostedTradeCompanyService(
    SqliteProfileHostStore profiles,
    ProfileAccessKeyHasher accessKeyHasher,
    SqliteMembershipStore? memberships = null)
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
        return await ResolveAuthenticatedHostAsync(host, companyId, cancellationToken);
    }

    public async Task<TradeCompanyAccessContext?> TryAuthenticateCachedAsync(
        string plaintextKey,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        var host = await profiles.TryAuthenticateCachedAsync(
            plaintextKey,
            accessKeyHasher,
            cancellationToken);
        return await ResolveAuthenticatedHostAsync(host, companyId, cancellationToken);
    }

    private async Task<TradeCompanyAccessContext?> ResolveAuthenticatedHostAsync(
        ProfileHostProfileResponse? host,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
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

    public async Task<TradeCompanyAccessContext?> ResolveProfileAccessAsync(
        Guid hostProfileId,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        if (hostProfileId == Guid.Empty ||
            await profiles.LoadProfileAsync(
                hostProfileId.ToString("D"),
                cancellationToken) == null ||
            await LoadCompanyProfileAsync(
                hostProfileId.ToString("D"),
                companyId,
                cancellationToken) == null)
        {
            return null;
        }

        return new TradeCompanyAccessContext(
            companyId,
            hostProfileId,
            TradeCompanyRole.Owner,
            hostProfileId);
    }

    public async Task<TradeCompanyAccessContext?> ResolveMembershipAccessAsync(
        Guid accountProfileId,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        if (accountProfileId == Guid.Empty)
        {
            return null;
        }

        var hosted = await profiles.FindObjectAsync(
            ProfileSyncCollections.TradeCompanyProfiles,
            companyId.ToString(),
            cancellationToken);
        if (hosted is not { Object.Deleted: false } ||
            !Guid.TryParse(hosted.ProfileId, out var hostProfileId) ||
            hostProfileId == Guid.Empty ||
            await LoadCompanyProfileAsync(
                hosted.ProfileId,
                companyId,
                cancellationToken) == null)
        {
            return null;
        }

        if (accountProfileId == hostProfileId)
        {
            return new TradeCompanyAccessContext(
                companyId,
                accountProfileId,
                TradeCompanyRole.Owner,
                hostProfileId);
        }

        var membership = memberships == null
            ? null
            : await memberships.LoadAsync(
                companyId,
                accountProfileId,
                cancellationToken);
        if (membership is not { State: MembershipState.Active })
        {
            return null;
        }

        return new TradeCompanyAccessContext(
            companyId,
            accountProfileId,
            membership.Role switch
            {
                MembershipRole.Owner => TradeCompanyRole.Owner,
                MembershipRole.Operator => TradeCompanyRole.Operator,
                _ => TradeCompanyRole.ReadOnly
            },
            hostProfileId);
    }

    public async Task<TradeCompanyAccessContext?> ResolveDelegatedOperatorAccessAsync(
        Guid grantProfileId,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        if (grantProfileId == Guid.Empty)
        {
            return null;
        }

        var hosted = await profiles.FindObjectAsync(
            ProfileSyncCollections.TradeCompanyProfiles,
            companyId.ToString(),
            cancellationToken);
        if (hosted is not { Object.Deleted: false } ||
            !Guid.TryParse(hosted.ProfileId, out var hostProfileId) ||
            hostProfileId == Guid.Empty ||
            await LoadCompanyProfileAsync(
                hosted.ProfileId,
                companyId,
                cancellationToken) == null)
        {
            return null;
        }

        return new TradeCompanyAccessContext(
            companyId,
            grantProfileId,
            TradeCompanyRole.Operator,
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
                ToRecord(access.CompanyId, recordKind, recordId, current),
                CompanyRevision: new CompanyRecordRevision(put.ServerRevision));
        }

        if (current != null &&
            string.Equals(current.PayloadJson, payloadJson, StringComparison.Ordinal))
        {
            return new TradeCompanyMutationResult(
                TradeCompanyMutationStatus.Replayed,
                ToRecord(access.CompanyId, recordKind, recordId, current),
                CompanyRevision: new CompanyRecordRevision(put.ServerRevision));
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

    public async Task<TradeCompanyMutationResult> AdoptSynchronizedOrderAsync(
        TradeCompanyAccessContext access,
        Guid orderId,
        CompanyRecordRevision sourceRevision,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (orderId == Guid.Empty ||
            sourceRevision.Value <= 0 ||
            string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Rejected(
                "invalid_order_adoption",
                "A synchronized Trade order revision is required for company adoption.");
        }

        var hostProfileId = RequireHostProfile(access);
        var objectId = orderId.ToString("D");
        var source = await profiles.LoadObjectAsync(
            access.GrantId.ToString("D"),
            ProfileSyncCollections.TradeOrders,
            objectId,
            cancellationToken);
        if (source is not { Deleted: false })
        {
            return Rejected(
                "source_order_missing",
                "The synchronized operator draft is unavailable.");
        }
        if (source.Revision != sourceRevision.Value)
        {
            return new TradeCompanyMutationResult(
                TradeCompanyMutationStatus.Conflict,
                null,
                ErrorCode: "source_revision_conflict",
                ErrorMessage: "The synchronized operator draft changed before company adoption.");
        }

        TradeOrder? sourceOrder;
        try
        {
            sourceOrder = JsonSerializer.Deserialize<TradeOrder>(source.PayloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            sourceOrder = null;
        }
        if (sourceOrder?.Id != orderId ||
            !OrderBelongsToCompany(sourceOrder, access.CompanyId))
        {
            return Rejected(
                "company_scope_mismatch",
                "The synchronized operator draft does not belong to the authenticated company.");
        }

        var current = await profiles.LoadObjectAsync(
            hostProfileId,
            ProfileSyncCollections.TradeOrders,
            objectId,
            cancellationToken);
        if (current is { Deleted: false } &&
            string.Equals(current.PayloadJson, source.PayloadJson, StringComparison.Ordinal))
        {
            return new TradeCompanyMutationResult(
                TradeCompanyMutationStatus.Replayed,
                ToRecord(access.CompanyId, TradeCompanyRecordKinds.Order, objectId, current));
        }
        if (current != null)
        {
            return new TradeCompanyMutationResult(
                TradeCompanyMutationStatus.Conflict,
                null,
                current.Deleted
                    ? null
                    : ToRecord(access.CompanyId, TradeCompanyRecordKinds.Order, objectId, current),
                "canonical_order_conflict",
                "The company already has a different authoritative order with this identity.");
        }
        if (sourceOrder.CompanyCommission != null)
        {
            return Rejected(
                "canonical_source_not_adoptable",
                "Only an unpublished synchronized draft can be adopted into a company workspace.");
        }

        var linkedPlan = await AdoptLinkedPlanDependencyAsync(
            access,
            hostProfileId,
            sourceOrder,
            cancellationToken);
        if (linkedPlan != null)
        {
            return linkedPlan;
        }

        return await PutRecordAsync(
            access,
            TradeCompanyRecordKinds.Order,
            objectId,
            source.PayloadJson,
            CompanyRecordRevision.None,
            idempotencyKey,
            cancellationToken);
    }

    private async Task<TradeCompanyMutationResult?> AdoptLinkedPlanDependencyAsync(
        TradeCompanyAccessContext access,
        string hostProfileId,
        TradeOrder sourceOrder,
        CancellationToken cancellationToken)
    {
        if (sourceOrder.CraftPlanLinkKind != TradeOrderCraftPlanLinkKind.OrderGenerated)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(sourceOrder.CraftPlanId) ||
            !sourceOrder.CraftPlanSavedAtUtc.HasValue)
        {
            return Rejected(
                "source_plan_missing",
                "The synchronized operator draft does not retain its exact generated plan revision.");
        }

        var sourcePlan = await profiles.LoadObjectAsync(
            access.GrantId.ToString("D"),
            ProfileSyncCollections.Plans,
            sourceOrder.CraftPlanId,
            cancellationToken);
        if (sourcePlan is not { Deleted: false })
        {
            return Rejected(
                "source_plan_missing",
                "The synchronized operator draft's generated plan is unavailable.");
        }

        var plan = ProfileSyncPlanPayloadCodec.Deserialize(
            sourcePlan.PayloadJson,
            sourceOrder.CraftPlanId);
        if (plan.LinkedOrderId != sourceOrder.Id ||
            plan.SavedAt != sourceOrder.CraftPlanSavedAtUtc.Value)
        {
            return Rejected(
                "source_plan_mismatch",
                "The synchronized operator draft's generated plan does not match its saved revision.");
        }

        var adopted = await profiles.PutObjectAsync(
            hostProfileId,
            ProfileSyncCollections.Plans,
            sourceOrder.CraftPlanId,
            sourcePlan.PayloadJson,
            expectedRevision: 0,
            ct: cancellationToken);
        return adopted.Success
            ? null
            : Rejected(
                adopted.ErrorCode ?? "canonical_plan_conflict",
                adopted.ErrorMessage ??
                "The company already has a different generated plan with this identity.");
    }

    public async Task<CompanyRecordRevision> MirrorOrderToGrantAsync(
        TradeCompanyAccessContext access,
        TradeOrder order,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var hostProfileId = RequireHostProfile(access);
        if (order.Id == Guid.Empty ||
            !OrderBelongsToCompany(order, access.CompanyId))
        {
            throw new InvalidOperationException(
                "The authoritative Trade order cannot be mirrored outside its company scope.");
        }

        var objectId = order.Id.ToString("D");
        if (string.Equals(
                hostProfileId,
                access.GrantId.ToString("D"),
                StringComparison.OrdinalIgnoreCase))
        {
            var canonical = await LoadRecordAsync(
                access,
                TradeCompanyRecordKinds.Order,
                objectId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The authoritative company order is unavailable for profile reconciliation.");
            return canonical.RecordRevision;
        }

        var payloadJson = JsonSerializer.Serialize(order, JsonOptions);
        var grantProfileId = access.GrantId.ToString("D");
        var current = await profiles.LoadObjectAsync(
            grantProfileId,
            ProfileSyncCollections.TradeOrders,
            objectId,
            cancellationToken);
        if (current is { Deleted: false } &&
            string.Equals(current.PayloadJson, payloadJson, StringComparison.Ordinal))
        {
            return new CompanyRecordRevision(current.Revision);
        }
        if (current is { Deleted: true })
        {
            throw new InvalidOperationException(
                "The operator profile deleted this order before company reconciliation completed.");
        }
        if (current != null &&
            !PayloadBelongsToCompany(
                access.CompanyId,
                TradeCompanyRecordKinds.Order,
                current.PayloadJson))
        {
            throw new InvalidOperationException(
                "The operator profile contains a conflicting cross-company order identity.");
        }

        var put = await profiles.PutObjectAsync(
            grantProfileId,
            ProfileSyncCollections.TradeOrders,
            objectId,
            payloadJson,
            current?.Revision ?? 0,
            cancellationToken);
        var committed = put.Object ?? put.RemoteObject;
        if ((put.Success && committed != null) ||
            (committed != null &&
             !committed.Deleted &&
             string.Equals(committed.PayloadJson, payloadJson, StringComparison.Ordinal)))
        {
            return new CompanyRecordRevision(committed!.Revision);
        }

        throw new InvalidOperationException(
            put.ErrorMessage ??
            "The operator profile changed before company reconciliation completed.");
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

    public async Task<CompanyId?> ResolveCommissionCompanyAsync(
        Guid commissionId,
        CancellationToken cancellationToken = default)
    {
        if (commissionId == Guid.Empty)
        {
            return null;
        }

        var found = await profiles.FindObjectAsync(
            ProfileSyncCollections.TradeOrders,
            commissionId.ToString("D"),
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
            var commission = order?.CompanyCommission;
            if (order?.Id != commissionId ||
                commission?.CommissionId != commissionId ||
                order.CompanyProfileId != commission.CompanyId.Value ||
                await LoadCompanyProfileAsync(
                    found.ProfileId,
                    commission.CompanyId,
                    cancellationToken) == null)
            {
                return null;
            }

            return commission.CompanyId;
        }
        catch (JsonException)
        {
            return null;
        }
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
        string publicId,
        CancellationToken cancellationToken = default)
    {
        var found = await LoadCanonicalPublishedOrderAsync(
            ownership,
            publicId,
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
        string publicId,
        CancellationToken cancellationToken = default)
    {
        var found = await LoadCanonicalPublishedOrderAsync(
            ownership,
            publicId,
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

    private async Task<HostedProfileObject?> LoadCanonicalPublishedOrderAsync(
        TradeCompanyPublicationOwnership ownership,
        string publicId,
        CancellationToken cancellationToken)
    {
        var publication = await profiles.FindObjectAsync(
            ToCollection(TradeCompanyRecordKinds.Publication),
            publicId,
            cancellationToken);
        if (publication == null ||
            DeserializeOwnership(publication.Object.PayloadJson) != ownership)
        {
            return null;
        }

        var order = await profiles.LoadObjectAsync(
            publication.ProfileId,
            ProfileSyncCollections.TradeOrders,
            ownership.OrderId.ToString("D"),
            cancellationToken);
        return order == null
            ? null
            : new HostedProfileObject(publication.ProfileId, order);
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
        if (access.Role is not (TradeCompanyRole.Owner or TradeCompanyRole.Operator) ||
            access.HostProfileId is not { } hostProfileId ||
            hostProfileId == Guid.Empty)
        {
            throw new UnauthorizedAccessException(
                "A hosted company operator capability is required.");
        }

        return hostProfileId.ToString("D");
    }

    private static TradeCompanyMutationResult Rejected(string code, string message) =>
        new(TradeCompanyMutationStatus.Rejected, null, ErrorCode: code, ErrorMessage: message);
}
