using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCompanyCollaborationService(
    TradeCompanyCollaborationClient client,
    TradeOperationsPersistenceService tradeOperations,
    ProfileSyncLocalStateService localState,
    ProfileSyncService profileSync)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<Guid, IReadOnlyList<TradeCommissionInterest>> _interests = [];
    private readonly Dictionary<Guid, TradeCommissionPublicationProjection> _publications = [];

    public IReadOnlyList<TradeCommissionInterest> GetPendingInterests(Guid orderId) =>
        _interests.GetValueOrDefault(orderId, [])
            .Where(claim => claim.State == TradeCommissionInterestState.Pending)
            .OrderBy(claim => claim.CreatedAtUtc)
            .ToArray();

    public TradeCommissionPublicationProjection? GetPublication(Guid orderId) =>
        _publications.GetValueOrDefault(orderId);

    public bool CanPerformExternalAction(TradeOrder order, out string reason)
    {
        if (!profileSync.CurrentStatus.IsConnected)
        {
            reason = "Connect Profile Hosting in Options first.";
            return false;
        }

        if (!profileSync.CurrentStatus.HostReachable)
        {
            reason = profileSync.CurrentStatus.Message ?? "Profile Hosting is unavailable.";
            return false;
        }

        var objectId = order.Id.ToString("D");
        if (profileSync.PendingSaves.Any(item =>
                string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal) &&
                string.Equals(item.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)) ||
            profileSync.Conflicts.Any(item =>
                string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal) &&
                string.Equals(item.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Resolve the pending hosted order update before continuing.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public async Task<TradeCompanyPublicationOwnership?> GetPublicationOwnershipAsync(
        TradeOrder order)
    {
        var connection = await localState.LoadConnectionSettingsAsync();
        var knownHosted = await localState.HasKnownHostedObjectAsync(
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"));
        if (connection.ProfileScopeId == null)
        {
            if (knownHosted)
            {
                throw new InvalidOperationException(
                    "Reconnect the order's hosted profile before publishing its company-owned link.");
            }

            return null;
        }

        if (!connection.IsConfigured)
        {
            throw new InvalidOperationException(
                "Reconnect Profile Hosting before publishing this hosted order.");
        }

        if (!CanPerformExternalAction(order, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var revision = await localState.LoadObjectRevisionAsync(
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"));
        if (revision <= 0)
        {
            throw new InvalidOperationException(
                "Sync this order through Profile Hosting before publishing its company-owned link.");
        }

        return new TradeCompanyPublicationOwnership(
            new CompanyId(order.CompanyProfileId),
            order.Id,
            new CompanyRecordRevision(revision));
    }

    public async Task RefreshAsync(
        Guid companyProfileId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var interests = await client.LoadPendingInterestsAsync(
            companyProfileId,
            orderId,
            cancellationToken);
        var publication = await client.LoadPublicationAsync(
            companyProfileId,
            orderId,
            cancellationToken);
        _interests[orderId] = interests;
        if (publication == null)
        {
            _publications.Remove(orderId);
        }
        else
        {
            _publications[orderId] = publication;
        }
    }

    public async Task<TradeCommissionWorkflowResult> PublishToDiscordAsync(
        TradeOrder order,
        CommissionBriefDocument brief,
        CancellationToken cancellationToken = default)
    {
        if (!CanPerformExternalAction(order, out var reason))
        {
            return Rejected(reason);
        }

        var revision = await localState.LoadObjectRevisionAsync(
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"));
        if (revision <= 0)
        {
            return Rejected("Sync this order through Profile Hosting before publishing it.");
        }

        try
        {
            var publication = await client.PublishAsync(
                order.CompanyProfileId,
                order.Id,
                revision,
                brief,
                $"discord-publish:{order.Id:D}:{revision}",
                cancellationToken);
            _publications[order.Id] = publication;
            return new TradeCommissionWorkflowResult(
                publication.State is
                    TradeCommissionDeliveryState.Pending or
                    TradeCommissionDeliveryState.Published,
                TradeCompanyMutationDisposition.Synced,
                publication,
                Message: publication.Message);
        }
        catch (Exception exception)
        {
            return Rejected(exception.Message);
        }
    }

    public async Task<TradeCommissionWorkflowResult> RetryDiscordPublicationAsync(
        TradeOrder order,
        string publicId,
        CancellationToken cancellationToken = default)
    {
        if (!CanPerformExternalAction(order, out var reason))
        {
            return Rejected(reason);
        }

        if (string.IsNullOrWhiteSpace(publicId))
        {
            return Rejected("The failed Discord publication identity is unavailable.");
        }

        try
        {
            var publication = await client.RetryPublicationAsync(
                order.CompanyProfileId,
                publicId,
                cancellationToken);
            _publications[order.Id] = publication;
            return new TradeCommissionWorkflowResult(
                publication.State == TradeCommissionDeliveryState.Pending,
                TradeCompanyMutationDisposition.Synced,
                publication,
                Message: publication.Message);
        }
        catch (Exception exception)
        {
            return Rejected(exception.Message);
        }
    }

    public async Task<PortableCommissionLink> PublishPortableLinkAsync(
        TradeOrder order,
        CommissionBriefDocument brief,
        CancellationToken cancellationToken = default)
    {
        if (!CanPerformExternalAction(order, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var revision = await localState.LoadObjectRevisionAsync(
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"));
        if (revision <= 0)
        {
            throw new InvalidOperationException(
                "Sync this order through Profile Hosting before publishing its company-owned link.");
        }

        var published = await client.PublishPortableLinkAsync(
            order.CompanyProfileId,
            order.Id,
            revision,
            brief,
            $"portable-link:{order.Id:D}:{revision}",
            cancellationToken);
        var expectedOwnership = new TradeCompanyPublicationOwnership(
            new CompanyId(order.CompanyProfileId),
            order.Id,
            new CompanyRecordRevision(revision));
        var hostedOrder = ReadHostedPublishedOrder(
            order,
            expectedOwnership,
            published);
        if (!await tradeOperations.ApplyCanonicalOrderAsync(hostedOrder))
        {
            throw new InvalidOperationException(
                "The company brief was attached by Profile Hosting, but browser storage could not apply the authoritative order.");
        }

        await localState.SaveObjectRevisionAsync(
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"),
            published.OrderRecord.RecordRevision.Value);
        return published.Link;
    }

    public Task<PortableCommissionLink> ResolvePortableLinkAsync(
        string publicId,
        CancellationToken cancellationToken = default) =>
        client.ResolvePortableLinkAsync(publicId, cancellationToken);

    public Task RevokePortableLinkAsync(
        TradeOrder order,
        string publicId,
        CancellationToken cancellationToken = default) =>
        client.RevokePortableLinkAsync(
            order.CompanyProfileId,
            publicId,
            cancellationToken);

    public Task<TradeCommissionWorkflowResult> AcceptInterestAsync(
        TradeOrder order,
        TradeCommissionInterest claim,
        Guid crafterId,
        CancellationToken cancellationToken = default) =>
        ResolveInterestAsync(true, order, claim, crafterId, cancellationToken);

    public Task<TradeCommissionWorkflowResult> DeclineInterestAsync(
        TradeOrder order,
        TradeCommissionInterest claim,
        CancellationToken cancellationToken = default) =>
        ResolveInterestAsync(false, order, claim, null, cancellationToken);

    public async Task RevokePublicationAsync(
        TradeCompanyPublicationOwnership ownership,
        string publicId,
        CancellationToken cancellationToken = default)
    {
        await client.RevokeAsync(
            ownership.CompanyId.Value,
            publicId,
            cancellationToken);
        _publications.Remove(ownership.OrderId);
    }

    private async Task<TradeCommissionWorkflowResult> ResolveInterestAsync(
        bool accept,
        TradeOrder order,
        TradeCommissionInterest claim,
        Guid? crafterId,
        CancellationToken cancellationToken)
    {
        if (!CanPerformExternalAction(order, out var reason))
        {
            return Rejected(reason);
        }

        try
        {
            var receipt = accept
                ? await client.AcceptAsync(
                    order.CompanyProfileId,
                    claim.ClaimId,
                    crafterId ?? throw new InvalidOperationException(
                        "Choose a hosted company crafter before accepting interest."),
                    $"discord-claim:{claim.ClaimId}:{crafterId:D}",
                    cancellationToken)
                : await client.DeclineAsync(
                    order.CompanyProfileId,
                    claim.ClaimId,
                    cancellationToken);

            if (receipt.UpdatedOrder != null)
            {
                if (!await tradeOperations.ApplyCanonicalOrderAsync(receipt.UpdatedOrder))
                {
                    return Rejected(
                        "The hosted assignment was accepted, but the order could not be saved locally.");
                }

                if (receipt.UpdatedOrderRevision is > 0)
                {
                    await localState.SaveObjectRevisionAsync(
                        ProfileSyncCollections.TradeOrders,
                        receipt.UpdatedOrder.Id.ToString("D"),
                        receipt.UpdatedOrderRevision.Value);
                }
            }

            _interests[order.Id] = GetPendingInterests(order.Id)
                .Where(candidate => candidate.ClaimId != claim.ClaimId)
                .ToArray();
            return new TradeCommissionWorkflowResult(
                !accept || receipt.UpdatedOrder != null,
                TradeCompanyMutationDisposition.Synced,
                Resolution: receipt,
                Message: receipt.Message);
        }
        catch (Exception exception)
        {
            return Rejected(exception.Message);
        }
    }

    private static TradeCommissionWorkflowResult Rejected(string message) =>
        new(false, TradeCompanyMutationDisposition.Rejected, Message: message);

    private static TradeOrder ReadHostedPublishedOrder(
        TradeOrder sourceOrder,
        TradeCompanyPublicationOwnership expectedOwnership,
        TradeCompanyPortablePublication published)
    {
        var record = published.OrderRecord;
        if (record.CompanyId.Value != sourceOrder.CompanyProfileId ||
            !string.Equals(
                record.RecordKind,
                TradeCompanyRecordKinds.Order,
                StringComparison.Ordinal) ||
            !string.Equals(
                record.RecordId,
                sourceOrder.Id.ToString("D"),
                StringComparison.OrdinalIgnoreCase) ||
            record.RecordRevision.Value <= 0)
        {
            throw new InvalidOperationException(
                "Portable commission publication returned the wrong authoritative Trade order.");
        }

        var order = JsonSerializer.Deserialize<TradeOrder>(
            record.PayloadJson,
            JsonOptions)
            ?? throw new InvalidOperationException(
                "Portable commission publication returned an invalid authoritative Trade order.");
        var publication = order.CommissionPublication;
        var validatedLink = publication?.PublicUrl is { Length: > 0 } publicUrl
            ? CommissionBriefClient.CreatePortableLink(
                publication.PublicId,
                publicUrl,
                publication.Version,
                publication.PublishedAtUtc)
            : null;
        if (order.Id != sourceOrder.Id ||
            order.CompanyProfileId != sourceOrder.CompanyProfileId ||
            publication?.Ownership is not { } ownership ||
            ownership != expectedOwnership ||
            !string.Equals(
                publication.PublicId,
                published.Link.PublicId,
                StringComparison.Ordinal) ||
            validatedLink != published.Link)
        {
            throw new InvalidOperationException(
                "Portable commission publication returned inconsistent authoritative link ownership.");
        }

        return order;
    }
}
