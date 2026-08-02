using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class TradeOrderProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private static readonly JsonSerializerOptions JsonOptions =
        ProfileSyncJson.CreateOptions();
    private readonly TradeOperationsPersistenceService _tradeOperations;
    private readonly HostedOrderProjectionStore _projections;
    private readonly ProfileSyncLocalStateService _localState;

    public TradeOrderProfileSyncAdapter(
        TradeOperationsPersistenceService tradeOperations,
        HostedOrderProjectionStore projections,
        ProfileSyncLocalStateService localState)
    {
        _tradeOperations = tradeOperations;
        _projections = projections;
        _localState = localState;
    }

    public string Collection => ProfileSyncCollections.TradeOrders;

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct)
    {
        var profiles = await _tradeOperations.LoadCompanyProfilesAsync();
        var orders = new List<TradeOrder>();
        foreach (var profile in profiles.OrderBy(profile => profile.Id))
        {
            ct.ThrowIfCancellationRequested();
            orders.AddRange(await _tradeOperations.LoadOrdersAsync(profile.Id));
        }

        var now = DateTime.UtcNow;
        return orders.Select(order => ToEnvelope(order, now)).ToArray();
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
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
        var authority = _projections.CaptureAuthorityScope();
        var previous = _projections.Get(order.Id);
        var adoption = await _projections.AdoptAndPersistCommittedOrderAsync(
            authority,
            order,
            envelope.Revision,
            async candidate =>
            {
                var persisted = candidate.Deleted
                    ? await _tradeOperations.DeleteOrderAsync(candidate.OrderId)
                    : await _tradeOperations.ApplyCanonicalOrderAsync(candidate.Order!);
                if (!persisted)
                {
                    throw new InvalidOperationException(
                        $"Browser storage could not apply hosted Trade order '{envelope.ObjectId}'.");
                }
                await _localState.SaveObjectRevisionAsync(
                    ProfileSyncCollections.TradeOrders,
                    candidate.OrderId.ToString("D"),
                    candidate.ObjectRevision);
            },
            rollback: previous?.Order == null
                ? null
                : async () =>
                {
                    _projections.TryRollbackCommittedOrder(
                        authority,
                        envelope.Revision,
                        previous);
                    await _tradeOperations.ApplyCanonicalOrderAsync(previous.Order);
                    await _localState.SaveObjectRevisionAsync(
                        ProfileSyncCollections.TradeOrders,
                        previous.OrderId.ToString("D"),
                        previous.ObjectRevision);
                });
        if (adoption is not (
            HostedOrderCommittedProjectionResult.Adopted or
            HostedOrderCommittedProjectionResult.AlreadyCurrent))
        {
            throw new InvalidOperationException(
                $"Hosted Trade order '{envelope.ObjectId}' could not be applied because its authority is {adoption}.");
        }
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        if (!Guid.TryParse(objectId, out var orderId))
        {
            throw new InvalidOperationException($"Hosted Trade order id '{objectId}' is not a valid GUID.");
        }

        if (!await _tradeOperations.DeleteOrderAsync(orderId))
        {
            throw new InvalidOperationException(
                $"Browser storage could not delete hosted Trade order '{objectId}'.");
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
