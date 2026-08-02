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

public readonly record struct HostedOrderAuthorityScope(string? ProfileId, long Epoch);

public enum HostedOrderCommittedProjectionResult
{
    Adopted,
    AlreadyCurrent,
    Stale,
    ScopeChanged,
    IdentityMismatch
}

public sealed class HostedOrderProjectionStore
{
    private const int CommittedWinnerReconciliationLimit = 4;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HostedOrderProjectionSnapshot> _orders = [];
    private string? _profileId;
    private long _scopeEpoch;
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

    public HostedOrderAuthorityScope CaptureAuthorityScope()
    {
        lock (_gate)
        {
            return new HostedOrderAuthorityScope(_profileId, _scopeEpoch);
        }
    }

    public bool IsCurrentAuthority(HostedOrderAuthorityScope authority)
    {
        lock (_gate)
        {
            return IsCurrentAuthorityUnderLock(authority);
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
            var profileChanged = !string.Equals(
                _profileId,
                profileId,
                StringComparison.OrdinalIgnoreCase);
            var scopeChanged = _profileId != null && profileChanged;
            if (scopeChanged)
            {
                _orders.Clear();
                reset = true;
            }
            if (profileChanged)
            {
                _scopeEpoch++;
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
            _scopeEpoch++;
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

    public HostedOrderCommittedProjectionResult TryAdoptCommittedOrder(
        HostedOrderAuthorityScope authority,
        TradeOrder order,
        long objectRevision)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectRevision);
        HostedOrderProjectionSnapshot candidate;
        HostedOrderCommittedProjectionResult result;
        lock (_gate)
        {
            if (!IsCurrentAuthorityUnderLock(authority))
            {
                return HostedOrderCommittedProjectionResult.ScopeChanged;
            }

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
            result = ClassifyCommittedCandidateUnderLock(current, candidate);
            if (result == HostedOrderCommittedProjectionResult.Adopted)
            {
                _orders[candidate.OrderId] = candidate;
                AdvanceRestoreRevisionUnderLock(candidate.ObjectRevision);
            }
        }

        if (result == HostedOrderCommittedProjectionResult.Adopted)
        {
            Changed?.Invoke(candidate);
            RestoreStateChanged?.Invoke(RestoreState);
        }
        return result;
    }

    public async Task<HostedOrderCommittedProjectionResult> AdoptAndPersistCommittedOrderAsync(
        HostedOrderAuthorityScope authority,
        TradeOrder order,
        long objectRevision,
        Func<HostedOrderProjectionSnapshot, Task> persist,
        Func<Task<bool>>? authorityIsCurrent = null,
        Func<Task>? rollback = null)
    {
        ArgumentNullException.ThrowIfNull(persist);
        authorityIsCurrent ??= () => Task.FromResult(IsCurrentAuthority(authority));
        if (!await authorityIsCurrent())
        {
            return HostedOrderCommittedProjectionResult.ScopeChanged;
        }

        var adoption = TryAdoptCommittedOrder(authority, order, objectRevision);
        if (adoption is not (
            HostedOrderCommittedProjectionResult.Adopted or
            HostedOrderCommittedProjectionResult.AlreadyCurrent))
        {
            return adoption;
        }

        var candidate = Get(order.Id);
        for (var attempt = 0; attempt < CommittedWinnerReconciliationLimit; attempt++)
        {
            if (candidate == null)
            {
                return HostedOrderCommittedProjectionResult.Stale;
            }
            if (!await authorityIsCurrent())
            {
                return HostedOrderCommittedProjectionResult.ScopeChanged;
            }

            await persist(candidate);
            if (!await authorityIsCurrent())
            {
                if (rollback != null)
                {
                    await rollback();
                }
                return HostedOrderCommittedProjectionResult.ScopeChanged;
            }

            var winner = Get(order.Id);
            if (winner == null)
            {
                return HostedOrderCommittedProjectionResult.Stale;
            }
            if (winner.ObjectRevision == candidate.ObjectRevision &&
                winner.Deleted == candidate.Deleted)
            {
                return adoption;
            }
            candidate = winner;
        }

        throw new InvalidOperationException(
            "The hosted order changed repeatedly while browser persistence reconciled its committed winner.");
    }

    public bool TryRollbackCommittedOrder(
        HostedOrderAuthorityScope authority,
        long expectedCommittedRevision,
        HostedOrderProjectionSnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var rolledBack = false;
        lock (_gate)
        {
            if (!IsCurrentAuthorityUnderLock(authority) ||
                !_orders.TryGetValue(previous.OrderId, out var current) ||
                current.ObjectRevision != expectedCommittedRevision)
            {
                return false;
            }
            _orders[previous.OrderId] = previous;
            rolledBack = true;
        }
        if (rolledBack)
        {
            Changed?.Invoke(previous);
        }
        return rolledBack;
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

    public bool TryPublishTombstone(
        Guid orderId,
        long objectRevision,
        Guid? companyProfileId = null)
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
            companyProfileId ??= existing?.CompanyProfileId;
            if (!companyProfileId.HasValue)
            {
                throw new InvalidOperationException(
                    "A cold hosted-order tombstone requires its company profile identity.");
            }
            candidate = new HostedOrderProjectionSnapshot(
                orderId,
                companyProfileId,
                objectRevision,
                existing?.CompanyRevision,
                Order: null,
                OwnerProjection: null,
                Deleted: true);
            accepted = TryAcceptUnderLock(candidate);
        }
        return NotifyIfAccepted(candidate, accepted);
    }

