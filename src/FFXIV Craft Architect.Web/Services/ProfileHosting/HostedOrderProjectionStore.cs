using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed record HostedOrderProjectionSnapshot(
    Guid OrderId,
    Guid? CompanyProfileId,
    long ObjectRevision,
    long? CompanyRevision,
    TradeOrder? Order,
    CompanyCommissionOwnerProjection? OwnerProjection,
    bool Deleted);

public sealed class HostedOrderProjectionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HostedOrderProjectionSnapshot> _orders = [];

    public event Action<HostedOrderProjectionSnapshot>? Changed;

    public HostedOrderProjectionSnapshot? Get(Guid orderId)
    {
        lock (_gate)
        {
            return _orders.GetValueOrDefault(orderId);
        }
    }

    public CompanyCommissionOwnerProjection? GetOwnerProjection(Guid orderId) =>
        Get(orderId)?.OwnerProjection;

    public bool TryPublishRemoteOrder(TradeOrder order, long objectRevision)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectRevision);
        var current = Get(order.Id);
        return TryPublish(new HostedOrderProjectionSnapshot(
            order.Id,
            order.CompanyProfileId,
            objectRevision,
            current?.CompanyRevision,
            order,
            current?.OwnerProjection,
            Deleted: false));
    }

    public bool TryPublishOwner(CompanyCommissionOwnerProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return TryPublish(new HostedOrderProjectionSnapshot(
            projection.Order.Id,
            projection.Order.CompanyProfileId,
            projection.ObjectRevision.Value,
            projection.CompanyRevision.Value,
            projection.Order,
            projection,
            Deleted: false));
    }

    public bool TryPublishTombstone(Guid orderId, long objectRevision)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("A hosted order identity is required.", nameof(orderId));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectRevision);

        HostedOrderProjectionSnapshot? existing;
        lock (_gate)
        {
            existing = _orders.GetValueOrDefault(orderId);
        }

        return TryPublish(new HostedOrderProjectionSnapshot(
            orderId,
            existing?.CompanyProfileId,
            objectRevision,
            existing?.CompanyRevision,
            Order: null,
            OwnerProjection: null,
            Deleted: true));
    }

    public void Remove(Guid orderId)
    {
        lock (_gate)
        {
            _orders.Remove(orderId);
        }
    }

    private bool TryPublish(HostedOrderProjectionSnapshot candidate)
    {
        var notify = false;
        lock (_gate)
        {
            if (_orders.TryGetValue(candidate.OrderId, out var current))
            {
                ValidateIdentity(current, candidate);
                if (candidate.ObjectRevision < current.ObjectRevision)
                {
                    return false;
                }

                if (candidate.ObjectRevision == current.ObjectRevision)
                {
                    var currentOwnerRevision =
                        current.OwnerProjection?.ObjectRevision.Value ?? 0;
                    var candidateOwnerRevision =
                        candidate.OwnerProjection?.ObjectRevision.Value ?? 0;
                    if (candidateOwnerRevision > currentOwnerRevision)
                    {
                        _orders[candidate.OrderId] = candidate;
                    }
                    return false;
                }
            }

            _orders[candidate.OrderId] = candidate;
            notify = true;
        }

        if (notify)
        {
            Changed?.Invoke(candidate);
        }
        return notify;
    }

    private static void ValidateIdentity(
        HostedOrderProjectionSnapshot current,
        HostedOrderProjectionSnapshot candidate)
    {
        if (current.CompanyProfileId.HasValue &&
            candidate.CompanyProfileId.HasValue &&
            current.CompanyProfileId != candidate.CompanyProfileId)
        {
            throw new InvalidOperationException(
                "A hosted order projection changed company identity.");
        }

        var currentCommission = current.Order?.CompanyCommission;
        var candidateCommission = candidate.Order?.CompanyCommission;
        if (currentCommission != null &&
            candidateCommission != null &&
            (currentCommission.CompanyId != candidateCommission.CompanyId ||
             currentCommission.CommissionId != candidateCommission.CommissionId))
        {
            throw new InvalidOperationException(
                "A hosted order projection changed commission identity.");
        }
    }
}
