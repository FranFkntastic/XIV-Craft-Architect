using System.Text;

using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Dialogs;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using FFXIV_Craft_Architect.Web.Shared;
using FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private const string OpsPaneWidthSettingKey = "ui.trade_orders_ops_pane_width";
    private const string PlanPaneExpandedSettingKey = "ui.trade_orders_plan_pane_expanded";
    private const int DefaultOpsPaneWidth = 820;
    private const int MinimumOpsPaneWidth = 720;
    private const int MaximumOpsPaneWidth = 860;

    private TradeCompanyProfile? _companyProfile;
    private List<TradeCrafterProfile> _crafters = [];
    private List<TradeOrder> _orders = [];
    private List<TradeOrderArchiveSummaryRecord> _archiveSummaryRecords = [];
    private readonly Dictionary<Guid, long> _orderHostedRevisions = [];
    private readonly HashSet<Guid> _fetchingArchiveOrderIds = [];
    private List<TradePayrollWorkflowDraft> _payrollDrafts = [];
    private TradeOrder? _pendingImport;
    private TradeOrder? _selectedOrder;
    private HostedOrderProjectionSnapshot? _selectedLocalHostedCollision;
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
    private bool _isDeletingSelectedOrder;
    private bool _isRetryingSelectedDeviceOnlyOrderSync;
    private bool _isDiscardingSelectedDraft;
    private bool _isRefreshingLiveProcurement;
    private bool _isLoadingSelectedOrderSupplyPlan;
    private bool _selectedOrderPlanRestoreRetryRequested;
    private string? _selectedOrderPlanRestoreError;
    private long _selectedOrderPlanRestoreGeneration;
    private CancellationTokenSource? _selectedOrderPlanRestoreCancellation;
    private int _activeOpsTab;
    private int _opsPaneWidth = DefaultOpsPaneWidth;
    private bool _isPlanPaneExpanded;
    private TradeOrderProcurementFilter _procurementFilter = TradeOrderProcurementFilter.All;
    private WebTableSortState<TradeOrderProcurementColumn> _procurementSortState =
        WebTableSortState<TradeOrderProcurementColumn>.Unsorted;
    private List<GarlandSearchResult> _requestedOrderSearchResults = [];
    private List<RequestedOrderOutputEditor> _requestedOrderOutputs = [];
    private List<TradeRequestedOrderOutputEditorRow> _selectedOrderOutputEditors = [];
    private string _selectedOrderOutputSearchQuery = string.Empty;
    private List<GarlandSearchResult> _selectedOrderOutputSearchResults = [];
    private bool _isSearchingSelectedOrderOutputs;
    private bool _isSavingSelectedOrderOutputs;
    private bool _isChangingProcurementSource;
    private string _detailTitle = string.Empty;
    private Guid? _detailCrafterId;
    private TradeOrderStatus _detailStatus;
    private string? _detailNotes;
    private CompanyCommissionPaymentSchedule _selectedOrderPaymentSchedule =
        CompanyCommissionPaymentSchedule.Advance;
    private string _selectedOrderCustomPaymentTerms = string.Empty;
    private bool _selectedOrderPaymentTermsDirty;
    private string _manualNote = string.Empty;
    private string? _loadError;
    private Guid? _pendingNavigationOrderId;
    private bool _isArchiveCollapsed = true;
    private bool _isDeviceOnlyCollapsed = true;
    private HashSet<string> _collapsedAttentionGroups = new(StringComparer.Ordinal);
    private WorkerTradeProjection? _liveProcurementSnapshot;
    private LiveProcurementKey? _liveProcurementKey;
    private int _liveProcurementRefreshRequestId;
    private ElementReference _boardElement;
    private ElementReference _opsSplitterElement;
    private IJSObjectReference? _tradeOrdersLayoutModule;
    private IJSObjectReference? _tradeOrdersLayoutRegistration;
    private DotNetObjectReference<TradeOrders>? _tradeOrdersReference;
    private bool _isDisposed;

    private bool IsPlanMutationTransactionRunning =>
        _isSavingSelectedOrderOutputs ||
        _isSavingSelectedOrderCraftPlan ||
        _isRepricingSelectedOrder ||
        _isChangingProcurementSource ||
        _isCommissionCommandRunning;

    private static readonly IReadOnlyList<CompactSelectOption> MaterialResponsibilityOptions =
    [
        new(nameof(CommissionMaterialResponsibility.Crafter), "Crafter"),
        new(nameof(CommissionMaterialResponsibility.Provided), "Provided")
    ];
    private static readonly IReadOnlyList<CompactSelectOption> PaymentScheduleOptions =
    [
        new(nameof(CompanyCommissionPaymentSchedule.Advance), "Advance payment"),
        new(nameof(CompanyCommissionPaymentSchedule.OnDelivery), "Payment on delivery"),
        new(nameof(CompanyCommissionPaymentSchedule.Custom), "Custom timing")
    ];

    private string NewOrderCrafterValue
    {
        get => _newOrderCrafterId?.ToString() ?? string.Empty;
        set => _newOrderCrafterId = ParseNullableGuid(value);
    }

    private string NewRequestedOrderCrafterValue
    {
        get => _newRequestedOrderCrafterId?.ToString() ?? string.Empty;
        set => _newRequestedOrderCrafterId = ParseNullableGuid(value);
    }

    private string DetailCrafterValue
    {
        get => _detailCrafterId?.ToString() ?? string.Empty;
        set => _detailCrafterId = ParseNullableGuid(value);
    }

    private string DetailStatusValue
    {
        get => _detailStatus.ToString();
        set
        {
            if (Enum.TryParse<TradeOrderStatus>(value, out var status))
            {
                _detailStatus = status;
            }
        }
    }

    private IReadOnlyList<CompactSelectOption> GetCrafterOptions() =>
    [
        new(string.Empty, "Unassigned"),
        .. _crafters.Select(crafter => new CompactSelectOption(crafter.Id.ToString(), crafter.DisplayName))
    ];

    private IReadOnlyList<CompactSelectOption> GetActiveStatusOptions() =>
        TradeOrderStatusWorkflow.ActiveStatuses
            .Where(status => status != TradeOrderStatus.Draft)
            .Select(status => new CompactSelectOption(status.ToString(), FormatStatus(status)))
            .ToArray();

    private static Guid? ParseNullableGuid(string value) =>
        Guid.TryParse(value, out var parsed) ? parsed : null;

    private Task SetSelectedOrderPaymentScheduleValueAsync(string value) =>
        Enum.TryParse<CompanyCommissionPaymentSchedule>(value, out var schedule)
            ? SetSelectedOrderPaymentScheduleAsync(schedule)
            : Task.CompletedTask;

    private async Task SetOrderMaterialResponsibilityValueAsync(
        TradeCommissionPaymentMaterial material,
        string value)
    {
        if (Enum.TryParse<CommissionMaterialResponsibility>(value, out var responsibility))
        {
            await SetOrderMaterialResponsibilityAsync(material, responsibility);
        }
    }

    private bool CanCreateRequestedOrder =>
        !string.IsNullOrWhiteSpace(_newRequestedOrderTitle) &&
        _requestedOrderOutputs.Any(output => output.Quantity > 0);

    private bool CanEditSelectedOrderOutputs =>
        _selectedOrder != null &&
        (_selectedOrder.CompanyCommission == null || CanEditCanonicalDraft) &&
        TradeOrderWorkflow.CanEditRequestedOutputs(_selectedOrder);

    private bool HasSelectedOrderOutputChanges =>
        _selectedOrder != null &&
        TradeRequestedOrderEditorMapper.HasChanges(_selectedOrder, _selectedOrderOutputEditors);

    private bool HasSelectedLocalDraftEditorChanges =>
        _selectedOrder is { CompanyCommission: null } &&
        (HasSelectedOrderOutputChanges ||
         HasSelectedOrderDetailChanges() ||
         _selectedOrderPaymentTermsDirty);

    private bool CanSaveSelectedOrderOutputs =>
        CanEditSelectedOrderOutputs &&
        !HasSelectedLocalHostedCollision &&
        HasSelectedOrderOutputChanges &&
        _selectedOrderOutputEditors.Count > 0 &&
        !_isSavingSelectedOrderOutputs;

    private string TradeOrdersBoardStyle =>
        $"--trade-orders-ops-width: {_opsPaneWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}px";

    private string TradeOrdersBoardClass =>
        $"trade-orders-board{(_isPlanPaneExpanded && _activeOpsTab == PlanTabIndex ? " is-plan-expanded" : string.Empty)}";

    private IReadOnlyList<TradeOrder> VisibleOrders => ComposeVisibleOrders();

    private IReadOnlyList<OrderAttentionGroup> ActiveOrderGroups =>
        BuildAttentionGroups(VisibleOrders.Where(order =>
            !IsOrderArchivedForAttention(order) &&
            !IsSupersededByArchiveSummary(order)));

    private IReadOnlyList<ArchivedOrderRow> ArchivedOrders => ComposeArchivedOrderRows();

    private IReadOnlyList<OrderAttentionGroup> FilteredActiveOrderGroups =>
        BuildAttentionGroups(VisibleOrders
            .Where(order =>
                !IsOrderArchivedForAttention(order) &&
                !IsSupersededByArchiveSummary(order))
            .Where(OrderMatchesSearch));

    private IReadOnlyList<ArchivedOrderRow> FilteredArchivedOrders => ArchivedOrders
        .Where(ArchiveOrderMatchesSearch)
        .ToArray();

    private IReadOnlyList<TradeOrder> DeviceOnlyOrders
    {
        get
        {
            if (!OrderRestoreState.ShowsCompleteProjection || _companyProfile == null)
            {
                return Array.Empty<TradeOrder>();
            }

            return TradeOrderWorkspaceCompositionPolicy.GetDeviceOnlyOrders(
                _orders,
                HostedOrders.GetAll(_companyProfile.Id));
        }
    }

    private IReadOnlyList<TradeOrder> FilteredDeviceOnlyOrders => DeviceOnlyOrders
        .Where(OrderMatchesSearch)
        .ToArray();

    private bool IsDeviceOnlyOrder(TradeOrder order) =>
        DeviceOnlyOrders.Any(candidate => candidate.Id == order.Id);

    private bool IsSelectedDeviceOnlyOrder =>
        _selectedOrder is { CompanyCommission: null } selected &&
        IsDeviceOnlyOrder(selected);

    private TradeOrderLifecycleAction SelectedLifecycleAction =>
        _selectedOrder == null
            ? TradeOrderLifecycleAction.None
            : TradeOrderWorkflow.GetLifecycleAction(_selectedOrder);

    private bool HasSelectedLifecycleAction =>
        SelectedLifecycleAction != TradeOrderLifecycleAction.None;

    private bool CanDiscardSelectedDraft =>
        SelectedLifecycleAction == TradeOrderLifecycleAction.DiscardDraft;

    private string SelectedLifecycleActionLabel =>
        SelectedLifecycleAction switch
        {
            TradeOrderLifecycleAction.DiscardDraft =>
                _isDiscardingSelectedDraft ? "Discarding..." : "Discard Draft",
            TradeOrderLifecycleAction.CancelCommission =>
                IsSelectedCanonicalOwnerMissing ? "Remove Stale Order" : "Cancel Commission",
            _ => string.Empty
        };

    private bool HasSelectedLocalHostedCollision =>
        _selectedOrder is { CompanyCommission: null } selected &&
        _selectedLocalHostedCollision is { Deleted: false, Order: not null } collision &&
        collision.OrderId == selected.Id;

    protected override async Task OnInitializedAsync()
    {
        HostedOrders.Changed += OnHostedOrderProjectionChanged;
        HostedOrders.Reset += OnHostedOrderProjectionsReset;
        HostedOrders.RestoreStateChanged += OnHostedOrderRestoreStateChanged;
        ArchiveSummaries.Changed += OnArchiveSummariesChanged;
        ProfileSync.StatusChanged += OnProfileSyncStatusChanged;
        WorkerProjections.Changed += OnWorkerProjectionChangedForPlanRestoration;
        _pendingNavigationOrderId = TryGetOrderIdFromNavigation() ?? AppState.SelectedTradeOrderId;
        await LoadAsync();
        try
        {
            _opsPaneWidth = Math.Clamp(
                await WebSettings.GetAsync(OpsPaneWidthSettingKey, DefaultOpsPaneWidth),
                MinimumOpsPaneWidth,
                MaximumOpsPaneWidth);
            _isPlanPaneExpanded = await WebSettings.GetAsync(
                PlanPaneExpandedSettingKey,
                false);
        }
        catch
        {
            _opsPaneWidth = DefaultOpsPaneWidth;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await EnsureLiveProcurementSnapshotAsync();
        if (_tradeOrdersLayoutRegistration == null &&
            string.IsNullOrWhiteSpace(_loadError))
        {
            await RegisterTradeOrdersLayoutAsync();
        }
    }

    private async Task RegisterTradeOrdersLayoutAsync()
    {
        _tradeOrdersLayoutModule = await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            "./tradeOrdersLayout.js");
        _tradeOrdersReference = DotNetObjectReference.Create(this);
        _tradeOrdersLayoutRegistration =
            await _tradeOrdersLayoutModule.InvokeAsync<IJSObjectReference>(
                "registerTradeOrdersLayout",
                _boardElement,
                _opsSplitterElement,
                _tradeOrdersReference,
                _opsPaneWidth,
                MinimumOpsPaneWidth,
                MaximumOpsPaneWidth);
    }

    [JSInvokable]
    public async Task SaveTradeOrdersOpsPaneWidthAsync(double width)
    {
        var nextWidth = Math.Clamp(
            (int)Math.Round(width),
            MinimumOpsPaneWidth,
            MaximumOpsPaneWidth);
        _opsPaneWidth = nextWidth;
        await WebSettings.SetAsync(OpsPaneWidthSettingKey, nextWidth);
        await InvokeAsync(StateHasChanged);
    }

    private async Task TogglePlanPaneExpandedAsync()
    {
        _isPlanPaneExpanded = !_isPlanPaneExpanded;
        await WebSettings.SetAsync(PlanPaneExpandedSettingKey, _isPlanPaneExpanded);
    }

    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        InvalidateSelectedOrderPlanRestoration();
        HostedOrders.Changed -= OnHostedOrderProjectionChanged;
        HostedOrders.Reset -= OnHostedOrderProjectionsReset;
        HostedOrders.RestoreStateChanged -= OnHostedOrderRestoreStateChanged;
        ArchiveSummaries.Changed -= OnArchiveSummariesChanged;
        ProfileSync.StatusChanged -= OnProfileSyncStatusChanged;
        WorkerProjections.Changed -= OnWorkerProjectionChangedForPlanRestoration;
        if (_tradeOrdersLayoutRegistration != null)
        {
            await _tradeOrdersLayoutRegistration.InvokeVoidAsync("dispose");
            await _tradeOrdersLayoutRegistration.DisposeAsync();
        }

        if (_tradeOrdersLayoutModule != null)
        {
            await _tradeOrdersLayoutModule.DisposeAsync();
        }

        _tradeOrdersReference?.Dispose();
    }

    private async Task LoadAsync()
    {
        Guid? selectedCanonicalOrderId =
            _selectedOrder?.CompanyCommission == null ? null : _selectedOrder.Id;
        var selectedTab = _activeOpsTab;
        var hadPendingNavigation = _pendingNavigationOrderId.HasValue;
        try
        {
            _loadError = null;
            _companyProfile = await TradeOperationsPersistence.GetOrCreateActiveCompanyProfileAsync();
            _crafters = (await TradeOperationsPersistence.LoadCraftersAsync(_companyProfile.Id)).ToList();
            _orders = (await TradeOperationsPersistence.LoadOrdersAsync(_companyProfile.Id)).ToList();
            await RefreshArchiveSummariesAsync();
            await LoadOrderHostedRevisionsAsync();
            _payrollDrafts = (await TradePayrollPersistence.LoadDraftsAsync(_companyProfile.Id)).ToList();
            SelectPendingNavigationOrder();
            if (!hadPendingNavigation &&
                selectedCanonicalOrderId.HasValue)
            {
                var refreshed = VisibleOrders.FirstOrDefault(
                    order => order.Id == selectedCanonicalOrderId.Value);
                if (refreshed != null)
                {
                    SelectOrder(refreshed);
                    _activeOpsTab = selectedTab;
                }
            }
        }
        catch (Exception ex)
        {
            _companyProfile = null;
            _crafters = [];
            _orders = [];
            _archiveSummaryRecords = [];
            _orderHostedRevisions.Clear();
            _payrollDrafts = [];
            _selectedOrder = null;
            _loadError = ex.Message;
            Snackbar.Add("Trade operations storage is unavailable.", Severity.Error);
        }
    }

    private void OnHostedOrderProjectionChanged(HostedOrderProjectionSnapshot snapshot)
    {
        if (_isDisposed)
        {
            return;
        }
        if (_companyProfile != null &&
            snapshot.CompanyProfileId.HasValue &&
            snapshot.CompanyProfileId != _companyProfile.Id)
        {
            return;
        }

        _ = InvokeAsync(() => ApplyHostedOrderProjection(snapshot));
    }

    private void OnHostedOrderProjectionsReset() =>
        _ = InvokeAsync(ApplyHostedOrderProjectionReset);

    private void OnHostedOrderRestoreStateChanged(HostedOrderRestoreState state) =>
        _ = InvokeAsync(() => ApplyHostedOrderRestoreState(state));

    private bool ShouldPreserveCanonicalEditor()
    {
        if (HasSelectedLocalDraftEditorChanges)
        {
            return true;
        }

        if (IsEditingCommissionTermsRevision)
        {
            return _commissionTermsRevisionDirty ||
                   HasCanonicalDraftDetailChanges ||
                   !string.IsNullOrWhiteSpace(_commissionTermsRevisionReason);
        }

        return CanEditCanonicalDraft &&
               (HasSelectedOrderOutputChanges ||
                HasCanonicalDraftDetailChanges ||
                _selectedOrderPaymentTermsDirty ||
                HasSelectedOrderDetailChanges());
    }

    private bool HasSelectedOrderDetailChanges() =>
        _selectedOrder != null &&
        (!string.Equals(_detailTitle.Trim(), _selectedOrder.Title, StringComparison.Ordinal) ||
         _detailCrafterId != _selectedOrder.AssignedCrafterId ||
         _detailStatus != _selectedOrder.Status ||
         !string.Equals(_detailNotes, _selectedOrder.Notes, StringComparison.Ordinal));

    private async Task<bool> SaveOrderAndNotifyAsync(TradeOrder order)
    {
        if (HasSelectedLocalHostedCollision && _selectedOrder?.Id == order.Id)
        {
            Snackbar.Add(
                "Rebase the local edits onto the hosted copy, or use the hosted copy before saving.",
                Severity.Warning);
            return false;
        }

        if (_companyProfile == null)
        {
            return false;
        }

        if (order.CompanyCommission != null)
        {
            Snackbar.Add(
                "Canonical commissions can only change through commission operations.",
                Severity.Error);
            return false;
        }

        var linkedPlanChanged =
            _selectedOrder?.Id != order.Id ||
            !string.Equals(_selectedOrder.CraftPlanId, order.CraftPlanId, StringComparison.Ordinal) ||
            _selectedOrder.CraftPlanSavedAtUtc != order.CraftPlanSavedAtUtc ||
            _selectedOrder.CraftPlanLinkKind != order.CraftPlanLinkKind;
        if (linkedPlanChanged &&
            order.CraftPlanLinkKind == TradeOrderCraftPlanLinkKind.OrderGenerated &&
            !string.IsNullOrWhiteSpace(order.CraftPlanId))
        {
            var linkedPlan = await PlanPersistence.LoadPlanPayloadAsync(order.CraftPlanId);
            if (linkedPlan == null ||
                !order.CraftPlanSavedAtUtc.HasValue ||
                linkedPlan.SavedAt != order.CraftPlanSavedAtUtc.Value ||
                linkedPlan.LinkedOrderId != order.Id)
            {
                Snackbar.Add(
                    "The exact linked plan revision is unavailable, so the order was not saved.",
                    Severity.Error);
                return false;
            }

            await ProfileSync.QueueLocalSaveAsync(
                ProfileSyncCollections.Plans,
                linkedPlan.Id);
        }

        if (!await TradeOperationsPersistence.SaveOrderAsync(order))
        {
            return false;
        }

        await ProfileSync.QueueLocalSaveAsync(
            ProfileSyncCollections.TradeOrders,
            order.Id.ToString("D"));
        AppState.NotifyTradeOperationsDataChanged();
        return true;
    }

    private async Task PrepareOrderImport()
    {
        if (_companyProfile == null)
        {
            return;
        }

        var source = await WorkerSession.GetTradeProjectionAsync();
        if (source == null)
        {
            Snackbar.Add("The active Worker plan is unavailable.", Severity.Warning);
            return;
        }

        var result = TradeOrderDraftFactory.CreateFromCurrentPlan(new TradeOrderCreateRequest(
            source,
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

    private void CloseNewOrderWorkspace()
    {
        _showNewOrderPanel = false;
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
