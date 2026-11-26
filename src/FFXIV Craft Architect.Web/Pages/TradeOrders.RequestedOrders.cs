using System.Text;
using System.Text.RegularExpressions;

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
    // Margins "Create commission" handoff: /trade/orders?newOrderItem={id}&newOrderName={name}.
    [SupplyParameterFromQuery(Name = "newOrderItem")]
    public int? NewOrderItem { get; set; }

    [SupplyParameterFromQuery(Name = "newOrderName")]
    public string? NewOrderName { get; set; }

    private int? _consumedNewOrderItem;

    private (int ItemId, string? Name)? TakeNewOrderSeed()
    {
        if (NewOrderItem is not { } itemId || itemId <= 0 || itemId == _consumedNewOrderItem)
        {
            return null;
        }

        _consumedNewOrderItem = itemId;
        return (itemId, NewOrderName);
    }

    private async Task ApplyNewOrderSeedAsync(int itemId, string? name)
    {
        if (NavigationManager.Uri.Contains("newOrderItem=", StringComparison.OrdinalIgnoreCase))
        {
            NavigationManager.NavigateTo("trade/orders", replace: true);
        }
        if (_companyProfile == null || !string.IsNullOrWhiteSpace(_loadError))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                name = (await GarlandService.GetItemAsync(itemId))?.Name;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                name = null;
            }
        }

        StartNewOrderWorkspace();
        if (!_requestedOrderOutputs.Any(output => output.ItemId == itemId))
        {
            _requestedOrderOutputs.Add(new RequestedOrderOutputEditor
            {
                ItemId = itemId,
                Name = string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name.Trim(),
                Quantity = 1
            });
        }
        SnapshotNewOrderScaleBase();
        RefreshRequestedOrderSuggestedTitle(force: _usingSuggestedRequestedOrderTitle ||
            string.IsNullOrWhiteSpace(_newRequestedOrderTitle));
    }

    private const int RequestedOrderSearchDebounceMilliseconds = 250;
    private const int RequestedOrderSearchMinimumLength = 2;

    // "Ancient Lumber x3996", "Ancient Lumber * 3996", "Ancient Lumber ×3996".
    private static readonly Regex RequestedOrderQuantitySuffix = new(
        @"^(?<name>.+?)\s*(?:\s[xX]|[×*])\s*(?<quantity>\d{1,4})\s*$",
        RegexOptions.CultureInvariant);

    private int _requestedOrderSearchGeneration;
    private int _requestedOrderAddQuantity = 1;
    private string? _requestedOrderSearchResultsQuery;

    private static (string Query, int? Quantity) ParseRequestedOrderSearch(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        var match = RequestedOrderQuantitySuffix.Match(trimmed);
        return match.Success &&
               int.TryParse(match.Groups["quantity"].Value, out var quantity) &&
               quantity > 0
            ? (match.Groups["name"].Value.Trim(), Math.Min(9999, quantity))
            : (trimmed, null);
    }

    private async Task OnRequestedOrderSearchChangedAsync(string? value)
    {
        _requestedOrderSearchQuery = value ?? string.Empty;
        var generation = ++_requestedOrderSearchGeneration;
        var (query, quantity) = ParseRequestedOrderSearch(_requestedOrderSearchQuery);
        if (quantity is { } typedQuantity)
        {
            _requestedOrderAddQuantity = typedQuantity;
        }
        if (query.Length < RequestedOrderSearchMinimumLength)
        {
            _requestedOrderSearchResults = [];
            _isSearchingRequestedOrderItems = false;
            return;
        }

        await Task.Delay(RequestedOrderSearchDebounceMilliseconds);
        if (generation != _requestedOrderSearchGeneration || _isDisposed)
        {
            return;
        }

        await RunRequestedOrderSearchAsync(query, generation);
    }

    private Task SearchRequestedOrderItemsAsync()
    {
        var (query, _) = ParseRequestedOrderSearch(_requestedOrderSearchQuery);
        return string.IsNullOrWhiteSpace(query)
            ? Task.CompletedTask
            : RunRequestedOrderSearchAsync(query, ++_requestedOrderSearchGeneration);
    }

    private async Task RunRequestedOrderSearchAsync(string query, int generation)
    {
        _isSearchingRequestedOrderItems = true;
        try
        {
            var results = (await GarlandService.SearchAsync(query))
                .Where(result => result.Id > 0 && !string.IsNullOrWhiteSpace(result.Object.Name))
                .Take(8)
                .ToList();
            if (generation == _requestedOrderSearchGeneration)
            {
                _requestedOrderSearchResults = results;
                _requestedOrderSearchResultsQuery = query;
            }
        }
        catch (Exception ex)
        {
            if (generation == _requestedOrderSearchGeneration)
            {
                Snackbar.Add($"Item search failed: {ex.Message}", Severity.Error);
            }
        }
        finally
        {
            if (generation == _requestedOrderSearchGeneration)
            {
                _isSearchingRequestedOrderItems = false;
            }
        }
    }

    private Task OnRequestedOrderSearchKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            ++_requestedOrderSearchGeneration;
            _requestedOrderSearchResults = [];
            _isSearchingRequestedOrderItems = false;
            return Task.CompletedTask;
        }

        if (e.Key != "Enter")
        {
            return Task.CompletedTask;
        }

        // Enter on a settled result list adds the top match; otherwise it searches now.
        var (query, _) = ParseRequestedOrderSearch(_requestedOrderSearchQuery);
        if (!_isSearchingRequestedOrderItems &&
            _requestedOrderSearchResults.Count > 0 &&
            string.Equals(_requestedOrderSearchResultsQuery, query, StringComparison.OrdinalIgnoreCase))
        {
            AddRequestedOrderOutput(_requestedOrderSearchResults[0]);
            return Task.CompletedTask;
        }

        return SearchRequestedOrderItemsAsync();
    }

    private void UpdateRequestedOrderAddQuantity(int quantity) =>
        _requestedOrderAddQuantity = Math.Clamp(quantity, 1, 9999);

    private void AddRequestedOrderOutput(GarlandSearchResult result)
    {
        var (_, typedQuantity) = ParseRequestedOrderSearch(_requestedOrderSearchQuery);
        var quantity = Math.Clamp(typedQuantity ?? _requestedOrderAddQuantity, 1, 9999);
        var existing = _requestedOrderOutputs.FirstOrDefault(output => output.ItemId == result.Id);
        if (existing != null)
        {
            existing.Quantity = Math.Min(9999, existing.Quantity + quantity);
        }
        else
        {
            _requestedOrderOutputs.Add(new RequestedOrderOutputEditor
            {
                ItemId = result.Id,
                Name = result.Object.Name,
                Quantity = quantity
            });
        }

        ++_requestedOrderSearchGeneration;
        _requestedOrderSearchQuery = string.Empty;
        _requestedOrderSearchResults = [];
        _isSearchingRequestedOrderItems = false;
        _requestedOrderAddQuantity = 1;
        SnapshotNewOrderScaleBase();
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
        SnapshotNewOrderScaleBase();
        RefreshRequestedOrderSuggestedTitle(force: _usingSuggestedRequestedOrderTitle);
    }

    private void UpdateRequestedOrderOutputHq(RequestedOrderOutputEditor output, bool mustBeHq)
    {
        output.MustBeHq = mustBeHq;
        UpdateNewOrderScaleBaseHq(_requestedOrderOutputs.IndexOf(output), mustBeHq);
    }

    private void RemoveRequestedOrderOutput(RequestedOrderOutputEditor output)
    {
        _requestedOrderOutputs.Remove(output);
        SnapshotNewOrderScaleBase();
        RefreshRequestedOrderSuggestedTitle(force: _usingSuggestedRequestedOrderTitle);
    }

    private int _selectedOrderOutputSearchGeneration;
    private int _selectedOrderOutputAddQuantity = 1;
    private string? _selectedOrderOutputSearchResultsQuery;

    private async Task OnSelectedOrderOutputSearchChangedAsync(string? value)
    {
        _selectedOrderOutputSearchQuery = value ?? string.Empty;
        var generation = ++_selectedOrderOutputSearchGeneration;
        var (query, quantity) = ParseRequestedOrderSearch(_selectedOrderOutputSearchQuery);
        if (quantity is { } typedQuantity)
        {
            _selectedOrderOutputAddQuantity = typedQuantity;
        }
        if (query.Length < RequestedOrderSearchMinimumLength)
        {
            _selectedOrderOutputSearchResults = [];
            _isSearchingSelectedOrderOutputs = false;
            return;
        }

        await Task.Delay(RequestedOrderSearchDebounceMilliseconds);
        if (generation != _selectedOrderOutputSearchGeneration || _isDisposed)
        {
            return;
        }

        await RunSelectedOrderOutputSearchAsync(query, generation);
    }

    private Task SearchSelectedOrderOutputItemsAsync()
    {
        var (query, _) = ParseRequestedOrderSearch(_selectedOrderOutputSearchQuery);
        return string.IsNullOrWhiteSpace(query)
            ? Task.CompletedTask
            : RunSelectedOrderOutputSearchAsync(query, ++_selectedOrderOutputSearchGeneration);
    }

    private async Task RunSelectedOrderOutputSearchAsync(string query, int generation)
    {
        _isSearchingSelectedOrderOutputs = true;
        try
        {
            var results = (await GarlandService.SearchAsync(query))
                .Where(result => result.Id > 0 && !string.IsNullOrWhiteSpace(result.Object.Name))
                .Take(8)
                .ToList();
            if (generation == _selectedOrderOutputSearchGeneration)
            {
                _selectedOrderOutputSearchResults = results;
                _selectedOrderOutputSearchResultsQuery = query;
            }
        }
        catch (Exception ex)
        {
            if (generation == _selectedOrderOutputSearchGeneration)
            {
                Snackbar.Add($"Item search failed: {ex.Message}", Severity.Error);
            }
        }
        finally
        {
            if (generation == _selectedOrderOutputSearchGeneration)
            {
                _isSearchingSelectedOrderOutputs = false;
            }
        }
    }

    private Task OnSelectedOrderOutputSearchKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            ++_selectedOrderOutputSearchGeneration;
            _selectedOrderOutputSearchResults = [];
            _isSearchingSelectedOrderOutputs = false;
            return Task.CompletedTask;
        }

        if (e.Key != "Enter")
        {
            return Task.CompletedTask;
        }

        // Enter on a settled result list adds the top match; otherwise it searches now.
        var (query, _) = ParseRequestedOrderSearch(_selectedOrderOutputSearchQuery);
        if (!_isSearchingSelectedOrderOutputs &&
            _selectedOrderOutputSearchResults.Count > 0 &&
            string.Equals(_selectedOrderOutputSearchResultsQuery, query, StringComparison.OrdinalIgnoreCase))
        {
            AddSelectedOrderOutput(_selectedOrderOutputSearchResults[0]);
            return Task.CompletedTask;
        }

        return SearchSelectedOrderOutputItemsAsync();
    }

    private void UpdateSelectedOrderOutputAddQuantity(int quantity) =>
        _selectedOrderOutputAddQuantity = TradeRequestedOrderEditorMapper.ClampQuantity(quantity);

    private void AddSelectedOrderOutput(GarlandSearchResult result)
    {
        var (_, typedQuantity) = ParseRequestedOrderSearch(_selectedOrderOutputSearchQuery);
        var quantity = TradeRequestedOrderEditorMapper.ClampQuantity(typedQuantity ?? _selectedOrderOutputAddQuantity);
        var previousOutputs = TradeRequestedOrderEditorMapper.ToOutputs(_selectedOrderOutputEditors);
        var existingIndex = _selectedOrderOutputEditors.FindIndex(output => output.ItemId == result.Id && !output.MustBeHq);
        if (existingIndex >= 0)
        {
            var existing = _selectedOrderOutputEditors[existingIndex];
            _selectedOrderOutputEditors[existingIndex] = existing with
            {
                Quantity = TradeRequestedOrderEditorMapper.ClampQuantity(existing.Quantity + quantity)
            };
        }
        else
        {
            _selectedOrderOutputEditors.Add(new TradeRequestedOrderOutputEditorRow(
                result.Id,
                result.Object.Name,
                quantity,
                MustBeHq: false,
                EstimatedSaleValue: 0m));
        }

        ++_selectedOrderOutputSearchGeneration;
        _selectedOrderOutputSearchQuery = string.Empty;
        _selectedOrderOutputSearchResults = [];
        _isSearchingSelectedOrderOutputs = false;
        _selectedOrderOutputAddQuantity = 1;
        RefreshSelectedOrderSuggestedTitle(previousOutputs);
    }

    private void UpdateSelectedOrderOutputQuantityAt(int index, int quantity)
    {
        if (index < 0 || index >= _selectedOrderOutputEditors.Count)
        {
            return;
        }

        var previousOutputs = TradeRequestedOrderEditorMapper.ToOutputs(_selectedOrderOutputEditors);
        _selectedOrderOutputEditors[index] = _selectedOrderOutputEditors[index] with
        {
            Quantity = TradeRequestedOrderEditorMapper.ClampQuantity(quantity)
        };
        RefreshSelectedOrderSuggestedTitle(previousOutputs);
    }

    private void UpdateSelectedOrderOutputHqAt(int index, bool mustBeHq)
    {
        if (index < 0 || index >= _selectedOrderOutputEditors.Count)
        {
            return;
        }

        _selectedOrderOutputEditors[index] = _selectedOrderOutputEditors[index] with { MustBeHq = mustBeHq };
    }

    // Removes by row position, not record value: rows are value-equal records,
    // so List.Remove(row) can hit a different identical row.
    private void RemoveSelectedOrderOutputAt(int index)
    {
        if (index < 0 || index >= _selectedOrderOutputEditors.Count)
        {
            return;
        }

        var previousOutputs = TradeRequestedOrderEditorMapper.ToOutputs(_selectedOrderOutputEditors);
        _selectedOrderOutputEditors.RemoveAt(index);
        RefreshSelectedOrderSuggestedTitle(previousOutputs);
    }

    /// <summary>
    /// Keeps the selected order title in step with its outputs while the title
    /// still equals the suggestion for the previous outputs (or for the saved
    /// order's outputs, which covers remove-last-then-add). An empty output
    /// list leaves the title alone.
    /// </summary>
    private void RefreshSelectedOrderSuggestedTitle(IReadOnlyList<TradeRequestedOrderOutput> previousOutputs)
    {
        if (_selectedOrder == null || HasCanonicalCommission || _selectedOrderOutputEditors.Count == 0)
        {
            return;
        }

        var currentTitle = _detailTitle.Trim();
        var savedOutputs = TradeRequestedOrderEditorMapper.ToOutputs(
            TradeRequestedOrderEditorMapper.FromOrder(_selectedOrder));
        var titleWasSuggested =
            string.IsNullOrWhiteSpace(currentTitle) ||
            string.Equals(currentTitle, TradeRequestedOrderWorkflow.CreateSuggestedTitle(previousOutputs), StringComparison.Ordinal) ||
            string.Equals(currentTitle, TradeRequestedOrderWorkflow.CreateSuggestedTitle(savedOutputs), StringComparison.Ordinal);
        if (!titleWasSuggested)
        {
            return;
        }

        var suggested = TradeRequestedOrderWorkflow.CreateSuggestedTitle(
            TradeRequestedOrderEditorMapper.ToOutputs(_selectedOrderOutputEditors));
        if (!string.IsNullOrWhiteSpace(suggested))
        {
            _detailTitle = suggested;
        }
    }

    private void ResetSelectedOrderOutputEdits()
    {
        if (_selectedOrder == null)
        {
            return;
        }

        var previousOutputs = TradeRequestedOrderEditorMapper.ToOutputs(_selectedOrderOutputEditors);
        _selectedOrderOutputEditors = TradeRequestedOrderEditorMapper.FromOrder(_selectedOrder);
        ++_selectedOrderOutputSearchGeneration;
        _selectedOrderOutputSearchQuery = string.Empty;
        _selectedOrderOutputSearchResults = [];
        _isSearchingSelectedOrderOutputs = false;
        _selectedOrderOutputAddQuantity = 1;
        RefreshSelectedOrderSuggestedTitle(previousOutputs);
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

    private async Task CreatePendingOrderAsync()
    {
        var companyProfile = _companyProfile;
        if (companyProfile == null || _pendingImport == null)
        {
            return;
        }

        var orderToSave = TradeOrderWorkflow.CopyOrder(_pendingImport);
        orderToSave.Title = _newOrderTitle.Trim();
        orderToSave.AssignedCrafterId = null;
        orderToSave.Status = TradeOrderStatus.ReadyToAssign;
        orderToSave.UpdatedAtUtc = DateTime.UtcNow;

        if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
            "Creating this Trade order",
            targetPlanId: orderToSave.CraftPlanId))
        {
            return;
        }

        // Company defaults apply to the plan's materials, never to its requested outputs.
        var companyDefaults = GetCompanyDefaultSupplyChoicesFor(
            (orderToSave.SourceSnapshot?.RootItems ?? []).Select(item => item.ItemId));
        var pricingResult = await TradeOrderPricingWorkflow.RebuildAndPriceAsync(
            orderToSave,
            new TradeOrderPricingWorkflowOptions(
                ForceRefreshMarketData: false,
                MaterialPricingPolicy: companyProfile.MaterialPricingPolicy,
                ReuseMatchingActivePlan: true,
                InitialSupplyChoices: companyDefaults.Count > 0 ? companyDefaults : null));

        // Only defaults that matched a material in this plan become the draft's responsibilities.
        var planMaterials = (pricingResult.UpdatedOrder?.SourceSnapshot?.Materials ?? [])
            .Select(material => (material.ItemId, material.RequiresHq))
            .ToHashSet();
        var responsibilities = TradeCommissionTemplateMapper.ToResponsibilityLines(
            companyDefaults
                .Where(choice => planMaterials.Contains((choice.ItemId, choice.RequiresHq)))
                .ToArray());
        if (await SaveSeededOrderAsync(pricingResult, responsibilities, companyProfile) is not { } created)
        {
            return;
        }

        _pendingImport = null;
        _newOrderTitle = string.Empty;
        if (!await SelectCreatedOrderAsync(created.Id))
        {
            return;
        }

        Snackbar.Add("Trade order created and priced", Severity.Success);
    }

    /// <summary>
    /// Adds a just-saved order to the board in place and selects it, matching the
    /// in-place pattern of <see cref="SaveSelectedOrderAsync"/>. A full page reload
    /// would repeat the hosted company/roster round trips and every local store read
    /// only to pick up this one new record.
    /// </summary>
    private async Task<bool> SelectCreatedOrderAsync(Guid orderId)
    {
        const string missingMessage = "Trade order was saved, but it could not be loaded.";
        var saved = await TradeOperationsPersistence.LoadOrderAsync(orderId);
        if (saved == null)
        {
            await LoadAsync();
            return string.IsNullOrWhiteSpace(_loadError) &&
                SelectOrderAfterReload(orderId, missingMessage);
        }

        var localIndex = _orders.FindIndex(order => order.Id == orderId);
        if (localIndex >= 0)
        {
            _orders[localIndex] = saved;
        }
        else
        {
            _orders.Add(saved);
        }

        return SelectOrderAfterReload(orderId, missingMessage);
    }

}
