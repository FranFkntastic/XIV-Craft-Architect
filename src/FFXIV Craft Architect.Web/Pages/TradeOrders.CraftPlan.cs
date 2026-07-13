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
    private async Task SaveSelectedOrderOutputsAsync()
    {
        if (_selectedOrder == null || _companyProfile == null || _isSavingSelectedOrderOutputs)
        {
            return;
        }

        if (!TradeOrderWorkflow.CanEditRequestedOutputs(_selectedOrder))
        {
            Snackbar.Add("Requested outputs can only be edited before work starts.", Severity.Warning);
            return;
        }

        var outputs = TradeRequestedOrderEditorMapper.ToOutputs(_selectedOrderOutputEditors);
        if (outputs.Count == 0)
        {
            Snackbar.Add("Add at least one requested output before saving.", Severity.Warning);
            return;
        }

        _isSavingSelectedOrderOutputs = true;
        var orderId = _selectedOrder.Id;

        try
        {
            var orderToSave = TradeOrderWorkflow.WithRequestedOutputs(_selectedOrder, outputs, DateTime.UtcNow);
            var saved = await SaveOrderAndNotifyAsync(orderToSave);
            if (!saved)
            {
                Snackbar.Add("Failed to save requested outputs.", Severity.Error);
                return;
            }

            await LoadAsync();
            if (string.IsNullOrWhiteSpace(_loadError) &&
                SelectOrderAfterReload(orderId, "Requested outputs were saved, but the order could not be loaded."))
            {
                _activeOpsTab = 0;
            }

            Snackbar.Add("Requested outputs saved. Rebuild the linked craft plan before using payment totals.", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to save requested outputs: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isSavingSelectedOrderOutputs = false;
        }
    }

    private async Task SaveSelectedOrderAsync()
    {
        if (_selectedOrder == null)
        {
            return;
        }

        if (TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status))
        {
            Snackbar.Add("Reopen archived orders before editing details.", Severity.Warning);
            return;
        }

        var title = _detailTitle.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            Snackbar.Add("Order title is required.", Severity.Warning);
            return;
        }

        if (TradeOrderStatusWorkflow.IsArchived(_detailStatus) || _detailStatus == TradeOrderStatus.Draft)
        {
            Snackbar.Add("Use the close order controls for archive transitions.", Severity.Warning);
            return;
        }

        var resolvedStatus = TradeOrderWorkflow.ResolveStatusForAssignment(_detailStatus, _detailCrafterId);
        if (!_detailCrafterId.HasValue && resolvedStatus == TradeOrderStatus.Assigned)
        {
            Snackbar.Add("Change status to Ready to Assign before clearing this assignment.", Severity.Warning);
            return;
        }
        else if (!_detailCrafterId.HasValue && (resolvedStatus == TradeOrderStatus.InProgress || resolvedStatus == TradeOrderStatus.AwaitingDelivery))
        {
            Snackbar.Add("Assign a crafter before using this status.", Severity.Warning);
            return;
        }

        var previousStatus = _selectedOrder.Status;
        var previousCrafterId = _selectedOrder.AssignedCrafterId;
        var orderId = _selectedOrder.Id;
        var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
        orderToSave.Title = title;
        orderToSave.AssignedCrafterId = _detailCrafterId;
        orderToSave.Status = resolvedStatus;
        orderToSave.Notes = _detailNotes;
        orderToSave.UpdatedAtUtc = DateTime.UtcNow;
        AddHistoryIfAssignmentChanged(orderToSave, previousCrafterId, _detailCrafterId);
        TradeOrderWorkflow.AppendStatusHistory(orderToSave, previousStatus, resolvedStatus, "Status changed from detail panel.", DateTime.UtcNow);
        var saved = await SaveOrderAndNotifyAsync(orderToSave);
        if (!saved)
        {
            Snackbar.Add("Failed to save Trade order.", Severity.Error);
            return;
        }

        await LoadAsync();
        if (string.IsNullOrWhiteSpace(_loadError))
        {
            SelectOrderAfterReload(orderId, "Trade order was saved, but it could not be loaded.");
        }
    }

    private async Task AddManualNoteAsync()
    {
        if (_selectedOrder == null || _companyProfile == null || string.IsNullOrWhiteSpace(_manualNote))
        {
            return;
        }

        var orderId = _selectedOrder.Id;
        var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
        var history = orderToSave.History.ToList();
        history.Add(TradeOrderHistoryEvent.CreateManualNote(_companyProfile.Id, orderToSave.Id, _manualNote.Trim(), DateTime.UtcNow));
        orderToSave.History = history;
        orderToSave.UpdatedAtUtc = DateTime.UtcNow;
        var saved = await SaveOrderAndNotifyAsync(orderToSave);
        if (!saved)
        {
            Snackbar.Add("Failed to save Trade order.", Severity.Error);
            return;
        }

        _manualNote = string.Empty;
        await LoadAsync();
        if (string.IsNullOrWhiteSpace(_loadError))
        {
            if (SelectOrderAfterReload(orderId, "Trade order note was saved, but the order could not be loaded."))
            {
                _activeOpsTab = 2;
            }
        }
    }

    private async Task CreateOrReplaceSelectedOrderCraftPlanAsync()
    {
        if (_selectedOrder == null || _companyProfile == null || _isSavingSelectedOrderCraftPlan)
        {
            return;
        }

        if (TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status))
        {
            Snackbar.Add("Reopen archived orders before replacing the linked craft plan.", Severity.Warning);
            return;
        }

        if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
            HasLinkedCraftPlan(_selectedOrder) ? "Rebuilding this order plan" : "Creating this order plan",
            _selectedOrder.CraftPlanId))
        {
            return;
        }

        var assessment = TradeOrderWorkflow.AssessGeneratedCraftPlanReplacement(_selectedOrder);
        if (assessment.RequiresConfirmation &&
            !await ConfirmCraftPlanReplacementAsync(assessment))
        {
            return;
        }

        _isSavingSelectedOrderCraftPlan = true;
        var orderId = _selectedOrder.Id;

        try
        {
            var result = await TradeOrderPricingWorkflow.RebuildAndPriceAsync(
                _selectedOrder,
                new TradeOrderPricingWorkflowOptions(
                    GetOrderDataCenter(_selectedOrder),
                    _selectedOrder.SourceSnapshot.World ?? string.Empty,
                    ForceRefreshMarketData: false));
            if (!result.HasUpdatedOrder || result.UpdatedOrder == null)
            {
                Snackbar.Add(result.Message, ToSnackbarSeverity(result.MessageLevel));
                return;
            }

            var saved = await SaveOrderAndNotifyAsync(result.UpdatedOrder);
            if (!saved)
            {
                Snackbar.Add("Craft plan saved, but failed to link it to the order.", Severity.Error);
                return;
            }

            await LoadAsync();
            if (string.IsNullOrWhiteSpace(_loadError))
            {
                SelectOrderAfterReload(orderId, "Craft plan was saved, but the order could not be loaded.");
            }

            Snackbar.Add(result.Message, ToSnackbarSeverity(result.MessageLevel));
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to rebuild and price linked craft plan: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isSavingSelectedOrderCraftPlan = false;
        }
    }

    private async Task<bool> ConfirmCraftPlanReplacementAsync(TradeOrderCraftPlanReplacementAssessment assessment)
    {
        if (assessment.Mode == TradeOrderCraftPlanReplacementMode.Create)
        {
            return true;
        }

        var parameters = new DialogParameters
        {
            ["Assessment"] = assessment
        };
        var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small };
        var dialog = await DialogService.ShowAsync<TradeOrderReplaceCraftPlanDialog>(
            "Rebuild Linked Craft Plan",
            parameters,
            options);
        var result = await dialog.Result;
        return result?.Data is true;
    }

    private async Task OpenSelectedOrderCraftPlanAsync()
    {
        if (_selectedOrder == null || _isOpeningSelectedOrderCraftPlan)
        {
            return;
        }

        if (!HasLinkedCraftPlan(_selectedOrder))
        {
            Snackbar.Add("Create a linked craft plan before opening it.", Severity.Warning);
            return;
        }

        _isOpeningSelectedOrderCraftPlan = true;

        try
        {
            if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
                "Opening this order plan",
                _selectedOrder.CraftPlanId))
            {
                return;
            }

            var result = await PlanPersistence.LoadPlanIntoSessionAsync(_selectedOrder.CraftPlanId!);
            if (result == null)
            {
                Snackbar.Add("Linked Craft Architect plan could not be loaded.", Severity.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Warning))
            {
                Snackbar.Add(result.Warning, Severity.Warning);
            }
            else
            {
                Snackbar.Add("Linked craft plan loaded", Severity.Success);
            }

            NavigationManager.NavigateTo("./");
        }
        finally
        {
            _isOpeningSelectedOrderCraftPlan = false;
        }
    }

    private async Task OpenMarketAnalysisForProcurementRowAsync(TradeOrderProcurementRow row)
    {
        if (!await LoadSelectedOrderCraftPlanForNavigationAsync())
        {
            return;
        }

        AppState.SelectMarketAnalysisItem(row.ItemId);
        AppState.RequestMarketItemAutoExpand(row.ItemId);
        NavigationManager.NavigateTo("market");
    }

    private async Task OpenAcquisitionEvaluationForProcurementRowAsync(TradeOrderProcurementRow row)
    {
        if (!await LoadSelectedOrderCraftPlanForNavigationAsync())
        {
            return;
        }

        NavigationManager.NavigateTo($"acquisition?itemId={row.ItemId}");
    }

    private async Task<bool> LoadSelectedOrderCraftPlanForNavigationAsync()
    {
        if (_selectedOrder == null)
        {
            return false;
        }

        if (!HasLinkedCraftPlan(_selectedOrder))
        {
            Snackbar.Add("Create a linked craft plan before opening Craft Architect details.", Severity.Warning);
            return false;
        }

        if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
            "Opening Craft Architect details for this order",
            _selectedOrder.CraftPlanId))
        {
            return false;
        }

        var result = await PlanPersistence.LoadPlanIntoSessionAsync(_selectedOrder.CraftPlanId!);
        if (result == null)
        {
            Snackbar.Add("Linked Craft Architect plan could not be loaded.", Severity.Warning);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(result.Warning))
        {
            Snackbar.Add(result.Warning, Severity.Warning);
        }

        return true;
    }

    private async Task RepriceSelectedOrderAsync()
    {
        if (_selectedOrder == null || _companyProfile == null || _isRepricingSelectedOrder)
        {
            return;
        }

        if (TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status))
        {
            Snackbar.Add("Reopen archived orders before repricing.", Severity.Warning);
            return;
        }

        if (!HasLinkedCraftPlan(_selectedOrder))
        {
            Snackbar.Add("Create a linked craft plan before repricing.", Severity.Warning);
            return;
        }

        _isRepricingSelectedOrder = true;
        var orderId = _selectedOrder.Id;

        try
        {
            if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
                "Repricing this order",
                _selectedOrder.CraftPlanId))
            {
                return;
            }

            var result = await TradeOrderPricingWorkflow.RepriceAsync(
                _selectedOrder,
                new TradeOrderPricingWorkflowOptions(
                    GetOrderDataCenter(_selectedOrder),
                    _selectedOrder.SourceSnapshot.World ?? string.Empty,
                    ForceRefreshMarketData: true));
            if (!result.HasUpdatedOrder || result.UpdatedOrder == null)
            {
                Snackbar.Add(result.Message, ToSnackbarSeverity(result.MessageLevel));
                return;
            }

            var saved = await SaveOrderAndNotifyAsync(result.UpdatedOrder);
            if (!saved)
            {
                Snackbar.Add("Order pricing updated, but failed to save it to the order.", Severity.Error);
                return;
            }

            await LoadAsync();
            if (string.IsNullOrWhiteSpace(_loadError))
            {
                if (SelectOrderAfterReload(orderId, "Order pricing was saved, but the order could not be loaded."))
                {
                    _activeOpsTab = 0;
                }
            }

            Snackbar.Add(result.Message, ToSnackbarSeverity(result.MessageLevel));
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to reprice order: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isRepricingSelectedOrder = false;
        }
    }

    private async Task<bool> ConfirmActiveCraftPlanCanBeReplacedAsync(
        string actionLabel,
        string? targetPlanId)
    {
        if (!ShouldGuardActiveCraftPlanReplacement(targetPlanId))
        {
            return true;
        }

        var parameters = new DialogParameters
        {
            ["ActionLabel"] = actionLabel,
            ["CurrentPlanName"] = AppState.CurrentPlanName ?? AppState.CurrentPlan?.Name,
            ["HasNamedPlan"] = !string.IsNullOrWhiteSpace(AppState.CurrentPlanId)
        };
        var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small };
        var dialog = await DialogService.ShowAsync<TradeActivePlanSaveGuardDialog>(
            "Active Craft Plan",
            parameters,
            options);
        var result = await dialog.Result;
        if (result == null || result.Canceled)
        {
            return false;
        }

        return result.Data is TradeActivePlanSaveGuardChoice choice &&
            (choice == TradeActivePlanSaveGuardChoice.ContinueWithoutSaving ||
             await SaveActiveCraftPlanBeforeTradeActionAsync());
    }

    private bool ShouldGuardActiveCraftPlanReplacement(string? targetPlanId)
    {
        if (!AppState.HasPlanOrProjectItems)
        {
            return false;
        }

        var dirtyBuckets = AppState.GetDirtyPersistedBuckets();
        if (string.IsNullOrWhiteSpace(AppState.CurrentPlanId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(targetPlanId) &&
            string.Equals(AppState.CurrentPlanId, targetPlanId, StringComparison.Ordinal) &&
            dirtyBuckets == PersistedStateBucket.None)
        {
            return false;
        }

        return dirtyBuckets != PersistedStateBucket.None;
    }

    private async Task<bool> SaveActiveCraftPlanBeforeTradeActionAsync()
    {
        if (!AppState.HasProjectItems)
        {
            Snackbar.Add("There are no project items to save.", Severity.Warning);
            return false;
        }

        var planId = AppState.CurrentPlanId;
        var planName = AppState.CurrentPlanName ?? AppState.CurrentPlan?.Name;
        if (string.IsNullOrWhiteSpace(planId))
        {
            var dialog = await DialogService.ShowAsync<SavePlanDialog>("Save Plan");
            var result = await dialog.Result;
            if (result?.Data is not string newName || string.IsNullOrWhiteSpace(newName))
            {
                return false;
            }

            planId = Guid.NewGuid().ToString("D");
            planName = newName.Trim();
        }
        else if (string.IsNullOrWhiteSpace(planName))
        {
            planName = "Saved Plan";
        }

        var versions = AppState.CurrentVersions;
        var dirtyBuckets = AppState.GetDirtyPersistedBuckets();
        var saved = await PlanPersistence.SaveCurrentPlanAsync(planId, planName);
        if (!saved)
        {
            Snackbar.Add("Failed to save the active Craft plan.", Severity.Error);
            return false;
        }

        AppState.TrackCurrentPlanIdentity(planId, planName);
        if (dirtyBuckets != PersistedStateBucket.None)
        {
            AppState.MarkPersisted(dirtyBuckets, versions);
        }

        Snackbar.Add($"Saved '{planName}'", Severity.Success);
        return true;
    }

    private string GetOrderDataCenter(TradeOrder order)
    {
        return string.IsNullOrWhiteSpace(order.SourceSnapshot.DataCenter)
            ? AppState.SelectedDataCenter
            : order.SourceSnapshot.DataCenter;
    }

    private static IReadOnlyList<TradeOrderRootItemSnapshot> GetOrderRootItems(TradeOrder order)
    {
        return order.SourceSnapshot?.RootItems ?? Array.Empty<TradeOrderRootItemSnapshot>();
    }

}
