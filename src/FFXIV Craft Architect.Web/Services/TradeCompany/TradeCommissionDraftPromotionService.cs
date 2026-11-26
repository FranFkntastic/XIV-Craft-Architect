using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public enum TradeCommissionDraftPromotionPhase
{
    Pending,
    Promoting,
    Refused
}

/// <summary>Why a Trade work package is still waiting to become a company commission draft.</summary>
public sealed record TradeCommissionDraftPromotionState(
    TradeCommissionDraftPromotionPhase Phase,
    string? Code,
    string Message)
{
    public string Describe() => Phase switch
    {
        TradeCommissionDraftPromotionPhase.Promoting => "Creating commission draft...",
        TradeCommissionDraftPromotionPhase.Refused =>
            $"Commission draft refused ({Code}): {Message}",
        _ => Code == null
            ? $"Saved here. Becomes a commission draft when the company host is ready. {Message}"
            : $"Saved here ({Code}). Becomes a commission draft when the company host is ready. {Message}"
    };
}

/// <summary>
/// Turns Trade work packages into canonical company commission drafts through the host's
/// create-draft command. New orders that could not reach the host stay on this device as pending
/// creations and are promoted when the authority is ready; older plain drafts are converted at
/// the exact revision this browser holds. Promotion never reprices: the brief is composed from
/// the stored materials and payroll responsibilities.
/// </summary>
public sealed class TradeCommissionDraftPromotionService(
    TradeCommissionOperationsService operations,
    TradeCompanyCollaborationService collaboration,
    ProfileSyncService profileSync,
    TradeOperationsPersistenceService persistence,
    ILogger<TradeCommissionDraftPromotionService> logger)
{
    /// <summary>Unexplained failures retry on later triggers this many times, then stop.</summary>
    private const int MaxUnexplainedFailures = 3;

    private readonly Dictionary<Guid, TradeCommissionDraftPromotionState> _states = [];
    private readonly Dictionary<Guid, SemaphoreSlim> _gates = [];
    private readonly Dictionary<Guid, int> _unexplainedFailures = [];

    public event Action<Guid>? StateChanged;

    public TradeCommissionDraftPromotionState? GetState(Guid orderId) =>
        _states.GetValueOrDefault(orderId);

    public bool IsPromoting(Guid orderId) =>
        _states.TryGetValue(orderId, out var state) &&
        state.Phase == TradeCommissionDraftPromotionPhase.Promoting;

    /// <summary>
    /// Runs <paramref name="action"/> while no promotion or other save of the same order is in
    /// flight, so a plain save can never interleave with that order becoming canonical.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Guid orderId, Func<Task<T>> action)
    {
        var gate = GetGate(orderId);
        await gate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Promotes one plain draft work package. Returns the committed canonical order (or the
    /// canonical copy another tab already stored), or null when the order stays a pending or
    /// refused creation (see <see cref="GetState"/>).
    /// </summary>
    public async Task<TradeOrder?> PromoteAsync(
        TradeOrder workPackage,
        TradePayrollWorkflowDraft? payrollDraft,
        TradeCompanyProfile company,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workPackage);
        ArgumentNullException.ThrowIfNull(company);
        if (!TradeCompanyCommissionMigrationService.IsDraftWorkPackage(workPackage) ||
            workPackage.CompanyProfileId != company.Id ||
            IsPromoting(workPackage.Id))
        {
            return null;
        }

        var gate = GetGate(workPackage.Id);
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Promote what is stored now, not the caller's copy: a save that finished while this
            // promotion waited must be part of the draft, and a tab that already converted the
            // order has stored the canonical copy.
            var stored = await persistence.LoadOrderAsync(workPackage.Id);
            if (stored?.CompanyCommission != null)
            {
                ClearState(workPackage.Id);
                return stored;
            }
            if (stored != null && !TradeCompanyCommissionMigrationService.IsDraftWorkPackage(stored))
            {
                ClearState(workPackage.Id);
                return null;
            }

            return await PromoteCoreAsync(stored ?? workPackage, payrollDraft, company, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<TradeOrder?> PromoteCoreAsync(
        TradeOrder workPackage,
        TradePayrollWorkflowDraft? payrollDraft,
        TradeCompanyProfile company,
        CancellationToken cancellationToken)
    {

        if (!IsAuthorityReady(out var waitReason))
        {
            SetState(workPackage.Id, new(TradeCommissionDraftPromotionPhase.Pending, null, waitReason));
            return null;
        }

        var previous = _states.GetValueOrDefault(workPackage.Id);
        SetState(
            workPackage.Id,
            new(TradeCommissionDraftPromotionPhase.Promoting, null, "Creating commission draft."));
        try
        {
            if (!await FlushPendingSaveAsync(workPackage.Id, cancellationToken))
            {
                return null;
            }

            var conversionBase = await collaboration.ResolveDraftConversionBaseAsync(
                workPackage,
                cancellationToken);
            var source = workPackage;
            if (conversionBase.CompanyWorkPackage is { } companyCopy)
            {
                logger.LogWarning(
                    "Trade order {OrderId} differed from the company's open draft ({Code}); converted the company's copy at revision {Revision} and replaced this device's copy.",
                    workPackage.Id,
                    "company_copy_converted",
                    conversionBase.Revision.Value);
                source = companyCopy;
            }

            var brief = TradeCommissionDraftComposer.ComposeNewDraft(source, payrollDraft, company);
            var created = await operations.CreateDraftAsync(
                source,
                brief,
                conversionBase.Revision,
                cancellationToken);
            _unexplainedFailures.Remove(workPackage.Id);
            ClearState(workPackage.Id);
            if (created.AdoptedFrom != null)
            {
                logger.LogInformation(
                    "Trade order {OrderId} was already a commission draft on the host ({Code}); adopted the hosted version.",
                    workPackage.Id,
                    created.AdoptedFrom);
            }
            return created.Projection.Order;
        }
        catch (OperationCanceledException)
        {
            RestoreOrClear(workPackage.Id, previous);
            throw;
        }
        catch (CompanyCommissionDraftRefusedException refused)
        {
            Refuse(workPackage.Id, refused.Code, refused.Message);
            return null;
        }
        catch (HttpRequestException exception)
        {
            Wait(workPackage.Id, "host_unreachable", exception.Message);
            return null;
        }
        catch (ProfileHostConnectionException exception)
        {
            Wait(workPackage.Id, "host_unreachable", exception.Message);
            return null;
        }
        catch (InvalidOperationException exception)
        {
            if (!IsAuthorityReady(out var changedReason))
            {
                // The profile or host switched mid-command; readiness will trigger the retry.
                Wait(workPackage.Id, "authority_changed", $"{changedReason} {exception.Message}");
                return null;
            }

            // Anything else retries on a few later triggers and then stops for good, so an
            // unexplained failure can never loop; the operator retries or discards the copy.
            var failures = _unexplainedFailures.GetValueOrDefault(workPackage.Id) + 1;
            _unexplainedFailures[workPackage.Id] = failures;
            if (failures < MaxUnexplainedFailures)
            {
                Wait(
                    workPackage.Id,
                    "promotion_failed",
                    $"{exception.Message} (attempt {failures} of {MaxUnexplainedFailures})");
            }
            else
            {
                Refuse(
                    workPackage.Id,
                    "promotion_failed",
                    $"{exception.Message} Stopped after {failures} attempts; retry, or keep or discard this device's copy.");
            }
            return null;
        }
    }

    /// <summary>Promotes every plain draft work package in the list, one at a time.</summary>
    public async Task<IReadOnlyList<TradeOrder>> PromotePendingAsync(
        IReadOnlyList<TradeOrder> orders,
        Func<TradeOrder, TradePayrollWorkflowDraft?> payrollDraftResolver,
        TradeCompanyProfile company,
        CancellationToken cancellationToken = default)
    {
        var promoted = new List<TradeOrder>();
        foreach (var order in orders.Where(order =>
                     order.CompanyProfileId == company.Id &&
                     TradeCompanyCommissionMigrationService.IsDraftWorkPackage(order)))
        {
            var state = GetState(order.Id);
            if (state?.Phase is TradeCommissionDraftPromotionPhase.Refused or
                TradeCommissionDraftPromotionPhase.Promoting)
            {
                // A refusal stays visible until the operator changes the order or retries.
                continue;
            }

            if (await PromoteAsync(order, payrollDraftResolver(order), company, cancellationToken)
                is { } canonical)
            {
                promoted.Add(canonical);
            }
            else if (GetState(order.Id)?.Phase == TradeCommissionDraftPromotionPhase.Pending &&
                     !IsAuthorityReady(out _))
            {
                break;
            }
        }
        return promoted;
    }

    /// <summary>Forgets a refusal so the next trigger retries the promotion.</summary>
    public void ResetRefusal(Guid orderId)
    {
        _unexplainedFailures.Remove(orderId);
        if (_states.TryGetValue(orderId, out var state) &&
            state.Phase == TradeCommissionDraftPromotionPhase.Refused)
        {
            ClearState(orderId);
        }
    }

    private SemaphoreSlim GetGate(Guid orderId)
    {
        if (!_gates.TryGetValue(orderId, out var gate))
        {
            gate = new SemaphoreSlim(1, 1);
            _gates[orderId] = gate;
        }
        return gate;
    }

    public void MarkPending(Guid orderId, string reason) =>
        SetState(orderId, new(TradeCommissionDraftPromotionPhase.Pending, null, reason));

    private bool IsAuthorityReady(out string reason)
    {
        var status = profileSync.CurrentStatus;
        if (!status.IsConnected)
        {
            reason = "No hosted profile is connected.";
            return false;
        }
        if (!status.HostReachable)
        {
            reason = "The company host is unreachable.";
            return false;
        }
        if (!status.OrderScopeReady)
        {
            reason = "Hosted orders are still loading.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private async Task<bool> FlushPendingSaveAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var objectId = orderId.ToString("D");
        if (HasConflict(objectId))
        {
            Refuse(orderId, "hosted_conflict", "This order has a hosted sync conflict. Resolve it first.");
            return false;
        }
        if (!HasPendingSave(objectId))
        {
            return true;
        }

        await profileSync.SyncNowAsync(cancellationToken);
        if (HasConflict(objectId))
        {
            Refuse(orderId, "hosted_conflict", "This order has a hosted sync conflict. Resolve it first.");
            return false;
        }
        if (HasPendingSave(objectId))
        {
            Wait(orderId, "upload_pending", "The order's earlier edits have not reached the host yet.");
            return false;
        }
        return true;
    }

    private bool HasPendingSave(string objectId) =>
        profileSync.PendingSaves.Any(save =>
            string.Equals(save.Collection, ProfileSyncCollections.TradeOrders, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(save.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));

    private bool HasConflict(string objectId) =>
        profileSync.Conflicts.Any(conflict =>
            string.Equals(conflict.Collection, ProfileSyncCollections.TradeOrders, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(conflict.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));

    private void Wait(Guid orderId, string code, string message)
    {
        logger.LogWarning(
            "Trade order {OrderId} is waiting to become a commission draft ({Code}): {Message}",
            orderId,
            code,
            message);
        SetState(orderId, new(TradeCommissionDraftPromotionPhase.Pending, code, message));
    }

    private void Refuse(Guid orderId, string code, string message)
    {
        logger.LogWarning(
            "Commission draft creation for Trade order {OrderId} was refused with {Code}: {Message}",
            orderId,
            code,
            message);
        SetState(orderId, new(TradeCommissionDraftPromotionPhase.Refused, code, message));
    }

    private void RestoreOrClear(Guid orderId, TradeCommissionDraftPromotionState? previous)
    {
        if (previous == null)
        {
            ClearState(orderId);
        }
        else
        {
            SetState(orderId, previous);
        }
    }

    private void SetState(Guid orderId, TradeCommissionDraftPromotionState state)
    {
        _states[orderId] = state;
        StateChanged?.Invoke(orderId);
    }

    private void ClearState(Guid orderId)
    {
        if (_states.Remove(orderId))
        {
            StateChanged?.Invoke(orderId);
        }
    }
}
