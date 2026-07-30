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
        var order = JsonSerializer.Deserialize<TradeOrder>(envelope.PayloadJson);
        if (order == null)
        {
            throw new InvalidOperationException($"Hosted Trade order payload '{envelope.ObjectId}' could not be deserialized.");
        }

        await _tradeOperations.RequireCompanyProfileAsync(
            order.CompanyProfileId,
            "order",
            envelope.ObjectId);
        if (!await _tradeOperations.ApplyCanonicalOrderAsync(order))
        {
            throw new InvalidOperationException(
                $"Browser storage could not apply hosted Trade order '{envelope.ObjectId}'.");
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
            PayloadJson = JsonSerializer.Serialize(order),
            UpdatedAtUtc = updatedAtUtc
        };
    }
}
