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
    private HostedOrderRestoreState _restoreState = HostedOrderRestoreState.Inactive(DateTime.UtcNow);

    public event Action<HostedOrderProjectionSnapshot>? Changed;
    public event Action? Reset;
    public event Action<HostedOrderRestoreState>? RestoreStateChanged;

    public HostedOrderRestoreState RestoreState
    {
        get
        {
            lock (_gate)
            {
                return _restoreState;
            }
        }
    }

    public void BeginProfileRestore(
        string profileId,
        bool hasTrustedProjection,
        long lastAppliedRevision,
        DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        HostedOrderRestoreState next;
        var reset = false;
        lock (_gate)
        {
            var retainIdentityOnly = !string.IsNullOrWhiteSpace(_profileId) &&
                                     string.Equals(
                                         _profileId,
                                         profileId,
                                         StringComparison.OrdinalIgnoreCase) &&
                                     _restoreState.Stage == HostedOrderRestoreStage.IdentityOnly;
            var scopeChanged = _profileId != null &&
                               !string.Equals(_profileId, profileId, StringComparison.OrdinalIgnoreCase);
            if (scopeChanged)
            {
                _orders.Clear();
                reset = true;
            }

            var retainedTrust = !scopeChanged &&
                                (_restoreState.HasTrustedProjection || hasTrustedProjection);
            _profileId = profileId;
            var nextRevision = scopeChanged
                ? lastAppliedRevision
                : Math.Max(_restoreState.LastAppliedRevision, lastAppliedRevision);
            next = HostedOrderRestoreState.BeginProfile(
                profileId,
                retainedTrust,
                nextRevision,
                scopeChanged,
                now);
            if (retainIdentityOnly)
            {
                next = next with
                {
                    Stage = HostedOrderRestoreStage.IdentityOnly,
                    Failure = _restoreState.Failure,
                    Message = _restoreState.Message,
                    ProgressStage = "Verifying restored profile authority"
                };
            }
            _restoreState = next;
        }

        if (reset)
        {
            Reset?.Invoke();
        }
        RestoreStateChanged?.Invoke(next);
    }

    public bool TryPublishRestoreState(HostedOrderRestoreState next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var accepted = false;
        lock (_gate)
        {
            if (!string.Equals(_profileId, next.ProfileId, StringComparison.OrdinalIgnoreCase) ||
                next.LastAppliedRevision < _restoreState.LastAppliedRevision)
            {
                return false;
            }

            if (next == _restoreState)
            {
                return false;
            }

            _restoreState = next;
            accepted = true;
        }

        if (accepted)
        {
            RestoreStateChanged?.Invoke(next);
        }
        return accepted;
    }

    public void ResetForProfile(string? profileId)
    {
        var changed = false;
        HostedOrderRestoreState? restoreState = null;
        lock (_gate)
        {
            if (string.Equals(_profileId, profileId, StringComparison.Ordinal))
            {
                return;
            }

            _profileId = profileId;
            _orders.Clear();
            _restoreState = profileId == null
                ? HostedOrderRestoreState.Inactive(DateTime.UtcNow)
                : HostedOrderRestoreState.BeginProfile(
                    profileId,
                    hasTrustedProjection: false,
                    lastAppliedRevision: 0,
                    scopeChanged: true,
                    DateTime.UtcNow);
            restoreState = _restoreState;
            changed = true;
        }
        if (changed)
        {
            Reset?.Invoke();
            RestoreStateChanged?.Invoke(restoreState!);
        }
    }

    public HostedOrderProjectionSnapshot? Get(Guid orderId)
    {
        lock (_gate)
        {
            return _orders.GetValueOrDefault(orderId);
        }
    }

    public IReadOnlyList<HostedOrderProjectionSnapshot> GetAll(Guid? companyProfileId = null)
    {
        lock (_gate)
        {
            return _orders.Values
                .Where(snapshot =>
                    !companyProfileId.HasValue ||
                    snapshot.CompanyProfileId == companyProfileId)
                .OrderBy(snapshot => snapshot.OrderId)
                .ToArray();
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
        if (candidate.ObjectRevision > _restoreState.LastAppliedRevision)
        {
            _restoreState = _restoreState with
            {
                LastAppliedRevision = candidate.ObjectRevision,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }
        return true;
    }

    private bool NotifyIfAccepted(
        HostedOrderProjectionSnapshot candidate,
        bool accepted)
    {
        if (accepted)
        {
            Changed?.Invoke(candidate);
            RestoreStateChanged?.Invoke(RestoreState);
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
