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
                ClearUnavailableSelectedOrder("This order is no longer available.");
            }
            else if (!ShouldPreserveCanonicalEditor())
            {
                var selectedTab = _activeOpsTab;
                var planExpanded = _isPlanPaneExpanded;
                SelectOrder(snapshot.OwnerProjection?.Order ?? snapshot.Order);
                _activeOpsTab = selectedTab;
                _isPlanPaneExpanded = planExpanded;
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
                 _companyProfile != null &&
                 !TradeOrderWorkspaceCompositionPolicy.IsHostedOrder(
                     _selectedOrder.Id,
                     _companyProfile.Id,
                     HostedOrders.GetAll(_companyProfile.Id)))
        {
            ClearUnavailableSelectedOrder(
                "That device-only order is stored separately from the hosted workspace.");
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
        AppState.SelectTradeOrder(null);
        ClearSelectedOrderNavigation();
        Snackbar.Add(message, Severity.Info);
    }
}
