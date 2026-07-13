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
    private TradeCompanyProfile? _companyProfile;
    private List<TradeCrafterProfile> _crafters = [];
    private List<TradeOrder> _orders = [];
    private List<TradePayrollWorkflowDraft> _payrollDrafts = [];
    private TradeOrder? _pendingImport;
    private TradeOrder? _selectedOrder;
    private bool _showNewOrderPanel;
    private string _newOrderTitle = string.Empty;
    private Guid? _newOrderCrafterId;
    private string _newRequestedOrderTitle = string.Empty;
    private bool _usingSuggestedRequestedOrderTitle;
    private Guid? _newRequestedOrderCrafterId;
    private string? _newRequestedOrderNotes;
    private string _requestedOrderSearchQuery = string.Empty;
    private string _orderSearchText = string.Empty;
    private bool _isSearchingRequestedOrderItems;
    private bool _isCreatingRequestedOrder;
    private bool _isOpeningSelectedOrderCraftPlan;
    private bool _isRepricingSelectedOrder;
    private bool _isSavingSelectedOrderCraftPlan;
    private bool _isRefreshingLiveProcurement;
    private int _activeOpsTab;
    private WebTableSortState<TradeOrderProcurementColumn> _procurementSortState =
        WebTableSortState<TradeOrderProcurementColumn>.Unsorted;
    private List<GarlandSearchResult> _requestedOrderSearchResults = [];
    private List<RequestedOrderOutputEditor> _requestedOrderOutputs = [];
    private List<TradeRequestedOrderOutputEditorRow> _selectedOrderOutputEditors = [];
    private string _selectedOrderOutputSearchQuery = string.Empty;
    private List<GarlandSearchResult> _selectedOrderOutputSearchResults = [];
    private bool _isSearchingSelectedOrderOutputs;
    private bool _isSavingSelectedOrderOutputs;
    private string _detailTitle = string.Empty;
    private Guid? _detailCrafterId;
    private TradeOrderStatus _detailStatus;
    private string? _detailNotes;
    private string _manualNote = string.Empty;
    private string? _loadError;
    private Guid? _pendingNavigationOrderId;
    private bool _isArchiveCollapsed = true;
    private HashSet<TradeOrderStatus> _collapsedStatuses = [];
    private AcquisitionEvaluationSnapshot? _liveProcurementSnapshot;
    private LiveProcurementKey? _liveProcurementKey;
    private int _liveProcurementRefreshRequestId;

    private bool CanCreateRequestedOrder =>
        !string.IsNullOrWhiteSpace(_newRequestedOrderTitle) &&
        _requestedOrderOutputs.Any(output => output.Quantity > 0);

    private bool CanEditSelectedOrderOutputs =>
        _selectedOrder != null &&
        TradeOrderWorkflow.CanEditRequestedOutputs(_selectedOrder);

    private bool HasSelectedOrderOutputChanges =>
        _selectedOrder != null &&
        TradeRequestedOrderEditorMapper.HasChanges(_selectedOrder, _selectedOrderOutputEditors);

    private bool CanSaveSelectedOrderOutputs =>
        CanEditSelectedOrderOutputs &&
        HasSelectedOrderOutputChanges &&
        _selectedOrderOutputEditors.Count > 0 &&
        !_isSavingSelectedOrderOutputs;

    private IReadOnlyList<OrderStatusGroup> ActiveOrderGroups => TradeOrderStatusWorkflow.ActiveStatuses
        .Where(status => status != TradeOrderStatus.Draft)
        .Select(status => new OrderStatusGroup(status, GetOrdersForStatus(status)))
        .Where(group => group.Orders.Count > 0)
        .ToArray();

    private IReadOnlyList<TradeOrder> ArchivedOrders => _orders
        .Where(order => TradeOrderStatusWorkflow.IsArchived(order.Status))
        .OrderByDescending(order => order.CommissionedAtUtc)
        .ToArray();

    private IReadOnlyList<OrderStatusGroup> FilteredActiveOrderGroups => TradeOrderStatusWorkflow.ActiveStatuses
        .Where(status => status != TradeOrderStatus.Draft)
        .Select(status => new OrderStatusGroup(status, GetOrdersForStatus(status).Where(OrderMatchesSearch).ToArray()))
        .Where(group => group.Orders.Count > 0)
        .ToArray();

    private IReadOnlyList<TradeOrder> FilteredArchivedOrders => ArchivedOrders
        .Where(OrderMatchesSearch)
        .ToArray();

    protected override async Task OnInitializedAsync()
    {
        _pendingNavigationOrderId = TryGetOrderIdFromNavigation() ?? AppState.SelectedTradeOrderId;
        await LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await EnsureLiveProcurementSnapshotAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _loadError = null;
            _companyProfile = await TradeOperationsPersistence.GetOrCreateActiveCompanyProfileAsync();
            _crafters = (await TradeOperationsPersistence.LoadCraftersAsync(_companyProfile.Id)).ToList();
            _orders = (await TradeOperationsPersistence.LoadOrdersAsync(_companyProfile.Id)).ToList();
            _payrollDrafts = (await TradePayrollPersistence.LoadDraftsAsync(_companyProfile.Id)).ToList();
            SelectPendingNavigationOrder();
        }
        catch (Exception ex)
        {
            _companyProfile = null;
            _crafters = [];
            _orders = [];
            _payrollDrafts = [];
            _selectedOrder = null;
            _loadError = ex.Message;
            Snackbar.Add("Trade operations storage is unavailable.", Severity.Error);
        }
    }

    private async Task<bool> SaveOrderAndNotifyAsync(TradeOrder order)
    {
        var saved = await TradeOperationsPersistence.SaveOrderAsync(order);
        if (saved)
        {
            AppState.NotifyTradeOperationsDataChanged();
        }

        return saved;
    }

    private void PrepareOrderImport()
    {
        if (_companyProfile == null)
        {
            return;
        }

        var result = TradeOrderDraftFactory.CreateFromCurrentPlan(new TradeOrderCreateRequest(
            AppState,
            _companyProfile.Id,
            _newOrderCrafterId,
            null,
            DateTime.UtcNow));
        if (!result.CanCreate || result.Order == null)
        {
            Snackbar.Add(result.UnavailableReason ?? "Could not create order from the active plan.", Severity.Warning);
            return;
        }

        _pendingImport = result.Order;
        _newOrderTitle = result.Order.Title;
        _selectedOrder = null;
        _showNewOrderPanel = false;
        AppState.SelectTradeOrder(null);
        ClearSelectedOrderNavigation();
    }

    private void ToggleNewOrderPanel()
    {
        StartNewOrderWorkspace();
    }

    private void StartNewOrderWorkspace()
    {
        _pendingImport = null;
        _selectedOrder = null;
        _showNewOrderPanel = true;
        _activeOpsTab = 0;
        AppState.SelectTradeOrder(null);
        ClearSelectedOrderNavigation();
        if (string.IsNullOrWhiteSpace(_newRequestedOrderTitle))
        {
            RefreshRequestedOrderSuggestedTitle(force: true);
        }
    }

    private void ResetNewOrderDraft()
    {
        _newRequestedOrderTitle = string.Empty;
        _usingSuggestedRequestedOrderTitle = false;
        _newRequestedOrderCrafterId = null;
        _newRequestedOrderNotes = null;
        _requestedOrderSearchQuery = string.Empty;
        _requestedOrderSearchResults = [];
        _requestedOrderOutputs = [];
    }

}
