using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private HostedOrderRestoreState OrderRestoreState => HostedOrders.RestoreState;

    private bool CanMutateHostedOrder => OrderRestoreState.CanMutate;

    private bool EnsureHostedOrderMutationAvailable()
    {
        if (HasSelectedLocalHostedCollision)
        {
            Snackbar.Add(
                "Resolve the local edit collision before changing the hosted order.",
                Severity.Warning);
            return false;
        }

        if (CanMutateHostedOrder)
        {
            return true;
        }

        Snackbar.Add(
            "Order changes are paused while the hosted order authority is verified. They will resume automatically.",
            Severity.Warning);
        return false;
    }

    private bool IsIdentityOnlyOrder(TradeOrder order) =>
        order.CompanyCommission != null &&
        !OrderRestoreState.ShowsCompleteProjection;

    private IReadOnlyList<TradeOrder> ComposeVisibleOrders()
    {
        var visible = OrderRestoreState.ShowsCompleteProjection
            ? new Dictionary<Guid, TradeOrder>()
            : _orders
                .Where(order => order.CompanyCommission == null)
                .ToDictionary(order => order.Id);

        if (OrderRestoreState.ShowsCompleteProjection && _companyProfile != null)
        {
            foreach (var snapshot in HostedOrders.GetAll(_companyProfile.Id))
            {
                if (!snapshot.Deleted && snapshot.Order != null)
                {
                    visible[snapshot.OrderId] = snapshot.Order;
                }
            }
        }
        else if (OrderRestoreState.Stage != HostedOrderRestoreStage.ScopeChanging)
        {
            // During a cold or unverifiable restore, persisted commission records
            // contribute identity only. Their terms never become page authority.
            foreach (var order in _orders.Where(order => order.CompanyCommission != null))
            {
                visible[order.Id] = order;
            }
        }

        return visible.Values.ToArray();
    }

    private void ApplyHostedOrderProjection(HostedOrderProjectionSnapshot snapshot)
    {
        if (_isDisposed ||
            (_companyProfile != null &&
             snapshot.CompanyProfileId.HasValue &&
             snapshot.CompanyProfileId != _companyProfile.Id))
        {
            return;
        }

        if (_selectedOrder?.Id == snapshot.OrderId)
        {
            if (snapshot.Deleted || snapshot.Order == null)
            {
                if (_selectedOrder.CompanyCommission != null)
                {
                    ClearUnavailableSelectedOrder("This order is no longer available.");
                }
                else
                {
                    _selectedLocalHostedCollision = null;
                }
            }
            else if (!ShouldPreserveCanonicalEditor())
            {
                var selectedTab = _activeOpsTab;
                var planExpanded = _isPlanPaneExpanded;
                SelectOrder(snapshot.OwnerProjection?.Order ?? snapshot.Order);
                _activeOpsTab = selectedTab;
                _isPlanPaneExpanded = planExpanded;
            }
            else if (_selectedOrder.CompanyCommission == null)
            {
                _selectedLocalHostedCollision = snapshot;
            }
        }

        StateHasChanged();
    }

    private void ApplyHostedOrderProjectionReset()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_selectedOrder?.CompanyCommission != null)
        {
            ClearUnavailableSelectedOrder("The active order workspace changed.");
        }

        StateHasChanged();
    }

    private void ApplyHostedOrderRestoreState(HostedOrderRestoreState state)
    {
        if (_isDisposed)
        {
            return;
        }

        if (state.Stage == HostedOrderRestoreStage.ScopeChanging &&
            _selectedOrder?.CompanyCommission != null)
        {
            ClearUnavailableSelectedOrder("The active company workspace changed.");
        }
        else if (state.ShowsCompleteProjection &&
                 _selectedOrder != null &&
                 _companyProfile != null)
        {
            var hosted = HostedOrders.Get(_selectedOrder.Id);
            if (hosted is { Deleted: false, Order: not null } &&
                hosted.CompanyProfileId == _companyProfile.Id)
            {
                if (HasSelectedLocalDraftEditorChanges)
                {
                    _selectedLocalHostedCollision = hosted;
                }
                else
                {
                    SelectOrder(hosted.OwnerProjection?.Order ?? hosted.Order);
                }
            }
            else if (_selectedOrder.CompanyCommission != null)
            {
                ClearUnavailableSelectedOrder(
                    "That device-only order is stored separately from the hosted workspace.");
            }
        }
        StateHasChanged();
    }

    private void ClearUnavailableSelectedOrder(string message)
    {
        _selectedOrder = null;
        _manualNote = string.Empty;
        _showCommissionTermsRevision = false;
        _commissionTermsRevisionWorkPackage = null;
        _commissionTermsRevisionBrief = null;
        _commissionTermsRevisionRollbackPlan = null;
        _commissionTermsRevisionDirty = false;
        _commissionTermsRevisionPaymentDirty = false;
        _selectedOrderPaymentTermsDirty = false;
        _selectedLocalHostedCollision = null;
        AppState.SelectTradeOrder(null);
        ClearSelectedOrderNavigation();
        Snackbar.Add(message, Severity.Info);
    }

    private void AdoptHostedCopyForSelectedLocalOrder()
    {
        if (_selectedLocalHostedCollision is not { Deleted: false, Order: not null } hosted ||
            _selectedOrder?.Id != hosted.OrderId)
        {
            return;
        }

        var selectedTab = _activeOpsTab;
        var planExpanded = _isPlanPaneExpanded;
        SelectOrder(hosted.OwnerProjection?.Order ?? hosted.Order);
        _activeOpsTab = selectedTab;
        _isPlanPaneExpanded = planExpanded;
    }

    private void RebaseSelectedLocalEditsOntoHostedCopy()
    {
        if (_selectedLocalHostedCollision is not { Deleted: false, Order: not null } hosted ||
            hosted.Order.CompanyCommission != null ||
            _selectedOrder is not { CompanyCommission: null } local ||
            local.Id != hosted.OrderId)
        {
            return;
        }

        var titleDirty = !string.Equals(
            _detailTitle.Trim(),
            local.Title,
            StringComparison.Ordinal);
        var crafterDirty = _detailCrafterId != local.AssignedCrafterId;
        var statusDirty = _detailStatus != local.Status;
        var notesDirty = !string.Equals(_detailNotes, local.Notes, StringComparison.Ordinal);
        var outputsDirty = HasSelectedOrderOutputChanges;
        var paymentDirty = _selectedOrderPaymentTermsDirty;
        var title = _detailTitle;
        var crafterId = _detailCrafterId;
        var status = _detailStatus;
        var notes = _detailNotes;
        var outputEditors = _selectedOrderOutputEditors.ToList();
        var paymentSchedule = _selectedOrderPaymentSchedule;
        var customPaymentTerms = _selectedOrderCustomPaymentTerms;
        var selectedTab = _activeOpsTab;
        var planExpanded = _isPlanPaneExpanded;

        SelectOrder(hosted.Order);
        if (titleDirty)
        {
            _detailTitle = title;
        }
        if (crafterDirty)
        {
            _detailCrafterId = crafterId;
        }
        if (statusDirty)
        {
            _detailStatus = status;
        }
        if (notesDirty)
        {
            _detailNotes = notes;
        }
        if (outputsDirty)
        {
            _selectedOrderOutputEditors = outputEditors;
        }
        if (paymentDirty)
        {
            _selectedOrderPaymentSchedule = paymentSchedule;
            _selectedOrderCustomPaymentTerms = customPaymentTerms;
            _selectedOrderPaymentTermsDirty = true;
        }

        _activeOpsTab = selectedTab;
        _isPlanPaneExpanded = planExpanded;
        Snackbar.Add(
            "Local edits rebased onto the hosted copy. Review and save them.",
            Severity.Info);
    }
}
