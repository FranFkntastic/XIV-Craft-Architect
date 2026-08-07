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
    private async Task<bool> SetOrderMaterialResponsibilityAsync(
        TradeCommissionPaymentMaterial material,
        CommissionMaterialResponsibility responsibility)
    {
        if (_selectedOrder == null || _companyProfile == null)
        {
            return false;
        }

        if (TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status))
        {
            Snackbar.Add("Reopen archived orders before editing payment responsibility.", Severity.Warning);
            return false;
        }

        var orderId = _selectedOrder.Id;
        var activeOpsTab = _activeOpsTab;
        var currentDraft = _selectedOrder.CompanyCommission != null
            ? GetSelectedOrderResponsibilityProjection() ?? new TradePayrollWorkflowDraft
            {
                CompanyProfileId = _selectedOrder.CompanyProfileId,
                OrderId = _selectedOrder.Id
            }
            : await GetOrCreatePayrollDraftForOrderAsync(_selectedOrder);
        var draftToSave = TradeOrderWorkflow.WithMaterialResponsibility(
            currentDraft,
            material.ItemId,
            material.RequiresHq,
            responsibility);
        if (_selectedOrder.CompanyCommission != null)
        {
            if (!CanEditCanonicalWorkPackage)
            {
                Snackbar.Add(
                    "Published responsibility is part of the accepted terms. Use Revise Terms to change it.",
                    Severity.Warning);
                return false;
            }

            var payment = TradeCommissionPaymentSummary.FromOrder(
                _selectedOrder,
                draftToSave,
                GetSelectedOrderEffectivePaymentPolicy());
            return await UpdateCanonicalDraftAsync(
                _selectedOrder,
                BuildCommissionBrief(_selectedOrder, payment),
                $"{material.Name} responsibility saved to the commission draft");
        }

        var savedDraft = await TradePayrollPersistence.SaveDraftAsync(draftToSave);
        if (!savedDraft)
        {
            Snackbar.Add("Failed to save payment responsibility.", Severity.Error);
            return false;
        }

        _payrollDrafts = _payrollDrafts
            .Where(existingDraft => existingDraft.Id != draftToSave.Id)
            .Append(draftToSave)
            .ToList();

        if (!string.Equals(_selectedOrder.PayrollDraftId, draftToSave.Id, StringComparison.OrdinalIgnoreCase))
        {
            var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
            orderToSave.PayrollDraftId = draftToSave.Id;
            orderToSave.UpdatedAtUtc = DateTime.UtcNow;
            TradeOrderWorkflow.AppendPayrollLinkedHistory(orderToSave, DateTime.UtcNow);
            var savedOrder = await SaveOrderAndNotifyAsync(orderToSave);
            if (!savedOrder)
            {
                Snackbar.Add("Payment responsibility saved, but failed to link it to the order.", Severity.Warning);
            }
        }

        await LoadAsync();
        if (string.IsNullOrWhiteSpace(_loadError))
        {
            if (SelectOrderAfterReload(orderId, "Payment responsibility was saved, but the order could not be loaded."))
            {
                _activeOpsTab = activeOpsTab;
            }
        }
        return true;
    }

    private async Task<TradePayrollWorkflowDraft> GetOrCreatePayrollDraftForOrderAsync(TradeOrder order)
    {
        var existing = GetPayrollDraftForOrder(order);
        if (existing != null)
        {
            return existing;
        }

        var draft = await TradePayrollPersistence.GetOrCreateDraftAsync(
            order.CompanyProfileId,
            order.Id,
            order.SourceSnapshot.PlanSessionVersion,
            order.SourceSnapshot.MarketAnalysisVersion,
            order.SourceSnapshot.SourcePlanName,
            order.AssignedCrafterId,
            order.AssignedCrafterId.HasValue ? FormatAssignedCrafter(order) : null,
            GetOrderEffectivePaymentPolicy(order));
        return draft;
    }

    private TradePaymentPolicy? GetOrderEffectivePaymentPolicy(TradeOrder order)
    {
        return TradeOrderWorkflow.ResolvePaymentPolicy(order, _companyProfile?.PaymentPolicy);
    }

    private TradePaymentPolicy? GetSelectedOrderEffectivePaymentPolicy()
    {
        return _selectedOrder == null
            ? _companyProfile?.PaymentPolicy
            : GetOrderEffectivePaymentPolicy(_selectedOrder);
    }

    private bool SelectedOrderUsesCompanyPaymentPolicy =>
        _selectedOrder?.PaymentPolicyOverride == null;

    private CompanyCommissionPaymentSchedule SelectedOrderPaymentSchedule =>
        _selectedOrderPaymentSchedule;

    private string SelectedOrderCustomPaymentTerms =>
        _selectedOrderCustomPaymentTerms;

    private string GetSelectedOrderPaymentPolicyLabel()
    {
        if (_selectedOrder == null)
        {
            return "Company default";
        }

        if (IsEditingCommissionTermsRevision &&
            _commissionTermsRevisionBrief?.Payment is { } revisionPayment)
        {
            return "Revision payment terms";
        }

        if (_selectedOrder.CompanyCommission?.CurrentTerms.Payment is { } canonicalPayment)
        {
            return CanEditCanonicalDraft ? "Draft payment terms" : "Accepted payment terms";
        }

        var policy = GetSelectedOrderEffectivePaymentPolicy() ?? TradePaymentPolicy.Default;
        var source = SelectedOrderUsesCompanyPaymentPolicy ? "Company default" : "Order override";
        return $"{source}: labor, material-value bonus, and reimbursement";
    }

    private async Task SetSelectedOrderUseCompanyPolicyAsync(bool useCompanyPolicy)
    {
        if (_selectedOrder == null)
        {
            return;
        }

        var orderToSave = useCompanyPolicy
            ? TradeOrderWorkflow.WithoutPaymentPolicyOverride(_selectedOrder)
            : TradeOrderWorkflow.WithPaymentPolicyOverride(
                _selectedOrder,
                TradeOrderWorkflow.ResolvePaymentPolicy(_selectedOrder, _companyProfile?.PaymentPolicy));

        await SaveSelectedPaymentPolicyOrderAsync(
            orderToSave,
            recalculateCanonicalPayment: true);
    }

    private async Task SetSelectedOrderPaymentScheduleAsync(
        CompanyCommissionPaymentSchedule schedule)
    {
        if (_selectedOrder == null)
        {
            return;
        }

        var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
        orderToSave.PaymentSchedule = schedule;
        _selectedOrderPaymentSchedule = schedule;
        _selectedOrderPaymentTermsDirty = true;
        if (IsEditingCommissionTermsRevision)
        {
            _commissionTermsRevisionPaymentDirty = true;
        }
        if (schedule != CompanyCommissionPaymentSchedule.Custom)
        {
            orderToSave.CustomPaymentTerms = null;
            _selectedOrderCustomPaymentTerms = string.Empty;
        }
        else
        {
            orderToSave.CustomPaymentTerms = _selectedOrderCustomPaymentTerms;
            if (IsEditingCommissionTermsRevision && _commissionTermsRevisionBrief != null)
            {
                _commissionTermsRevisionBrief.Payment = _commissionTermsRevisionBrief.Payment with
                {
                    Schedule = schedule,
                    CustomTerms = _selectedOrderCustomPaymentTerms
                };
                _commissionTermsRevisionDirty = true;
            }
            return;
        }

        await SaveSelectedPaymentPolicyOrderAsync(
            orderToSave,
            schedule,
            orderToSave.CustomPaymentTerms,
            "Payment timing saved to the commission draft");
    }

    private async Task SaveSelectedOrderCustomPaymentTermsAsync()
    {
        if (_selectedOrder == null ||
            SelectedOrderPaymentSchedule != CompanyCommissionPaymentSchedule.Custom ||
            string.IsNullOrWhiteSpace(_selectedOrderCustomPaymentTerms))
        {
            return;
        }

        var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
        orderToSave.PaymentSchedule = CompanyCommissionPaymentSchedule.Custom;
        orderToSave.CustomPaymentTerms = _selectedOrderCustomPaymentTerms.Trim();
        await SaveSelectedPaymentPolicyOrderAsync(
            orderToSave,
            orderToSave.PaymentSchedule,
            orderToSave.CustomPaymentTerms,
            "Custom payment timing saved to the commission draft");
    }

    private void SetSelectedOrderCustomPaymentTerms(string? value)
    {
        if (_selectedOrder == null)
        {
            return;
        }

        _selectedOrderCustomPaymentTerms = value ?? string.Empty;
        _selectedOrderPaymentTermsDirty = true;
        if (IsEditingCommissionTermsRevision)
        {
            _commissionTermsRevisionPaymentDirty = true;
        }
        if (IsEditingCommissionTermsRevision && _commissionTermsRevisionBrief != null)
        {
            _commissionTermsRevisionBrief.Payment = _commissionTermsRevisionBrief.Payment with
            {
                CustomTerms = value
            };
            _commissionTermsRevisionDirty = true;
        }
    }

    private async Task SaveSelectedPaymentPolicyOrderAsync(
        TradeOrder orderToSave,
        CompanyCommissionPaymentSchedule? schedule = null,
        string? customTerms = null,
        string successMessage = "Payment basis saved to the commission draft",
        bool recalculateCanonicalPayment = false)
    {
        if (HasSelectedLocalHostedCollision)
        {
            Snackbar.Add(
                "Rebase the local edits onto the hosted copy, or use the hosted copy before saving.",
                Severity.Warning);
            return;
        }

        if (TradeOrderStatusWorkflow.IsArchived(orderToSave.Status))
        {
            Snackbar.Add("Reopen archived orders before editing payment policy.", Severity.Warning);
            return;
        }

        if (orderToSave.CompanyCommission != null)
        {
            if (!CanEditCanonicalWorkPackage)
            {
                Snackbar.Add(
                    "Published payment terms can only change through Revise terms.",
                    Severity.Warning);
                return;
            }

            if (IsEditingCommissionTermsRevision)
            {
                _commissionTermsRevisionPaymentDirty = true;
            }

            var canonicalWorkPackage = IsEditingCommissionTermsRevision
                ? orderToSave
                : TradeOrderWorkflow.CopyOrder(
                    SelectedCommissionOwner?.Order ?? orderToSave);
            if (recalculateCanonicalPayment)
            {
                canonicalWorkPackage.PaymentPolicyOverride =
                    orderToSave.PaymentPolicyOverride;
            }
            canonicalWorkPackage.PaymentSchedule = orderToSave.PaymentSchedule;
            canonicalWorkPackage.CustomPaymentTerms = orderToSave.CustomPaymentTerms;

            var brief = recalculateCanonicalPayment
                ? BuildCommissionBrief(
                    canonicalWorkPackage,
                    TradeCommissionPaymentSummary.FromOrder(
                        canonicalWorkPackage,
                        GetSelectedOrderResponsibilityProjection(),
                        GetOrderEffectivePaymentPolicy(canonicalWorkPackage)))
                : IsEditingCommissionTermsRevision && _commissionTermsRevisionBrief != null
                    ? _commissionTermsRevisionBrief
                    : BuildCanonicalCommissionBrief(
                        SelectedCommissionOwner?.Order ?? canonicalWorkPackage,
                        SelectedCommissionOwner?.Order.CompanyCommission ??
                            canonicalWorkPackage.CompanyCommission!);
            if (schedule.HasValue)
            {
                brief.Payment = brief.Payment with
                {
                    Schedule = schedule.Value,
                    CustomTerms = schedule == CompanyCommissionPaymentSchedule.Custom
                        ? customTerms?.Trim()
                        : null
                };
            }
            if (await UpdateCanonicalDraftAsync(
                    canonicalWorkPackage,
                    brief,
                    successMessage))
            {
                _selectedOrderPaymentTermsDirty = false;
            }
            return;
        }

        var orderId = orderToSave.Id;
        var activeOpsTab = _activeOpsTab;
        var saved = await SaveOrderAndNotifyAsync(orderToSave);
        if (!saved)
        {
            Snackbar.Add("Failed to save payment policy.", Severity.Error);
            return;
        }

        _selectedOrderPaymentTermsDirty = false;

        await LoadAsync();
        if (string.IsNullOrWhiteSpace(_loadError))
        {
            if (SelectOrderAfterReload(orderId, "Payment policy was saved, but the order could not be loaded."))
            {
                _activeOpsTab = activeOpsTab;
            }
        }
    }

    private async Task CopyGilAmountAsync(decimal value, string successMessage)
    {
        await CopyTextToClipboardAsync(Math.Round(value, 0).ToString("0"), successMessage);
    }

    private async Task CopyOrderPaymentReceiptAsync()
    {
        if (_selectedOrder == null)
        {
            return;
        }

        await CopyTextToClipboardAsync(
            TradeOrderPaymentCopyFormatter.BuildReceipt(CreateOrderPaymentCopyContext(_selectedOrder)),
            "Payment receipt copied");
    }

    private async Task CopyOrderPaymentSummaryAsync()
    {
        if (_selectedOrder == null)
        {
            return;
        }

        await CopyTextToClipboardAsync(
            TradeOrderPaymentCopyFormatter.BuildSummary(CreateOrderPaymentCopyContext(_selectedOrder)),
            "Payment summary copied");
    }

    private async Task RetrySelectedDeviceOnlyOrderSyncAsync()
    {
        if (!IsSelectedDeviceOnlyOrder ||
            _selectedOrder == null ||
            _isRetryingSelectedDeviceOnlyOrderSync)
        {
            return;
        }

        if (HasSelectedLocalDraftEditorChanges)
        {
            Snackbar.Add(
                "Save the current draft edits before retrying sync.",
                Severity.Warning);
            return;
        }

        _isRetryingSelectedDeviceOnlyOrderSync = true;
        var orderId = _selectedOrder.Id;
        try
        {
            if (_selectedOrder.CraftPlanLinkKind == TradeOrderCraftPlanLinkKind.OrderGenerated &&
                !string.IsNullOrWhiteSpace(_selectedOrder.CraftPlanId))
            {
                var linkedPlan = await PlanPersistence.LoadPlanPayloadAsync(
                    _selectedOrder.CraftPlanId);
                if (linkedPlan == null ||
                    linkedPlan.LinkedOrderId != orderId ||
                    !_selectedOrder.CraftPlanSavedAtUtc.HasValue ||
                    linkedPlan.SavedAt != _selectedOrder.CraftPlanSavedAtUtc.Value)
                {
                    Snackbar.Add(
                        "The draft's exact generated plan is unavailable. Reconstruct the plan before retrying sync.",
                        Severity.Warning);
                    return;
                }

                await ProfileSync.QueueLocalSaveAsync(
                    ProfileSyncCollections.Plans,
                    linkedPlan.Id);
            }

            await ProfileSync.QueueLocalSaveAsync(
                ProfileSyncCollections.TradeOrders,
                orderId.ToString("D"));
            await ProfileSync.SyncNowAsync();
            await LoadAsync();
            if (HostedOrders.Get(orderId) is { Deleted: false, Order: not null } hosted)
            {
                if (HasSelectedLocalDraftEditorChanges || HasSelectedLocalHostedCollision)
                {
                    _selectedLocalHostedCollision = hosted;
                    Snackbar.Add(
                        "The hosted copy arrived while you were editing. Rebase or discard the local buffer.",
                        Severity.Warning);
                    return;
                }

                SelectOrder(hosted.OwnerProjection?.Order ?? hosted.Order);
                Snackbar.Add("Draft joined the hosted order workspace.", Severity.Success);
                return;
            }

            SelectOrderAfterReload(
                orderId,
                "The local draft is still saved, but it could not be reloaded.");
            var conflict = ProfileSync.Conflicts.Any(item =>
                string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal) &&
                string.Equals(item.ObjectId, orderId.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Snackbar.Add(
                conflict
                    ? "The hosted order changed. Resolve the sync conflict before this draft can join the workspace."
                    : "Draft is saved on this device and waiting to sync.",
                conflict ? Severity.Warning : Severity.Info);
        }
        catch (Exception exception)
        {
            Snackbar.Add($"Draft sync could not be retried: {exception.Message}", Severity.Error);
        }
        finally
        {
            _isRetryingSelectedDeviceOnlyOrderSync = false;
        }
    }

    private Task InvokeSelectedOrderLifecycleActionAsync() =>
        SelectedLifecycleAction switch
        {
            TradeOrderLifecycleAction.DiscardDraft => DiscardSelectedDraftAsync(),
            TradeOrderLifecycleAction.CancelCommission =>
                OpenCloseOrderDialogAsync(TradeOrderStatus.Canceled),
            _ => Task.CompletedTask
        };

    private async Task DiscardSelectedDraftAsync()
    {
        if (!CanDiscardSelectedDraft ||
            _selectedOrder == null ||
            _isDiscardingSelectedDraft)
        {
            return;
        }

        var order = _selectedOrder;
        var location = IsSelectedDeviceOnlyOrder
            ? "from this device"
            : "from the hosted workspace and every connected browser";
        var confirmed = await DialogService.ShowMessageBox(
            "Discard Draft",
            $"Discard '{order.Title}' {location} and remove its generated plans? This cannot be undone.",
            yesText: "Discard Draft",
            cancelText: "Keep Draft");
        if (confirmed != true)
        {
            return;
        }

        _isDiscardingSelectedDraft = true;
        try
        {
            await OrderLifecycle.DiscardDraftAsync(order);
            await LoadAsync();
            _selectedOrder = null;
            AppState.SelectTradeOrder(null);
            ClearSelectedOrderNavigation();
            Snackbar.Add("Draft discarded.", Severity.Success);
        }
        catch (Exception exception)
        {
            await LoadAsync();
            var current = VisibleOrders.FirstOrDefault(candidate => candidate.Id == order.Id);
            if (current != null)
            {
                SelectOrder(current);
            }
            Snackbar.Add(
                $"Draft cleanup did not finish. The current order was preserved: {exception.Message}",
                Severity.Error);
        }
        finally
        {
            _isDiscardingSelectedDraft = false;
        }
    }

    private async Task OpenCloseOrderDialogAsync(TradeOrderStatus status)
    {
        if (_selectedOrder == null)
        {
            return;
        }

        var parameters = new DialogParameters
        {
            ["Status"] = status,
            ["OrderTitle"] = _selectedOrder.Title,
            ["IsOrphanCleanup"] = status == TradeOrderStatus.Canceled &&
                IsSelectedCanonicalOwnerMissing
        };
        var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small };
        var dialog = await DialogService.ShowAsync<TradeOrderCloseDialog>("Close Order", parameters, options);
        var result = await dialog.Result;

        if (result?.Data is TradeOrderCloseDialogResult closeResult)
        {
            await CloseSelectedOrderAsync(closeResult.Status, closeResult.Note);
        }
    }

    private async Task CloseSelectedOrderAsync(TradeOrderStatus status, string? note)
    {
        if (_selectedOrder == null)
        {
            return;
        }

        var orderId = _selectedOrder.Id;
        if (status == TradeOrderStatus.Canceled)
        {
            try
            {
                var cancellation = await OrderLifecycle.CancelAndRetractAsync(
                    _selectedOrder,
                    note);
                if (cancellation.RemovedOrphanedLocalOrder)
                {
                    await LoadAsync();
                    _selectedOrder = null;
                    _manualNote = string.Empty;
                    if (AppState.SelectedTradeOrderId == orderId)
                    {
                        AppState.SelectTradeOrder(null);
                    }

                    ClearSelectedOrderNavigation();
                    Snackbar.Add(
                        "The hosted commission had already been removed, so its stale local order and any remaining Discord publication were removed.",
                        Severity.Success);
                    return;
                }

                Snackbar.Add("Order canceled and removed from the Discord commissions channel.", Severity.Success);
            }
            catch (Exception exception)
            {
                Snackbar.Add($"Could not cancel order: {exception.Message}", Severity.Error);
                return;
            }

            await LoadAsync();
            if (string.IsNullOrWhiteSpace(_loadError))
            {
                SelectOrderAfterReload(orderId, "The order was canceled, but it could not be loaded.");
            }
            return;
        }

        var previousStatus = _selectedOrder.Status;
        var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
        orderToSave.Status = status;
        orderToSave.UpdatedAtUtc = DateTime.UtcNow;
        TradeOrderWorkflow.AppendStatusHistory(orderToSave, previousStatus, status, string.IsNullOrWhiteSpace(note) ? FormatStatus(status) : note.Trim(), DateTime.UtcNow);
        var saved = await SaveOrderAndNotifyAsync(orderToSave);
        if (!saved)
        {
            Snackbar.Add("Failed to save Trade order.", Severity.Error);
            return;
        }

        await LoadAsync();
        if (string.IsNullOrWhiteSpace(_loadError))
        {
            SelectOrderAfterReload(orderId, "Trade order was closed, but it could not be loaded.");
        }
    }

    private async Task DeleteSelectedOrderAsync()
    {
        if (_selectedOrder == null || _isDeletingSelectedOrder)
        {
            return;
        }

        var order = _selectedOrder;
        var confirmed = await DialogService.ShowMessageBox(
            "Delete Order Permanently",
            $"Delete '{order.Title}' and its linked payroll draft, generated craft plan, and Discord publication? This cannot be undone.",
            yesText: "Delete Permanently",
            cancelText: "Keep Order");
        if (confirmed != true)
        {
            return;
        }

        _isDeletingSelectedOrder = true;
        try
        {
            await OrderLifecycle.DeleteOrderAsync(order);
            await LoadAsync();
            _selectedOrder = null;
            _manualNote = string.Empty;
            if (AppState.SelectedTradeOrderId == order.Id)
            {
                AppState.SelectTradeOrder(null);
            }
            Snackbar.Add("Order and linked commission data deleted.", Severity.Success);
        }
        catch (Exception exception)
        {
            Snackbar.Add($"Could not delete order: {exception.Message}", Severity.Error);
        }
        finally
        {
            _isDeletingSelectedOrder = false;
        }
    }

    private string FormatAssignedCrafter(TradeOrder order)
    {
        if (!order.AssignedCrafterId.HasValue)
        {
            return "Unassigned";
        }

        return _crafters.FirstOrDefault(crafter => crafter.Id == order.AssignedCrafterId.Value)?.DisplayName ?? "Assigned";
    }

    private async Task ReopenSelectedOrderAsync()
    {
        if (_selectedOrder == null)
        {
            return;
        }

        var previousStatus = _selectedOrder.Status;
        var orderId = _selectedOrder.Id;
        var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
        orderToSave.Status = orderToSave.AssignedCrafterId.HasValue
            ? TradeOrderStatus.Assigned
            : TradeOrderStatus.ReadyToAssign;
        orderToSave.UpdatedAtUtc = DateTime.UtcNow;
        TradeOrderWorkflow.AppendReopenedHistory(orderToSave, previousStatus, orderToSave.Status, DateTime.UtcNow);
        var saved = await SaveOrderAndNotifyAsync(orderToSave);
        if (!saved)
        {
            Snackbar.Add("Failed to save Trade order.", Severity.Error);
            return;
        }

        await LoadAsync();
        if (string.IsNullOrWhiteSpace(_loadError))
        {
            SelectOrderAfterReload(orderId, "Trade order was reopened, but it could not be loaded.");
        }
    }

    private void AddHistoryIfAssignmentChanged(TradeOrder order, Guid? previousCrafterId, Guid? newCrafterId)
    {
        var crafterName = newCrafterId.HasValue
            ? _crafters.FirstOrDefault(crafter => crafter.Id == newCrafterId.Value)?.DisplayName ?? "unknown crafter"
            : null;
        TradeOrderWorkflow.AppendAssignmentHistory(
            order,
            previousCrafterId,
            newCrafterId,
            crafterName,
            DateTime.UtcNow);
    }

    private static string FormatStatus(TradeOrderStatus status)
    {
        return status switch
        {
            TradeOrderStatus.ReadyToAssign => "Ready to Assign",
            TradeOrderStatus.Assigned => "Assigned",
            TradeOrderStatus.InProgress => "Crafting",
            TradeOrderStatus.AwaitingDelivery => "Ready for Delivery",
            TradeOrderStatus.ResolutionRequired => "Resolution Required",
            _ => status.ToString()
        };
    }

    private static string FormatHistoryKind(TradeOrderHistoryEventKind kind)
    {
        return kind switch
        {
            TradeOrderHistoryEventKind.ManualNote => "Note",
            TradeOrderHistoryEventKind.StatusChanged => "Status",
            TradeOrderHistoryEventKind.Reopened => "Reopened",
            TradeOrderHistoryEventKind.PayrollLinked => "Payroll",
            TradeOrderHistoryEventKind.CraftPlanLinked => "Plan",
            TradeOrderHistoryEventKind.PricingRefreshed => "Pricing",
            TradeOrderHistoryEventKind.CommissionPublished => "Published",
            TradeOrderHistoryEventKind.CommissionRevoked => "Revoked",
            _ => kind.ToString()
        };
    }

    private static string FormatHq(bool mustBeHq)
    {
        return mustBeHq ? "HQ" : string.Empty;
    }

    private static Severity ToSnackbarSeverity(RecipePlannerCommandMessageLevel level)
    {
        return level switch
        {
            RecipePlannerCommandMessageLevel.Success => Severity.Success,
            RecipePlannerCommandMessageLevel.Warning => Severity.Warning,
            RecipePlannerCommandMessageLevel.Error => Severity.Error,
            _ => Severity.Info
        };
    }

    private static string GetPaymentMaterialKey(TradeCommissionPaymentMaterial material)
    {
        return $"{material.ItemId}:{material.RequiresHq}";
    }

}
