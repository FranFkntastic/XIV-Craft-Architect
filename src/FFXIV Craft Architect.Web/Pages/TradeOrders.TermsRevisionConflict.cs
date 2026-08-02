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
            var planId = _commissionTermsRevisionWorkPackage.CraftPlanId;
            var planName = _commissionTermsRevisionWorkPackage.CraftPlanName ??
                TradeOrderWorkflow.CreateGeneratedCraftPlanName(_commissionTermsRevisionWorkPackage);
            var localDraftPlan = string.IsNullOrWhiteSpace(planId)
                ? null
                : await WorkerSession.ExportStoredPlanAsync(
                    planId,
                    planName,
                    includeSourcePlanIdentity: true);
            if (!string.IsNullOrWhiteSpace(planId) && localDraftPlan == null)
            {
                Snackbar.Add(
                    "The local plan revision could not be captured safely, so it was not rebased.",
                    Severity.Error);
                return;
            }

            var latestBaseline = await BuildLatestCanonicalPlanBaselineAsync(
                owner.Order,
                localDraftPlan);
            if (latestBaseline == null)
            {
                return;
            }
            if (localDraftPlan != null)
            {
                if (!await RestoreStagedProcurementPlanAsync(localDraftPlan))
                {
                    return;
                }
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
        var recoveryPlan = await CaptureActiveRevisionPlanAsync();
        return await BuildLatestCanonicalPlanBaselineAsync(
            canonicalOrder,
            recoveryPlan) != null;
    }

    private async Task<StoredPlan?> CaptureActiveRevisionPlanAsync()
    {
        var workPackage = _commissionTermsRevisionWorkPackage;
        if (workPackage == null || string.IsNullOrWhiteSpace(workPackage.CraftPlanId))
        {
            return null;
        }

        return await WorkerSession.ExportStoredPlanAsync(
            workPackage.CraftPlanId,
            workPackage.CraftPlanName ??
                TradeOrderWorkflow.CreateGeneratedCraftPlanName(workPackage),
            includeSourcePlanIdentity: true);
    }

    private async Task<StoredPlan?> BuildLatestCanonicalPlanBaselineAsync(
        TradeOrder canonicalOrder,
        StoredPlan? recoveryPlan)
    {
        var result = await TradeOrderPricingWorkflow.RebuildAndPriceAsync(
            canonicalOrder,
            new TradeOrderPricingWorkflowOptions(
                GetOrderDataCenter(canonicalOrder),
                canonicalOrder.SourceSnapshot.World ?? string.Empty,
                ForceRefreshMarketData: false));
        if (!result.HasUpdatedOrder || result.UpdatedOrder == null)
        {
            if (recoveryPlan != null)
            {
                await RestoreStagedProcurementPlanAsync(recoveryPlan);
            }
            Snackbar.Add(
                $"The latest canonical plan could not be restored. {result.Message}",
                Severity.Error);
            return null;
        }

        var updated = result.UpdatedOrder;
        var baseline = string.IsNullOrWhiteSpace(updated.CraftPlanId)
            ? null
            : await WorkerSession.ExportStoredPlanAsync(
                updated.CraftPlanId,
                updated.CraftPlanName ?? TradeOrderWorkflow.CreateGeneratedCraftPlanName(updated),
                includeSourcePlanIdentity: true);
        if (baseline != null)
        {
            return baseline;
        }

        if (recoveryPlan != null)
        {
            await RestoreStagedProcurementPlanAsync(recoveryPlan);
        }
        Snackbar.Add(
            "The latest canonical plan was restored but could not be captured safely.",
            Severity.Error);
        return null;
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
