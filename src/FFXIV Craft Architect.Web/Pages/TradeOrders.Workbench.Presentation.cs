using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Pages;

public sealed record TradeOrderCenterOutputPresentation(
    string Name,
    int RequiredQuantity,
    bool MustBeHq,
    int CompletedQuantity,
    int ReadyQuantity);

public sealed record TradeOrderCenterBlockerPresentation(
    string Label,
    string Detail);

public sealed record TradeOrderProgressStepPresentation(
    string Label,
    string State,
    bool IsComplete,
    bool IsCurrent,
    bool OpensPlan = false);

public sealed record TradeOrderCenterOverviewPresentation(
    string Title,
    string Status,
    string Client,
    string Crafter,
    int TermsVersion,
    IReadOnlyList<TradeOrderCenterOutputPresentation> Outputs,
    IReadOnlyList<TradeOrderCenterBlockerPresentation> Blockers,
    IReadOnlyList<TradeOrderProgressStepPresentation> Progress,
    bool CanReviseTerms);

public partial class TradeOrders
{
    private const int WorkTabIndex = 0;
    private const int PlanTabIndex = 1;
    private const int HistoryTabIndex = 2;
    private const int ShareTabIndex = 3;

    // Compatibility aliases keep existing command handlers pointed at the stable
    // four-tab workbench while their behavior remains unchanged.
    private const int PaymentTabIndex = WorkTabIndex;
    private const int ProcurementTabIndex = PlanTabIndex;
    private const int TimelineTabIndex = HistoryTabIndex;
    private const int SharingTabIndex = ShareTabIndex;

    private HostedOrderProjectionSnapshot? SelectedHostedOrderSnapshot =>
        _selectedOrder == null
            ? null
            : HostedOrders.Get(_selectedOrder.Id) is { Deleted: false, Order: not null } snapshot
                ? snapshot
                : null;

    private TradeOrderCenterOverviewPresentation? BuildSelectedOrderCenterOverview()
    {
        var snapshot = SelectedHostedOrderSnapshot;
        var owner = snapshot?.OwnerProjection;
        var order = owner?.Order;
        var commission = owner?.Order.CompanyCommission;
        if (order == null || commission == null)
        {
            return null;
        }

        var outputs = commission.CurrentTerms.Outputs
            .Select(output =>
            {
                var progress = commission.OutputProgress.FirstOrDefault(item => item.LineId == output.LineId);
                return new TradeOrderCenterOutputPresentation(
                    output.Name,
                    output.RequiredQuantity,
                    output.MustBeHq,
                    progress?.CompletedQuantity ?? 0,
                    progress?.ReadyQuantity ?? 0);
            })
            .ToArray();

        var blockers = BuildCenterBlockers(commission);
        return new TradeOrderCenterOverviewPresentation(
            order.Title,
            FormatWorkbenchStatus(order, commission),
            FormatCanonicalCommissionClaimant(commission),
            FormatCanonicalCommissionCrafter(order, commission),
            commission.CurrentTermsVersion,
            outputs,
            blockers,
            BuildCenterProgress(order, commission),
            !TradeOrderStatusWorkflow.IsArchived(order.Status) &&
            commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Published &&
            !IsEditingCommissionTermsRevision);
    }

    private static IReadOnlyList<TradeOrderCenterBlockerPresentation> BuildCenterBlockers(
        TradeCompanyCommission commission)
    {
        var blockers = new List<TradeOrderCenterBlockerPresentation>();
        if (commission.Gates.Identity.State == CompanyCommissionClearanceState.Pending)
        {
            blockers.Add(new("Identity", "Company roster confirmation required"));
        }
        if (commission.Gates.Payment.State == CompanyCommissionClearanceState.Pending)
        {
            blockers.Add(new(
                "Payment",
                $"{commission.Gates.Payment.ConfirmationCount} of 2 confirmations"));
        }
        if (commission.Gates.CompanyMaterials.State == CompanyCommissionClearanceState.Pending)
        {
            var provided = commission.CurrentTerms.Materials
                .Where(material => material.Responsibility == CommissionMaterialResponsibility.Provided)
                .ToArray();
            var detail = provided.Length switch
            {
                0 => "Company material handoff pending",
                1 => $"{provided[0].Name} x{provided[0].Quantity:N0} not handed off",
                _ => $"{provided.Length:N0} company-provided material lines not handed off"
            };
            blockers.Add(new("Company materials", detail));
        }
        return blockers;
    }

