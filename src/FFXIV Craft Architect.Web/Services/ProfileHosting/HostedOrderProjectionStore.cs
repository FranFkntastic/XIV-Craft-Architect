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
    private string? _profileId;

    public event Action<HostedOrderProjectionSnapshot>? Changed;
    public event Action? Reset;

    public void ResetForProfile(string? profileId)
    {
        var changed = false;
        lock (_gate)
        {
            if (string.Equals(_profileId, profileId, StringComparison.Ordinal))
            {
                return;
            }

            _profileId = profileId;
            _orders.Clear();
            changed = true;
        }
        if (changed)
        {
            Reset?.Invoke();
        }
    }

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
        HostedOrderProjectionSnapshot candidate;
        bool accepted;
        lock (_gate)
        {
            var current = _orders.GetValueOrDefault(order.Id);
            candidate = new HostedOrderProjectionSnapshot(
                order.Id,
                order.CompanyProfileId,
                objectRevision,
                current?.CompanyRevision,
                order,
                current?.ObjectRevision == objectRevision
                    ? current.OwnerProjection
                    : null,
                Deleted: false);
            accepted = TryAcceptUnderLock(candidate);
        }
        return NotifyIfAccepted(candidate, accepted);
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

        HostedOrderProjectionSnapshot candidate;
        bool accepted;
        lock (_gate)
        {
            var existing = _orders.GetValueOrDefault(orderId);
            candidate = new HostedOrderProjectionSnapshot(
                orderId,
                existing?.CompanyProfileId,
                objectRevision,
                existing?.CompanyRevision,
                Order: null,
                OwnerProjection: null,
                Deleted: true);
            accepted = TryAcceptUnderLock(candidate);
        }
        return NotifyIfAccepted(candidate, accepted);
    }

    public void ClearOwner(Guid orderId)
    {
        HostedOrderProjectionSnapshot? changed = null;
        lock (_gate)
        {
            if (_orders.TryGetValue(orderId, out var current) &&
                current.OwnerProjection != null)
            {
                changed = current with { OwnerProjection = null };
                _orders[orderId] = changed;
            }
        }
        if (changed != null)
        {
            Changed?.Invoke(changed);
        }
    }

    private bool TryPublish(HostedOrderProjectionSnapshot candidate)
    {
        bool accepted;
        lock (_gate)
        {
            accepted = TryAcceptUnderLock(candidate);
        }
        return NotifyIfAccepted(candidate, accepted);
    }

    private bool TryAcceptUnderLock(HostedOrderProjectionSnapshot candidate)
    {
        if (_orders.TryGetValue(candidate.OrderId, out var current))
        {
            ValidateIdentity(current, candidate);
            if (candidate.ObjectRevision < current.ObjectRevision ||
                (candidate.ObjectRevision == current.ObjectRevision &&
                 current.Deleted && !candidate.Deleted))
            {
                return false;
            }

            if (candidate.ObjectRevision == current.ObjectRevision &&
                current.Deleted == candidate.Deleted &&
                (candidate.CompanyRevision ?? 0) <= (current.CompanyRevision ?? 0) &&
                (candidate.OwnerProjection?.ObjectRevision.Value ?? 0) <=
                (current.OwnerProjection?.ObjectRevision.Value ?? 0))
            {
                return false;
            }
        }

        _orders[candidate.OrderId] = candidate;
        return true;
    }

    private bool NotifyIfAccepted(
        HostedOrderProjectionSnapshot candidate,
        bool accepted)
    {
        if (accepted)
        {
            Changed?.Invoke(candidate);
        }
        return accepted;
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
