using System.Text;

using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Dialogs;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private IReadOnlyList<TradeOrder> GetOrdersForStatus(TradeOrderStatus status)
    {
        return _orders
            .Where(order => order.Status == status)
            .OrderByDescending(order => order.CommissionedAtUtc)
            .ToArray();
    }

    private bool OrderMatchesSearch(TradeOrder order)
    {
        if (string.IsNullOrWhiteSpace(_orderSearchText))
        {
            return true;
        }

        var query = _orderSearchText.Trim();
        return order.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            FormatAssignedCrafter(order).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            FormatStatus(order.Status).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private string GetRailOrderClass(TradeOrder order)
    {
        return _selectedOrder?.Id == order.Id
            ? "trade-orders-rail-order is-selected"
            : "trade-orders-rail-order";
    }

    private string FormatOrderRailMeta(TradeOrder order)
    {
        return $"{FormatAssignedCrafter(order)} - {order.CommissionedAtUtc.ToLocalTime():yyyy-MM-dd}";
    }

    private static string FormatRailStatusChip(TradeOrder order)
    {
        return order.Status switch
        {
            TradeOrderStatus.ReadyToAssign => "New",
            TradeOrderStatus.Assigned => "Pay",
            TradeOrderStatus.InProgress => "Work",
            TradeOrderStatus.AwaitingDelivery => "Deliver",
            TradeOrderStatus.Completed => "Done",
            TradeOrderStatus.Canceled => "Canceled",
            _ => order.Status.ToString()
        };
    }

    private static bool CanOpenCraftPlan(TradeOrder order)
    {
        return GetOrderRootItems(order).Any(item => item.Quantity > 0);
    }

    private static bool HasLinkedCraftPlan(TradeOrder order)
    {
        return !string.IsNullOrWhiteSpace(order.CraftPlanId);
    }

    private static bool HasMaterialBreakdown(TradeOrder order)
    {
        return TradeOrderWorkflow.GetProcurementEvidenceState(order).HasMaterials;
    }

    private static bool HasProcurementEvidence(TradeOrder order)
    {
        return TradeOrderWorkflow.GetProcurementEvidenceState(order).IsFullyPriced;
    }

    private bool IsPaymentReady(TradeOrder order)
    {
        return HasProcurementEvidence(order) &&
            TradeOrderWorkflow.IsPaymentReady(
                order,
                GetPayrollDraftForOrder(order),
                GetOrderEffectivePaymentPolicy(order));
    }

    private static string GetPipelineStageClass(bool isComplete, bool isWarning = false)
    {
        return isComplete
            ? "trade-orders-pipeline-stage is-complete"
            : isWarning
                ? "trade-orders-pipeline-stage is-warning"
                : "trade-orders-pipeline-stage";
    }

    private string GetSettlementStageLabel(TradeOrder order)
    {
        if (TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            return FormatStatus(order.Status);
        }

        if (order.Status == TradeOrderStatus.AwaitingDelivery)
        {
            return "Awaiting delivery";
        }

        if (order.Status == TradeOrderStatus.InProgress)
        {
            return "In progress";
        }

        return IsPaymentReady(order) ? "Payment ready" : "Awaiting payment";
    }

    private static string GetLinkedCraftPlanName(TradeOrder order)
    {
        if (!string.IsNullOrWhiteSpace(order.CraftPlanName))
        {
            return order.CraftPlanName;
        }

        return HasLinkedCraftPlan(order) ? "Saved craft plan" : "No linked plan";
    }

    private static string GetProcurementEvidenceLabel(TradeOrder order)
    {
        var evidence = TradeOrderWorkflow.GetProcurementEvidenceState(order);
        if (!evidence.HasMaterials)
        {
            return "No material breakdown";
        }

        return evidence.IsFullyPriced
            ? $"{evidence.PricedMaterialCount:N0} priced material lines"
            : $"{evidence.PricedMaterialCount:N0} of {evidence.MaterialCount:N0} material lines priced";
    }

    private static string FormatLinkedCraftPlanDate(TradeOrder order)
    {
        return order.CraftPlanSavedAtUtc.HasValue
            ? order.CraftPlanSavedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "Not saved yet";
    }

    private string GetCraftPlanBuildButtonText(TradeOrder order)
    {
        if (_isSavingSelectedOrderCraftPlan)
        {
            return "Saving...";
        }

        if (!HasLinkedCraftPlan(order))
        {
            return "Create Craft Plan";
        }

        return order.CraftPlanLinkKind == TradeOrderCraftPlanLinkKind.Unknown
            ? "Replace Linked Plan"
            : "Rebuild Linked Plan";
    }

    private static string GetLatestHistoryCue(TradeOrder order)
    {
        var latest = order.History
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault();
        return latest == null
            ? "No history yet"
            : $"{latest.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} - {latest.Note}";
    }

    private void SelectOrder(TradeOrder order)
    {
        _selectedOrder = order;
        _pendingImport = null;
        _showNewOrderPanel = false;
        _activeOpsTab = 0;
        _detailTitle = order.Title;
        _detailCrafterId = order.AssignedCrafterId;
        _detailStatus = order.Status;
        _detailNotes = order.Notes;
        _selectedOrderOutputEditors = TradeRequestedOrderEditorMapper.FromOrder(order);
        _selectedOrderOutputSearchQuery = string.Empty;
        _selectedOrderOutputSearchResults = [];
        _manualNote = string.Empty;
        AppState.SelectTradeOrder(order.Id);
        PersistSelectedOrderInNavigation(order.Id);
    }

    private bool IsSelectedOrderArchived => _selectedOrder != null && TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status);

    private Guid? TryGetOrderIdFromNavigation()
    {
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        var query = uri.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 ||
                !string.Equals(Uri.UnescapeDataString(parts[0]), "orderId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Guid.TryParse(Uri.UnescapeDataString(parts[1]), out var orderId)
                ? orderId
                : null;
        }

        return null;
    }

    private void SelectPendingNavigationOrder()
    {
        if (!_pendingNavigationOrderId.HasValue)
        {
            return;
        }

        var orderId = _pendingNavigationOrderId.Value;
        _pendingNavigationOrderId = null;
        SelectOrderAfterReload(orderId, "Linked Trade order could not be loaded.");
    }

    private bool IsStatusGroupCollapsed(TradeOrderStatus status)
    {
        return _collapsedStatuses.Contains(status);
    }

    private void ToggleStatusGroup(TradeOrderStatus status)
    {
        if (!_collapsedStatuses.Add(status))
        {
            _collapsedStatuses.Remove(status);
        }
    }

    private void ToggleArchiveGroup()
    {
        _isArchiveCollapsed = !_isArchiveCollapsed;
    }

    private void ExpandGroupForOrder(TradeOrder order)
    {
        if (TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            _isArchiveCollapsed = false;
            return;
        }

        _collapsedStatuses.Remove(order.Status);
    }

    private bool SelectOrderAfterReload(Guid orderId, string missingMessage)
    {
        var reloadedOrder = _orders.FirstOrDefault(order => order.Id == orderId);
        if (reloadedOrder == null)
        {
            _selectedOrder = null;
            _manualNote = string.Empty;
            if (AppState.SelectedTradeOrderId == orderId)
            {
                AppState.SelectTradeOrder(null);
            }

            Snackbar.Add(missingMessage, Severity.Warning);
            return false;
        }

        SelectOrder(reloadedOrder);
        ExpandGroupForOrder(reloadedOrder);
        return true;
    }

    private void PersistSelectedOrderInNavigation(Guid orderId)
    {
        var current = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        var target = new UriBuilder(current)
        {
            Query = $"orderId={Uri.EscapeDataString(orderId.ToString("D"))}"
        }.Uri;
        if (!string.Equals(current.PathAndQuery, target.PathAndQuery, StringComparison.Ordinal))
        {
            NavigationManager.NavigateTo(target.PathAndQuery, replace: true);
        }
    }

    private void ClearSelectedOrderNavigation()
    {
        var current = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        if (!string.IsNullOrWhiteSpace(current.Query))
        {
            NavigationManager.NavigateTo(current.AbsolutePath, replace: true);
        }
    }

}
