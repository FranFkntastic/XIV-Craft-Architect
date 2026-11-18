using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Pages;

public sealed record TradeOrderCenterOutputPresentation(
    string Name,
    int RequiredQuantity,
    bool MustBeHq,
    int CompletedQuantity);

public sealed record TradeOrderCenterCrafterUpdatePresentation(
    int CompletedQuantity,
    int RequiredQuantity,
    DateTime ReportedAtUtc,
    string ReportedBy,
    string? Comment);

public sealed record TradeOrderProgressStepPresentation(
    string Label,
    string State,
    bool IsComplete,
    bool IsCurrent,
    bool OpensPlan = false);

public sealed record TradeOrderCenterOverviewPresentation(
    string Title,
    string Status,
    string Claimant,
    string Crafter,
    int TermsVersion,
    IReadOnlyList<TradeOrderCenterOutputPresentation> Outputs,
    TradeOrderCenterCrafterUpdatePresentation? LatestCrafterUpdate,
    IReadOnlyList<TradeOrderProgressStepPresentation> Progress,
    string? TermsActionLabel);

public partial class TradeOrders
{
    private const int PlanTabIndex = 0;
    private const int HistoryTabIndex = 1;
    private const int ShareTabIndex = 2;

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
        var order = owner?.Order ?? snapshot?.Order;
        var commission = order?.CompanyCommission;
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
                    progress?.CompletedQuantity ?? 0);
            })
            .ToArray();

        return new TradeOrderCenterOverviewPresentation(
            order.Title,
            FormatWorkbenchStatus(order, commission),
            FormatCanonicalCommissionClaimant(commission),
            FormatCanonicalCommissionCrafter(order, commission),
            commission.CurrentTermsVersion,
            outputs,
            BuildLatestCrafterUpdate(commission),
            BuildCenterProgress(order, commission),
            owner != null &&
            CanMutateHostedOrder &&
            !HasSelectedLocalHostedCollision &&
            !TradeOrderStatusWorkflow.IsArchived(order.Status)
                ? commission.PublicMetadata.ViewState switch
                {
                    CompanyCommissionPublicViewState.Draft => "Edit draft terms",
                    CompanyCommissionPublicViewState.Published
                        when !IsEditingCommissionTermsRevision &&
                             CanReviseCanonicalTerms(commission) =>
                        "Revise terms",
                    _ => null
                }
                : null);
    }

    private static TradeOrderCenterCrafterUpdatePresentation? BuildLatestCrafterUpdate(
        TradeCompanyCommission commission)
    {
        var latestReport = commission.Activity
            .Where(item => item.Kind == CompanyCommissionActivityKind.ProgressReported)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.CommissionRevision)
            .FirstOrDefault();
        if (latestReport == null)
        {
            return null;
        }

        return new TradeOrderCenterCrafterUpdatePresentation(
            commission.OutputProgress.Sum(item => item.CompletedQuantity),
            commission.OutputProgress.Sum(item => item.RequiredQuantity),
            latestReport.CreatedAtUtc,
            latestReport.Actor.DisplayName ?? FormatCommissionActor(latestReport.Actor.Kind),
            string.IsNullOrWhiteSpace(latestReport.Comment) ? null : latestReport.Comment.Trim());
    }

    private static IReadOnlyList<TradeOrderProgressStepPresentation> BuildCenterProgress(
        TradeOrder order,
        TradeCompanyCommission commission)
    {
        var claimed = commission.ActiveClaim != null;
        var planned = !string.IsNullOrWhiteSpace(order.CraftPlanId);
        var termsAcknowledged = commission.ActiveClaim == null ||
            commission.ParticipantAcknowledgedTermsVersion == commission.CurrentTermsVersion;
        var cleared = commission.ClearedToWork && termsAcknowledged;
        var craftingStarted = cleared || order.Status is TradeOrderStatus.InProgress or
            TradeOrderStatus.AwaitingDelivery or
            TradeOrderStatus.Completed;
        var craftingComplete = order.Status is TradeOrderStatus.AwaitingDelivery or
            TradeOrderStatus.Completed;
        var delivered = order.Status == TradeOrderStatus.Completed;
        var waitingToStart = claimed && !cleared && order.Status == TradeOrderStatus.Assigned;

        return
        [
            new("Requested", "Done", commission.CurrentTerms.Outputs.Count > 0, false),
            new("Claimed", claimed ? "Done" : "Next", claimed, !claimed),
            new("Planned", planned ? "Done" : "Next", planned, claimed && !planned, OpensPlan: true),
            new("Requirements", cleared ? "Done" : waitingToStart ? "Current" : "Next", cleared, waitingToStart),
            new(
                "Crafting",
                craftingComplete ? "Done" : craftingStarted ? "Current" : cleared ? "Next" : "Later",
                craftingComplete,
                craftingStarted && !craftingComplete),
            new(
                "Delivery",
                delivered ? "Done" : order.Status == TradeOrderStatus.AwaitingDelivery ? "Current" : "Later",
                delivered,
                order.Status == TradeOrderStatus.AwaitingDelivery)
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
        if (order.Status == TradeOrderStatus.ResolutionRequired || commission.ManualResolution != null)
        {
            return "Resolution required";
        }
        if (order.Status == TradeOrderStatus.Completed)
        {
            return commission.SettlementState == CompanyCommissionSettlementState.Satisfied
                ? "Completed"
                : "Delivery accepted";
        }
        if (order.Status == TradeOrderStatus.AwaitingDelivery)
        {
            return "Awaiting delivery review";
        }
        if (order.Status == TradeOrderStatus.InProgress)
        {
            return "Crafting";
        }
        if (commission.ActiveClaim == null)
        {
            return "Open";
        }
        if (commission.Gates.Identity.State == CompanyCommissionClearanceState.Pending)
        {
            return "Identity review";
        }
        if (commission.ParticipantAcknowledgedTermsVersion != commission.CurrentTermsVersion)
        {
            return "Terms review";
        }
        return commission.ClearedToWork ? "Crafting" : "Needs prerequisites";
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
        return snapshot is { Deleted: false, Order.CompanyCommission: { } commission }
            ? FormatWorkbenchStatus(snapshot.Order, commission)
            : "Status unavailable";
    }

    private Task OpenPlanWorkbenchAsync() => SetActiveOpsTabAsync(PlanTabIndex);

}
