using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using FFXIV_Craft_Architect.Web.Shared;
using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private string _commissionIdentityRejectionReason = string.Empty;
    private bool _blockRejectedCommissionContact;
    private string _commissionPaymentDecisionReason = string.Empty;
    private string _commissionPaymentObservation = string.Empty;
    private string _commissionPaymentRetractionReason = string.Empty;
    private string _commissionTermsRevisionReason = string.Empty;
    private string _commissionReturnReason = string.Empty;
    private string _commissionSettlementObservation = string.Empty;
    private string _commissionSettlementRetractionReason = string.Empty;
    private string _commissionResolutionNote = string.Empty;
    private string _commissionSharedComment = string.Empty;
    private Guid? _commissionIdentityCrafterId;
    private Guid? _retryingCommissionDiagnosticId;
    private bool _isCommissionCommandRunning;
    private bool _showCommissionTermsRevision;
    private TradeOrder? _commissionTermsRevisionWorkPackage;
    private CommissionBriefDocument? _commissionTermsRevisionBrief;
    private StoredPlan? _commissionTermsRevisionRollbackPlan;
    private bool _commissionTermsRevisionDirty;
    private bool _commissionTermsRevisionPaymentDirty;

    private CompanyCommissionOwnerProjection? SelectedCommissionOwner
    {
        get
        {
            if (_selectedOrder == null)
            {
                return null;
            }

            var snapshot = HostedOrders.Get(_selectedOrder.Id);
            return snapshot is
            {
                Deleted: false,
                DisplayState: HostedOrderDisplayState.Verified
            } &&
                   !HostedOrderSyncCoordinator.NeedsOwnerAdoption(snapshot)
                ? CommissionOperations.GetForOrder(_selectedOrder.Id)
                : null;
        }
    }

    private string? SelectedCommissionOperationsError =>
        _selectedOrder == null
            ? null
            : CommissionOperations.GetErrorForOrder(_selectedOrder.Id);

    private IReadOnlyList<TradeDiscordNotificationDiagnostic>
        SelectedCommissionNotificationDiagnostics =>
        _selectedOrder == null
            ? []
            : CommissionOperations.GetNotificationDiagnostics(_selectedOrder.Id);

    private string? SelectedCommissionNotificationError =>
        _selectedOrder == null
            ? null
            : CommissionOperations.GetNotificationError(_selectedOrder.Id);

    private TradeCompanyCommission? SelectedCanonicalCommission =>
        SelectedCommissionOwner?.Order.CompanyCommission ??
        SelectedHostedOrderSnapshot?.Order?.CompanyCommission;

    private PendingPaymentPolicyRequest? SelectedPaymentPolicyRequest =>
        SelectedCommissionOwner == null
            ? null
            : TradeCommissionOperationsPresentation.GetPendingPaymentPolicyRequest(
                SelectedCommissionOwner);

    private bool HasCanonicalCommission =>
        _selectedOrder?.CompanyCommission != null;

    private bool IsSelectedCanonicalOwnerMissing =>
        _selectedOrder != null &&
        CommissionOperations.IsCanonicalOwnerMissing(_selectedOrder.Id);

    private bool CanEditCanonicalDraft =>
        SelectedCommissionOwner is { Order.CompanyCommission: { } commission } owner &&
        owner.Order.Id == _selectedOrder?.Id &&
        commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Draft &&
        commission.ActiveClaim == null &&
        !TradeOrderStatusWorkflow.IsArchived(owner.Order.Status);

    private bool IsEditingCommissionTermsRevision =>
        _showCommissionTermsRevision &&
        _selectedOrder != null &&
        _commissionTermsRevisionWorkPackage?.Id == _selectedOrder.Id;

    private bool CanEditCanonicalWorkPackage =>
        CanEditCanonicalDraft || IsEditingCommissionTermsRevision;

    private static bool CanReviseCanonicalTerms(TradeCompanyCommission commission) =>
        commission.OutputProgress.All(item =>
            item.CompletedQuantity == 0 &&
            item.ReadyQuantity == 0 &&
            item.AcceptedQuantity == 0) &&
        !commission.DeliveryReadiness.IsReady;

    private bool CanEditSelectedOrderPlan =>
        _selectedOrder != null &&
        !HasSelectedLocalHostedCollision &&
        !TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status) &&
        (_selectedOrder.CompanyCommission == null || CanEditCanonicalWorkPackage);

    private bool CanBeginSelectedOrderPlanReconstruction =>
        _selectedOrder != null &&
        !IsPlanMutationTransactionRunning &&
        !TradeOrderStatusWorkflow.IsArchived(_selectedOrder.Status) &&
        GetOrderRootItems(_selectedOrder).Count > 0 &&
        (_selectedOrder.CompanyCommission == null ||
         CanEditCanonicalWorkPackage ||
         CanMutateHostedOrder &&
         SelectedCanonicalCommission is
         {
             PublicMetadata.ViewState: CompanyCommissionPublicViewState.Published
         } commission &&
         CanReviseCanonicalTerms(commission));

    private async Task OpenSelectedOrderTermsAsync()
    {
        if (HasSelectedLocalHostedCollision)
        {
            _ = EnsureHostedOrderMutationAvailable();
            return;
        }

        if (SelectedCanonicalCommission?.PublicMetadata.ViewState ==
                CompanyCommissionPublicViewState.Published &&
            !IsEditingCommissionTermsRevision)
        {
            ShowCommissionTermsRevision();
        }

        await SetActiveOpsTabAsync(PlanTabIndex);
    }

    private static string GetSettlementChipClass(
        CompanyCommissionSettlementState state) =>
        state == CompanyCommissionSettlementState.Satisfied
            ? "trade-orders-publication-chip is-live"
            : "trade-orders-publication-chip is-attention";

    private void ShowCommissionTermsRevision()
    {
        if (!EnsureHostedOrderMutationAvailable())
        {
            return;
        }

        var owner = SelectedCommissionOwner;
        var commission = owner?.Order.CompanyCommission;
        if (owner == null ||
            commission == null ||
            _selectedOrder == null ||
            commission.PublicMetadata.ViewState != CompanyCommissionPublicViewState.Published)
        {
            return;
        }

        _showCommissionTermsRevision = true;
        _commissionTermsRevisionWorkPackage = CreateCanonicalTermsWorkPackage(
            owner.Order,
            commission.CurrentTerms);
        _commissionTermsRevisionBrief = BuildCanonicalCommissionBrief(
            owner.Order,
            commission);
        _commissionTermsRevisionRollbackPlan = null;
        _commissionTermsRevisionDirty = false;
        _commissionTermsRevisionPaymentDirty = false;
        CaptureCommissionTermsRevisionBase(owner, commission);
        _selectedOrder = _commissionTermsRevisionWorkPackage;
        _selectedOrderOutputEditors = TradeRequestedOrderEditorMapper.FromOrder(_selectedOrder);
        _commissionContact = commission.CurrentTerms.ContactInstructions;
        _commissionDeliveryInstructions = commission.CurrentTerms.DeliveryInstructions;
        _activeOpsTab = 0;
    }

    private static TradeOrder CreateCanonicalTermsWorkPackage(
        TradeOrder source,
        CompanyCommissionTermsVersion terms)
    {
        var copy = TradeOrderWorkflow.CopyOrder(source);
        copy.SourceSnapshot.RootItems = terms.Outputs.Select(output =>
            new TradeOrderRootItemSnapshot(
                output.ItemId,
                output.Name,
                output.RequiredQuantity,
                output.MustBeHq,
                EstimatedSaleValue: 0m)).ToArray();
        copy.SourceSnapshot.Materials = terms.Materials.Select(material =>
            new TradeOrderMaterialSnapshot(
                material.ItemId,
                material.Name,
                material.Quantity,
                material.RequiresHq,
                material.UnitCost,
                material.TotalCost,
                terms.PricingEvidence.CostBasis,
                $"Canonical terms v{terms.Version}",
                terms.PricingEvidence.CapturedAtUtc)).ToArray();
        copy.SourceSnapshot.CraftLabor = terms.Payment.CraftSynthCount <= 0
            ? []
            :
            [
                new TradeOrderCraftLaborSnapshot(
                    $"commission-terms:{terms.Version}",
                    terms.Outputs.FirstOrDefault()?.ItemId ?? 0,
                    "Commission craft labor",
                    terms.Outputs.Sum(output => output.RequiredQuantity),
                    terms.Payment.CraftSynthCount)
            ];
        copy.SourceSnapshot.MaterialQuote = terms.PricingEvidence.MaterialQuote;
        copy.SourceSnapshot.Warnings = terms.PricingEvidence.Warnings?.ToArray() ?? [];
        if (terms.PricingEvidence.MaterialQuote != null)
        {
            copy.SourceSnapshot.CostBasis = CommissionCostBasis.ProcurementRoute;
        }
        copy.PaymentSchedule = terms.Payment.Schedule;
        copy.CustomPaymentTerms = terms.Payment.CustomTerms;
        return copy;
    }

    private Task CancelCommissionTermsRevisionAsync() =>
        CancelCommissionTermsRevisionAsync(discardConflict: false);

    private async Task CancelCommissionTermsRevisionAsync(bool discardConflict)
    {
        if (_isCommissionCommandRunning)
        {
            return;
        }

        _isCommissionCommandRunning = true;
        var rollbackFence = CaptureCurrentWorkerPlanFence(
            _commissionTermsRevisionWorkPackage?.CraftPlanId);
        try
        {
            var owner = SelectedCommissionOwner;
            if (discardConflict &&
                owner != null &&
                !await ReconcileLatestCanonicalPlanAsync(owner.Order))
            {
                return;
            }

            var rollback = discardConflict ? null : _commissionTermsRevisionRollbackPlan;
            if (rollback != null)
            {
                if (rollbackFence is not { } ownedFence)
                {
                    Snackbar.Add(
                        "The active plan no longer matches this terms edit, so it was preserved instead of being rolled back.",
                        Severity.Info);
                    return;
                }
                if (!await RestoreStagedProcurementPlanAsync(
                        rollback,
                        ownedFence))
                {
                    return;
                }
            }

            _commissionTermsRevisionWorkPackage = null;
            _commissionTermsRevisionBrief = null;
            _commissionTermsRevisionRollbackPlan = null;
            _commissionTermsRevisionDirty = false;
            _commissionTermsRevisionPaymentDirty = false;
            _showCommissionTermsRevision = false;
            ResetCommissionTermsRevisionBase();

            if (owner != null)
            {
                SelectOrder(owner.Order);
            }
        }
        finally
        {
            _isCommissionCommandRunning = false;
            ScheduleSelectedOrderPlanRestoration();
        }
    }

    private void PrepareCompanyCommissionEditor(TradeOrder order)
    {
        _commissionIdentityRejectionReason = string.Empty;
        _blockRejectedCommissionContact = false;
        _commissionPaymentDecisionReason = string.Empty;
        _commissionPaymentObservation = string.Empty;
        _commissionPaymentRetractionReason = string.Empty;
        _commissionTermsRevisionReason = string.Empty;
        _commissionReturnReason = string.Empty;
        _commissionSettlementObservation = string.Empty;
        _commissionSettlementRetractionReason = string.Empty;
        _commissionSharedComment = string.Empty;
        _showCommissionTermsRevision = false;
        _commissionTermsRevisionWorkPackage = null;
        _commissionTermsRevisionBrief = null;
        _commissionTermsRevisionRollbackPlan = null;
        _commissionTermsRevisionDirty = false;
        _commissionTermsRevisionPaymentDirty = false;
        ResetCommissionTermsRevisionBase();
        var provisional = CommissionOperations.GetForOrder(order.Id)?.Order.CompanyCommission?.ProvisionalCrafter;
        _commissionIdentityCrafterId = provisional == null
            ? order.AssignedCrafterId
            : _crafters.FirstOrDefault(crafter =>
                    !string.IsNullOrWhiteSpace(provisional.LodestoneCharacterId) &&
                    string.Equals(
                        crafter.LodestoneCharacterId,
                        provisional.LodestoneCharacterId,
                        StringComparison.Ordinal))
                ?.Id;
    }

    private CompanyCommissionOutputProgress? GetSelectedOutputProgress(
        Guid lineId) =>
        SelectedCommissionOwner == null
            ? null
            : TradeCommissionOperationsPresentation.GetOutputProgress(
                SelectedCommissionOwner,
                lineId);

    private int? GetSelectedCompanyMaterialQuantity(
        int itemId,
        bool requiresHq)
    {
        var materials = SelectedCanonicalCommission?.CurrentTerms.Materials
            .Where(material =>
                material.ItemId == itemId &&
                material.RequiresHq == requiresHq &&
                material.Responsibility == CommissionMaterialResponsibility.Provided)
            .ToArray();
        return materials is { Length: > 0 }
            ? materials.Sum(material => material.Quantity)
            : null;
    }

    private string FormatCompanyMaterialReadyQuantity(
        int itemId,
        bool requiresHq)
    {
        var quantity = GetSelectedCompanyMaterialQuantity(itemId, requiresHq);
        if (!quantity.HasValue)
        {
            return "-";
        }

        return SelectedCanonicalCommission?.Gates.CompanyMaterials.ReadyAtUtc.HasValue == true
            ? quantity.Value.ToString("N0")
            : "0";
    }

    private string FormatCompanyMaterialReceivedQuantity(
        int itemId,
        bool requiresHq)
    {
        var quantity = GetSelectedCompanyMaterialQuantity(itemId, requiresHq);
        if (!quantity.HasValue)
        {
            return "-";
        }

        return SelectedCanonicalCommission?.Gates.CompanyMaterials.ReceivedAtUtc.HasValue == true
            ? quantity.Value.ToString("N0")
            : "0";
    }

    private string CommissionIdentityCrafterValue
    {
        get => _commissionIdentityCrafterId?.ToString("D") ?? string.Empty;
        set => _commissionIdentityCrafterId = ParseNullableGuid(value);
    }

    private IReadOnlyList<CompactSelectOption> GetCommissionCrafterOptions() =>
    [
        new(string.Empty, "Create from provisional identity"),
        .. _crafters.Select(crafter =>
            new CompactSelectOption(crafter.Id.ToString("D"), crafter.DisplayName))
    ];

    private async Task ConfirmCommissionIdentityAsync()
    {
        if (!EnsureHostedOrderMutationAvailable())
        {
            return;
        }

        var owner = SelectedCommissionOwner;
        var provisional = owner?.Order.CompanyCommission?.ProvisionalCrafter;
        if (owner == null || provisional == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(provisional.LodestoneCharacterId))
        {
            Snackbar.Add(
                "Lodestone character existence must be verified before ownership can be confirmed.",
                Severity.Warning);
            return;
        }

        _isCommissionCommandRunning = true;
        try
        {
            var crafter = BuildConfirmedCommissionCrafter(
                owner.Order.CompanyProfileId,
                provisional);
            if (crafter == null)
            {
                return;
            }

            var result = await CommissionOperations.ConfirmIdentityAsync(
                owner,
                crafter,
                provisional.LodestoneCharacterId);
            ApplyCommissionResult(result, "Crafter identity confirmed");
            if (result.Success && _companyProfile != null)
            {
                _crafters = (await TradeOperationsPersistence.LoadCraftersAsync(
                    _companyProfile.Id)).ToList();
            }
        }
        finally
        {
            _isCommissionCommandRunning = false;
        }
    }

    private Task RejectCommissionClaimAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.RejectClaimAsync(
                owner,
                _commissionIdentityRejectionReason,
                _blockRejectedCommissionContact),
            "Claim rejected and released",
            copyClaimUrl: true);

    private Task DecideCommissionPaymentPolicyAsync(bool accepted)
    {
        var request = SelectedPaymentPolicyRequest;
        if (request == null || SelectedCommissionOwner == null)
        {
            return Task.CompletedTask;
        }

        return RunCommissionCommandAsync(
            owner => CommissionOperations.DecidePaymentPolicyAsync(
                owner,
                request,
                accepted,
                _commissionPaymentDecisionReason),
            accepted
                ? "Payment timing accepted; crafter acknowledgement is now required"
                : "Payment timing request refused");
    }

    private Task RecordCommissionPaymentAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.RecordPaymentAsync(
                owner,
                _commissionPaymentObservation),
            "Payment sent recorded; awaiting crafter confirmation");

    private Task RetractCommissionPaymentAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.RetractPaymentAsync(
                owner,
                _commissionPaymentRetractionReason),
            "Commissioner payment confirmation retracted");

    private async Task AmendCommissionTermsAsync()
    {
        if (!EnsureHostedOrderMutationAvailable())
        {
            return;
        }

        var owner = SelectedCommissionOwner;
        var commission = owner?.Order.CompanyCommission;
        if (owner == null ||
            commission == null ||
            _selectedOrder == null ||
            string.IsNullOrWhiteSpace(_commissionTermsRevisionReason))
        {
            return;
        }

        if (HasCommissionTermsRevisionConflict)
        {
            Snackbar.Add(
                "Canonical terms changed while this revision was being edited. Rebase onto the latest terms or discard the local changes before publishing.",
                Severity.Warning);
            return;
        }

        var reason = _commissionTermsRevisionReason.Trim();
        var confirmed = await DialogService.ShowMessageBox(
            "Create Terms Revision",
            $"Create terms v{commission.CurrentTermsVersion + 1} from the current work package and pricing? The crafter must acknowledge it before work can continue.",
            yesText: "Create Revision",
            cancelText: "Cancel");
        if (confirmed != true)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var workPackage = _commissionTermsRevisionWorkPackage ?? _selectedOrder;
        var brief = _commissionTermsRevisionBrief ?? BuildCommissionBrief(
            workPackage,
            TradeCommissionPaymentSummary.FromOrder(
                workPackage,
                GetSelectedOrderResponsibilityProjection(),
                GetSelectedOrderEffectivePaymentPolicy()));
        brief.Contact = _commissionContact?.Trim() ?? string.Empty;
        brief.DeliveryInstructions =
            _commissionDeliveryInstructions?.Trim() ?? string.Empty;
        var terms = TradeCompanyCommissionMigrationService.CreateTermsRevision(
            workPackage,
            brief,
            checked(commission.CurrentTermsVersion + 1),
            reason,
            now);
        _isCommissionCommandRunning = true;
        try
        {
            var result = await CommissionOperations.AmendTermsAsync(
                owner,
                terms,
                workPackage,
                reason);
            ApplyCommissionResult(result, $"Terms v{terms.Version} created");
            if (result.Success)
            {
                _commissionTermsRevisionReason = string.Empty;
                _commissionTermsRevisionDirty = false;
                _commissionTermsRevisionPaymentDirty = false;
                _showCommissionTermsRevision = false;
                ResetCommissionTermsRevisionBase();
            }
        }
        finally
        {
            _isCommissionCommandRunning = false;
        }
    }

    private async Task<bool> UpdateCanonicalDraftAsync(
        TradeOrder workPackage,
        CommissionBriefDocument brief,
        string? successMessage,
        Action<TradeCommissionOperatorResult>? observeResult = null)
    {
        var owner = SelectedCommissionOwner;
        var commission = owner?.Order.CompanyCommission;
        if (owner == null || commission == null)
        {
            return false;
        }

        if (IsEditingCommissionTermsRevision)
        {
            _commissionTermsRevisionWorkPackage = TradeOrderWorkflow.CopyOrder(workPackage);
            _commissionTermsRevisionBrief = brief;
            _commissionTermsRevisionDirty = true;
            _selectedOrder = _commissionTermsRevisionWorkPackage;
            _selectedOrderOutputEditors = TradeRequestedOrderEditorMapper.FromOrder(_selectedOrder);
            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                Snackbar.Add(successMessage.Replace("commission draft", "terms revision"), Severity.Success);
            }
            return true;
        }

        if (!EnsureHostedOrderMutationAvailable())
        {
            return false;
        }

        if (!CanEditCanonicalDraft)
        {
            Snackbar.Add(
                "Start a terms revision before changing a published commission.",
                Severity.Warning);
            return false;
        }

        var terms = TradeCompanyCommissionMigrationService.CreateDraftTerms(
            workPackage,
            brief,
            commission.CurrentTerms,
            DateTime.UtcNow);
        _isCommissionCommandRunning = true;
        try
        {
            var result = await CommissionOperations.UpdateDraftAsync(
                owner,
                terms,
                workPackage);
            observeResult?.Invoke(result);
            ApplyCommissionResult(result, successMessage);
            return result.Success;
        }
        finally
        {
            _isCommissionCommandRunning = false;
        }
    }

    private Task MarkCommissionMaterialsReadyAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.MarkCompanyMaterialsReadyAsync(owner),
            "Complete company material bundle marked ready");

    private Task ReturnCommissionToWorkAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.ReturnToWorkAsync(
                owner,
                _commissionReturnReason),
            "Delivery returned to work");

    private Task AcceptCommissionDeliveryAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.AcceptDeliveryAsync(owner),
            "Complete delivery accepted");

    private Task RecordCommissionSettlementAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.RecordSettlementAsync(
                owner,
                _commissionSettlementObservation),
            "Final payment marked sent");

    private Task RetractCommissionSettlementAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.RetractSettlementAsync(
                owner,
                _commissionSettlementRetractionReason),
            "Final-payment confirmation retracted");

    private Task RevokeCanonicalCommissionPublicationAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.RevokePublicationAsync(owner),
            "Commission publication revoked");

    private async Task ReopenCanonicalCommissionAsync()
    {
        if (string.IsNullOrWhiteSpace(_commissionResolutionNote))
        {
            Snackbar.Add(
                "Describe how the cancellation or participant withdrawal was resolved before reopening.",
                Severity.Warning);
            return;
        }

        var note = _commissionResolutionNote;
        await RunCommissionCommandAsync(
            owner => CommissionOperations.ReopenAsync(owner, note),
            "Commission reopened and queued for a fresh Discord post");
        if (SelectedCommissionOwner?.Order.Status == TradeOrderStatus.ReadyToAssign)
        {
            _commissionResolutionNote = string.Empty;
        }
    }

    private Task AddCommissionCommentAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.AddCommentAsync(
                owner,
                _commissionSharedComment),
            "Shared commission comment added");

    private Task RecoverCommissionParticipantAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.RecoverParticipantAsync(owner),
            "One-time recovery link issued",
            copyRecoveryUrl: true);

    private Task CopyCommissionClaimLinkAsync() =>
        RunCommissionCommandAsync(
            owner => CommissionOperations.IssueClaimLinkAsync(owner),
            "Fresh claim link issued",
            copyClaimUrl: true);

    private async Task RetryCommissionNotificationAsync(
        TradeDiscordNotificationDiagnostic diagnostic)
    {
        if (!EnsureHostedOrderMutationAvailable())
        {
            return;
        }

        var owner = SelectedCommissionOwner;
        if (owner == null || !diagnostic.CanRetry)
        {
            return;
        }

        _retryingCommissionDiagnosticId = diagnostic.DiagnosticId;
        try
        {
            var error = await CommissionOperations.RetryNotificationDiagnosticAsync(
                owner,
                diagnostic.DiagnosticId);
            Snackbar.Add(
                error ?? "Discord notification requeued",
                error == null ? Severity.Success : Severity.Error);
        }
        finally
        {
            _retryingCommissionDiagnosticId = null;
        }
    }

    private async Task RunCommissionCommandAsync(
        Func<CompanyCommissionOwnerProjection, Task<TradeCommissionOperatorResult>> command,
        string successMessage,
        bool copyRecoveryUrl = false,
        bool copyClaimUrl = false)
    {
        if (!EnsureHostedOrderMutationAvailable())
        {
            return;
        }

        var owner = SelectedCommissionOwner;
        if (owner == null || _isCommissionCommandRunning)
        {
            return;
        }

        _isCommissionCommandRunning = true;
        try
        {
            var result = await command(owner);
            ApplyCommissionResult(result, successMessage);
            if (result.Success &&
                copyRecoveryUrl &&
                !string.IsNullOrWhiteSpace(result.RecoveryUrl))
            {
                await CopyTextToClipboardAsync(
                    result.RecoveryUrl,
                    "One-time recovery link copied");
            }
            if (result.Success &&
                copyClaimUrl &&
                !string.IsNullOrWhiteSpace(result.ClaimUrl))
            {
                await CopyTextToClipboardAsync(
                    result.ClaimUrl,
                    "Claim link copied");
            }
        }
        finally
        {
            _isCommissionCommandRunning = false;
        }
    }

    private void ApplyCommissionResult(
        TradeCommissionOperatorResult result,
        string? successMessage)
    {
        if (!result.Success)
        {
            if (result.HostCommitted && result.Projection != null)
            {
                var current = GetCurrentCommissionProjection(result.Projection);
                if (current != null)
                {
                    ReplaceSelectedCommissionProjection(current);
                }
            }
            Snackbar.Add(
                result.Message ?? "The commissioner action failed.",
                Severity.Error);
            return;
        }
        var committed = result.Projection == null
            ? null
            : GetCurrentCommissionProjection(result.Projection);
        if (committed == null)
        {
            if (_selectedOrder?.CompanyCommission == null &&
                _selectedOrder?.Id == result.Projection?.Order.Id)
            {
                if (!string.IsNullOrWhiteSpace(successMessage))
                {
                    Snackbar.Add(successMessage, Severity.Success);
                }
                return;
            }
            Snackbar.Add(
                "The hosted order authority changed before the committed order could be displayed.",
                Severity.Error);
            return;
        }

        ReplaceSelectedCommissionProjection(committed);
        if (!string.IsNullOrWhiteSpace(successMessage))
        {
            Snackbar.Add(successMessage, Severity.Success);
        }
    }

    private CompanyCommissionOwnerProjection? GetCurrentCommissionProjection(
        CompanyCommissionOwnerProjection candidate)
    {
        var current = CommissionOperations.GetForOrder(candidate.Order.Id);
        return current != null &&
               current.Order.CompanyProfileId == candidate.Order.CompanyProfileId &&
               current.ObjectRevision.Value >= candidate.ObjectRevision.Value
            ? current
            : null;
    }

    private void ReplaceSelectedCommissionProjection(CompanyCommissionOwnerProjection projection)
    {
        var activeTab = _activeOpsTab;
        _orders = _orders
            .Where(order => order.Id != projection.Order.Id)
            .Append(projection.Order)
            .ToList();
        SelectOrder(projection.Order);
        _activeOpsTab = activeTab;
    }

    private TradeCrafterProfile? BuildConfirmedCommissionCrafter(
        Guid companyProfileId,
        CompanyCommissionProvisionalCrafter provisional)
    {
        TradeCrafterProfile crafter;
        if (_commissionIdentityCrafterId is { } selectedId)
        {
            var existing = _crafters.FirstOrDefault(item => item.Id == selectedId);
            if (existing == null)
            {
                Snackbar.Add(
                    "The selected company crafter no longer exists.",
                    Severity.Error);
                return null;
            }
            if (!string.IsNullOrWhiteSpace(existing.LodestoneCharacterId) &&
                !string.Equals(
                    existing.LodestoneCharacterId,
                    provisional.LodestoneCharacterId,
                    StringComparison.Ordinal))
            {
                Snackbar.Add(
                    "The selected crafter belongs to a different Lodestone character.",
                    Severity.Error);
                return null;
            }

            crafter = CopyCrafterProfile(existing);
        }
        else
        {
            var collision = _crafters.FirstOrDefault(
                item => item.Id == provisional.ProvisionalCrafterId);
            if (collision != null)
            {
                Snackbar.Add(
                    "The provisional identity collides with an existing company crafter.",
                    Severity.Error);
                return null;
            }

            crafter = new TradeCrafterProfile
            {
                Id = provisional.ProvisionalCrafterId,
                CompanyProfileId = companyProfileId,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        crafter.DisplayName = provisional.CharacterName.Trim();
        crafter.WorldName = provisional.HomeWorld.Trim();
        crafter.ContactHandle = provisional.ContactValue.Trim();
        crafter.LodestoneCharacterId = provisional.LodestoneCharacterId?.Trim();
        crafter.LodestoneProfileUrl = provisional.LodestoneProfileUrl?.Trim();
        crafter.LodestoneLastSyncedAtUtc = DateTime.UtcNow;
        crafter.UpdatedAtUtc = DateTime.UtcNow;
        return crafter;
    }

    private static TradeCrafterProfile CopyCrafterProfile(
        TradeCrafterProfile source) =>
        new()
        {
            Id = source.Id,
            CompanyProfileId = source.CompanyProfileId,
            DisplayName = source.DisplayName,
            Alias = source.Alias,
            ContactHandle = source.ContactHandle,
            DiscordHandle = source.DiscordHandle,
            SocialProfileUrl = source.SocialProfileUrl,
            WorldName = source.WorldName,
            DataCenter = source.DataCenter,
            LodestoneCharacterId = source.LodestoneCharacterId,
            LodestoneProfileUrl = source.LodestoneProfileUrl,
            LodestoneLastSyncedAtUtc = source.LodestoneLastSyncedAtUtc,
            LodestoneAvatarUrl = source.LodestoneAvatarUrl,
            LodestonePortraitUrl = source.LodestonePortraitUrl,
            LodestoneFreeCompanyName = source.LodestoneFreeCompanyName,
            LodestoneRace = source.LodestoneRace,
            LodestoneClan = source.LodestoneClan,
            LodestoneGender = source.LodestoneGender,
            AvailabilityNotes = source.AvailabilityNotes,
            PaymentNotes = source.PaymentNotes,
            OperatorNotes = source.OperatorNotes,
            JobLevels = source.JobLevels.ToArray(),
            RemoteId = source.RemoteId,
            SyncState = source.SyncState,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc
        };

    private static string FormatCanonicalCommissionState(
        TradeOrder order,
        TradeCompanyCommission commission) =>
        FormatWorkbenchStatus(order, commission);

    private string FormatCanonicalCommissionCrafter(
        TradeOrder order,
        TradeCompanyCommission commission) =>
        commission.ActiveClaim == null
            ? "Unassigned"
            : commission.Gates.Identity.State == CompanyCommissionClearanceState.Pending
                ? "Pending identity confirmation"
                : FormatAssignedCrafter(order);

    private static string FormatCanonicalCommissionClaimant(
        TradeCompanyCommission commission) =>
        commission.ProvisionalCrafter is { } provisional
            ? $"{provisional.CharacterName} @ {provisional.HomeWorld}"
            : commission.ActiveClaim == null
                ? "Open claim slot"
                : "Confirmed company crafter";

    private static string GetCompanyCommissionSharingChipClass(
        TradeOrder order,
        TradeCompanyCommission commission) =>
        order.Status is TradeOrderStatus.Completed or TradeOrderStatus.Canceled ||
        commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Revoked
            ? "trade-orders-publication-chip"
            : commission.ActiveClaim == null
                ? "trade-orders-publication-chip is-live"
                : "trade-orders-publication-chip is-attention";

    private static string FormatPaymentConfirmationProgress(
        CompanyCommissionPaymentClearance payment) =>
        payment.State == CompanyCommissionClearanceState.NotRequired
            ? "Not required"
            : payment.State == CompanyCommissionClearanceState.Satisfied &&
              payment.ConfirmationCount == 0
                ? "Legacy cleared"
                : $"{payment.ConfirmationCount} of 2 confirmed";

    private static string FormatCommissionGate(
        CompanyCommissionClearanceState state) =>
        state switch
        {
            CompanyCommissionClearanceState.NotRequired => "Not required",
            CompanyCommissionClearanceState.Satisfied => "Cleared",
            _ => "Pending"
        };

    private static string GetCommissionGateChipClass(
        CompanyCommissionClearanceState state) =>
        state == CompanyCommissionClearanceState.Satisfied
            ? "trade-orders-publication-chip is-live"
            : state == CompanyCommissionClearanceState.Pending
                ? "trade-orders-publication-chip is-attention"
                : "trade-orders-publication-chip";

    private static string FormatCommissionPaymentSchedule(
        CompanyCommissionPaymentTerms payment) =>
        payment.Schedule == CompanyCommissionPaymentSchedule.Custom
            ? payment.CustomTerms ?? "Custom timing"
            : payment.Schedule == CompanyCommissionPaymentSchedule.OnDelivery
                ? "On delivery"
                : "Advance";

    private static string FormatPaymentRequestSchedule(
        PendingPaymentPolicyRequest request) =>
        request.RequestedSchedule switch
        {
            CompanyCommissionPaymentSchedule.OnDelivery => "payment on delivery",
            CompanyCommissionPaymentSchedule.Advance => "advance payment",
            CompanyCommissionPaymentSchedule.Custom =>
                request.RequestedCustomTerms ?? "custom payment timing",
            _ => "an unreadable payment schedule"
        };

    private static string FormatCommissionActivity(
        CompanyCommissionActivityKind kind) =>
        kind switch
        {
            CompanyCommissionActivityKind.DraftUpdated => "Draft updated",
            CompanyCommissionActivityKind.CommissionOpened => "Commission opened",
            CompanyCommissionActivityKind.ClaimAccepted => "Claim reserved",
            CompanyCommissionActivityKind.ClaimRejected => "Claim rejected",
            CompanyCommissionActivityKind.ClaimReleased => "Claim released",
            CompanyCommissionActivityKind.ClaimRecovered => "Claim access recovered",
            CompanyCommissionActivityKind.ProvisionalIdentitySubmitted => "Identity submitted",
            CompanyCommissionActivityKind.ProvisionalIdentityConfirmed => "Identity confirmed",
            CompanyCommissionActivityKind.ProvisionalIdentityRejected => "Identity rejected",
            CompanyCommissionActivityKind.PaymentPolicyChangeRequested => "Payment timing requested",
            CompanyCommissionActivityKind.PaymentPolicyChangeAccepted => "Payment timing accepted",
            CompanyCommissionActivityKind.PaymentPolicyChangeRefused => "Payment timing refused",
            CompanyCommissionActivityKind.TermsAcknowledged => "Terms acknowledged",
            CompanyCommissionActivityKind.PaymentClearanceRecorded => "Payment observed",
            CompanyCommissionActivityKind.TermsAmended => "Terms revised",
            CompanyCommissionActivityKind.PaymentSentRecorded => "Payment sent",
            CompanyCommissionActivityKind.PaymentReceivedConfirmed => "Advance payment receipt confirmed",
            CompanyCommissionActivityKind.PaymentAttestationRetracted => "Payment confirmation retracted",
            CompanyCommissionActivityKind.CompanyMaterialsReady => "Materials ready",
            CompanyCommissionActivityKind.CompanyMaterialsReceived => "Materials received",
            CompanyCommissionActivityKind.WorkClearanceAchieved => "Cleared to work",
            CompanyCommissionActivityKind.ProgressReported => "Progress updated",
            CompanyCommissionActivityKind.DeliveryHandoffRecorded => "Sent for delivery review",
            CompanyCommissionActivityKind.DeliveryReadinessWithdrawn => "Delivery readiness withdrawn",
            CompanyCommissionActivityKind.DeliveryReadinessDeclared => "Sent for delivery review",
            CompanyCommissionActivityKind.DeliveryReturnedToWork => "Returned to work",
            CompanyCommissionActivityKind.DeliveryAccepted => "Delivery accepted",
            CompanyCommissionActivityKind.SettlementRecorded => "Settlement recorded",
            CompanyCommissionActivityKind.SettlementPaymentSentRecorded => "Final payment sent",
            CompanyCommissionActivityKind.SettlementPaymentReceivedConfirmed => "Final payment receipt confirmed",
            CompanyCommissionActivityKind.SettlementPaymentAttestationRetracted => "Final-payment confirmation retracted",
            CompanyCommissionActivityKind.CommentAdded => "Comment",
            CompanyCommissionActivityKind.CommissionCanceled => "Commission canceled",
            CompanyCommissionActivityKind.ClaimResolutionRequired => "Company resolution required",
            CompanyCommissionActivityKind.CommissionReopened => "Commission reopened",
            CompanyCommissionActivityKind.CommissionClosed => "Commission closed",
            CompanyCommissionActivityKind.CommissionPublicationRevoked => "Public access revoked",
            CompanyCommissionActivityKind.ParticipantRecoveryIssued => "Recovery access issued",
            CompanyCommissionActivityKind.ParticipantRecoveryRedeemed => "Recovery access redeemed",
            CompanyCommissionActivityKind.MigratedFromTradeOrder => "Commission created",
            CompanyCommissionActivityKind.MigratedTradeOrderHistory => "Planning history imported",
            _ => "Commission updated"
        };
}
