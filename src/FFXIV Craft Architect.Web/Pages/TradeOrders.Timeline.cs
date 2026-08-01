using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
using MudBlazor;

namespace FFXIV_Craft_Architect.Web.Pages;

public partial class TradeOrders
{
    private string _timelineComment = string.Empty;
    private CommissionTimelineVisibility _timelineComposerVisibility =
        CommissionTimelineVisibility.CompanyOnly;
    private CommissionTimelineFilter _timelineFilter = CommissionTimelineFilter.All;

    private IReadOnlyList<CommissionTimelineItem> GetSelectedTimelineItems()
    {
        if (_selectedOrder == null)
        {
            return [];
        }

        var activity = SelectedCanonicalCommission?.Activity ?? [];
        var activityIds = activity.Select(item => item.EventId).ToHashSet();
        var canonical = activity.Select(item => new CommissionTimelineItem(
            item.EventId,
            item.CreatedAtUtc,
            FormatCommissionActivity(item.Kind),
            item.Comment ?? FormatCommissionActivityDetail(item.Kind),
            item.Visibility == CompanyCommissionActivityVisibility.CompanyOnly
                ? CommissionTimelineVisibility.CompanyOnly
                : CommissionTimelineVisibility.Shared,
            CommissionTimelineSource.Commission,
            item.Actor.DisplayName ?? FormatCommissionActor(item.Actor.Kind),
            ResolveTimelineImportance(item.Kind)));
        var planning = (_selectedOrder.History ?? [])
            .Where(item => !activityIds.Contains(item.Id))
            .Select(item => new CommissionTimelineItem(
                item.Id,
                item.CreatedAtUtc,
                FormatHistoryKind(item.Kind),
                item.Note,
                item.Kind == TradeOrderHistoryEventKind.ManualNote
                    ? CommissionTimelineVisibility.CompanyOnly
                    : CommissionTimelineVisibility.Planning,
                CommissionTimelineSource.Planning,
                "Trade Architect",
                item.Kind == TradeOrderHistoryEventKind.ManualNote
                    ? CommissionTimelineImportance.Human
                    : CommissionTimelineImportance.Routine));
        return canonical
            .Concat(planning)
            .Where(MatchesTimelineFilter)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id)
            .ToArray();
    }

    private IReadOnlyList<CommissionTimelineGroup> GetSelectedTimelineGroups()
    {
        var groups = new List<CommissionTimelineGroup>();
        var routine = new List<CommissionTimelineItem>();

        void FlushRoutine()
        {
            if (routine.Count == 0)
            {
                return;
            }

            groups.Add(new CommissionTimelineGroup(routine.ToArray()));
            routine.Clear();
        }

        foreach (var item in GetSelectedTimelineItems())
        {
            if (item.Importance == CommissionTimelineImportance.Routine)
            {
                routine.Add(item);
                continue;
            }

            FlushRoutine();
            groups.Add(new CommissionTimelineGroup([item]));
        }

        FlushRoutine();
        return groups;
    }

    private CommissionTimelineSummary GetSelectedTimelineSummary()
    {
        var items = GetSelectedTimelineItems();
        return new CommissionTimelineSummary(
            items.Count(item => item.Importance == CommissionTimelineImportance.Attention),
            items.Count(item => item.Importance == CommissionTimelineImportance.Human),
            items.Count(item => item.Importance == CommissionTimelineImportance.Milestone),
            items.Count(item => item.Importance == CommissionTimelineImportance.Routine));
    }

    private bool MatchesTimelineFilter(CommissionTimelineItem item) =>
        _timelineFilter switch
        {
            CommissionTimelineFilter.Commission =>
                item.Source == CommissionTimelineSource.Commission,
            CommissionTimelineFilter.Planning =>
                item.Visibility == CommissionTimelineVisibility.Planning,
            CommissionTimelineFilter.Shared =>
                item.Visibility == CommissionTimelineVisibility.Shared,
            CommissionTimelineFilter.CompanyOnly =>
                item.Visibility == CommissionTimelineVisibility.CompanyOnly,
            _ => true
        };

    private void SetTimelineFilter(CommissionTimelineFilter filter) =>
        _timelineFilter = filter;

    private string GetTimelineFilterClass(CommissionTimelineFilter filter) =>
        _timelineFilter == filter
            ? "trade-orders-timeline-filter is-active"
            : "trade-orders-timeline-filter";

    private string GetTimelineItemClass(CommissionTimelineItem item)
    {
        var visibilityClass = item.Visibility switch
        {
            CommissionTimelineVisibility.CompanyOnly =>
                "is-private",
            CommissionTimelineVisibility.Planning =>
                "is-planning",
            _ => "is-shared"
        };
        var importanceClass = item.Importance switch
        {
            CommissionTimelineImportance.Attention => "is-attention",
            CommissionTimelineImportance.Human => "is-human",
            CommissionTimelineImportance.Milestone => "is-milestone",
            _ => "is-routine"
        };
        return $"trade-orders-timeline-item {visibilityClass} {importanceClass}";
    }

    private static string FormatTimelineImportance(CommissionTimelineImportance importance) =>
        importance switch
        {
            CommissionTimelineImportance.Attention => "Needs attention",
            CommissionTimelineImportance.Human => "Conversation",
            CommissionTimelineImportance.Milestone => "Milestone",
            _ => "Routine"
        };

    private static CommissionTimelineImportance ResolveTimelineImportance(
        CompanyCommissionActivityKind kind) =>
        kind switch
        {
            CompanyCommissionActivityKind.ClaimRejected or
            CompanyCommissionActivityKind.ClaimReleased or
            CompanyCommissionActivityKind.ProvisionalIdentityRejected or
            CompanyCommissionActivityKind.PaymentPolicyChangeRefused or
            CompanyCommissionActivityKind.PaymentAttestationRetracted or
            CompanyCommissionActivityKind.DeliveryReadinessWithdrawn or
            CompanyCommissionActivityKind.DeliveryReturnedToWork or
            CompanyCommissionActivityKind.SettlementPaymentAttestationRetracted or
            CompanyCommissionActivityKind.CommissionCanceled or
            CompanyCommissionActivityKind.CommissionPublicationRevoked =>
                CommissionTimelineImportance.Attention,
            CompanyCommissionActivityKind.CommentAdded =>
                CommissionTimelineImportance.Human,
            CompanyCommissionActivityKind.ProgressReported or
            CompanyCommissionActivityKind.MigratedTradeOrderHistory =>
                CommissionTimelineImportance.Routine,
            _ => CommissionTimelineImportance.Milestone
        };

    private static string FormatTimelineVisibility(CommissionTimelineVisibility visibility) =>
        visibility switch
        {
            CommissionTimelineVisibility.CompanyOnly => "Company only",
            CommissionTimelineVisibility.Planning => "Planning",
            _ => "Shared"
        };

    private async Task AddTimelineEntryAsync()
    {
        if (_selectedOrder == null || string.IsNullOrWhiteSpace(_timelineComment))
        {
            return;
        }

        var comment = _timelineComment.Trim();
        if (SelectedCommissionOwner is { } owner)
        {
            if (!EnsureHostedOrderMutationAvailable())
            {
                return;
            }

            _isCommissionCommandRunning = true;
            try
            {
                var result = _timelineComposerVisibility ==
                             CommissionTimelineVisibility.CompanyOnly
                    ? await CommissionOperations.AddPrivateNoteAsync(owner, comment)
                    : await CommissionOperations.AddCommentAsync(owner, comment);
                ApplyCommissionResult(
                    result,
                    _timelineComposerVisibility ==
                    CommissionTimelineVisibility.CompanyOnly
                        ? "Company note added"
                        : "Crafter update posted");
                if (result.Success)
                {
                    _timelineComment = string.Empty;
                    _activeOpsTab = TimelineTabIndex;
                }
            }
            finally
            {
                _isCommissionCommandRunning = false;
            }

            return;
        }

        if (_companyProfile == null)
        {
            return;
        }

        var orderId = _selectedOrder.Id;
        var orderToSave = TradeOrderWorkflow.CopyOrder(_selectedOrder);
        var history = orderToSave.History.ToList();
        history.Add(TradeOrderHistoryEvent.CreateManualNote(
            _companyProfile.Id,
            orderToSave.Id,
            comment,
            DateTime.UtcNow));
        orderToSave.History = history;
        orderToSave.UpdatedAtUtc = DateTime.UtcNow;
        if (!await SaveOrderAndNotifyAsync(orderToSave))
        {
            Snackbar.Add("Failed to save the company note.", Severity.Error);
            return;
        }

        _timelineComment = string.Empty;
        await LoadAsync();
        SelectOrderAfterReload(
            orderId,
            "The note was saved, but the order could not be reloaded.");
        _activeOpsTab = TimelineTabIndex;
    }

    private static string FormatCommissionActor(CompanyCommissionActorKind actor) =>
        actor switch
        {
            CompanyCommissionActorKind.Commissioner => "Commissioner",
            CompanyCommissionActorKind.Crafter => "Crafter",
            CompanyCommissionActorKind.Migration => "Migration",
            _ => "Craft Architect"
        };

    private static string FormatCommissionActivityDetail(
        CompanyCommissionActivityKind kind) =>
        kind switch
        {
            CompanyCommissionActivityKind.ClaimAccepted =>
                "The one active claim slot was reserved.",
            CompanyCommissionActivityKind.TermsAmended =>
                "The commissioner created a new immutable terms version.",
            CompanyCommissionActivityKind.PaymentSentRecorded =>
                "The commissioner marked the current-terms payment sent.",
            CompanyCommissionActivityKind.PaymentReceivedConfirmed =>
                "The crafter confirmed receipt against the current terms.",
            CompanyCommissionActivityKind.PaymentAttestationRetracted =>
                "A party withdrew its payment confirmation.",
            CompanyCommissionActivityKind.SettlementPaymentSentRecorded =>
                "The commissioner marked the final payment sent.",
            CompanyCommissionActivityKind.SettlementPaymentReceivedConfirmed =>
                "The crafter confirmed receipt of the final payment.",
            CompanyCommissionActivityKind.SettlementPaymentAttestationRetracted =>
                "A party withdrew its final-payment confirmation.",
            _ => "Commission state updated."
        };
}

public enum CommissionTimelineFilter
{
    All,
    Commission,
    Planning,
    Shared,
    CompanyOnly
}

public enum CommissionTimelineVisibility
{
    Shared,
    CompanyOnly,
    Planning
}

public enum CommissionTimelineSource
{
    Commission,
    Planning
}

public enum CommissionTimelineImportance
{
    Attention,
    Human,
    Milestone,
    Routine
}

public sealed record CommissionTimelineItem(
    Guid Id,
    DateTime CreatedAtUtc,
    string Title,
    string Detail,
    CommissionTimelineVisibility Visibility,
    CommissionTimelineSource Source,
    string Actor,
    CommissionTimelineImportance Importance);

public sealed record CommissionTimelineGroup(
    IReadOnlyList<CommissionTimelineItem> Items)
{
    public bool IsRoutineCluster =>
        Items.Count > 1 &&
        Items.All(item => item.Importance == CommissionTimelineImportance.Routine);
}

public sealed record CommissionTimelineSummary(
    int Attention,
    int Human,
    int Milestone,
    int Routine);
