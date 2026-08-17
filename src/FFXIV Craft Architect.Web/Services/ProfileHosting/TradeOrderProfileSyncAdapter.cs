using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class TradeOrderProfileSyncAdapter :
    IProfileSyncCollectionAdapter,
    IProfileSyncSingleObjectAdapter,
    IHostedOrderProfileSyncAdapter
{
    private static readonly JsonSerializerOptions JsonOptions =
        ProfileSyncJson.CreateOptions();
    private readonly TradeOperationsPersistenceService _tradeOperations;
    private readonly HostedOrderProjectionStore _projections;
    private readonly ProfileSyncLocalStateService _localState;
    private readonly TradeOrderArchiveSummaryStore? _archiveSummaries;

    public TradeOrderProfileSyncAdapter(
        TradeOperationsPersistenceService tradeOperations,
        HostedOrderProjectionStore projections,
        ProfileSyncLocalStateService localState,
        TradeOrderArchiveSummaryStore? archiveSummaries = null)
    {
        _tradeOperations = tradeOperations;
        _projections = projections;
        _localState = localState;
        _archiveSummaries = archiveSummaries;
    }

    public string Collection => ProfileSyncCollections.TradeOrders;

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct)
    {
        var orders = await _tradeOperations.LoadAllOrdersAsync();
        ct.ThrowIfCancellationRequested();

        var now = DateTime.UtcNow;
        return orders.Select(order => ToEnvelope(order, now)).ToArray();
    }

    public async Task<ProfileSyncObjectEnvelope?> LoadLocalObjectAsync(
        string objectId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParse(objectId, out var orderId))
        {
            return null;
        }

        var order = await _tradeOperations.LoadOrderAsync(orderId);
        return order == null ? null : ToEnvelope(order, DateTime.UtcNow);
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tombstoneConnection = await _localState.LoadConnectionSettingsAsync();
        var clearDeepArchiveTombstoneAfterAdoption = false;
        if (tombstoneConnection.ProfileScopeId is { } tombstoneScope)
        {
            var tombstoneRevision = (await _localState.LoadOrderTombstonesAsync(tombstoneScope))
                .GetValueOrDefault(envelope.ObjectId);
            if (tombstoneRevision > 0)
            {
                if (envelope.DeepArchived && envelope.Revision == tombstoneRevision)
                {
                    clearDeepArchiveTombstoneAfterAdoption = true;
                }
                else if (envelope.Revision <= tombstoneRevision)
                {
                    return;
                }
                else
                {
                    await _localState.ClearOrderTombstoneAsync(tombstoneScope, envelope.ObjectId);
                }
            }
        }

        if (envelope.IsSummary)
        {
            var archiveSummaries = _archiveSummaries
                ?? throw new InvalidOperationException(
                    "Archived Trade order summary storage is unavailable.");
            var summary = TradeOrderArchiveSummaryCodec.Deserialize(
                envelope.SummaryJson ?? string.Empty,
                envelope.ObjectId);
            if (!TradeOrderStatusWorkflow.IsArchived(summary.Status))
            {
                throw new InvalidOperationException(
                    $"Archived Trade order summary '{envelope.ObjectId}' has active status '{summary.Status}'.");
            }
            var summaryConnection = await _localState.LoadConnectionSettingsAsync();
            var connectionScopeId = summaryConnection.ConnectionScopeId
                ?? throw new InvalidOperationException(
                    "Archived Trade order summary persistence requires a connected profile authority.");
            await archiveSummaries.UpsertAsync(summary, envelope.Revision, connectionScopeId);
            await _localState.SaveObjectRevisionAsync(
                summaryConnection,
                ProfileSyncCollections.TradeOrders,
                envelope.ObjectId,
                envelope.Revision);
            return;
        }

        var order = JsonSerializer.Deserialize<TradeOrder>(
            envelope.PayloadJson,
            JsonOptions);
        if (order == null)
        {
            throw new InvalidOperationException($"Hosted Trade order payload '{envelope.ObjectId}' could not be deserialized.");
        }

        await _tradeOperations.RequireCompanyProfileAsync(
            order.CompanyProfileId,
            "order",
            envelope.ObjectId);
        var connection = await _localState.LoadConnectionSettingsAsync();
        var authority = _projections.CaptureAuthorityScope();
        if (!string.Equals(
                authority.ConnectionScopeId,
                connection.ConnectionScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Hosted Trade order '{envelope.ObjectId}' belongs to a previous connection scope.");
        }
        Func<HostedOrderProjectionSnapshot, Task> persist = async candidate =>
        {
            await _localState.PersistHostedTradeOrderStateAsync(
                connection,
                candidate.Order,
                candidate.OrderId,
                candidate.ObjectRevision,
                candidate.Deleted);
            if (!await IsCurrentAuthorityAsync(authority, connection.ConnectionScopeId))
            {
                throw new InvalidOperationException(
                    $"Hosted Trade order '{envelope.ObjectId}' changed authority while browser persistence was in progress.");
            }
        };
        var adoption = envelope.DeepArchived
            ? await _projections.AdoptAndPersistDeepArchivedOrderAsync(
                authority,
                order,
                envelope.Revision,
                persist,
                () => IsCurrentAuthorityAsync(authority, connection.ConnectionScopeId))
            : await _projections.AdoptAndPersistCommittedOrderAsync(
                authority,
                order,
                envelope.Revision,
                persist,
                () => IsCurrentAuthorityAsync(authority, connection.ConnectionScopeId));
        if (adoption is not (
            HostedOrderCommittedProjectionResult.Adopted or
            HostedOrderCommittedProjectionResult.AlreadyCurrent))
        {
            throw new InvalidOperationException(
                $"Hosted Trade order '{envelope.ObjectId}' could not be applied because its authority is {adoption}.");
        }
        if (clearDeepArchiveTombstoneAfterAdoption &&
            tombstoneConnection.ProfileScopeId is { } adoptedTombstoneScope)
        {
            await _localState.ClearOrderTombstoneAsync(
                adoptedTombstoneScope,
                envelope.ObjectId);
        }
        if (_archiveSummaries != null && connection.ConnectionScopeId != null)
        {
            await _archiveSummaries.RemoveIfSupersededAsync(
                connection.ConnectionScopeId,
                order.Id,
                envelope.Revision);
        }
    }

    public async Task ApplyRemoteDeletionAsync(
        Guid orderId,
        Guid companyProfileId,
        long revision,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var connection = await _localState.LoadConnectionSettingsAsync();
        var authority = _projections.CaptureAuthorityScope();
        if (!string.Equals(
                authority.ConnectionScopeId,
                connection.ConnectionScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Hosted Trade order '{orderId:D}' belongs to a previous connection scope.");
        }

        var adoption = await _projections.AdoptAndPersistCommittedTombstoneAsync(
            authority,
            orderId,
            companyProfileId,
            revision,
            async candidate =>
            {
                ct.ThrowIfCancellationRequested();
                await _localState.PersistHostedTradeOrderStateAsync(
                    connection,
                    candidate.Order,
                    candidate.OrderId,
                    candidate.ObjectRevision,
                    candidate.Deleted);
                if (!await IsCurrentAuthorityAsync(authority, connection.ConnectionScopeId))
                {
                    throw new InvalidOperationException(
                        $"Hosted Trade order '{orderId:D}' changed authority while browser persistence was in progress.");
                }
            },
            () => IsCurrentAuthorityAsync(authority, connection.ConnectionScopeId));
        if (adoption is not (
            HostedOrderCommittedProjectionResult.Adopted or
            HostedOrderCommittedProjectionResult.AlreadyCurrent))
        {
            throw new InvalidOperationException(
                $"Hosted Trade order '{orderId:D}' deletion could not be applied because its authority is {adoption}.");
        }
        if (connection.ProfileScopeId is { } tombstoneScope)
        {
            await _localState.SaveOrderTombstoneAsync(
                tombstoneScope,
                orderId.ToString("D"),
                revision);
        }
        if (_archiveSummaries != null)
        {
            await _archiveSummaries.RemoveAsync(connection.ConnectionScopeId!, orderId);
        }
    }

    public async Task ReapResurrectedOrdersAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        var tombstones = await _localState.LoadOrderTombstonesAsync(profileId);
        if (tombstones.Count == 0)
        {
            return;
        }

        foreach (var tombstone in tombstones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(tombstone.Key, out var orderId))
            {
                continue;
            }

            var order = await _tradeOperations.LoadOrderAsync(orderId);
            if (order == null)
            {
                continue;
            }

            var objectId = orderId.ToString("D");
            var localRevision = await _localState.LoadObjectRevisionAsync(
                profileId,
                ProfileSyncCollections.TradeOrders,
                objectId);
            if (localRevision <= tombstone.Value)
            {
                await _tradeOperations.DeleteOrderAsync(order.Id);
            }
        }
    }

    private async Task<bool> IsCurrentAuthorityAsync(
        HostedOrderAuthorityScope authority,
        string? connectionScopeId)
    {
        if (!_projections.IsCurrentAuthority(authority))
        {
            return false;
        }
        var current = await _localState.LoadConnectionSettingsAsync();
        return string.Equals(
            connectionScopeId,
            current.ConnectionScopeId,
            StringComparison.Ordinal);
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        if (!Guid.TryParse(objectId, out var orderId))
        {
            throw new InvalidOperationException($"Hosted Trade order id '{objectId}' is not a valid GUID.");
        }

        var connection = await _localState.LoadConnectionSettingsAsync();
        if (!await _tradeOperations.DeleteOrderAsync(orderId, connection.ConnectionScopeId))
        {
            throw new InvalidOperationException(
                $"Browser storage could not delete hosted Trade order '{objectId}'.");
        }
        if (_archiveSummaries != null && connection.ConnectionScopeId != null)
        {
            await _archiveSummaries.RemoveAsync(connection.ConnectionScopeId, orderId);
        }
    }

    private static ProfileSyncObjectEnvelope ToEnvelope(TradeOrder order, DateTime updatedAtUtc)
    {
        return new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.TradeOrders,
            ObjectId = order.Id.ToString("D"),
            PayloadJson = JsonSerializer.Serialize(order, JsonOptions),
            UpdatedAtUtc = updatedAtUtc
        };
    }
}
