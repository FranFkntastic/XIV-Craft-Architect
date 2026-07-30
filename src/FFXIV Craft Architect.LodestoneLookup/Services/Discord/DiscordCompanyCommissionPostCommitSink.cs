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
        if (activity.Actor.Kind != CompanyCommissionActorKind.Crafter ||
            commission?.PublicMetadata.ViewState !=
                CompanyCommissionPublicViewState.Published ||
            !Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri))
        {
            return;
        }

        var delivery = scope.ServiceProvider
            .GetRequiredService<ICompanyCommissionDiscordDelivery>();
        var activityUrl = new UriBuilder(publicUri)
        {
            Fragment = $"activity={activity.EventId:D}"
        }.Uri;
        var notification = new CommittedCompanyCommissionNotification(
            access.CompanyId,
            CompanyCommissionProjectionService.CreatePublicBrief(
                committed.Order,
                committed.CompanyDisplayName),
            activity.EventId,
            activity.CommissionRevision,
            activity.Kind,
            activity.CreatedAtUtc,
            BuildSummary(activity),
            activity.Actor.DisplayName ??
                FormatCrafterDisplayName(commission.ProvisionalCrafter),
            activityUrl);
        try
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

    private static string? FormatCrafterDisplayName(
        CompanyCommissionProvisionalCrafter? crafter) =>
        crafter == null
            ? null
            : $"{crafter.CharacterName} @ {crafter.HomeWorld}";

    private static string BuildSummary(CompanyCommissionActivityEvent activity) =>
        activity.Kind switch
        {
            CompanyCommissionActivityKind.CommissionOpened =>
                "The commission was opened.",
            CompanyCommissionActivityKind.ClaimAccepted =>
                "A crafter claimed the commission.",
            CompanyCommissionActivityKind.ClaimRejected =>
                "The crafter claim was rejected.",
            CompanyCommissionActivityKind.ClaimReleased =>
                "The crafter released the commission.",
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
            CompanyCommissionActivityKind.CommissionCanceled =>
                "The commission was canceled.",
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
            _ => throw new ArgumentOutOfRangeException(
                nameof(activity),
                activity.Kind,
                null)
        };
}
