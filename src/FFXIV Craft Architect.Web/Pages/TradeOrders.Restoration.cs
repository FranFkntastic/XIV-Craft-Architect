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
        if (HasSelectedLocalHostedCollision)
        {
            Snackbar.Add(
                "Resolve the local edit collision before changing the hosted order.",
                Severity.Warning);
            return false;
        }

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

    private IReadOnlyList<ArchivedOrderRow> ComposeArchivedOrderRows()
    {
        var rows = VisibleOrders
            .Where(IsOrderArchivedForAttention)
            .ToDictionary(
                order => order.Id,
                order => new ArchivedOrderRow(
                    order.Id,
                    order.Title,
                    order.Status,
                    order.CommissionedAtUtc,
                    GetOrderRootItems(order)
                        .Select(item => item.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    order,
                    null));

        foreach (var record in _archiveSummaryRecords.Where(record =>
                     _companyProfile != null &&
                     record.CompanyProfileId == _companyProfile.Id &&
                     TradeOrderStatusWorkflow.IsArchived(record.Summary.Status)))
        {
            if (rows.TryGetValue(record.OrderId, out var full) &&
                full.Order != null &&
                GetKnownHostedRevision(full.Order) >= record.HostedRevision)
            {
                continue;
            }

            rows[record.OrderId] = new ArchivedOrderRow(
                record.OrderId,
                record.Summary.Title,
                record.Summary.Status,
                record.Summary.CommissionedAtUtc,
                record.Summary.Outputs
                    .Select(output => output.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                null,
                record);
        }

        return rows.Values
            .OrderByDescending(order => order.CommissionedAtUtc)
            .ToArray();
    }

    private bool IsSupersededByArchiveSummary(TradeOrder order)
    {
        var summary = _archiveSummaryRecords.FirstOrDefault(record =>
            record.OrderId == order.Id &&
            _companyProfile != null &&
            record.CompanyProfileId == _companyProfile.Id);
        return summary != null && summary.HostedRevision > GetKnownHostedRevision(order);
    }

    private long GetKnownHostedRevision(TradeOrder order)
    {
        return HostedOrders.Get(order.Id)?.ObjectRevision ??
            _orderHostedRevisions.GetValueOrDefault(order.Id);
    }

    private async Task LoadOrderHostedRevisionsAsync()
    {
        _orderHostedRevisions.Clear();
        var connection = await ProfileSyncLocalState.LoadConnectionSettingsAsync();
        if (connection.ProfileScopeId == null)
        {
            return;
        }

        var revisions = await ProfileSyncLocalState.LoadObjectRevisionsAsync(
            connection.ProfileScopeId,
            ProfileSyncCollections.TradeOrders,
            _orders.Select(order => order.Id.ToString("D")));
        foreach (var order in _orders)
        {
            var revision = revisions.GetValueOrDefault(order.Id.ToString("D"));
            if (revision > 0)
            {
                _orderHostedRevisions[order.Id] = revision;
            }
        }
    }

    private async Task RefreshArchiveSummariesAsync()
    {
        await ArchiveSummaries.LoadAsync();
        var connection = await ProfileSyncLocalState.LoadConnectionSettingsAsync();
        _archiveSummaryRecords = ArchiveSummaries
            .GetAll(connection.ConnectionScopeId)
            .ToList();
    }

    private void OnArchiveSummariesChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            await RefreshArchiveSummariesAsync();
            StateHasChanged();
        });
    }

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
        ApplyHostedOrderProjectionState(snapshot);
        StateHasChanged();
    }

    private void ApplyHostedOrderProjections(
        IReadOnlyList<HostedOrderProjectionSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            if (_companyProfile != null &&
                snapshot.CompanyProfileId.HasValue &&
                snapshot.CompanyProfileId != _companyProfile.Id)
            {
                continue;
            }

            ApplyHostedOrderProjectionState(snapshot);
        }

        StateHasChanged();
    }

    private void ApplyHostedOrderProjectionState(HostedOrderProjectionSnapshot snapshot)
    {
        _orderHostedRevisions[snapshot.OrderId] = snapshot.ObjectRevision;
        switch (TradeOrderWorkspaceProjectionPolicy.Decide(
                    _selectedOrder?.Id,
                    _selectedOrder?.CompanyCommission != null,
                    snapshot,
                    snapshot.Order?.CompanyCommission != null,
                    HasSelectedLocalDraftEditorChanges,
                    OwnsSelectedWorkspaceWorkingState()))
        {
            case TradeOrderWorkspaceProjectionAction.Ignore:
                break;
            case TradeOrderWorkspaceProjectionAction.ClearUnavailableSelection:
                ClearUnavailableSelectedOrder("This order is no longer available.");
                break;
            case TradeOrderWorkspaceProjectionAction.ClearLocalCollision:
                _selectedLocalHostedCollision = null;
                break;
            case TradeOrderWorkspaceProjectionAction.RecordLocalCollision:
                _selectedLocalHostedCollision = snapshot;
                break;
            case TradeOrderWorkspaceProjectionAction.AdoptHostedCanonicalWorkspace:
                AdoptHostedCanonicalWorkspace(
                    snapshot.OwnerProjection?.Order ?? snapshot.Order!,
                    snapshot.ObjectRevision);
                break;
            case TradeOrderWorkspaceProjectionAction.PreserveWorkingState:
                if (_pendingSelectedOrderProjection == null ||
                    snapshot.ObjectRevision >= _pendingSelectedOrderProjection.ObjectRevision)
                {
                    _pendingSelectedOrderProjection = snapshot;
                }
                break;
            case TradeOrderWorkspaceProjectionAction.RefreshReadModel:
                RefreshSelectedOrderReadModel(
                    snapshot.OwnerProjection?.Order ?? snapshot.Order!,
                    snapshot.ObjectRevision);
                break;
        }
    }

    private void RefreshSelectedOrderReadModel(TradeOrder order, long objectRevision)
    {
        if (_selectedOrder == null || _selectedOrder.Id != order.Id)
        {
            return;
        }

        var refreshDetails = !HasSelectedOrderDetailChanges();
        var refreshOutputs = !HasSelectedOrderOutputChanges;
        var refreshPayment = !_selectedOrderPaymentTermsDirty;
        var refreshCommissionDetails = !HasCanonicalDraftDetailChanges;
        var linkedPlanChanged =
            !string.Equals(_selectedOrder.CraftPlanId, order.CraftPlanId, StringComparison.Ordinal) ||
            _selectedOrder.CraftPlanSavedAtUtc != order.CraftPlanSavedAtUtc ||
            _selectedOrder.CraftPlanLinkKind != order.CraftPlanLinkKind;

        if (linkedPlanChanged)
        {
            InvalidateSelectedOrderPlanRestoration();
            ClearLiveProcurementSnapshot();
            _selectedOrderPlanRestoreError = null;
        }

        _selectedOrder = order;
        _selectedOrderProjectionRevision = objectRevision;
        _selectedLocalHostedCollision = null;

        if (refreshDetails)
        {
            _detailTitle = order.Title;
            _detailCrafterId = order.AssignedCrafterId;
            _detailStatus = order.Status;
            _detailNotes = order.Notes;
        }
        if (refreshOutputs)
        {
            _selectedOrderOutputEditors = TradeRequestedOrderEditorMapper.FromOrder(order);
        }
        if (refreshPayment)
        {
            var payment = order.CompanyCommission?.CurrentTerms.Payment;
            _selectedOrderPaymentSchedule = payment?.Schedule ?? order.PaymentSchedule;
            _selectedOrderCustomPaymentTerms = payment?.CustomTerms ??
                                               order.CustomPaymentTerms ??
                                               string.Empty;
        }
        if (refreshCommissionDetails)
        {
            var terms = order.CompanyCommission?.CurrentTerms;
            _commissionContact = terms?.ContactInstructions ??
                                 _companyProfile?.CommissionContact ??
                                 string.Empty;
            _commissionDeliveryInstructions = terms?.DeliveryInstructions ?? string.Empty;
        }

        if (linkedPlanChanged)
        {
            ScheduleSelectedOrderPlanRestoration();
        }
        ScheduleSelectedCommissionOwnerRefresh(order);
    }

    private bool ApplyPendingSelectedOrderProjectionIfIdle()
    {
        if (_pendingSelectedOrderProjection is not { } pending)
        {
            return false;
        }

        if (!TradeOrderWorkspaceProjectionPolicy.CanApplyPendingProjection(
                OwnsSelectedWorkspaceWorkingState(),
                _selectedOrderProjectionRevision,
                pending.ObjectRevision))
        {
            if (_selectedOrderProjectionRevision.HasValue &&
                pending.ObjectRevision <= _selectedOrderProjectionRevision.Value)
            {
                _pendingSelectedOrderProjection = null;
            }
            return false;
        }

        _pendingSelectedOrderProjection = null;
        ApplyHostedOrderProjectionState(pending);
        return true;
    }

    private async Task ApplyHostedOrderProjectionReset()
    {
        if (_isDisposed)
        {
            return;
        }

        _pendingSelectedOrderProjection = null;
        if (_selectedOrder?.CompanyCommission != null)
        {
            ClearUnavailableSelectedOrder("The active order workspace changed.");
        }

        _orderHostedRevisions.Clear();
        await RefreshArchiveSummariesAsync();
        StateHasChanged();
    }

    private void AdoptHostedCanonicalWorkspace(TradeOrder order, long objectRevision)
    {
        var selectedTab = _activeOpsTab;
        var planExpanded = _isPlanPaneExpanded;
        SelectOrder(order);
        _selectedOrderProjectionRevision = objectRevision;
        _activeOpsTab = selectedTab;
        _isPlanPaneExpanded = planExpanded;
    }

    private async Task ApplyHostedOrderRestoreState(HostedOrderRestoreState state)
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
        if (state.Stage == HostedOrderRestoreStage.ScopeChanging)
        {
            _pendingSelectedOrderProjection = null;
            _archiveSummaryRecords = [];
            _orderHostedRevisions.Clear();
        }
        else if (state.ShowsCompleteProjection &&
                 _pendingNotificationNavigation?.ActivityId != null)
        {
            await SelectPendingNavigationOrderAsync();
        }
        else if (state.ShowsCompleteProjection &&
                 _selectedOrder != null &&
                 _companyProfile != null)
        {
            var hosted = HostedOrders.Get(_selectedOrder.Id);
            if (hosted is { Deleted: false, Order: not null } &&
                hosted.CompanyProfileId == _companyProfile.Id)
            {
                if (HasSelectedLocalDraftEditorChanges)
                {
                    _selectedLocalHostedCollision = hosted;
                }
                else if (TradeOrderWorkspaceProjectionPolicy.ShouldApplyRestoreProjection(
                             _selectedOrderProjectionRevision,
                             hosted.ObjectRevision))
                {
                    ApplyHostedOrderProjectionState(hosted);
                }
            }
            else if (_selectedOrder.CompanyCommission != null)
            {
                ClearUnavailableSelectedOrder(
                    "That device-only order is stored separately from the hosted workspace.");
            }
        }
        StateHasChanged();
    }

    private void ClearUnavailableSelectedOrder(string message)
    {
        InvalidateSelectedCommissionOwnerRefresh();
        _selectedOrder = null;
        _selectedOrderProjectionRevision = null;
        _pendingSelectedOrderProjection = null;
        _manualNote = string.Empty;
        _showCommissionTermsRevision = false;
        _commissionTermsRevisionWorkPackage = null;
        _commissionTermsRevisionBrief = null;
        _commissionTermsRevisionRollbackPlan = null;
        _commissionTermsRevisionDirty = false;
        _commissionTermsRevisionPaymentDirty = false;
        _selectedOrderPaymentTermsDirty = false;
        _selectedLocalHostedCollision = null;
        AppState.SelectTradeOrder(null);
        ClearSelectedOrderNavigation();
        Snackbar.Add(message, Severity.Info);
    }

    private void AdoptHostedCopyForSelectedLocalOrder()
    {
        if (_selectedLocalHostedCollision is not { Deleted: false, Order: not null } hosted ||
            _selectedOrder?.Id != hosted.OrderId)
        {
            return;
        }

        var selectedTab = _activeOpsTab;
        var planExpanded = _isPlanPaneExpanded;
        SelectOrder(hosted.OwnerProjection?.Order ?? hosted.Order);
        _activeOpsTab = selectedTab;
        _isPlanPaneExpanded = planExpanded;
    }

    private void RebaseSelectedLocalEditsOntoHostedCopy()
    {
        if (_selectedLocalHostedCollision is not { Deleted: false, Order: not null } hosted ||
            hosted.Order.CompanyCommission != null ||
            _selectedOrder is not { CompanyCommission: null } local ||
            local.Id != hosted.OrderId)
        {
            return;
        }

        var titleDirty = !string.Equals(
            _detailTitle.Trim(),
            local.Title,
            StringComparison.Ordinal);
        var crafterDirty = _detailCrafterId != local.AssignedCrafterId;
        var statusDirty = _detailStatus != local.Status;
        var notesDirty = !string.Equals(_detailNotes, local.Notes, StringComparison.Ordinal);
        var outputsDirty = HasSelectedOrderOutputChanges;
        var paymentDirty = _selectedOrderPaymentTermsDirty;
        var title = _detailTitle;
        var crafterId = _detailCrafterId;
        var status = _detailStatus;
        var notes = _detailNotes;
        var outputEditors = _selectedOrderOutputEditors.ToList();
        var paymentSchedule = _selectedOrderPaymentSchedule;
        var customPaymentTerms = _selectedOrderCustomPaymentTerms;
        var selectedTab = _activeOpsTab;
        var planExpanded = _isPlanPaneExpanded;

        SelectOrder(hosted.Order);
        if (titleDirty)
        {
            _detailTitle = title;
        }
        if (crafterDirty)
        {
            _detailCrafterId = crafterId;
        }
        if (statusDirty)
        {
            _detailStatus = status;
        }
        if (notesDirty)
        {
            _detailNotes = notes;
        }
        if (outputsDirty)
        {
            _selectedOrderOutputEditors = outputEditors;
        }
        if (paymentDirty)
        {
            _selectedOrderPaymentSchedule = paymentSchedule;
            _selectedOrderCustomPaymentTerms = customPaymentTerms;
            _selectedOrderPaymentTermsDirty = true;
        }

        _activeOpsTab = selectedTab;
        _isPlanPaneExpanded = planExpanded;
        Snackbar.Add(
            "Local edits rebased onto the hosted copy. Review and save them.",
            Severity.Info);
    }
}