    public HostedOrderCommittedProjectionResult TryAdoptCommittedTombstone(
        HostedOrderAuthorityScope authority,
        Guid orderId,
        Guid companyProfileId,
        long objectRevision)
    {
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("A hosted order identity is required.", nameof(orderId));
        }
        if (companyProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A hosted order company identity is required.",
                nameof(companyProfileId));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectRevision);

        HostedOrderProjectionSnapshot candidate;
        HostedOrderCommittedProjectionResult result;
        lock (_gate)
        {
            if (!IsCurrentAuthorityUnderLock(authority))
            {
                return HostedOrderCommittedProjectionResult.ScopeChanged;
            }

            var current = _orders.GetValueOrDefault(orderId);
            candidate = new HostedOrderProjectionSnapshot(
                orderId,
                companyProfileId,
                objectRevision,
                current?.CompanyRevision,
                Order: null,
                OwnerProjection: null,
                Deleted: true);
            result = ClassifyCommittedCandidateUnderLock(current, candidate);
            if (result == HostedOrderCommittedProjectionResult.Adopted)
            {
                _orders[candidate.OrderId] = candidate;
                AdvanceRestoreRevisionUnderLock(candidate.ObjectRevision);
            }
        }

        if (result == HostedOrderCommittedProjectionResult.Adopted)
        {
            Changed?.Invoke(candidate);
            RestoreStateChanged?.Invoke(RestoreState);
        }
        return result;
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
        AdvanceRestoreRevisionUnderLock(candidate.ObjectRevision);
        return true;
    }

    private HostedOrderCommittedProjectionResult ClassifyCommittedCandidateUnderLock(
        HostedOrderProjectionSnapshot? current,
        HostedOrderProjectionSnapshot candidate)
    {
        if (current == null)
        {
            return HostedOrderCommittedProjectionResult.Adopted;
        }

        try
        {
            ValidateIdentity(current, candidate);
        }
        catch (InvalidOperationException)
        {
            return HostedOrderCommittedProjectionResult.IdentityMismatch;
        }

        if (candidate.ObjectRevision < current.ObjectRevision ||
            (candidate.ObjectRevision == current.ObjectRevision &&
             current.Deleted && !candidate.Deleted))
        {
            return HostedOrderCommittedProjectionResult.Stale;
        }

        return candidate.ObjectRevision == current.ObjectRevision &&
               candidate.Deleted == current.Deleted
            ? HostedOrderCommittedProjectionResult.AlreadyCurrent
            : HostedOrderCommittedProjectionResult.Adopted;
    }

    private bool IsCurrentAuthorityUnderLock(HostedOrderAuthorityScope authority) =>
        authority.Epoch == _scopeEpoch &&
        string.Equals(
            authority.ProfileId,
            _profileId,
            StringComparison.OrdinalIgnoreCase);

    private void AdvanceRestoreRevisionUnderLock(long objectRevision)
    {
        if (objectRevision > _restoreState.LastAppliedRevision)
        {
            _restoreState = _restoreState with
            {
                LastAppliedRevision = objectRevision,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }
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
