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
    private async Task SetOrderMaterialResponsibilityAsync(
        TradeCommissionPaymentMaterial material,
        CommissionMaterialResponsibility responsibility)
    {
        if (_selectedOrder == null || _companyProfile == null)
        {
            return;
        }

        if (TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status))
        {
            Snackbar.Add("Reopen archived orders before editing payment responsibility.", Severity.Warning);
            return;
        }

        var orderId = _selectedOrder.Id;
        var currentDraft = await GetOrCreatePayrollDraftForOrderAsync(_selectedOrder);
        var draftToSave = TradeOrderWorkflow.WithMaterialResponsibility(
            currentDraft,
            material.ItemId,
            material.RequiresHq,
            responsibility);
        var savedDraft = await TradePayrollPersistence.SaveDraftAsync(draftToSave);
        if (!savedDraft)
        {
            Snackbar.Add("Failed to save payment responsibility.", Severity.Error);
            return;
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
            SelectOrderAfterReload(orderId, "Payment responsibility was saved, but the order could not be loaded.");
        }
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

    private TradePaymentContractMode SelectedOrderPaymentContract =>
        GetSelectedOrderEffectivePaymentPolicy()?.ActiveContract ?? TradePaymentContractMode.LegacyCommission;

    private string GetSelectedOrderPaymentPolicyLabel()
    {
        if (_selectedOrder == null)
        {
            return "Company default";
        }

        var policy = GetSelectedOrderEffectivePaymentPolicy() ?? TradePaymentPolicy.LegacyDefault;
        var source = SelectedOrderUsesCompanyPaymentPolicy ? "Company default" : "Order override";
        return $"{source}: {FormatPaymentContract(policy.ActiveContract)}";
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

        await SaveSelectedPaymentPolicyOrderAsync(orderToSave);
    }

    private async Task SetSelectedOrderPaymentContractAsync(TradePaymentContractMode contract)
    {
        if (_selectedOrder == null)
        {
            return;
        }

        var current = TradeOrderWorkflow.ResolvePaymentPolicy(_selectedOrder, _companyProfile?.PaymentPolicy);
        var orderToSave = TradeOrderWorkflow.WithPaymentPolicyOverride(
            _selectedOrder,
            current with { ActiveContract = contract });

        await SaveSelectedPaymentPolicyOrderAsync(orderToSave);
    }

    private async Task SaveSelectedPaymentPolicyOrderAsync(TradeOrder orderToSave)
    {
        if (TradeOrderStatusWorkflow.IsArchived(orderToSave.Status))
        {
            Snackbar.Add("Reopen archived orders before editing payment policy.", Severity.Warning);
            return;
        }

        var orderId = orderToSave.Id;
        var saved = await SaveOrderAndNotifyAsync(orderToSave);
        if (!saved)
        {
            Snackbar.Add("Failed to save payment policy.", Severity.Error);
            return;
        }

        await LoadAsync();
        if (string.IsNullOrWhiteSpace(_loadError))
        {
            if (SelectOrderAfterReload(orderId, "Payment policy was saved, but the order could not be loaded."))
            {
                _activeOpsTab = 0;
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

    private async Task OpenCloseOrderDialogAsync(TradeOrderStatus status)
    {
        if (_selectedOrder == null)
        {
            return;
        }

        var parameters = new DialogParameters
        {
            ["Status"] = status,
            ["OrderTitle"] = _selectedOrder.Title
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

        var previousStatus = _selectedOrder.Status;
        var orderId = _selectedOrder.Id;
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
            TradeOrderStatus.Assigned => "Assigned Awaiting Payment",
            TradeOrderStatus.InProgress => "In Progress",
            TradeOrderStatus.AwaitingDelivery => "Awaiting Delivery",
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
