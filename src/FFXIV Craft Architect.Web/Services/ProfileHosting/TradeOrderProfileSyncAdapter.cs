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
        var connection = await _localState.LoadConnectionSettingsAsync();
        var authority = _projections.CaptureAuthorityScope();
        if (!string.Equals(
                authority.ConnectionScopeId,
                connection.ConnectionScopeId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Hosted Trade order '{envelope.ObjectId}' belongs to a previous connection scope.");
        }
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
                if (!await IsCurrentAuthorityAsync(authority, connection.ConnectionScopeId))
                {
                    throw new InvalidOperationException(
                        $"Hosted Trade order '{envelope.ObjectId}' changed authority while browser persistence was in progress.");
                }
                await _localState.SaveObjectRevisionAsync(
                    connection,
                    ProfileSyncCollections.TradeOrders,
                    candidate.OrderId.ToString("D"),
                    candidate.ObjectRevision);
            },
            () => IsCurrentAuthorityAsync(authority, connection.ConnectionScopeId));
        if (adoption is not (
            HostedOrderCommittedProjectionResult.Adopted or
            HostedOrderCommittedProjectionResult.AlreadyCurrent))
        {
            throw new InvalidOperationException(
                $"Hosted Trade order '{envelope.ObjectId}' could not be applied because its authority is {adoption}.");
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
            StringComparison.OrdinalIgnoreCase);
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
