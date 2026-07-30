using FFXIV_Craft_Architect.Core.Models;
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
    private string _commissionReturnReason = string.Empty;
    private string _commissionSettlementObservation = string.Empty;
    private string _commissionSharedComment = string.Empty;
    private Guid? _commissionIdentityCrafterId;
    private Guid? _retryingCommissionDiagnosticId;
    private bool _isCommissionCommandRunning;

    private CompanyCommissionOwnerProjection? SelectedCommissionOwner =>
        _selectedOrder == null
            ? null
            : CommissionOperations.GetForOrder(_selectedOrder.Id);

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
        SelectedCommissionOwner?.Order.CompanyCommission;

    private PendingPaymentPolicyRequest? SelectedPaymentPolicyRequest =>
        SelectedCommissionOwner == null
            ? null
            : TradeCommissionOperationsPresentation.GetPendingPaymentPolicyRequest(
                SelectedCommissionOwner);

    private bool HasCanonicalCommission =>
        _selectedOrder?.CompanyCommission != null;

    private void PrepareCompanyCommissionEditor(TradeOrder order)
    {
        _commissionIdentityRejectionReason = string.Empty;
        _blockRejectedCommissionContact = false;
        _commissionPaymentDecisionReason = string.Empty;
        _commissionPaymentObservation = string.Empty;
        _commissionReturnReason = string.Empty;
        _commissionSettlementObservation = string.Empty;
        _commissionSharedComment = string.Empty;
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
            var crafterId = _commissionIdentityCrafterId ??
                provisional.ProvisionalCrafterId;
            var result = await CommissionOperations.ConfirmIdentityAsync(
                owner,
                crafterId,
                provisional.LodestoneCharacterId);
            ApplyCommissionResult(result, "Crafter identity confirmed");
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
            "Payment observation recorded");

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
            "Settlement observation recorded");

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
        string successMessage)
    {
        if (!result.Success || result.Projection == null)
        {
            Snackbar.Add(
                result.Message ?? "The commissioner action failed.",
                Severity.Error);
            return;
        }

        var activeTab = _activeOpsTab;
        _orders = _orders
            .Where(order => order.Id != result.Projection.Order.Id)
            .Append(result.Projection.Order)
            .ToList();
        SelectOrder(result.Projection.Order);
        _activeOpsTab = activeTab;
        Snackbar.Add(successMessage, Severity.Success);
    }

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
            CompanyCommissionActivityKind.ProvisionalIdentitySubmitted => "Identity submitted",
            CompanyCommissionActivityKind.ProvisionalIdentityConfirmed => "Identity confirmed",
            CompanyCommissionActivityKind.ProvisionalIdentityRejected => "Identity rejected",
            CompanyCommissionActivityKind.PaymentPolicyChangeRequested => "Payment timing requested",
            CompanyCommissionActivityKind.PaymentPolicyChangeAccepted => "Payment timing accepted",
            CompanyCommissionActivityKind.PaymentPolicyChangeRefused => "Payment timing refused",
            CompanyCommissionActivityKind.PaymentClearanceRecorded => "Payment observed",
            CompanyCommissionActivityKind.CompanyMaterialsReady => "Materials ready",
            CompanyCommissionActivityKind.CompanyMaterialsReceived => "Materials received",
            CompanyCommissionActivityKind.DeliveryReadinessDeclared => "Ready for delivery",
            CompanyCommissionActivityKind.DeliveryReturnedToWork => "Returned to work",
            CompanyCommissionActivityKind.DeliveryAccepted => "Delivery accepted",
            CompanyCommissionActivityKind.SettlementRecorded => "Settlement recorded",
            CompanyCommissionActivityKind.CommentAdded => "Comment",
            _ => kind.ToString()
        };
}
