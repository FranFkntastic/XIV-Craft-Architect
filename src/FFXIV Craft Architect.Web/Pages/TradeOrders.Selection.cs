using System.Text;

using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Dialogs;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private IReadOnlyList<OrderAttentionGroup> BuildAttentionGroups(
        IEnumerable<TradeOrder> orders)
    {
        return orders
            .GroupBy(GetOrderAttentionKey)
            .OrderBy(group => GetAttentionSort(group.Key))
            .Select(group => new OrderAttentionGroup(
                group.Key,
                FormatAttentionGroup(group.Key),
                group.OrderByDescending(order => order.CommissionedAtUtc).ToArray()))
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
            (order.CompanyCommission == null
                ? FormatStatus(order.Status)
                : FormatWorkbenchStatus(order))
            .Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private bool ArchiveOrderMatchesSearch(ArchivedOrderRow order)
    {
        if (string.IsNullOrWhiteSpace(_orderSearchText))
        {
            return true;
        }

        var query = _orderSearchText.Trim();
        return order.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            FormatRailStatusChip(order.Status).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            order.OutputNames.Any(name => name.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            order.Order != null && OrderMatchesSearch(order.Order);
    }

    private string GetRailOrderClass(TradeOrder order)
    {
        return _selectedOrder?.Id == order.Id
            ? "trade-orders-rail-order is-selected"
            : "trade-orders-rail-order";
    }

    private string GetRailOrderClass(ArchivedOrderRow order)
    {
        return _selectedOrder?.Id == order.OrderId
            ? "trade-orders-rail-order is-selected"
            : "trade-orders-rail-order";
    }

    private string FormatOrderRailMeta(TradeOrder order)
    {
        if (IsIdentityOnlyOrder(order))
        {
            return "Saved order identity";
        }

        return $"{FormatAssignedCrafter(order)} - {order.CommissionedAtUtc.ToLocalTime():yyyy-MM-dd}";
    }

    private string FormatRailStatusChip(TradeOrder order)
    {
        if (order.CompanyCommission != null)
        {
            return IsIdentityOnlyOrder(order)
                ? "Verifying"
                : FormatWorkbenchStatus(order);
        }

        if (order.Status == TradeOrderStatus.InProgress)
        {
            return "Work";
        }

        if (order.Status == TradeOrderStatus.AwaitingDelivery)
        {
            return "Deliver";
        }

        return order.Status switch
        {
            TradeOrderStatus.ReadyToAssign => "New",
            TradeOrderStatus.Assigned => "Pay",
            TradeOrderStatus.InProgress => "Work",
            TradeOrderStatus.AwaitingDelivery => "Deliver",
            TradeOrderStatus.ResolutionRequired => "Resolve",
            TradeOrderStatus.Completed => "Done",
            TradeOrderStatus.Canceled => "Canceled",
            _ => order.Status.ToString()
        };
    }

    private static string FormatRailStatusChip(TradeOrderStatus status) =>
        status switch
        {
            TradeOrderStatus.Completed => "Done",
            TradeOrderStatus.Canceled => "Canceled",
            _ => status.ToString()
        };

    private static string FormatArchiveOrderDate(ArchivedOrderRow order) =>
        order.CommissionedAtUtc.ToLocalTime().ToString("yyyy-MM-dd");

    private static string FormatArchiveOrderOutputs(ArchivedOrderRow order) =>
        order.OutputNames.Count == 0
            ? "No requested outputs"
            : string.Join(", ", order.OutputNames);

    private bool IsFetchingArchiveOrder(Guid orderId) =>
        _fetchingArchiveOrderIds.Contains(orderId);

    private async Task OpenArchiveOrderAsync(ArchivedOrderRow row)
    {
        if (row.Order != null)
        {
            SelectOrder(row.Order);
            return;
        }
        if (row.SummaryRecord == null || !_fetchingArchiveOrderIds.Add(row.OrderId))
        {
            return;
        }

        try
        {
            var connection = await ProfileSyncLocalState.LoadConnectionSettingsAsync();
            if (!connection.IsConfigured)
            {
                throw new InvalidOperationException(
                    "Connect to the hosted profile before opening this archived order.");
            }

            var envelope = await ProfileHostClient.GetObjectAsync(
                connection.HostUrl!,
                connection.AccessKey!,
                ProfileSyncCollections.TradeOrders,
                row.OrderId.ToString("D"),
                CancellationToken.None);
            if (envelope == null)
            {
                await ArchiveSummaries.RemoveAsync(row.OrderId);
                Snackbar.Add(
                    "This archived order is no longer available on the profile host.",
                    Severity.Info);
                return;
            }
            if (!string.Equals(
                    envelope.Collection,
                    ProfileSyncCollections.TradeOrders,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    envelope.ObjectId,
                    row.OrderId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase) ||
                envelope.Deleted ||
                envelope.IsSummary)
            {
                throw new InvalidOperationException(
                    "The profile host returned an invalid archived order payload.");
            }

            await TradeOrderSyncAdapter.ApplyRemoteObjectAsync(
                envelope,
                CancellationToken.None);
            var hosted = HostedOrders.Get(row.OrderId);
            if (hosted is not { Deleted: false, Order: not null })
            {
                throw new InvalidOperationException(
                    "The archived order payload could not be adopted into this workspace.");
            }

            _orderHostedRevisions[row.OrderId] = hosted.ObjectRevision;
            SelectOrder(hosted.OwnerProjection?.Order ?? hosted.Order);
            ExpandGroupForOrder(hosted.Order);
        }
        catch (Exception ex)
        {
            Snackbar.Add(
                $"Could not open archived order. {ex.Message}",
                Severity.Warning);
        }
        finally
        {
            _fetchingArchiveOrderIds.Remove(row.OrderId);
        }
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
            return "Ready for delivery";
        }

        if (order.Status == TradeOrderStatus.InProgress)
        {
            return "Crafting";
        }

        return IsPaymentReady(order) ? "Payment calculated" : "Calculate payment";
    }

    private static string GetLinkedCraftPlanName(TradeOrder order)
    {
        if (!string.IsNullOrWhiteSpace(order.CraftPlanName))
        {
            return order.CraftPlanName;
        }

        return HasLinkedCraftPlan(order) ? "Linked craft plan" : "No linked plan";
    }

    private static string GetProcurementEvidenceLabel(TradeOrder order)
    {
        var evidence = TradeOrderWorkflow.GetProcurementEvidenceState(order);
        if (!evidence.HasMaterials)
        {
            return "No material breakdown";
        }

        return evidence.IsFullyPriced
            ? $"{evidence.PricedMaterialCount:N0} resolved supply lines"
            : $"{evidence.PricedMaterialCount:N0} of {evidence.MaterialCount:N0} supply lines resolved";
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
            : "Rebuild from Requested Outputs";
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
        var isSameOrder = _selectedOrder?.Id == order.Id;
        var isSameLinkedPlan = isSameOrder &&
            !string.IsNullOrWhiteSpace(_selectedOrder?.CraftPlanId) &&
            string.Equals(_selectedOrder.CraftPlanId, order.CraftPlanId, StringComparison.Ordinal) &&
            _selectedOrder.CraftPlanSavedAtUtc == order.CraftPlanSavedAtUtc;
        _selectedLocalHostedCollision = null;
        if (!isSameLinkedPlan)
        {
            InvalidateSelectedOrderPlanRestoration();
            ClearLiveProcurementSnapshot();
        }
        _selectedOrderPlanRestoreError = null;
        _selectedOrder = order;
        _pendingImport = null;
        _showNewOrderPanel = false;
        if (!isSameOrder)
        {
            _activeOpsTab = 0;
        }
        _detailTitle = order.Title;
        _detailCrafterId = order.AssignedCrafterId;
        _detailStatus = order.Status;
        _detailNotes = order.Notes;
        var paymentTiming = order.CompanyCommission?.CurrentTerms.Payment;
        _selectedOrderPaymentSchedule = paymentTiming?.Schedule ?? order.PaymentSchedule;
        _selectedOrderCustomPaymentTerms = paymentTiming?.CustomTerms ?? order.CustomPaymentTerms ?? string.Empty;
        _selectedOrderPaymentTermsDirty = false;
        _selectedOrderOutputEditors = TradeRequestedOrderEditorMapper.FromOrder(order);
        _selectedOrderOutputSearchQuery = string.Empty;
        _selectedOrderOutputSearchResults = [];
        _manualNote = string.Empty;
        PrepareCommissionDraft(order);
        PrepareCompanyCommissionEditor(order);
        AppState.SelectTradeOrder(order.Id);
        PersistSelectedOrderInNavigation(order.Id);
        ScheduleSelectedOrderPlanRestoration();
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

    private bool IsAttentionGroupCollapsed(string key)
    {
        return _collapsedAttentionGroups.Contains(key);
    }

    private void ToggleAttentionGroup(string key)
    {
        if (!_collapsedAttentionGroups.Add(key))
        {
            _collapsedAttentionGroups.Remove(key);
        }
    }

    private void ToggleArchiveGroup()
    {
        _isArchiveCollapsed = !_isArchiveCollapsed;
    }

    private void ToggleDeviceOnlyGroup()
    {
        _isDeviceOnlyCollapsed = !_isDeviceOnlyCollapsed;
    }

    private void ExpandGroupForOrder(TradeOrder order)
    {
        if (IsDeviceOnlyOrder(order))
        {
            _isDeviceOnlyCollapsed = false;
            return;
        }

        if (IsOrderArchivedForAttention(order))
        {
            _isArchiveCollapsed = false;
            return;
        }

        _collapsedAttentionGroups.Remove(GetOrderAttentionKey(order));
    }

    private sealed record ArchivedOrderRow(
        Guid OrderId,
        string Title,
        TradeOrderStatus Status,
        DateTime CommissionedAtUtc,
        IReadOnlyList<string> OutputNames,
        TradeOrder? Order,
        TradeOrderArchiveSummaryRecord? SummaryRecord);

    private bool IsOrderArchivedForAttention(TradeOrder order)
    {
        if (CommissionOperations.GetForOrder(order.Id) is { } projection &&
            projection.Order.CompanyCommission is { } commission)
        {
            return order.Status == TradeOrderStatus.Canceled ||
                commission.IsClosed(projection.Order.Status);
        }

        if (order.CompanyCommission != null)
        {
            return false;
        }

        return TradeOrderStatusWorkflow.IsArchived(order.Status);
    }

    private string GetOrderAttentionKey(TradeOrder order)
    {
        if (CommissionOperations.GetForOrder(order.Id) is { } projection)
        {
            return TradeCommissionOperationsPresentation.GetAttentionGroup(projection);
        }

        if (order.CompanyCommission != null)
        {
            return TradeCommissionOperationsPresentation.SyncAttention;
        }

        return order.Status switch
        {
            TradeOrderStatus.ReadyToAssign => TradeCommissionOperationsPresentation.OpenAttention,
            TradeOrderStatus.Assigned => TradeCommissionOperationsPresentation.PreWorkAttention,
            TradeOrderStatus.InProgress => TradeCommissionOperationsPresentation.WorkAttention,
            TradeOrderStatus.AwaitingDelivery => TradeCommissionOperationsPresentation.DeliveryAttention,
            TradeOrderStatus.ResolutionRequired => TradeCommissionOperationsPresentation.ResolutionAttention,
            _ => TradeCommissionOperationsPresentation.OpenAttention
        };
    }

    private static int GetAttentionSort(string key) =>
        key switch
        {
            TradeCommissionOperationsPresentation.SyncAttention => 0,
            TradeCommissionOperationsPresentation.ResolutionAttention => 1,
            TradeCommissionOperationsPresentation.ClaimAttention => 2,
            TradeCommissionOperationsPresentation.PreWorkAttention => 3,
            TradeCommissionOperationsPresentation.DeliveryAttention => 4,
            TradeCommissionOperationsPresentation.WorkAttention => 5,
            _ => 6
        };

    private static string FormatAttentionGroup(string key) =>
        key switch
        {
            TradeCommissionOperationsPresentation.SyncAttention => "Needs Attention",
            TradeCommissionOperationsPresentation.ResolutionAttention => "Manual resolution",
            TradeCommissionOperationsPresentation.ClaimAttention => "Claim / Identity Review",
            TradeCommissionOperationsPresentation.PreWorkAttention => "Needs prerequisites",
            TradeCommissionOperationsPresentation.DeliveryAttention => "Ready for delivery",
            TradeCommissionOperationsPresentation.WorkAttention => "Crafting",
            _ => "Open"
        };

    private bool SelectOrderAfterReload(Guid orderId, string missingMessage)
    {
        var reloadedOrder = VisibleOrders.FirstOrDefault(order => order.Id == orderId) ??
            DeviceOnlyOrders.FirstOrDefault(order =>
                order.Id == orderId && order.CompanyCommission == null);
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
