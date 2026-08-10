using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCompanyCollaborationService(
    TradeCompanyCollaborationClient client,
    TradeOperationsPersistenceService tradeOperations,
    ProfileSyncLocalStateService localState,
    ProfileSyncService profileSync,
    HostedOrderProjectionStore hostedOrders)
{
    private static readonly TimeSpan PublicationRefreshLifetime = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record OrderCommandAuthority(
        HostedOrderAuthorityScope Projection,
        HostedProfileConnectionSettings Connection);
    private readonly Dictionary<Guid, TradeCommissionPublicationProjection> _publications = [];
    private readonly Dictionary<Guid, DateTime> _publicationRefreshedAtUtc = [];
    private readonly Dictionary<Guid, Task> _publicationRefreshes = [];
    private string? _dictionaryProfileId;
    private string? _dictionaryConnectionScopeId;

    public TradeCommissionPublicationProjection? GetPublication(Guid orderId) =>
        IsDictionaryAuthorityCurrent()
            ? _publications.GetValueOrDefault(orderId)
            : null;

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
        TradeOrder order,
        CancellationToken cancellationToken = default)
    {
        var connection = (await localState.LoadConnectionSettingsAsync()).Snapshot();
        if (connection.ProfileScopeId == null)
        {
            var knownHosted = await localState.HasKnownHostedObjectAsync(
                ProfileSyncCollections.TradeOrders,
                order.Id.ToString("D"));
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

        var authority = await CaptureOrderAuthorityAsync();
        if (!string.Equals(
                authority.Connection.ConnectionScopeId,
                connection.ConnectionScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The hosted order authority changed while publication ownership was being resolved.");
        }
        var revision = await ResolveHostedOrderRevisionAsync(
            order,
            authority,
            cancellationToken);
        if (revision <= 0)
        {
            throw new InvalidOperationException(
                "The hosted order differs from this browser and needs conflict review before publishing.");
        }

        return new TradeCompanyPublicationOwnership(
            new CompanyId(order.CompanyProfileId),
            order.Id,
            new CompanyRecordRevision(revision));
    }

    public Task RefreshAsync(
        Guid companyProfileId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        if (IsDictionaryAuthorityCurrent() &&
            _publicationRefreshedAtUtc.TryGetValue(orderId, out var refreshedAtUtc) &&
            DateTime.UtcNow - refreshedAtUtc < PublicationRefreshLifetime)
        {
            return Task.CompletedTask;
        }

        if (_publicationRefreshes.TryGetValue(orderId, out var pending))
        {
            return pending.WaitAsync(cancellationToken);
        }

        var refresh = RefreshCoreAsync(companyProfileId, orderId, cancellationToken);
        _publicationRefreshes[orderId] = refresh;
        return ObserveRefreshAsync(orderId, refresh);
    }

    private async Task RefreshCoreAsync(
        Guid companyProfileId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var authority = await CaptureOrderAuthorityAsync();
        var publication = await client.LoadPublicationAsync(
            companyProfileId,
            orderId,
            cancellationToken,
            authority.Connection);
        if (publication is not null && publication.OrderId != orderId)
        {
            throw new InvalidOperationException(
                "The collaboration refresh returned data for a different order.");
        }
        if (!await IsCurrentAuthorityAsync(authority))
        {
            InvalidateDictionaryAuthority();
            throw new InvalidOperationException(
                "The hosted order authority changed while collaboration details were refreshing.");
        }
        AdoptDictionaryAuthority(authority);
        if (publication == null)
        {
            _publications.Remove(orderId);
        }
        else
        {
            _publications[orderId] = publication;
        }
        _publicationRefreshedAtUtc[orderId] = DateTime.UtcNow;
    }

    private async Task ObserveRefreshAsync(Guid orderId, Task refresh)
    {
        try
        {
            await refresh;
        }
        finally
        {
            if (_publicationRefreshes.GetValueOrDefault(orderId) == refresh)
            {
                _publicationRefreshes.Remove(orderId);
            }
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

        try
        {
            var authority = await CaptureOrderAuthorityAsync();
            var revision = await ResolveHostedOrderRevisionAsync(
                order,
                authority,
                cancellationToken);
            if (revision <= 0)
            {
                return Rejected(
                    "The hosted order differs from this browser and needs conflict review before publishing.");
            }

            var publication = await client.PublishAsync(
                order.CompanyProfileId,
                order.Id,
                revision,
                brief,
                $"discord-publish:{order.Id:D}:{revision}",
                cancellationToken,
                authority.Connection);
            await RequireCurrentPublicationAuthorityAsync(
                authority,
                order.Id,
                publication);
            AdoptDictionaryAuthority(authority);
            _publications[order.Id] = publication;
            _publicationRefreshedAtUtc[order.Id] = DateTime.UtcNow;
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
            var authority = await CaptureOrderAuthorityAsync();
            var publication = await client.RetryPublicationAsync(
                order.CompanyProfileId,
                publicId,
                cancellationToken,
                authority.Connection);
            await RequireCurrentPublicationAuthorityAsync(
                authority,
                order.Id,
                publication);
            AdoptDictionaryAuthority(authority);
            _publications[order.Id] = publication;
            _publicationRefreshedAtUtc[order.Id] = DateTime.UtcNow;
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

    public async Task<TradeCommissionWorkflowResult> ReconcileDiscordPublicationAsync(
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
            return Rejected("The Discord publication identity is unavailable.");
        }

        try
        {
            var authority = await CaptureOrderAuthorityAsync();
            var publication = await client.ReconcilePublicationAsync(
                order.CompanyProfileId,
                publicId,
                cancellationToken,
                authority.Connection);
            await RequireCurrentPublicationAuthorityAsync(
                authority,
                order.Id,
                publication);
            AdoptDictionaryAuthority(authority);
            _publications[order.Id] = publication;
            _publicationRefreshedAtUtc[order.Id] = DateTime.UtcNow;
            return new TradeCommissionWorkflowResult(
                Success: true,
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

        var authority = await CaptureOrderAuthorityAsync();
        var revision = await ResolveHostedOrderRevisionAsync(
            order,
            authority,
            cancellationToken);
        if (revision <= 0)
        {
            throw new InvalidOperationException(
                "The hosted order differs from this browser and needs conflict review before publishing.");
        }
        if (!await IsCurrentAuthorityAsync(authority))
        {
            throw new InvalidOperationException(
                "The hosted order authority changed before publication began.");
        }

        var published = await client.PublishPortableLinkAsync(
            order.CompanyProfileId,
            order.Id,
            revision,
            brief,
            $"portable-link:{order.Id:D}:{revision}",
            cancellationToken,
            authority.Connection);
        var expectedOwnership = new TradeCompanyPublicationOwnership(
            new CompanyId(order.CompanyProfileId),
            order.Id,
            new CompanyRecordRevision(revision));
        var hostedOrder = ReadHostedPublishedOrder(
            order,
            expectedOwnership,
            published);
        await AdoptCommittedOrderAsync(
            authority,
            hostedOrder,
            published.OrderRecord.RecordRevision.Value,
            "The company brief was attached by Profile Hosting, but browser storage could not apply the authoritative order.");
        AdoptDictionaryAuthority(authority);
        _publications.Remove(order.Id);
        _publicationRefreshedAtUtc[order.Id] = DateTime.UtcNow;
        return published.Link;
    }

    public Task<PortableCommissionLink> ResolvePortableLinkAsync(
        string publicId,
        CancellationToken cancellationToken = default) =>
        client.ResolvePortableLinkAsync(publicId, cancellationToken);

    public async Task RevokePortableLinkAsync(
        TradeOrder order,
        string publicId,
        CancellationToken cancellationToken = default)
    {
        var authority = await CaptureOrderAuthorityAsync();
        await client.RevokePortableLinkAsync(
            order.CompanyProfileId,
            publicId,
            cancellationToken,
            authority.Connection);
        if (!await IsCurrentAuthorityAsync(authority))
        {
            InvalidateDictionaryAuthority();
            throw new InvalidOperationException(
                "The hosted order authority changed while the portable publication was being revoked.");
        }
    }

    public async Task RevokePublicationAsync(
        TradeCompanyPublicationOwnership ownership,
        string publicId,
        CancellationToken cancellationToken = default)
    {
        var authority = await CaptureOrderAuthorityAsync();
        await client.RevokeAsync(
            ownership.CompanyId.Value,
            publicId,
            cancellationToken,
            authority.Connection);
        if (!await IsCurrentAuthorityAsync(authority))
        {
            InvalidateDictionaryAuthority();
            throw new InvalidOperationException(
                "The hosted order authority changed while the Discord publication was being revoked.");
        }
        AdoptDictionaryAuthority(authority);
        _publications.Remove(ownership.OrderId);
        _publicationRefreshedAtUtc[ownership.OrderId] = DateTime.UtcNow;
    }

    private async Task<OrderCommandAuthority> CaptureOrderAuthorityAsync()
    {
        var projection = hostedOrders.CaptureAuthorityScope();
        var connection = await localState.LoadConnectionSettingsAsync();
        if (string.IsNullOrWhiteSpace(projection.ProfileId) ||
            string.IsNullOrWhiteSpace(connection.ConnectionScopeId) ||
            !string.Equals(
                projection.ProfileId,
                profileSync.CurrentStatus.ProfileId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                projection.ProfileId,
                connection.ProfileScopeId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                projection.ConnectionScopeId,
                connection.ConnectionScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The hosted order authority is not ready for this command.");
        }
        return new OrderCommandAuthority(
            projection,
            connection.Snapshot());
    }

    private async Task AdoptCommittedOrderAsync(
        OrderCommandAuthority authority,
        TradeOrder order,
        long revision,
        string persistenceFailure)
    {
        var adoption = await hostedOrders.AdoptAndPersistCommittedOrderAsync(
            authority.Projection,
            order,
            revision,
            async candidate =>
            {
                var persisted = candidate.Deleted
                    ? await tradeOperations.DeleteOrderAsync(candidate.OrderId)
                    : await tradeOperations.ApplyCanonicalOrderAsync(candidate.Order!);
                if (!persisted)
                {
                    throw new InvalidOperationException(persistenceFailure);
                }
                if (!await IsCurrentAuthorityAsync(authority))
                {
                    throw new InvalidOperationException(
                        "The hosted order authority changed while browser persistence was in progress.");
                }
                await localState.SaveObjectRevisionAsync(
                    authority.Connection,
                    ProfileSyncCollections.TradeOrders,
                    candidate.OrderId.ToString("D"),
                    candidate.ObjectRevision);
            },
            () => IsCurrentAuthorityAsync(authority));
        if (adoption is not (
            HostedOrderCommittedProjectionResult.Adopted or
            HostedOrderCommittedProjectionResult.AlreadyCurrent))
        {
            throw new InvalidOperationException(
                $"The committed order response was not applied because its authority is {adoption}.");
        }
    }

    private async Task<bool> IsCurrentAuthorityAsync(OrderCommandAuthority authority)
    {
        if (!hostedOrders.IsCurrentAuthority(authority.Projection))
        {
            return false;
        }
        var connection = await localState.LoadConnectionSettingsAsync();
        return string.Equals(
            authority.Connection.ConnectionScopeId,
            connection.ConnectionScopeId,
            StringComparison.Ordinal);
    }

    private async Task<long> ResolveHostedOrderRevisionAsync(
        TradeOrder order,
        OrderCommandAuthority authority,
        CancellationToken cancellationToken)
    {
        if (!await IsCurrentAuthorityAsync(authority))
        {
            throw new InvalidOperationException(
                "The hosted order authority changed before its revision was resolved.");
        }

        var projected = hostedOrders.Get(order.Id);
        if (projected != null)
        {
            return !projected.Deleted &&
                   projected.Order?.CompanyProfileId == order.CompanyProfileId
                ? projected.ObjectRevision
                : 0;
        }

        var profileId = authority.Connection.ProfileScopeId
            ?? throw new InvalidOperationException(
                "Hosted order revision lookup requires a captured profile authority.");
        var objectId = order.Id.ToString("D");
        var revision = await localState.LoadObjectRevisionAsync(
            profileId,
            ProfileSyncCollections.TradeOrders,
            objectId);
        if (!await IsCurrentAuthorityAsync(authority))
        {
            throw new InvalidOperationException(
                "The hosted order authority changed while its revision was being read.");
        }
        if (revision > 0)
        {
            return revision;
        }

        revision = await profileSync.EnsureHostedObjectRevisionAsync(
            ProfileSyncCollections.TradeOrders,
            objectId,
            authority.Connection,
            cancellationToken);
        if (!await IsCurrentAuthorityAsync(authority))
        {
            throw new InvalidOperationException(
                "The hosted order authority changed while its revision was being acquired.");
        }
        return revision;
    }

    private async Task RequireCurrentPublicationAuthorityAsync(
        OrderCommandAuthority authority,
        Guid expectedOrderId,
        TradeCommissionPublicationProjection publication)
    {
        if (publication.OrderId != expectedOrderId)
        {
            throw new InvalidOperationException(
                "The collaboration response returned a publication for a different order.");
        }
        if (!await IsCurrentAuthorityAsync(authority))
        {
            InvalidateDictionaryAuthority();
            throw new InvalidOperationException(
                "The hosted order authority changed while the collaboration response was in progress.");
        }
    }

    private bool IsDictionaryAuthorityCurrent()
    {
        var current = hostedOrders.CaptureAuthorityScope();
        return string.Equals(
                   _dictionaryProfileId,
                   current.ProfileId,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   _dictionaryConnectionScopeId,
                   current.ConnectionScopeId,
                   StringComparison.Ordinal);
    }

    private void AdoptDictionaryAuthority(OrderCommandAuthority authority)
    {
        if (!string.Equals(
                _dictionaryProfileId,
                authority.Projection.ProfileId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                _dictionaryConnectionScopeId,
                authority.Projection.ConnectionScopeId,
                StringComparison.Ordinal))
        {
            _publications.Clear();
            _publicationRefreshedAtUtc.Clear();
        }
        _dictionaryProfileId = authority.Projection.ProfileId;
        _dictionaryConnectionScopeId = authority.Projection.ConnectionScopeId;
    }

    private void InvalidateDictionaryAuthority()
    {
        _publications.Clear();
        _publicationRefreshedAtUtc.Clear();
        _dictionaryProfileId = null;
        _dictionaryConnectionScopeId = null;
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
