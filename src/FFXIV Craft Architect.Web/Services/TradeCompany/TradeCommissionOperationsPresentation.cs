using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public static class TradeCommissionOperationsPresentation
{
    public const string OpenAttention = "open";
    public const string ClaimAttention = "claim";
    public const string PreWorkAttention = "prework";
    public const string WorkAttention = "work";
    public const string DeliveryAttention = "delivery";
    public const string ResolutionAttention = "resolution";
    public const string SyncAttention = "sync";

    public static bool IsArchivedForAttention(
        TradeOrder order,
        CompanyCommissionOwnerProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(order);

        var presentedOrder = projection?.Order ?? order;

        if (presentedOrder.Status == TradeOrderStatus.Canceled)
        {
            return true;
        }

        if (presentedOrder.CompanyCommission is { } commission)
        {
            return commission.IsClosed(presentedOrder.Status);
        }

        return TradeOrderStatusWorkflow.IsArchived(presentedOrder.Status);
    }

    public static string GetAttentionGroup(CompanyCommissionOwnerProjection projection) =>
        GetAttentionGroup(projection.Order);

    public static string GetAttentionGroup(TradeOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var commission = RequireCommission(order);
        if (order.Status == TradeOrderStatus.ResolutionRequired || commission.ManualResolution != null)
        {
            return ResolutionAttention;
        }

        if (TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            return DeliveryAttention;
        }

        if (order.Status == TradeOrderStatus.AwaitingDelivery)
        {
            return DeliveryAttention;
        }

        if (order.Status == TradeOrderStatus.InProgress)
        {
            return WorkAttention;
        }

        if (commission.ActiveClaim == null)
        {
            return OpenAttention;
        }

        if (commission.Gates.Identity.State == CompanyCommissionClearanceState.Pending)
        {
            return ClaimAttention;
        }

        if (!commission.ClearedToWork)
        {
            return PreWorkAttention;
        }

        if (commission.DeliveryReadiness.IsReady ||
            order.Status == TradeOrderStatus.AwaitingDelivery ||
            order.Status == TradeOrderStatus.Completed &&
            commission.SettlementState != CompanyCommissionSettlementState.Satisfied)
        {
            return DeliveryAttention;
        }

        if (order.Status == TradeOrderStatus.Assigned)
        {
            return WorkAttention;
        }

        return WorkAttention;
    }

    public static PendingPaymentPolicyRequest? GetPendingPaymentPolicyRequest(
        CompanyCommissionOwnerProjection projection)
    {
        var activity = RequireCommission(projection).Activity;
        var latestResolutionRevision = activity
            .Where(item => item.Kind is
                CompanyCommissionActivityKind.PaymentPolicyChangeAccepted or
                CompanyCommissionActivityKind.PaymentPolicyChangeRefused)
            .Select(item => item.CommissionRevision)
            .DefaultIfEmpty(0)
            .Max();
        var requested = activity
            .Where(item =>
                item.Kind == CompanyCommissionActivityKind.PaymentPolicyChangeRequested &&
                item.CommissionRevision > latestResolutionRevision)
            .OrderByDescending(item => item.CommissionRevision)
            .FirstOrDefault();
        if (requested == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(requested.PayloadJson))
        {
            return new PendingPaymentPolicyRequest(
                requested.EventId,
                null,
                null,
                requested.Comment,
                "The payment request is missing its structured schedule.");
        }

        try
        {
            using var document = JsonDocument.Parse(requested.PayloadJson);
            var root = document.RootElement;
            var scheduleText = ReadString(root, "requestedSchedule");
            var customTerms = ReadString(root, "requestedCustomTerms");
            var reason = ReadString(root, "reason") ?? requested.Comment;
            return new PendingPaymentPolicyRequest(
                requested.EventId,
                Enum.TryParse<CompanyCommissionPaymentSchedule>(
                    scheduleText,
                    ignoreCase: true,
                    out var schedule)
                    ? schedule
                    : null,
                customTerms,
                reason,
                scheduleText == null
                    ? "The payment request is missing its structured schedule."
                    : null);
        }
        catch (JsonException)
        {
            return new PendingPaymentPolicyRequest(
                requested.EventId,
                null,
                null,
                requested.Comment,
                "The payment request payload is invalid.");
        }
    }

    public static CompanyCommissionOutputProgress? GetOutputProgress(
        CompanyCommissionOwnerProjection projection,
        Guid lineId)
    {
        var commission = RequireCommission(projection);
        return commission.OutputProgress
            .Where(progress => progress.LineId == lineId)
            .OrderByDescending(progress => progress.UpdatedAtUtc)
            .FirstOrDefault();
    }

    private static TradeCompanyCommission RequireCommission(
        CompanyCommissionOwnerProjection projection) =>
        RequireCommission(projection.Order);

    private static TradeCompanyCommission RequireCommission(TradeOrder order) =>
        order.CompanyCommission ??
        throw new InvalidOperationException(
            "The presented order does not contain a company commission.");

    private static string? ReadString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ToString();
        }

        return null;
    }
}

public sealed record PendingPaymentPolicyRequest(
    Guid EventId,
    CompanyCommissionPaymentSchedule? RequestedSchedule,
    string? RequestedCustomTerms,
    string? Reason,
    string? Error);
