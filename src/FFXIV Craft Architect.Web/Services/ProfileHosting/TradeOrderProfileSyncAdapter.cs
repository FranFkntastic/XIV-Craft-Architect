using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class TradeOrderProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private readonly TradeOperationsPersistenceService _tradeOperations;

    public TradeOrderProfileSyncAdapter(TradeOperationsPersistenceService tradeOperations)
    {
        _tradeOperations = tradeOperations;
    }

    public string Collection => ProfileSyncCollections.TradeOrders;

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct)
    {
        var profile = await _tradeOperations.GetOrCreateActiveCompanyProfileAsync();
        var orders = await _tradeOperations.LoadOrdersAsync(profile.Id);
        var now = DateTime.UtcNow;
        return orders.Select(order => ToEnvelope(order, now)).ToArray();
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        var order = JsonSerializer.Deserialize<TradeOrder>(envelope.PayloadJson);
        if (order == null)
        {
            throw new InvalidOperationException($"Hosted Trade order payload '{envelope.ObjectId}' could not be deserialized.");
        }

        await _tradeOperations.SaveOrderAsync(order);
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        if (!Guid.TryParse(objectId, out var orderId))
        {
            throw new InvalidOperationException($"Hosted Trade order id '{objectId}' is not a valid GUID.");
        }

        await _tradeOperations.DeleteOrderAsync(orderId);
    }

    private static ProfileSyncObjectEnvelope ToEnvelope(TradeOrder order, DateTime updatedAtUtc)
    {
        return new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.TradeOrders,
            ObjectId = order.Id.ToString("D"),
            PayloadJson = JsonSerializer.Serialize(order),
            UpdatedAtUtc = updatedAtUtc
        };
    }
}
