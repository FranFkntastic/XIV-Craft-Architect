using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed class DiscordCompanyCommissionPostCommitSink(
    IServiceScopeFactory scopeFactory,
    ILogger<DiscordCompanyCommissionPostCommitSink> logger)
    : ICompanyCommissionPostCommitSink
{
    public async Task OnCommittedAsync(
        TradeCompanyAccessContext access,
        HostedCompanyCommissionSnapshot committed,
        CompanyCommissionActivityEvent activity,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var publications = scope.ServiceProvider
            .GetRequiredService<DiscordPublicationService>();
        try
        {
            await publications.RefreshCommittedCommissionAsync(
                access,
                committed,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Discord trade-channel projection could not be queued for commission " +
                "{CommissionId}, event {EventId}.",
                activity.CommissionId,
                activity.EventId);
        }

        var commission = committed.Order.CompanyCommission;
        var publicUrl = commission?.PublicMetadata.PublicUrl;
        if (commission == null ||
            !Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri))
        {
            return;
        }

        var delivery = scope.ServiceProvider
            .GetRequiredService<ICompanyCommissionDiscordDelivery>();
        var publicBrief = CompanyCommissionProjectionService.CreatePublicBrief(
            committed.Order,
            committed.CompanyDisplayName);
        var activityUrl = CompanyCommissionNotificationLinks.BuildOperatorActivityUrl(
            publicUri,
            activity.CommissionId,
            activity.EventId);
        var notification = new CommittedCompanyCommissionNotification(
            access.CompanyId,
            publicBrief,
            activity.EventId,
            activity.CommissionRevision,
            activity.Kind,
            activity.CreatedAtUtc,
            BuildSummary(activity, publicBrief),
            activity.Actor.DisplayName,
            ResolveActionLabel(activity.Kind, publicBrief),
            activityUrl);
        try
        {
            if (ShouldNotifyCommissioner(activity))
            {
                var result = await delivery.NotifyAsync(notification, cancellationToken);
                if (result.Status == DiscordNotificationEnqueueStatus.Invalid)
                {
                    logger.LogError(
                        "Committed Discord notification was rejected for commission " +
                        "{CommissionId}, event {EventId}: {Error}",
                        activity.CommissionId,
                        activity.EventId,
                        result.Error ?? "invalid notification projection");
                }
            }
            if (ShouldNotifyMembers(activity))
            {
                await delivery.NotifyMembersAsync(
                    notification,
                    commission,
                    publicUri,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Discord commissioner notification could not be queued for commission " +
                "{CommissionId}, event {EventId}.",
                activity.CommissionId,
                activity.EventId);
        }
    }

    internal static string BuildSummary(
        CompanyCommissionActivityEvent activity,
        CompanyCommissionPublicBrief commission)
    {
        var changedFact = activity.Kind switch
        {
            CompanyCommissionActivityKind.CommissionOpened =>
                "The commission was opened.",
            CompanyCommissionActivityKind.ClaimAccepted =>
                "The crafter's claim was accepted.",
            CompanyCommissionActivityKind.ClaimRejected =>
                "The crafter claim was rejected.",
            CompanyCommissionActivityKind.ClaimReleased =>
                "The crafter released the commission.",
            CompanyCommissionActivityKind.ClaimResolutionRequired =>
                "The crafter withdrew after work or an exchange began.",
            CompanyCommissionActivityKind.ClaimRecovered =>
                "Crafter access to the commission was recovered.",
            CompanyCommissionActivityKind.ProvisionalIdentitySubmitted =>
                "The crafter submitted an in-game identity for confirmation.",
            CompanyCommissionActivityKind.ProvisionalIdentityConfirmed =>
                "The crafter identity was confirmed.",
            CompanyCommissionActivityKind.ProvisionalIdentityRejected =>
                "The submitted crafter identity was rejected.",
            CompanyCommissionActivityKind.PaymentPolicyChangeRequested =>
                "The crafter requested different payment terms.",
            CompanyCommissionActivityKind.PaymentPolicyChangeAccepted =>
                "The requested payment terms were accepted.",
            CompanyCommissionActivityKind.PaymentPolicyChangeRefused =>
                "The requested payment terms were refused.",
            CompanyCommissionActivityKind.TermsAcknowledged =>
                "The crafter acknowledged the current commission terms.",
            CompanyCommissionActivityKind.PaymentClearanceRecorded =>
                "Payment clearance was recorded.",
            CompanyCommissionActivityKind.PaymentSentRecorded =>
                "The commissioner marked the advance payment sent.",
            CompanyCommissionActivityKind.PaymentReceivedConfirmed =>
                "The crafter confirmed the advance payment received.",
            CompanyCommissionActivityKind.PaymentAttestationRetracted =>
                "A payment confirmation was retracted.",
            CompanyCommissionActivityKind.CompanyMaterialsReady =>
                "Commissioner-provided materials are ready for handoff.",
            CompanyCommissionActivityKind.CompanyMaterialsReceived =>
                "The crafter confirmed receipt of commissioner-provided materials.",
            CompanyCommissionActivityKind.WorkClearanceAchieved =>
                "The commission is cleared for work.",
            CompanyCommissionActivityKind.ProgressReported =>
                "The crafter updated production progress.",
            CompanyCommissionActivityKind.CommentAdded =>
                DiscordProjectionSanitizer.Text(
                    activity.Comment ?? "A commission comment was added.",
                    4096),
            CompanyCommissionActivityKind.DeliveryReadinessDeclared =>
                "The crafter marked the full commission ready for delivery.",
            CompanyCommissionActivityKind.DeliveryReadinessWithdrawn =>
                "The crafter withdrew delivery readiness.",
            CompanyCommissionActivityKind.DeliveryReturnedToWork =>
                "The commission was returned to active work.",
            CompanyCommissionActivityKind.DeliveryAccepted =>
                "The commissioner accepted delivery.",
            CompanyCommissionActivityKind.SettlementRecorded =>
                "Commission settlement was recorded.",
            CompanyCommissionActivityKind.SettlementPaymentSentRecorded =>
                "The commissioner marked the final payment sent.",
            CompanyCommissionActivityKind.SettlementPaymentReceivedConfirmed =>
                "The crafter confirmed the final payment received.",
            CompanyCommissionActivityKind.SettlementPaymentAttestationRetracted =>
                "A final-payment confirmation was retracted.",
            CompanyCommissionActivityKind.CommissionCanceled =>
                "The commission was canceled.",
            CompanyCommissionActivityKind.CommissionReopened =>
                "The commission was reopened for claiming.",
            CompanyCommissionActivityKind.CommissionClosed =>
                "The commission was closed.",
            CompanyCommissionActivityKind.CommissionPublicationRevoked =>
                "The public commission brief was revoked.",
            CompanyCommissionActivityKind.ParticipantRecoveryIssued =>
                "A new crafter recovery link was issued.",
            CompanyCommissionActivityKind.ParticipantRecoveryRedeemed =>
                "The crafter recovered access to the commission.",
            CompanyCommissionActivityKind.MigratedFromTradeOrder =>
                "The existing Trade order was converted to a company commission.",
            CompanyCommissionActivityKind.MigratedTradeOrderHistory =>
                "Existing Trade order history was preserved in the commission.",
            CompanyCommissionActivityKind.TermsAmended =>
                "The commissioner published revised commission terms.",
            _ => throw new ArgumentOutOfRangeException(
                nameof(activity),
                activity.Kind,
                null)
        };

        var resultingState = ResolveResultingState(commission);
        return string.Equals(changedFact, resultingState, StringComparison.Ordinal)
            ? changedFact
            : $"{changedFact} {resultingState}";
    }

    internal static string ResolveActionLabel(
        CompanyCommissionActivityKind eventKind,
        CompanyCommissionPublicBrief commission) =>
        eventKind switch
        {
            CompanyCommissionActivityKind.ClaimResolutionRequired => "Review claim",
            CompanyCommissionActivityKind.ClaimAccepted
                when commission.Gates.Identity == CompanyCommissionClearanceState.Pending =>
                "Review identity",
            CompanyCommissionActivityKind.ProvisionalIdentitySubmitted => "Review identity",
            CompanyCommissionActivityKind.PaymentPolicyChangeRequested => "Review payment",
            CompanyCommissionActivityKind.CompanyMaterialsReceived => "View order",
            CompanyCommissionActivityKind.DeliveryReadinessDeclared => "Review delivery",
            CompanyCommissionActivityKind.ProgressReported => "View progress",
            CompanyCommissionActivityKind.CommentAdded => "View comment",
            _ => "View order"
        };

    internal static bool ShouldNotifyCommissioner(
        CompanyCommissionActivityEvent activity) =>
        activity.Actor.Kind == CompanyCommissionActorKind.Crafter &&
        activity.Visibility == CompanyCommissionActivityVisibility.Shared &&
        activity.Kind != CompanyCommissionActivityKind.DraftUpdated;

    internal static bool ShouldNotifyMembers(
        CompanyCommissionActivityEvent activity) =>
        activity.Visibility == CompanyCommissionActivityVisibility.Shared &&
        activity.Kind != CompanyCommissionActivityKind.DraftUpdated;

    private static string ResolveResultingState(
        CompanyCommissionPublicBrief commission)
    {
        if (commission.RequiresManualResolution ||
            commission.Status == TradeOrderStatus.ResolutionRequired)
        {
            return "Company resolution is required before work can continue.";
        }

        if (commission.Status == TradeOrderStatus.Canceled)
        {
            return "The commission is canceled and no longer publicly actionable.";
        }

        if (commission.Status == TradeOrderStatus.Completed)
        {
            return commission.SettlementState == CompanyCommissionSettlementState.Satisfied
                ? "Delivery and settlement are complete."
                : "Delivery is accepted; final settlement remains pending.";
        }

        if (commission.DeliveryReadiness.IsReady ||
            commission.Status == TradeOrderStatus.AwaitingDelivery)
        {
            return "The completed outputs are ready for delivery review.";
        }

        if (commission.Status == TradeOrderStatus.InProgress)
        {
            return "Work remains in progress.";
        }

        if (!commission.IsClaimed)
        {
            return "The single claim slot is open.";
        }

        if (commission.Gates.Identity == CompanyCommissionClearanceState.Pending)
        {
            return "Identity review is required before work can begin.";
        }

        if (!commission.ClearedToWork)
        {
            var pending = new List<string>();
            if (commission.Gates.Payment == CompanyCommissionClearanceState.Pending)
            {
                pending.Add("payment");
            }
            if (commission.Gates.CompanyMaterials == CompanyCommissionClearanceState.Pending)
            {
                pending.Add("company materials");
            }

            return pending.Count == 0
                ? "The commission remains assigned pending work clearance."
                : $"The commission remains assigned pending {string.Join(" and ", pending)}.";
        }

        return "The commission is cleared for work.";
    }
}