    private static IReadOnlyList<TradeOrderProgressStepPresentation> BuildCenterProgress(
        TradeOrder order,
        TradeCompanyCommission commission)
    {
        var claimed = commission.ActiveClaim != null;
        var planned = commission.CurrentTerms.Outputs.Count > 0;
        var cleared = commission.ClearedToWork;
        var crafting = order.Status is TradeOrderStatus.InProgress or
            TradeOrderStatus.AwaitingDelivery or
            TradeOrderStatus.Completed;
        var delivered = order.Status == TradeOrderStatus.Completed;
        var waitingToStart = claimed && !cleared && order.Status == TradeOrderStatus.Assigned;

        return
        [
            new("Requested", "Done", commission.CurrentTerms.Outputs.Count > 0, false),
            new("Claimed", claimed ? "Done" : "Next", claimed, !claimed),
            new("Planned", planned ? "Done · open" : "Next", planned, claimed && !planned, OpensPlan: true),
            new("Clear to start", cleared ? "Done" : waitingToStart ? "Current" : "Next", cleared, waitingToStart),
            new("Crafting", crafting ? "Done" : cleared ? "Current" : "Next", crafting, cleared && !crafting),
            new("Delivery", delivered ? "Done" : crafting ? "Current" : "Later", delivered, crafting && !delivered)
        ];
    }

    private static string FormatWorkbenchStatus(
        TradeOrder order,
        TradeCompanyCommission commission)
    {
        if (commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Revoked)
        {
            return "Publication revoked";
        }
        if (order.Status == TradeOrderStatus.Canceled)
        {
            return "Canceled";
        }
        if (order.Status == TradeOrderStatus.Completed)
        {
            return commission.SettlementState == CompanyCommissionSettlementState.Satisfied
                ? "Completed"
                : "Delivery accepted";
        }
        if (order.Status == TradeOrderStatus.AwaitingDelivery)
        {
            return "Awaiting delivery";
        }
        if (order.Status == TradeOrderStatus.InProgress)
        {
            return "In progress";
        }
        if (commission.ActiveClaim == null)
        {
            return "Open";
        }
        if (commission.Gates.Identity.State == CompanyCommissionClearanceState.Pending)
        {
            return "Identity review";
        }
        return commission.ClearedToWork ? "Ready to craft" : "Waiting to start";
    }

    private static string FormatCommissionMaterialHandoff(
        CompanyCommissionMaterialClearance materials) =>
        materials.State == CompanyCommissionClearanceState.NotRequired
            ? "Not required"
            : materials.State == CompanyCommissionClearanceState.Satisfied
                ? "Handed off"
                : materials.ReadyAtUtc.HasValue
                    ? "Bundle ready; awaiting receipt"
                    : "Not handed off";

    private string FormatWorkbenchStatus(TradeOrder order)
    {
        var snapshot = HostedOrders.Get(order.Id);
        return snapshot is { Deleted: false, OwnerProjection.Order.CompanyCommission: { } commission }
            ? FormatWorkbenchStatus(snapshot.OwnerProjection.Order, commission)
            : "Status unavailable";
    }

    private Task OpenPlanWorkbenchAsync()
    {
        _activeOpsTab = PlanTabIndex;
        return Task.CompletedTask;
    }

    private Task OpenWorkWorkbenchAsync()
    {
        _activeOpsTab = WorkTabIndex;
        return Task.CompletedTask;
    }
}
