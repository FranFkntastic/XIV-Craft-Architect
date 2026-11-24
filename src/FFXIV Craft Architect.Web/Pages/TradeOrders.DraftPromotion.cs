using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;

using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

/// <summary>
/// Every Trade order becomes a canonical company commission draft. New work packages are saved
/// on this device only and promoted through the host's create-draft command; while the host is
/// unreachable they stay "Saved here" and are promoted when the authority is ready. Older plain
/// drafts are converted the same way, at the revision this browser holds and without repricing.
/// </summary>
public partial class TradeOrders
{
    private bool _isPromotingDrafts;
    private bool _draftPromotionRetryRequested;
    private bool _draftPromotionAuthorityWasReady;

    private bool IsSelectedDraftPromotionCandidate =>
        _selectedOrder != null &&
        TradeCompanyCommissionMigrationService.IsDraftWorkPackage(_selectedOrder);

    private TradeCommissionDraftPromotionState? SelectedDraftPromotionState =>
        _selectedOrder == null ? null : DraftPromotions.GetState(_selectedOrder.Id);

    private bool IsOrderHostedSynchronized(TradeOrder order) =>
        HostedOrders.Get(order.Id) != null ||
        _orderHostedRevisions.ContainsKey(order.Id);

    /// <summary>
    /// Saves a freshly built work package on this device without queueing a profile upload,
    /// then promotes it to a commission draft. Returns the canonical order when the host
    /// committed it, otherwise the locally saved pending creation; null when nothing was saved.
    /// </summary>
    private async Task<TradeOrder?> SaveNewWorkPackageAsync(
        TradeOrder order,
        TradeCompanyProfile companyProfile)
    {
        if (!await SaveOrderLocallyAsync(order, verifyLinkedPlan: true, queueProfileUpload: false))
        {
            return null;
        }

        var canonical = await DraftPromotions.PromoteAsync(
            order,
            GetPayrollDraftForOrder(order),
            companyProfile);
        if (canonical == null)
        {
            ReportDraftPromotionState(order.Id);
            return order;
        }

        ReplaceBoardOrder(canonical);
        return canonical;
    }

    /// <summary>
    /// Persists a plain order and its exact generated plan. Upload of the order itself is
    /// queued only when requested; pending creations never upload as plain orders.
    /// </summary>
    private async Task<bool> SaveOrderLocallyAsync(
        TradeOrder order,
        bool verifyLinkedPlan,
        bool queueProfileUpload)
    {
        if (verifyLinkedPlan &&
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

        if (queueProfileUpload)
        {
            await ProfileSync.QueueLocalSaveAsync(
                ProfileSyncCollections.TradeOrders,
                order.Id.ToString("D"));
        }
        AppState.NotifyTradeOperationsDataChanged();
        return true;
    }

    private void ReportDraftPromotionState(Guid orderId)
    {
        if (DraftPromotions.GetState(orderId) is not { } state)
        {
            return;
        }

        Snackbar.Add(
            state.Describe(),
            state.Phase == TradeCommissionDraftPromotionPhase.Refused ? Severity.Warning : Severity.Info);
    }

    /// <summary>
    /// Promotes every plain draft on the board. Runs after loads and whenever profile sync
    /// reports a change, so pending creations follow the authority as soon as it is ready.
    /// </summary>
    private async Task PromotePendingDraftsAsync()
    {
        if (_isDisposed || _companyProfile is not { } company)
        {
            return;
        }
        if (_isPromotingDrafts)
        {
            _draftPromotionRetryRequested = true;
            return;
        }

        _isPromotingDrafts = true;
        try
        {
            // One follow-up pass covers saves that arrived mid-run; status churn caused by the
            // promotion's own sync never re-enters an unbounded loop.
            for (var pass = 0; pass < 2; pass++)
            {
                _draftPromotionRetryRequested = false;
                var candidates = _orders
                    .Where(TradeCompanyCommissionMigrationService.IsDraftWorkPackage)
                    .ToArray();
                if (candidates.Length == 0)
                {
                    return;
                }

                var promoted = await DraftPromotions.PromotePendingAsync(
                    candidates,
                    GetPayrollDraftForOrder,
                    company);
                if (_isDisposed || _companyProfile?.Id != company.Id)
                {
                    return;
                }
                foreach (var canonical in promoted)
                {
                    ReplaceBoardOrder(canonical);
                }
                StateHasChanged();
                if (!_draftPromotionRetryRequested)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            Snackbar.Add($"Commission drafts could not be created: {exception.Message}", Severity.Warning);
        }
        finally
        {
            _isPromotingDrafts = false;
        }
    }

    private async Task PromoteSelectedDraftAsync()
    {
        if (_selectedOrder is not { } selected ||
            _companyProfile is not { } company ||
            !IsSelectedDraftPromotionCandidate)
        {
            return;
        }
        if (HasSelectedLocalDraftEditorChanges)
        {
            Snackbar.Add("Save the current draft edits first.", Severity.Warning);
            return;
        }

        DraftPromotions.ResetRefusal(selected.Id);
        var stored = await TradeOperationsPersistence.LoadOrderAsync(selected.Id) ?? selected;
        var canonical = await DraftPromotions.PromoteAsync(
            stored,
            GetPayrollDraftForOrder(stored),
            company);
        if (canonical == null)
        {
            ReportDraftPromotionState(selected.Id);
            return;
        }

        ReplaceBoardOrder(canonical);
        Snackbar.Add("Commission draft created.", Severity.Success);
    }

    /// <summary>
    /// Puts the committed canonical order on the board in place of the plain work package and
    /// re-selects it unless the operator is mid-edit on that order.
    /// </summary>
    private void ReplaceBoardOrder(TradeOrder canonical)
    {
        var index = _orders.FindIndex(order => order.Id == canonical.Id);
        if (index >= 0)
        {
            _orders[index] = canonical;
        }
        else
        {
            _orders.Add(canonical);
        }

        if (_selectedOrder?.Id == canonical.Id &&
            _selectedOrder.CompanyCommission == null &&
            !OwnsSelectedWorkspaceWorkingState())
        {
            var tab = _activeOpsTab;
            SelectOrder(canonical);
            _activeOpsTab = tab;
        }
    }

    private void OnDraftPromotionStateChanged(Guid orderId)
    {
        if (_isDisposed)
        {
            return;
        }

        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>Promotes pending drafts when profile sync becomes ready, not on every status tick.</summary>
    private void OnProfileSyncReadinessChangedForDraftPromotion()
    {
        var ready = ProfileSync.CurrentStatus is
        {
            IsConnected: true,
            HostReachable: true,
            OrderScopeReady: true
        };
        var becameReady = ready && !_draftPromotionAuthorityWasReady;
        _draftPromotionAuthorityWasReady = ready;
        if (becameReady)
        {
            SchedulePendingDraftPromotion();
        }
    }

    private void SchedulePendingDraftPromotion()
    {
        if (_isDisposed)
        {
            return;
        }

        _ = InvokeAsync(PromotePendingDraftsAsync);
    }

    private string GetDraftPromotionChip(TradeOrder order) =>
        DraftPromotions.GetState(order.Id)?.Phase switch
        {
            TradeCommissionDraftPromotionPhase.Promoting => "Creating draft",
            TradeCommissionDraftPromotionPhase.Refused => "Draft refused",
            _ => "Saved here"
        };

    private string GetDraftPromotionTitle(TradeOrder order) =>
        DraftPromotions.GetState(order.Id)?.Describe() ??
        "Saved on this device; becomes a commission draft when the company host is ready.";
}
