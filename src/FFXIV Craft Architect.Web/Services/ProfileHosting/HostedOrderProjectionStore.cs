using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public enum HostedOrderDisplayState
{
    LastKnown,
    Verified
}

public sealed record HostedOrderProjectionSnapshot(
    Guid OrderId,
    Guid? CompanyProfileId,
    long ObjectRevision,
    long? CompanyRevision,
    TradeOrder? Order,
    CompanyCommissionOwnerProjection? OwnerProjection,
    bool Deleted,
    HostedOrderDisplayState DisplayState = HostedOrderDisplayState.LastKnown);

public sealed record HostedOrderOwnerBatchApplyResult(
    int ChangedCount,
    int VerificationCount,
    int RejectedCount,
    IReadOnlyList<Guid> AppliedOrderIds);

public readonly record struct HostedOrderAuthorityScope(
    string? ProfileId,
    string? ConnectionScopeId,
    long Epoch);

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
    private static readonly JsonSerializerOptions ProjectionComparisonJson =
        ProfileSyncJson.CreateOptions();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, HostedOrderProjectionSnapshot> _orders = [];
    private string? _profileId;
    private string? _connectionScopeId;
    private long _scopeEpoch;
    private HostedOrderRestoreState _restoreState = HostedOrderRestoreState.Inactive(DateTime.UtcNow);

    public event Action<HostedOrderProjectionSnapshot>? Changed;
    public event Action<IReadOnlyList<HostedOrderProjectionSnapshot>>? BatchChanged;
    public event Action<HostedOrderProjectionSnapshot>? VerificationChanged;
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
            return new HostedOrderAuthorityScope(
                _profileId,
                _connectionScopeId,
                _scopeEpoch);
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
        DateTime now,
        string connectionScopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionScopeId);
        HostedOrderRestoreState next;
        var reset = false;
        lock (_gate)
        {
            connectionScopeId = connectionScopeId.Trim();
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
            var connectionChanged = !string.Equals(
                _connectionScopeId,
                connectionScopeId,
                StringComparison.Ordinal);
            var scopeChanged = _profileId != null &&
                               (profileChanged || connectionChanged);
            if (scopeChanged)
            {
                _orders.Clear();
                reset = true;
            }
            if (profileChanged || connectionChanged)
            {
                _scopeEpoch++;
            }

            var retainedTrust = !scopeChanged &&
                                (_restoreState.HasTrustedProjection || hasTrustedProjection);
            _profileId = profileId;
            _connectionScopeId = connectionScopeId;
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
            _connectionScopeId = profileId;
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
                Deleted: false,
                current?.ObjectRevision == objectRevision
                    ? current.DisplayState
                    : HostedOrderDisplayState.LastKnown);
            accepted = TryAcceptUnderLock(candidate);
        }
        return NotifyIfAccepted(candidate, accepted);
    }

    public int PublishRemoteOrders(
        IReadOnlyList<(TradeOrder Order, long ObjectRevision)> projections)
    {
        ArgumentNullException.ThrowIfNull(projections);
        if (projections.Count == 0)
        {
            return 0;
        }

        var accepted = new List<HostedOrderProjectionSnapshot>(projections.Count);
        var restored = 0;
        lock (_gate)
        {
            foreach (var (order, objectRevision) in projections)
            {
                ArgumentNullException.ThrowIfNull(order);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectRevision);
                var current = _orders.GetValueOrDefault(order.Id);
                var candidate = new HostedOrderProjectionSnapshot(
                    order.Id,
                    order.CompanyProfileId,
                    objectRevision,
                    current?.CompanyRevision,
                    order,
                    current?.ObjectRevision == objectRevision
                        ? current.OwnerProjection
                        : null,
                    Deleted: false,
                    current?.ObjectRevision == objectRevision
                        ? current.DisplayState
                        : HostedOrderDisplayState.LastKnown);
                if (TryAcceptUnderLock(candidate))
                {
                    accepted.Add(candidate);
                }

                if (_orders.GetValueOrDefault(order.Id)?.ObjectRevision == objectRevision)
                {
                    restored++;
                }
            }
        }

        if (accepted.Count > 0)
        {
            BatchChanged?.Invoke(accepted);
            RestoreStateChanged?.Invoke(RestoreState);
        }
        return restored;
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
                Deleted: false,
                current?.ObjectRevision == objectRevision
                    ? current.DisplayState
                    : HostedOrderDisplayState.LastKnown);
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
        Func<Task<bool>>? authorityIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(persist);
        authorityIsCurrent ??= () => Task.FromResult(IsCurrentAuthority(authority));
        if (!await authorityIsCurrent())
        {
            return HostedOrderCommittedProjectionResult.ScopeChanged;
        }
        var adoption = TryAdoptCommittedOrder(authority, order, objectRevision);
        return await ReconcileCommittedProjectionAsync(
            authority,
            order.Id,
            adoption,
            persist,
            authorityIsCurrent);
    }

    public async Task<HostedOrderCommittedProjectionResult> AdoptAndPersistDeepArchivedOrderAsync(
        HostedOrderAuthorityScope authority,
        TradeOrder order,
        long objectRevision,
        Func<HostedOrderProjectionSnapshot, Task> persist,
        Func<Task<bool>>? authorityIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(persist);
        authorityIsCurrent ??= () => Task.FromResult(IsCurrentAuthority(authority));
        if (!await authorityIsCurrent())
        {
            return HostedOrderCommittedProjectionResult.ScopeChanged;
        }

        HostedOrderProjectionSnapshot candidate;
        HostedOrderCommittedProjectionResult adoption;
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
                Deleted: false,
                current?.ObjectRevision == objectRevision
                    ? current.DisplayState
                    : HostedOrderDisplayState.LastKnown);
            try
            {
                if (current != null)
                {
                    ValidateIdentity(current, candidate);
                }
            }
            catch (InvalidOperationException)
            {
                return HostedOrderCommittedProjectionResult.IdentityMismatch;
            }

            if (current == null ||
                current.Deleted && current.ObjectRevision == objectRevision)
            {
                _orders[candidate.OrderId] = candidate;
                AdvanceRestoreRevisionUnderLock(candidate.ObjectRevision);
                adoption = HostedOrderCommittedProjectionResult.Adopted;
            }
            else if (current.ObjectRevision == objectRevision && !current.Deleted)
            {
                adoption = HostedOrderCommittedProjectionResult.AlreadyCurrent;
            }
            else
            {
                return HostedOrderCommittedProjectionResult.Stale;
            }
        }

        if (adoption == HostedOrderCommittedProjectionResult.Adopted)
        {
            Changed?.Invoke(candidate);
            RestoreStateChanged?.Invoke(RestoreState);
        }
        return await ReconcileCommittedProjectionAsync(
            authority,
            order.Id,
            adoption,
            persist,
            authorityIsCurrent);
    }

    public async Task<HostedOrderCommittedProjectionResult> AdoptAndPersistCommittedTombstoneAsync(
        HostedOrderAuthorityScope authority,
        Guid orderId,
        Guid companyProfileId,
        long objectRevision,
        Func<HostedOrderProjectionSnapshot, Task> persist,
        Func<Task<bool>>? authorityIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(persist);
        authorityIsCurrent ??= () => Task.FromResult(IsCurrentAuthority(authority));
        if (!await authorityIsCurrent())
        {
            return HostedOrderCommittedProjectionResult.ScopeChanged;
        }
        var adoption = TryAdoptCommittedTombstone(
            authority,
            orderId,
            companyProfileId,
            objectRevision);
        return await ReconcileCommittedProjectionAsync(
            authority,
            orderId,
            adoption,
            persist,
            authorityIsCurrent);
    }

    public async Task<HostedOrderCommittedProjectionResult> AdoptAndPersistCommittedOwnerAsync(
        HostedOrderAuthorityScope authority,
        CompanyCommissionOwnerProjection projection,
        Func<HostedOrderProjectionSnapshot, Task> persist,
        Func<Task<bool>>? authorityIsCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(persist);
        authorityIsCurrent ??= () => Task.FromResult(IsCurrentAuthority(authority));
        if (!await authorityIsCurrent())
        {
            return HostedOrderCommittedProjectionResult.ScopeChanged;
        }
        var adoption = TryAdoptCommittedOwner(authority, projection);
        return await ReconcileCommittedProjectionAsync(
            authority,
            projection.Order.Id,
            adoption,
            persist,
            authorityIsCurrent);
    }

    private async Task<HostedOrderCommittedProjectionResult> ReconcileCommittedProjectionAsync(
        HostedOrderAuthorityScope authority,
        Guid orderId,
        HostedOrderCommittedProjectionResult adoption,
        Func<HostedOrderProjectionSnapshot, Task> persist,
        Func<Task<bool>>? authorityIsCurrent)
    {
        ArgumentNullException.ThrowIfNull(persist);
        authorityIsCurrent ??= () => Task.FromResult(IsCurrentAuthority(authority));
        if (!await authorityIsCurrent())
        {
            return HostedOrderCommittedProjectionResult.ScopeChanged;
        }
        if (adoption is not (
            HostedOrderCommittedProjectionResult.Adopted or
            HostedOrderCommittedProjectionResult.AlreadyCurrent))
        {
            return adoption;
        }

        var candidate = Get(orderId);
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
                return HostedOrderCommittedProjectionResult.ScopeChanged;
            }

            var winner = Get(orderId);
            if (winner == null)
            {
                return HostedOrderCommittedProjectionResult.Stale;
            }
            if (HasSameVersionTuple(winner, candidate))
            {
                return adoption;
            }
            candidate = winner;
        }

        throw new InvalidOperationException(
            "The hosted order changed repeatedly while browser persistence reconciled its committed winner.");
    }

    public bool TryPublishOwner(CompanyCommissionOwnerProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var before = Get(projection.Order.Id);
        var result = TryAdoptCommittedOwner(CaptureAuthorityScope(), projection);
        var after = Get(projection.Order.Id);
        return result == HostedOrderCommittedProjectionResult.Adopted ||
               !ReferenceEquals(before, after);
    }

    public HostedOrderCommittedProjectionResult TryAdoptCommittedOwner(
        HostedOrderAuthorityScope authority,
        CompanyCommissionOwnerProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var candidate = new HostedOrderProjectionSnapshot(
            projection.Order.Id,
            projection.Order.CompanyProfileId,
            (projection.ProfileObjectRevision ?? projection.ObjectRevision).Value,
            projection.CompanyRevision.Value,
            projection.Order,
            projection,
            Deleted: false,
            HostedOrderDisplayState.Verified);
        HostedOrderCommittedProjectionResult result;
        HostedOrderProjectionSnapshot? verificationChanged = null;
        lock (_gate)
        {
            if (!IsCurrentAuthorityUnderLock(authority))
            {
                return HostedOrderCommittedProjectionResult.ScopeChanged;
            }

            var current = _orders.GetValueOrDefault(candidate.OrderId);
            try
            {
                if (current != null)
                {
                    ValidateIdentity(current, candidate);
                }
            }
            catch (InvalidOperationException)
            {
                return HostedOrderCommittedProjectionResult.IdentityMismatch;
            }

            if (current != null &&
                (candidate.ObjectRevision < current.ObjectRevision ||
                 (candidate.ObjectRevision == current.ObjectRevision && current.Deleted)))
            {
                return HostedOrderCommittedProjectionResult.Stale;
            }
            if (current != null && HasSameVersionTuple(current, candidate))
            {
                return HostedOrderCommittedProjectionResult.AlreadyCurrent;
            }

            if (current is { Deleted: false, Order: not null } &&
                HasSamePresentedOrder(current.Order, candidate.Order!))
            {
                if (!HasNewerOwnerVerification(current, candidate))
                {
                    return HostedOrderCommittedProjectionResult.AlreadyCurrent;
                }

                verificationChanged = candidate with { Order = current.Order };
                _orders[candidate.OrderId] = verificationChanged;
                AdvanceRestoreRevisionUnderLock(candidate.ObjectRevision);
                result = HostedOrderCommittedProjectionResult.AlreadyCurrent;
            }
            else
            {
                result = TryAcceptUnderLock(candidate)
                    ? HostedOrderCommittedProjectionResult.Adopted
                    : HostedOrderCommittedProjectionResult.AlreadyCurrent;
            }
        }

        if (result == HostedOrderCommittedProjectionResult.Adopted)
        {
            Changed?.Invoke(candidate);
            RestoreStateChanged?.Invoke(RestoreState);
        }
        else if (verificationChanged != null)
        {
            VerificationChanged?.Invoke(verificationChanged);
        }
        return result;
    }

    public HostedOrderOwnerBatchApplyResult TryAdoptCommittedOwnerBatch(
        HostedOrderAuthorityScope authority,
        IReadOnlyList<CompanyCommissionOwnerProjection> projections)
    {
        ArgumentNullException.ThrowIfNull(projections);
        if (projections.Count == 0)
        {
            return new HostedOrderOwnerBatchApplyResult(0, 0, 0, []);
        }

        var changed = new List<HostedOrderProjectionSnapshot>(projections.Count);
        var verified = new List<HostedOrderProjectionSnapshot>(projections.Count);
        var applied = new List<Guid>(projections.Count);
        var rejected = 0;
        lock (_gate)
        {
            if (!IsCurrentAuthorityUnderLock(authority))
            {
                return new HostedOrderOwnerBatchApplyResult(
                    0,
                    0,
                    projections.Count,
                    []);
            }

            foreach (var projection in projections)
            {
                var candidate = new HostedOrderProjectionSnapshot(
                    projection.Order.Id,
                    projection.Order.CompanyProfileId,
                    (projection.ProfileObjectRevision ?? projection.ObjectRevision).Value,
                    projection.CompanyRevision.Value,
                    projection.Order,
                    projection,
                    Deleted: false,
                    HostedOrderDisplayState.Verified);
                var current = _orders.GetValueOrDefault(candidate.OrderId);
                try
                {
                    if (current != null)
                    {
                        ValidateIdentity(current, candidate);
                    }
                }
                catch (InvalidOperationException)
                {
                    rejected++;
                    continue;
                }

                if (current != null &&
                    (candidate.ObjectRevision < current.ObjectRevision ||
                     candidate.ObjectRevision == current.ObjectRevision && current.Deleted))
                {
                    rejected++;
                    continue;
                }
                if (current != null && HasSameVersionTuple(current, candidate))
                {
                    applied.Add(candidate.OrderId);
                    continue;
                }
                if (current is { Deleted: false, Order: not null } &&
                    HasSamePresentedOrder(current.Order, candidate.Order!))
                {
                    if (HasNewerOwnerVerification(current, candidate))
                    {
                        var verification = candidate with { Order = current.Order };
                        _orders[candidate.OrderId] = verification;
                        AdvanceRestoreRevisionUnderLock(candidate.ObjectRevision);
                        verified.Add(verification);
                    }
                    applied.Add(candidate.OrderId);
                    continue;
                }
                if (TryAcceptUnderLock(candidate))
                {
                    changed.Add(candidate);
                    applied.Add(candidate.OrderId);
                }
                else
                {
                    rejected++;
                }
            }
        }

        if (changed.Count > 0)
        {
            BatchChanged?.Invoke(changed);
            RestoreStateChanged?.Invoke(RestoreState);
        }
        foreach (var verification in verified)
        {
            VerificationChanged?.Invoke(verification);
        }
        return new HostedOrderOwnerBatchApplyResult(
            changed.Count,
            verified.Count,
            rejected,
            applied);
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
                Deleted: true,
                HostedOrderDisplayState.LastKnown);
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
                Deleted: true,
                HostedOrderDisplayState.LastKnown);
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
                changed = current with
                {
                    OwnerProjection = null,
                    DisplayState = HostedOrderDisplayState.LastKnown
                };
                _orders[orderId] = changed;
            }
        }
        if (changed != null)
        {
            VerificationChanged?.Invoke(changed);
        }
    }

    public bool TryClearOwner(
        HostedOrderAuthorityScope authority,
        Guid orderId,
        HostedOrderProjectionSnapshot? expected)
    {
        HostedOrderProjectionSnapshot? changed = null;
        lock (_gate)
        {
            if (!IsCurrentAuthorityUnderLock(authority))
            {
                return false;
            }
            var current = _orders.GetValueOrDefault(orderId);
            if ((current == null) != (expected == null) ||
                current != null && !HasSameVersionTuple(current, expected!))
            {
                return false;
            }
            if (current?.OwnerProjection != null)
            {
                changed = current with
                {
                    OwnerProjection = null,
                    DisplayState = HostedOrderDisplayState.LastKnown
                };
                _orders[orderId] = changed;
            }
        }
        if (changed != null)
        {
            VerificationChanged?.Invoke(changed);
        }
        return true;
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
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            authority.ConnectionScopeId,
            _connectionScopeId,
            StringComparison.Ordinal);

    private static bool HasSameVersionTuple(
        HostedOrderProjectionSnapshot left,
        HostedOrderProjectionSnapshot right) =>
        left.ObjectRevision == right.ObjectRevision &&
        left.CompanyRevision == right.CompanyRevision &&
        left.Deleted == right.Deleted &&
        ReferenceEquals(left.Order, right.Order) &&
        left.OwnerProjection?.ObjectRevision == right.OwnerProjection?.ObjectRevision &&
        left.OwnerProjection?.CompanyRevision == right.OwnerProjection?.CompanyRevision &&
        ReferenceEquals(left.OwnerProjection, right.OwnerProjection);

    private static bool HasNewerOwnerVerification(
        HostedOrderProjectionSnapshot current,
        HostedOrderProjectionSnapshot candidate) =>
        current.DisplayState != HostedOrderDisplayState.Verified ||
        candidate.ObjectRevision > current.ObjectRevision ||
        (candidate.CompanyRevision ?? 0) > (current.CompanyRevision ?? 0) ||
        (candidate.OwnerProjection?.ObjectRevision.Value ?? 0) >
        (current.OwnerProjection?.ObjectRevision.Value ?? 0) ||
        (candidate.OwnerProjection?.ProfileObjectRevision?.Value ?? 0) >
        (current.OwnerProjection?.ProfileObjectRevision?.Value ?? 0);

    private static bool HasSamePresentedOrder(TradeOrder left, TradeOrder right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        var leftJson = JsonSerializer.SerializeToUtf8Bytes(
            left,
            ProjectionComparisonJson);
        var rightJson = JsonSerializer.SerializeToUtf8Bytes(
            right,
            ProjectionComparisonJson);
        return leftJson.AsSpan().SequenceEqual(rightJson);
    }

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
