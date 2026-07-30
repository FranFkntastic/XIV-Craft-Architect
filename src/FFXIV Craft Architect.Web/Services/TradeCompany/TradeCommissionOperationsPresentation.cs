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
    public const string SyncAttention = "sync";

    public static string GetAttentionGroup(CompanyCommissionOwnerProjection projection)
    {
        var order = projection.Order;
        var commission = RequireCommission(projection);
        if (TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            return DeliveryAttention;
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

        return WorkAttention;
    }

    public static string GetNextAction(CompanyCommissionOwnerProjection projection)
    {
        var order = projection.Order;
        var commission = RequireCommission(projection);
        if (commission.ActiveClaim == null)
        {
            return "Awaiting claim";
        }

        if (commission.Gates.Identity.State == CompanyCommissionClearanceState.Pending)
        {
            return "Review identity";
        }

        if (GetPendingPaymentPolicyRequest(projection) != null)
        {
            return "Review payment timing";
        }

        if (commission.Gates.Payment.State == CompanyCommissionClearanceState.Pending)
        {
            return "Record payment";
        }

        if (commission.Gates.CompanyMaterials.State == CompanyCommissionClearanceState.Pending)
        {
            return commission.Gates.CompanyMaterials.ReadyAtUtc.HasValue
                ? "Awaiting receipt"
                : "Prepare materials";
        }

        if (commission.DeliveryReadiness.IsReady ||
            order.Status == TradeOrderStatus.AwaitingDelivery)
        {
            return "Review delivery";
        }

        if (order.Status == TradeOrderStatus.Completed &&
            commission.SettlementState != CompanyCommissionSettlementState.Satisfied)
        {
            return "Record settlement";
        }

        return commission.ClearedToWork ? "Work in progress" : "Clear prerequisites";
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
        projection.Order.CompanyCommission ??
        throw new InvalidOperationException(
            "The authenticated owner projection does not contain a company commission.");

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
