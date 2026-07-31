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
            item.Actor.DisplayName ?? FormatCommissionActor(item.Actor.Kind)));
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
                "Trade Architect"));
        return canonical
            .Concat(planning)
            .Where(MatchesTimelineFilter)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ThenByDescending(item => item.Id)
            .ToArray();
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

    private string GetTimelineItemClass(CommissionTimelineItem item) =>
        item.Visibility switch
        {
            CommissionTimelineVisibility.CompanyOnly =>
                "trade-orders-timeline-item is-private",
            CommissionTimelineVisibility.Planning =>
                "trade-orders-timeline-item is-planning",
            _ => "trade-orders-timeline-item is-shared"
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

public sealed record CommissionTimelineItem(
    Guid Id,
    DateTime CreatedAtUtc,
    string Title,
    string Detail,
    CommissionTimelineVisibility Visibility,
    CommissionTimelineSource Source,
    string Actor);
