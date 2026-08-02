using System.Text;

using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Dialogs;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
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
            if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
                    "Updating this order plan",
                    _selectedOrder.CraftPlanId))
            {
                return;
            }

            var rollbackPlan = string.IsNullOrWhiteSpace(_selectedOrder.CraftPlanId)
                ? null
                : await PlanPersistence.LoadPlanPayloadAsync(_selectedOrder.CraftPlanId);
            var orderToSave = TradeOrderWorkflow.WithRequestedOutputs(
                _selectedOrder,
                outputs,
                DateTime.UtcNow);
            if (rollbackPlan != null &&
                _selectedOrder.CraftPlanLinkKind == TradeOrderCraftPlanLinkKind.OrderGenerated)
            {
                orderToSave.CraftPlanId = _selectedOrder.CraftPlanId;
                orderToSave.CraftPlanName = _selectedOrder.CraftPlanName;
                orderToSave.CraftPlanSavedAtUtc = _selectedOrder.CraftPlanSavedAtUtc;
                orderToSave.CraftPlanLinkKind = _selectedOrder.CraftPlanLinkKind;
            }
            if (IsEditingCommissionTermsRevision &&
                _commissionTermsRevisionRollbackPlan == null)
            {
                _commissionTermsRevisionRollbackPlan = rollbackPlan;
            }

            var pricingResult = await TradeOrderPricingWorkflow.RebuildAndPriceAsync(
                orderToSave,
                new TradeOrderPricingWorkflowOptions(
                    GetOrderDataCenter(_selectedOrder),
                    _selectedOrder.SourceSnapshot.World ?? string.Empty,
                    ForceRefreshMarketData: false));
            if (!pricingResult.HasUpdatedOrder || pricingResult.UpdatedOrder == null)
            {
                if (rollbackPlan != null)
                {
                    if (pricingResult.ActivePlanFence is { } failedFence)
                    {
                        await RestoreStagedProcurementPlanAsync(
                            rollbackPlan,
                            failedFence);
                    }
                }
                Snackbar.Add(
                    $"Requested outputs were not saved because the plan could not be updated. {pricingResult.Message}",
                    ToSnackbarSeverity(pricingResult.MessageLevel));
                return;
            }

            orderToSave = pricingResult.UpdatedOrder;
            var saved = _selectedOrder.CompanyCommission == null
                ? await SaveOrderAndNotifyAsync(orderToSave)
                : await UpdateCanonicalDraftAsync(
                    orderToSave,
                    BuildCommissionBrief(
                        orderToSave,
                        TradeCommissionPaymentSummary.FromOrder(
                            orderToSave,
                            GetSelectedOrderResponsibilityProjection(),
                            GetSelectedOrderEffectivePaymentPolicy())),
                    "Requested outputs, craft plan, and pricing saved to the commission draft");
            if (!saved)
            {
                if (rollbackPlan != null)
                {
                    if (pricingResult.ActivePlanFence is { } failedSaveFence)
                    {
                        await RestoreStagedProcurementPlanAsync(
                            rollbackPlan,
                            failedSaveFence);
                    }
                }
                Snackbar.Add(
                    "The updated plan was prepared, but the order was not saved. Your output edits remain available to retry.",
                    Severity.Error);
                return;
            }

            if (_selectedOrder.CompanyCommission == null)
            {
                await LoadAsync();
                if (string.IsNullOrWhiteSpace(_loadError) &&
                    SelectOrderAfterReload(
                        orderId,
                        "Requested outputs were saved, but the order could not be loaded."))
                {
                    await SetActiveOpsTabAsync(ProcurementTabIndex);
                }
            }

            Snackbar.Add(pricingResult.Message, ToSnackbarSeverity(pricingResult.MessageLevel));
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to save requested outputs: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isSavingSelectedOrderOutputs = false;
            ScheduleSelectedOrderPlanRestoration();
        }
    }

    private async Task SaveSelectedOrderAsync()
    {
        if (_selectedOrder == null)
        {
            return;
        }

        if (_selectedOrder.CompanyCommission != null)
        {
            Snackbar.Add(
                "Published commission details are projection-driven. Use commission operations to revise terms.",
                Severity.Warning);
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
                _activeOpsTab = TimelineTabIndex;
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

        if (_selectedOrder.CompanyCommission != null && !CanEditCanonicalDraft)
        {
            Snackbar.Add(
                "Published work packages can only change through Revise Terms.",
                Severity.Warning);
            return;
        }

        if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
            HasLinkedCraftPlan(_selectedOrder) ? "Updating this order plan" : "Creating this order plan",
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

            var saved = _selectedOrder.CompanyCommission == null
                ? await SaveOrderAndNotifyAsync(result.UpdatedOrder)
                : await UpdateCanonicalDraftAsync(
                    result.UpdatedOrder,
                    BuildCommissionBrief(
                        result.UpdatedOrder,
                        TradeCommissionPaymentSummary.FromOrder(
                            result.UpdatedOrder,
                            GetSelectedOrderResponsibilityProjection(),
                            GetSelectedOrderEffectivePaymentPolicy())),
                    "Craft plan updated and saved to the commission draft");
            if (!saved)
            {
                Snackbar.Add("Craft plan saved, but failed to link it to the order.", Severity.Error);
                return;
            }

            if (_selectedOrder.CompanyCommission == null)
            {
                await LoadAsync();
                if (string.IsNullOrWhiteSpace(_loadError))
                {
                    SelectOrderAfterReload(orderId, "Craft plan was saved, but the order could not be loaded.");
                }
            }

            Snackbar.Add(result.Message, ToSnackbarSeverity(result.MessageLevel));
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to update and price the linked craft plan: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isSavingSelectedOrderCraftPlan = false;
            ScheduleSelectedOrderPlanRestoration();
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
            "Replace Linked Craft Plan",
            parameters,
            options);
        var result = await dialog.Result;
        return result?.Data is true;
    }

    private async Task OpenSelectedOrderCraftPlanAsync()
    {
        var order = _selectedOrder;
        if (order == null || _isOpeningSelectedOrderCraftPlan)
        {
            return;
        }

        if (!HasLinkedCraftPlan(order))
        {
            Snackbar.Add("Create a linked craft plan before opening it.", Severity.Warning);
            return;
        }

        _isOpeningSelectedOrderCraftPlan = true;

        try
        {
            if (!await LoadExactOrderPlanAsync(
                    order,
                    "Opening this order plan"))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(WorkerProjections.Shell.RestoreWarning))
            {
                Snackbar.Add(WorkerProjections.Shell.RestoreWarning, Severity.Warning);
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

        NavigationManager.NavigateTo($"market?itemId={row.ItemId}");
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
        var order = _selectedOrder;
        if (order == null)
        {
            return false;
        }

        if (!HasLinkedCraftPlan(order))
        {
            Snackbar.Add("Create a linked craft plan before opening Craft Architect details.", Severity.Warning);
            return false;
        }

        if (!await LoadExactOrderPlanAsync(
                order,
                "Opening Craft Architect details for this order"))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(WorkerProjections.Shell.RestoreWarning))
        {
            Snackbar.Add(WorkerProjections.Shell.RestoreWarning, Severity.Warning);
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

        if (_selectedOrder.CompanyCommission != null && !CanEditCanonicalDraft)
        {
            Snackbar.Add(
                "Published pricing is part of the accepted terms. Use Revise Terms to refresh it.",
                Severity.Warning);
            return;
        }

        if (!HasLinkedCraftPlan(_selectedOrder))
        {
            Snackbar.Add("Create a linked craft plan before repricing.", Severity.Warning);
            return;
        }

        _isRepricingSelectedOrder = true;
        var orderId = _selectedOrder.Id;
        var activeOpsTab = _activeOpsTab;
        var rollbackPlan = await PlanPersistence.LoadPlanPayloadAsync(
            _selectedOrder.CraftPlanId!);
        TradeOrderPricingWorkflowResult? pricingResult = null;
        TradeCommissionOperatorResult? commissionResult = null;

        try
        {
            if (!await ConfirmActiveCraftPlanCanBeReplacedAsync(
                "Repricing this order",
                _selectedOrder.CraftPlanId))
            {
                return;
            }

            pricingResult = await TradeOrderPricingWorkflow.RepriceAsync(
                _selectedOrder,
                new TradeOrderPricingWorkflowOptions(
                    GetOrderDataCenter(_selectedOrder),
                    _selectedOrder.SourceSnapshot.World ?? string.Empty,
                    ForceRefreshMarketData: true));
            if (!pricingResult.HasUpdatedOrder || pricingResult.UpdatedOrder == null)
            {
                if (rollbackPlan != null && pricingResult.ActivePlanFence is { } failedFence)
                {
                    await RestoreStagedProcurementPlanAsync(
                        rollbackPlan,
                        failedFence);
                }
                Snackbar.Add(
                    pricingResult.Message,
                    ToSnackbarSeverity(pricingResult.MessageLevel));
                return;
            }

            var saved = _selectedOrder.CompanyCommission == null
                ? await SaveOrderAndNotifyAsync(pricingResult.UpdatedOrder)
                : await UpdateCanonicalDraftAsync(
                    pricingResult.UpdatedOrder,
                    BuildCommissionBrief(
                        pricingResult.UpdatedOrder,
                        TradeCommissionPaymentSummary.FromOrder(
                            pricingResult.UpdatedOrder,
                            GetSelectedOrderResponsibilityProjection(),
                            GetSelectedOrderEffectivePaymentPolicy())),
                    "Pricing refreshed and saved to the commission draft",
                    result => commissionResult = result);
            if (!saved)
            {
                if (commissionResult?.HostCommitted != true &&
                    rollbackPlan != null &&
                    pricingResult.ActivePlanFence is { } failedSaveFence)
                {
                    await RestoreStagedProcurementPlanAsync(
                        rollbackPlan,
                        failedSaveFence);
                }
                Snackbar.Add("Order pricing updated, but failed to save it to the order.", Severity.Error);
                return;
            }

            if (_selectedOrder.CompanyCommission == null)
            {
                await LoadAsync();
                if (string.IsNullOrWhiteSpace(_loadError) &&
                    SelectOrderAfterReload(orderId, "Order pricing was saved, but the order could not be loaded."))
                {
                    _activeOpsTab = activeOpsTab;
                }
            }

            Snackbar.Add(
                pricingResult.Message,
                ToSnackbarSeverity(pricingResult.MessageLevel));
        }
        catch (Exception ex)
        {
            if (commissionResult?.HostCommitted != true &&
                rollbackPlan != null &&
                pricingResult?.ActivePlanFence is { } failedExceptionFence)
            {
                await RestoreStagedProcurementPlanAsync(
                    rollbackPlan,
                    failedExceptionFence);
            }
            Snackbar.Add($"Failed to reprice order: {ex.Message}", Severity.Error);
        }
        finally
        {
            _isRepricingSelectedOrder = false;
            ScheduleSelectedOrderPlanRestoration();
        }
    }

    private async Task<bool> ConfirmActiveCraftPlanCanBeReplacedAsync(
        string actionLabel,
        string? targetPlanId)
    {
        if (!WorkerProjections.Shell.HasSession ||
            string.Equals(
                WorkerProjections.Shell.PlanId,
                targetPlanId,
                StringComparison.Ordinal))
        {
            return true;
        }

        return await SaveActiveCraftPlanBeforeTradeActionAsync();
    }

    private async Task<bool> SaveActiveCraftPlanBeforeTradeActionAsync()
    {
        if (!WorkerProjections.Shell.HasSession)
        {
            Snackbar.Add("There are no project items to save.", Severity.Warning);
            return false;
        }

        var planId = WorkerProjections.Shell.PlanId;
        var planName = WorkerProjections.Shell.PlanName;
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

        var snapshot = await WorkerSession.ExportStoredPlanAsync(
            planId,
            planName,
            includeSourcePlanIdentity: true);
        if (snapshot == null)
        {
            Snackbar.Add("Failed to save the active Craft plan.", Severity.Error);
            return false;
        }

        var preservation = await PlanPersistence.PreserveBeforeReplacementAsync(snapshot);
        if (!preservation.Success)
        {
            Snackbar.Add("Failed to preserve the active Craft plan.", Severity.Error);
            return false;
        }
        if (preservation.Forked)
        {
            Snackbar.Add(
                $"Saved active changes as '{preservation.PlanName}'",
                Severity.Success);
        }
        else if (!preservation.AlreadyDurable)
        {
            Snackbar.Add($"Saved '{preservation.PlanName}'", Severity.Success);
        }
        return true;
    }

    private async Task<bool> LoadExactOrderPlanAsync(
        TradeOrder order,
        string actionLabel)
    {
        if (_isDisposed || !HasLinkedCraftPlan(order))
        {
            return false;
        }

        InvalidateSelectedOrderPlanRestoration();
        var requiredTab = _activeOpsTab;
        var request = new TradeOrderPlanRestoreRequest(
            Interlocked.Increment(ref _selectedOrderPlanRestoreGeneration),
            order.Id,
            order.CraftPlanId!,
            WorkerProjections.Shell.Revision,
            order.CraftPlanSavedAtUtc);
        using var cancellation = new CancellationTokenSource();
        var priorCancellation = Interlocked.Exchange(
            ref _selectedOrderPlanRestoreCancellation,
            cancellation);
        priorCancellation?.Cancel();

        try
        {
            if (!IsCurrentPlanRequest(request, requiredTab))
            {
                return false;
            }

            if (WorkerProjections.Shell.HasSession &&
                string.IsNullOrWhiteSpace(WorkerProjections.Shell.PlanId))
            {
                Snackbar.Add(
                    "Your unsaved active plan is being preserved. Save or discard it before opening this order's plan.",
                    Severity.Warning);
                return false;
            }

            if (IsSelectedOrderLinkedPlanActive())
            {
                return true;
            }

            var canReplaceActivePlan = await ConfirmActiveCraftPlanCanBeReplacedAsync(
                actionLabel,
                request.PlanId);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentPlanRequest(request, requiredTab))
            {
                return false;
            }
            if (!canReplaceActivePlan)
            {
                return false;
            }

            var read = await TradeOrderPlanRestorePolicy.ReadExactPlanAsync(
                _ => PlanPersistence.LoadPlanPayloadAsync(request.PlanId),
                () => ProfileSync.CurrentStatus,
                waitsForProfilePlanAuthority: order.CompanyCommission != null,
                cancellationToken: cancellation.Token,
                canContinue: () => IsCurrentPlanRequest(request, requiredTab));
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentPlanRequest(request, requiredTab))
            {
                return false;
            }

            if (read.Payload == null ||
                !TradeOrderPlanRestorePolicy.IsExactSavedRevision(
                    request,
                    read.Payload))
            {
                Snackbar.Add(
                    read.Outcome == TradeOrderPlanReadOutcome.WaitForHostedPlan
                        ? "The saved craft plan is still arriving. Try again in a moment."
                        : "The exact saved craft plan revision is unavailable here. The order was left unchanged so its acquisition choices aren't replaced.",
                    Severity.Warning);
                return false;
            }

            var adoptedRequest = await AdoptExactOrderPlanAsync(
                request,
                read.Payload,
                requiredTab,
                cancellation.Token,
                SaveActiveCraftPlanBeforeTradeActionAsync);
            return adoptedRequest.HasValue;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (IsCurrentPlanRequest(request, requiredTab))
            {
                Snackbar.Add(
                    "The exact saved craft plan could not be opened. The active plan was left unchanged.",
                    Severity.Error);
            }
            Console.Error.WriteLine($"Linked Trade order plan open failed: {ex.Message}");
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _selectedOrderPlanRestoreCancellation,
                null,
                cancellation);
        }

    }

    private async Task<TradeOrderPlanRestoreRequest?> AdoptExactOrderPlanAsync(
        TradeOrderPlanRestoreRequest request,
        StoredPlan payload,
        int requiredTab,
        CancellationToken cancellationToken,
        Func<Task<bool>>? preserveCompetingPlanBeforeRetry = null)
    {
        const int maximumAttempts = 2;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentPlanRequest(request, requiredTab))
            {
                return null;
            }

            request = request with
            {
                WorkerRevision = WorkerProjections.Shell.Revision
            };
            try
            {
                await PlanLifecycle.ReplaceStoredPlanAsync(
                    payload,
                    trackStoredPlanIdentity: true,
                    derivation: PlanDerivationDispatch.Deferred,
                    cancellationToken: cancellationToken,
                    expectedWorkerRevision: request.WorkerRevision);
            }
            catch (WorkerSessionCommandRejectedException ex)
                when (attempt < maximumAttempts &&
                      string.Equals(
                          ex.RejectionCode,
                          "stale-revision",
                          StringComparison.Ordinal) &&
                      IsCurrentPlanRequest(request, requiredTab))
            {
                if (preserveCompetingPlanBeforeRetry == null ||
                    !await preserveCompetingPlanBeforeRetry())
                {
                    return null;
                }
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentPlanRequest(request, requiredTab) ||
                !string.Equals(
                    WorkerProjections.Shell.PlanId,
                    request.PlanId,
                    StringComparison.Ordinal))
            {
                return null;
            }

            return request with
            {
                WorkerRevision = WorkerProjections.Shell.Revision
            };
        }

        return null;
    }

    private string GetOrderDataCenter(TradeOrder order)
    {
        return string.IsNullOrWhiteSpace(order.SourceSnapshot.DataCenter)
            ? WorkerProjections.Shell.SelectedDataCenter
            : order.SourceSnapshot.DataCenter;
    }

    private static IReadOnlyList<TradeOrderRootItemSnapshot> GetOrderRootItems(TradeOrder order)
    {
        return order.SourceSnapshot?.RootItems ?? Array.Empty<TradeOrderRootItemSnapshot>();
    }

}
