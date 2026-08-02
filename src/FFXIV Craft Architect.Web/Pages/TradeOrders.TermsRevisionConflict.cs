using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;

using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private CompanyCommissionTermsRevisionBase? _commissionTermsRevisionBase;

    private bool HasCommissionTermsRevisionLocalChanges =>
        IsEditingCommissionTermsRevision &&
        (_commissionTermsRevisionDirty ||
         HasSelectedOrderOutputChanges ||
         HasCanonicalDraftDetailChanges ||
         !string.IsNullOrWhiteSpace(_commissionTermsRevisionReason));

    private bool HasCommissionTermsRevisionConflict
    {
        get
        {
            var owner = SelectedCommissionOwner;
            var commission = owner?.Order.CompanyCommission;
            return _commissionTermsRevisionBase is { } revisionBase &&
                owner != null &&
                commission != null &&
                CompanyCommissionTermsRevisionConflictPolicy.HasConflict(
                    revisionBase,
                    owner.ObjectRevision,
                    commission.CurrentTermsVersion,
                    HasCommissionTermsRevisionLocalChanges);
        }
    }

    private void CaptureCommissionTermsRevisionBase(
        CompanyCommissionOwnerProjection owner,
        TradeCompanyCommission commission)
    {
        _commissionTermsRevisionBase = new CompanyCommissionTermsRevisionBase(
            owner.ObjectRevision,
            commission.CurrentTermsVersion);
    }

    private void ResetCommissionTermsRevisionBase()
    {
        _commissionTermsRevisionBase = null;
    }

    private async Task RebaseCommissionTermsRevisionAsync()
    {
        var owner = SelectedCommissionOwner;
        var latestCommission = owner?.Order.CompanyCommission;
        if (!HasCommissionTermsRevisionConflict ||
            owner == null ||
            latestCommission == null ||
            _commissionTermsRevisionWorkPackage == null)
        {
            return;
        }

        _isCommissionCommandRunning = true;
        try
        {
            var latestBaseline = await ReadLatestCanonicalPlanAsync(
                owner.Order,
                owner.ObjectRevision,
                "rebased");
            if (!string.IsNullOrWhiteSpace(owner.Order.CraftPlanId) && latestBaseline == null)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(owner.Order.CraftPlanId) &&
                !string.IsNullOrWhiteSpace(_commissionTermsRevisionWorkPackage.CraftPlanId))
            {
                Snackbar.Add(
                    "The latest terms do not identify an exact saved craft plan, so the local revision was not rebased.",
                    Severity.Error);
                return;
            }

            var rebased = TradeOrderWorkflow.CopyOrder(_commissionTermsRevisionWorkPackage);
            rebased.Status = owner.Order.Status;
            rebased.AssignedCrafterId = owner.Order.AssignedCrafterId;
            rebased.CommissionPublication = owner.Order.CommissionPublication;
            rebased.CompanyCommission = latestCommission;
            rebased.UpdatedAtUtc = owner.Order.UpdatedAtUtc;

            _commissionTermsRevisionWorkPackage = rebased;
            _selectedOrder = rebased;
            _commissionTermsRevisionRollbackPlan = latestBaseline;
            CaptureCommissionTermsRevisionBase(owner, latestCommission);
            Snackbar.Add(
                $"Local changes rebased onto terms v{latestCommission.CurrentTermsVersion}. Review them before publishing.",
                Severity.Info);
        }
        finally
        {
            _isCommissionCommandRunning = false;
        }

    }

    private Task DiscardConflictedCommissionTermsRevisionAsync() =>
        CancelCommissionTermsRevisionAsync(discardConflict: true);

    private async Task<bool> ReconcileLatestCanonicalPlanAsync(TradeOrder canonicalOrder)
    {
        var rollbackFence = CaptureCurrentWorkerPlanFence(
            _commissionTermsRevisionWorkPackage?.CraftPlanId);
        if (string.IsNullOrWhiteSpace(canonicalOrder.CraftPlanId))
        {
            if (string.IsNullOrWhiteSpace(_commissionTermsRevisionWorkPackage?.CraftPlanId))
            {
                return true;
            }

            Snackbar.Add(
                "The latest terms do not identify an exact saved craft plan, so the local revision was not discarded.",
                Severity.Error);
            return false;
        }

        var ownerRevision = SelectedCommissionOwner?.ObjectRevision;
        if (ownerRevision == null)
        {
            return false;
        }

        var latestPlan = await ReadLatestCanonicalPlanAsync(
            canonicalOrder,
            ownerRevision.Value,
            "discarded");
        if (latestPlan == null)
        {
            return false;
        }
        if (!rollbackFence.HasValue)
        {
            Snackbar.Add(
                "The active plan changed while the latest terms were loading, so it was preserved instead of being discarded.",
                Severity.Info);
            return false;
        }

        return await RestoreStagedProcurementPlanAsync(
            latestPlan,
            rollbackFence.Value);
    }

    private async Task<StoredPlan?> ReadLatestCanonicalPlanAsync(
        TradeOrder canonicalOrder,
        CompanyRecordRevision ownerRevision,
        string action)
    {
        var orderId = canonicalOrder.Id;
        var planId = canonicalOrder.CraftPlanId;
        var planSavedAtUtc = canonicalOrder.CraftPlanSavedAtUtc;
        if (string.IsNullOrWhiteSpace(planId))
        {
            return null;
        }
        if (planSavedAtUtc == null)
        {
            Snackbar.Add(
                $"The latest terms do not identify an exact saved craft plan revision, so the local revision was not {action}.",
                Severity.Warning);
            return null;
        }

        bool CanContinue()
        {
            var owner = SelectedCommissionOwner;
            return !_isDisposed &&
                _selectedOrder?.Id == orderId &&
                owner?.ObjectRevision == ownerRevision &&
                string.Equals(owner.Order.CraftPlanId, planId, StringComparison.Ordinal) &&
                owner.Order.CraftPlanSavedAtUtc == planSavedAtUtc;
        }

        var read = await TradeOrderPlanRestorePolicy.ReadExactPlanAsync(
            _ => PlanPersistence.LoadPlanPayloadAsync(planId),
            () => ProfileSync.CurrentStatus,
            waitsForProfilePlanAuthority: canonicalOrder.CompanyCommission != null,
            canContinue: CanContinue);
        if (!CanContinue())
        {
            return null;
        }

        var exactRevision = new TradeOrderPlanRestoreRequest(
            Generation: 0,
            OrderId: orderId,
            PlanId: planId,
            WorkerRevision: 0,
            PlanSavedAtUtc: planSavedAtUtc);
        if (read.Payload == null ||
            !TradeOrderPlanRestorePolicy.IsExactSavedRevision(
                exactRevision,
                read.Payload))
        {
            if (read.Outcome != TradeOrderPlanReadOutcome.RequestSuperseded)
            {
                Snackbar.Add(
                    read.Outcome == TradeOrderPlanReadOutcome.WaitForHostedPlan
                        ? $"The latest saved craft plan is still arriving, so the local revision was not {action}."
                        : $"The exact saved craft plan for the latest terms is unavailable. The local revision was not {action}.",
                    Severity.Warning);
            }
            return null;
        }

        return read.Payload;
    }
}

public readonly record struct CompanyCommissionTermsRevisionBase(
    CompanyRecordRevision ObjectRevision,
    int TermsVersion);

public static class CompanyCommissionTermsRevisionConflictPolicy
{
    public static bool HasConflict(
        CompanyCommissionTermsRevisionBase revisionBase,
        CompanyRecordRevision currentObjectRevision,
        int currentTermsVersion,
        bool hasLocalChanges) =>
        hasLocalChanges &&
        currentTermsVersion > revisionBase.TermsVersion;
}
