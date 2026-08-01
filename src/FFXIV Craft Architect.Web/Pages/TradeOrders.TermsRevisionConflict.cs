using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
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

    private void RebaseCommissionTermsRevision()
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

        var rebased = TradeOrderWorkflow.CopyOrder(_commissionTermsRevisionWorkPackage);
        rebased.Status = owner.Order.Status;
        rebased.AssignedCrafterId = owner.Order.AssignedCrafterId;
        rebased.CommissionPublication = owner.Order.CommissionPublication;
        rebased.CompanyCommission = latestCommission;
        rebased.UpdatedAtUtc = owner.Order.UpdatedAtUtc;

        _commissionTermsRevisionWorkPackage = rebased;
        _selectedOrder = rebased;
        _commissionTermsRevisionRollbackPlan = null;
        CaptureCommissionTermsRevisionBase(owner, latestCommission);
        Snackbar.Add(
            $"Local changes rebased onto terms v{latestCommission.CurrentTermsVersion}. Review them before publishing.",
            Severity.Info);
    }

    private Task DiscardConflictedCommissionTermsRevisionAsync() =>
        CancelCommissionTermsRevisionAsync(discardConflict: true);
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
