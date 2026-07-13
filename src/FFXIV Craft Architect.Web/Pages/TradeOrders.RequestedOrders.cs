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
    private async Task SearchRequestedOrderItemsAsync()
    {
        if (string.IsNullOrWhiteSpace(_requestedOrderSearchQuery) || _isSearchingRequestedOrderItems)
        {
            return;
        }

        _isSearchingRequestedOrderItems = true;
        _requestedOrderSearchResults = [];

        try
        {
            _requestedOrderSearchResults = (await GarlandService.SearchAsync(_requestedOrderSearchQuery))
                .Where(result => result.Id > 0 && !string.IsNullOrWhiteSpace(result.Object.Name))
                .Take(8)
                .ToList();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Item search failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isSearchingRequestedOrderItems = false;
        }
    }

    private Task OnRequestedOrderSearchKeyUp(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
        {
            return SearchRequestedOrderItemsAsync();
        }

        return Task.CompletedTask;
    }

    private void AddRequestedOrderOutput(GarlandSearchResult result)
    {
        var existing = _requestedOrderOutputs.FirstOrDefault(output => output.ItemId == result.Id);
        if (existing != null)
        {
            existing.Quantity = Math.Min(9999, existing.Quantity + 1);
        }
        else
        {
            _requestedOrderOutputs.Add(new RequestedOrderOutputEditor
            {
                ItemId = result.Id,
                Name = result.Object.Name,
                Quantity = 1
            });
        }

        _requestedOrderSearchQuery = string.Empty;
        _requestedOrderSearchResults = [];
        RefreshRequestedOrderSuggestedTitle(force: _usingSuggestedRequestedOrderTitle);
    }

    private Task OnRequestedOrderTitleChanged(string value)
    {
        _newRequestedOrderTitle = value;
        _usingSuggestedRequestedOrderTitle = false;
        return Task.CompletedTask;
    }

    private void UpdateRequestedOrderOutputQuantity(RequestedOrderOutputEditor output, int quantity)
    {
        output.Quantity = Math.Clamp(quantity, 1, 9999);
        RefreshRequestedOrderSuggestedTitle(force: _usingSuggestedRequestedOrderTitle);
    }

    private void UpdateRequestedOrderOutputHq(RequestedOrderOutputEditor output, bool mustBeHq)
    {
        output.MustBeHq = mustBeHq;
    }

    private void RemoveRequestedOrderOutput(RequestedOrderOutputEditor output)
    {
        _requestedOrderOutputs.Remove(output);
        RefreshRequestedOrderSuggestedTitle(force: _usingSuggestedRequestedOrderTitle);
    }

    private async Task SearchSelectedOrderOutputItemsAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedOrderOutputSearchQuery) || _isSearchingSelectedOrderOutputs)
        {
            return;
        }

        _isSearchingSelectedOrderOutputs = true;
        _selectedOrderOutputSearchResults = [];

        try
        {
            _selectedOrderOutputSearchResults = (await GarlandService.SearchAsync(_selectedOrderOutputSearchQuery))
                .Where(result => result.Id > 0 && !string.IsNullOrWhiteSpace(result.Object.Name))
                .Take(8)
                .ToList();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Item search failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isSearchingSelectedOrderOutputs = false;
        }
    }

    private Task OnSelectedOrderOutputSearchKeyUp(KeyboardEventArgs e)
    {
        return e.Key == "Enter"
            ? SearchSelectedOrderOutputItemsAsync()
            : Task.CompletedTask;
    }

    private void AddSelectedOrderOutput(GarlandSearchResult result)
    {
        var existing = _selectedOrderOutputEditors.FirstOrDefault(output => output.ItemId == result.Id && !output.MustBeHq);
        if (existing != null)
        {
            ReplaceSelectedOrderOutput(existing, existing with
            {
                Quantity = TradeRequestedOrderEditorMapper.ClampQuantity(existing.Quantity + 1)
            });
        }
        else
        {
            _selectedOrderOutputEditors.Add(new TradeRequestedOrderOutputEditorRow(
                result.Id,
                result.Object.Name,
                1,
                MustBeHq: false,
                EstimatedSaleValue: 0m));
        }

        _selectedOrderOutputSearchQuery = string.Empty;
        _selectedOrderOutputSearchResults = [];
    }

    private void UpdateSelectedOrderOutputQuantity(TradeRequestedOrderOutputEditorRow output, int quantity)
    {
        ReplaceSelectedOrderOutput(output, output with
        {
            Quantity = TradeRequestedOrderEditorMapper.ClampQuantity(quantity)
        });
    }

    private void UpdateSelectedOrderOutputHq(TradeRequestedOrderOutputEditorRow output, bool mustBeHq)
    {
        ReplaceSelectedOrderOutput(output, output with { MustBeHq = mustBeHq });
    }

    private void RemoveSelectedOrderOutput(TradeRequestedOrderOutputEditorRow output)
    {
        _selectedOrderOutputEditors.Remove(output);
    }

    private void ResetSelectedOrderOutputEdits()
    {
        if (_selectedOrder == null)
        {
            return;
        }

        _selectedOrderOutputEditors = TradeRequestedOrderEditorMapper.FromOrder(_selectedOrder);
        _selectedOrderOutputSearchQuery = string.Empty;
        _selectedOrderOutputSearchResults = [];
    }

    private void ReplaceSelectedOrderOutput(
        TradeRequestedOrderOutputEditorRow existing,
        TradeRequestedOrderOutputEditorRow replacement)
    {
        var index = _selectedOrderOutputEditors.IndexOf(existing);
        if (index >= 0)
        {
            _selectedOrderOutputEditors[index] = replacement;
        }
    }

    private void RefreshRequestedOrderSuggestedTitle(bool force = false)
    {
        if ((!force && !string.IsNullOrWhiteSpace(_newRequestedOrderTitle)) || _requestedOrderOutputs.Count == 0)
        {
            return;
        }

        _newRequestedOrderTitle = TradeRequestedOrderWorkflow.CreateSuggestedTitle(ToRequestedOrderOutputs());
        _usingSuggestedRequestedOrderTitle = true;
    }

    private async Task CreateRequestedOrderAsync()
    {
        if (_companyProfile == null || !CanCreateRequestedOrder || _isCreatingRequestedOrder)
        {
            return;
        }

        _isCreatingRequestedOrder = true;

        try
        {
            if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
                "Creating this Trade order",
                targetPlanId: null))
            {
                return;
            }

            var outputs = ToRequestedOrderOutputs();
            var draftResult = TradeOrderDraftFactory.CreateFromRequestedOutputs(new TradeRequestedOrderCreateRequest(
                _companyProfile.Id,
                _newRequestedOrderCrafterId,
                _newRequestedOrderTitle,
                outputs,
                AppState.SelectedDataCenter,
                World: null,
                _newRequestedOrderNotes,
                DateTime.UtcNow));
            if (!draftResult.CanCreate || draftResult.Order == null)
            {
                Snackbar.Add(draftResult.UnavailableReason ?? "Could not create Trade order.", Severity.Warning);
                return;
            }

            var pricingResult = await TradeOrderPricingWorkflow.RebuildAndPriceAsync(
                draftResult.Order,
                new TradeOrderPricingWorkflowOptions(
                    AppState.SelectedDataCenter,
                    draftResult.Order.SourceSnapshot.World ?? string.Empty,
                    ForceRefreshMarketData: false));
            if (!pricingResult.HasUpdatedOrder || pricingResult.UpdatedOrder == null)
            {
                Snackbar.Add(pricingResult.Message, ToSnackbarSeverity(pricingResult.MessageLevel));
                return;
            }

            var saved = await SaveOrderAndNotifyAsync(pricingResult.UpdatedOrder);
            if (!saved)
            {
                Snackbar.Add("Failed to save Trade order.", Severity.Error);
                return;
            }

            ResetNewOrderDraft();
            _showNewOrderPanel = false;
            await LoadAsync();
            if (!string.IsNullOrWhiteSpace(_loadError) ||
                !SelectOrderAfterReload(pricingResult.UpdatedOrder.Id, "Trade order was saved, but it could not be loaded."))
            {
                return;
            }

            Snackbar.Add("Trade order created and priced", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to create Trade order: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isCreatingRequestedOrder = false;
        }
    }

    private async Task CreatePendingOrderAsync()
    {
        if (_companyProfile == null || _pendingImport == null)
        {
            return;
        }

        var orderToSave = TradeOrderWorkflow.CopyOrder(_pendingImport);
        var previousCrafterId = orderToSave.AssignedCrafterId;
        orderToSave.Title = _newOrderTitle.Trim();
        orderToSave.AssignedCrafterId = _newOrderCrafterId;
        orderToSave.Status = _newOrderCrafterId.HasValue
            ? TradeOrderStatus.Assigned
            : TradeOrderStatus.ReadyToAssign;
        orderToSave.UpdatedAtUtc = DateTime.UtcNow;
        AddHistoryIfAssignmentChanged(orderToSave, previousCrafterId, _newOrderCrafterId);

        if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
            "Creating this Trade order",
            targetPlanId: orderToSave.CraftPlanId))
        {
            return;
        }

        var pricingResult = await TradeOrderPricingWorkflow.RebuildAndPriceAsync(
            orderToSave,
            new TradeOrderPricingWorkflowOptions(
                GetOrderDataCenter(orderToSave),
                orderToSave.SourceSnapshot.World ?? string.Empty,
                ForceRefreshMarketData: false));
        if (!pricingResult.HasUpdatedOrder || pricingResult.UpdatedOrder == null)
        {
            Snackbar.Add(pricingResult.Message, ToSnackbarSeverity(pricingResult.MessageLevel));
            return;
        }

        var saved = await SaveOrderAndNotifyAsync(pricingResult.UpdatedOrder);
        if (!saved)
        {
            Snackbar.Add("Failed to save Trade order.", Severity.Error);
            return;
        }

        _pendingImport = null;
        _newOrderTitle = string.Empty;
        _newOrderCrafterId = null;
        await LoadAsync();
        if (!string.IsNullOrWhiteSpace(_loadError) ||
            !SelectOrderAfterReload(pricingResult.UpdatedOrder.Id, "Trade order was saved, but it could not be loaded."))
        {
            return;
        }

        Snackbar.Add("Trade order created and priced", Severity.Success);
    }

}
