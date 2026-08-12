using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed record CommittedCompanyCommissionDiscordProjection(
    CompanyId CompanyId,
    CompanyCommissionPublicBrief Commission,
    CompanyRecordRevision ObjectRevision,
    Guid EventId,
    long CommissionRevision,
    CompanyCommissionActivityKind EventKind,
    DateTime CommittedAtUtc,
    string Summary,
    Uri PublicViewUrl,
    Uri? ClaimUrl);

public enum DiscordNotificationAttentionClass
{
    Routine,
    ActionRequired,
    CriticalException
}

public enum DiscordNotificationMentionBehavior
{
    Push,
    SilentPing,
    NoPing,
    Off
}

public enum DiscordNotificationDestinationMode
{
    CommissionerDirectMessage,
    UpdateChannel,
    Both
}

public enum DiscordDirectMessageFallback
{
    None,
    UpdateChannel
}

public enum DiscordNotificationDestinationKind
{
    CommissionerDirectMessage,
    UpdateChannel,
    MemberDirectMessage
}

public sealed record CommittedCompanyCommissionNotification(
    CompanyId CompanyId,
    CompanyCommissionPublicBrief Commission,
    Guid EventId,
    long CommissionRevision,
    CompanyCommissionActivityKind EventKind,
    DateTime CommittedAtUtc,
    string Summary,
    string? ActorDisplayName,
    string ActionLabel,
    Uri ActivityUrl);

public static class CompanyCommissionNotificationLinks
{
    public static Uri BuildOperatorActivityUrl(
        Uri publicViewUrl,
        Guid commissionId,
        Guid eventId) =>
        Build(
            publicViewUrl,
            "/trade/orders",
            ("orderId", commissionId),
            ("activityId", eventId));

    public static Uri BuildMemberActivityUrl(
        Uri publicViewUrl,
        CompanyId companyId,
        Guid commissionId,
        Guid eventId) =>
        Build(
            publicViewUrl,
            $"/companies/{companyId.Value:D}",
            ("commissionId", commissionId),
            ("activityId", eventId));

    public static bool IsCanonicalOperatorActivityUrl(
        Uri publicViewUrl,
        Uri activityUrl,
        Guid commissionId,
        Guid eventId) =>
        IsCanonical(
            publicViewUrl,
            activityUrl,
            "/trade/orders",
            ("orderId", commissionId),
            ("activityId", eventId));

    public static bool IsCanonicalMemberActivityUrl(
        Uri publicViewUrl,
        Uri activityUrl,
        CompanyId companyId,
        Guid commissionId,
        Guid eventId) =>
        IsCanonical(
            publicViewUrl,
            activityUrl,
            $"/companies/{companyId.Value:D}",
            ("commissionId", commissionId),
            ("activityId", eventId));

    private static Uri Build(
        Uri publicViewUrl,
        string path,
        params (string Name, Guid Value)[] query)
    {
        ArgumentNullException.ThrowIfNull(publicViewUrl);
        if (!publicViewUrl.IsAbsoluteUri ||
            publicViewUrl.Scheme is not ("https" or "http") ||
            publicViewUrl.Scheme == "http" && !publicViewUrl.IsLoopback ||
            !string.IsNullOrEmpty(publicViewUrl.UserInfo))
        {
            throw new ArgumentException(
                "A safe absolute Craft Architect URL is required.",
                nameof(publicViewUrl));
        }

        if (query.Any(item => item.Value == Guid.Empty))
        {
            throw new ArgumentException("Notification link identities cannot be empty.");
        }

        return new UriBuilder(publicViewUrl)
        {
            Path = path,
            Query = string.Join(
                '&',
                query.Select(item => $"{item.Name}={item.Value:D}")),
            Fragment = string.Empty
        }.Uri;
    }

    private static bool IsCanonical(
        Uri publicViewUrl,
        Uri activityUrl,
        string expectedPath,
        params (string Name, Guid Value)[] expectedQuery)
    {
        if (!publicViewUrl.IsAbsoluteUri ||
            !activityUrl.IsAbsoluteUri ||
            !string.IsNullOrEmpty(activityUrl.UserInfo) ||
            !string.IsNullOrEmpty(activityUrl.Fragment) ||
            !string.Equals(
                publicViewUrl.Scheme,
                activityUrl.Scheme,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                publicViewUrl.Host,
                activityUrl.Host,
                StringComparison.OrdinalIgnoreCase) ||
            publicViewUrl.Port != activityUrl.Port ||
            !string.Equals(activityUrl.AbsolutePath, expectedPath, StringComparison.Ordinal))
        {
            return false;
        }

        var pairs = activityUrl.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (pairs.Length != expectedQuery.Length)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                return false;
            }

            string name;
            string value;
            try
            {
                name = Uri.UnescapeDataString(parts[0]);
                value = Uri.UnescapeDataString(parts[1]);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (!values.TryAdd(name, value))
            {
                return false;
            }
        }

        return expectedQuery.All(expected =>
            values.TryGetValue(expected.Name, out var value) &&
            Guid.TryParse(value, out var parsed) &&
            parsed == expected.Value);
    }
}

public sealed record DiscordNotificationRouteConfiguration(
    CompanyId CompanyId,
    string CommissionerDiscordUserId,
    DiscordNotificationDestinationMode DestinationMode,
    string? UpdateChannelId,
    DiscordDirectMessageFallback DirectMessageFallback,
    DiscordNotificationMentionBehavior RoutineBehavior,
    DiscordNotificationMentionBehavior ActionRequiredBehavior,
    DiscordNotificationMentionBehavior CriticalExceptionBehavior,
    long Revision,
    DateTimeOffset UpdatedAt);

public sealed record DiscordNotificationRouteUpdate(
    string CommissionerDiscordUserId,
    DiscordNotificationDestinationMode DestinationMode,
    string? UpdateChannelId,
    DiscordDirectMessageFallback DirectMessageFallback,
    DiscordNotificationMentionBehavior RoutineBehavior,
    DiscordNotificationMentionBehavior ActionRequiredBehavior,
    DiscordNotificationMentionBehavior CriticalExceptionBehavior,
    long ExpectedRevision,
    string IdempotencyKey);

public enum DiscordNotificationRouteUpdateStatus
{
    Applied,
    Replayed,
    Conflict,
    Invalid
}

public sealed record DiscordNotificationRouteUpdateResult(
    DiscordNotificationRouteUpdateStatus Status,
    DiscordNotificationRouteConfiguration? Configuration,
    string? Error = null)
{
    public bool Success =>
        Status is DiscordNotificationRouteUpdateStatus.Applied or
            DiscordNotificationRouteUpdateStatus.Replayed;
}

public enum DiscordNotificationEnqueueStatus
{
    Queued,
    Replayed,
    Suppressed,
    Unconfigured,
    Invalid
}

public sealed record DiscordNotificationEnqueueResult(
    DiscordNotificationEnqueueStatus Status,
    DiscordNotificationAttentionClass AttentionClass,
    IReadOnlyList<Guid> WorkItemIds,
    string? Error = null)
{
    public bool Success =>
        Status is DiscordNotificationEnqueueStatus.Queued or
            DiscordNotificationEnqueueStatus.Replayed or
            DiscordNotificationEnqueueStatus.Suppressed;
}

public sealed record DiscordOriginContact(
    string DiscordUserId,
    string DisplayNameSnapshot);

public sealed record CommittedDiscordClaimContact(
    CompanyId CompanyId,
    Guid CommissionId,
    Guid ClaimId,
    Guid ClaimEventId,
    long CommissionRevision,
    CompanyCommissionActivityKind EventKind,
    DateTime CommittedAtUtc,
    string InteractionId,
    DiscordOriginContact Contact);

public sealed record PendingDiscordClaimContactExpectation(
    CompanyId CompanyId,
    Guid CommissionId,
    string PublicBriefId,
    Guid ClaimCapabilityId,
    long ClaimCapabilityRevision,
    string InteractionId,
    DiscordOriginContact Contact,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public enum DiscordNotificationDiagnosticState
{
    Failed,
    ReconciliationRequired
}

public sealed record DiscordNotificationDiagnostic(
    Guid DiagnosticId,
    Guid CommissionId,
    Guid EventId,
    long DesiredProjectionRevision,
    DiscordNotificationDestinationKind Destination,
    DiscordNotificationDiagnosticState State,
    string Summary,
    string Detail,
    string RecommendedAction,
    bool CanRetry,
    bool FallbackQueued,
    DateTimeOffset UpdatedAt);

public interface ICompanyCommissionDiscordDelivery
{
    Task<DiscordPublicationCreateResult> ProjectAsync(
        CommittedCompanyCommissionDiscordProjection projection,
        CancellationToken cancellationToken = default);

    Task<DiscordNotificationEnqueueResult> NotifyAsync(
        CommittedCompanyCommissionNotification notification,
        CancellationToken cancellationToken = default);

    Task<DiscordNotificationEnqueueResult> NotifyMembersAsync(
        CommittedCompanyCommissionNotification notification,
        TradeCompanyCommission commission,
        Uri publicUrl,
        CancellationToken cancellationToken = default);

    Task CaptureDiscordClaimContactAsync(
        CommittedDiscordClaimContact contact,
        CancellationToken cancellationToken = default);
}

public static class CompanyCommissionNotificationPolicy
{
    public static DiscordNotificationAttentionClass Classify(
        CompanyCommissionActivityKind eventKind) =>
        eventKind switch
        {
            CompanyCommissionActivityKind.ProgressReported or
            CompanyCommissionActivityKind.CommentAdded or
            CompanyCommissionActivityKind.ProvisionalIdentityConfirmed or
            CompanyCommissionActivityKind.PaymentPolicyChangeAccepted or
            CompanyCommissionActivityKind.PaymentPolicyChangeRefused or
            CompanyCommissionActivityKind.TermsAcknowledged or
            CompanyCommissionActivityKind.PaymentClearanceRecorded or
            CompanyCommissionActivityKind.PaymentSentRecorded or
            CompanyCommissionActivityKind.PaymentReceivedConfirmed or
            CompanyCommissionActivityKind.PaymentAttestationRetracted or
            CompanyCommissionActivityKind.CompanyMaterialsReady or
            CompanyCommissionActivityKind.WorkClearanceAchieved or
            CompanyCommissionActivityKind.DeliveryReadinessWithdrawn or
            CompanyCommissionActivityKind.DeliveryAccepted or
            CompanyCommissionActivityKind.SettlementRecorded or
            CompanyCommissionActivityKind.SettlementPaymentSentRecorded or
            CompanyCommissionActivityKind.SettlementPaymentReceivedConfirmed or
            CompanyCommissionActivityKind.SettlementPaymentAttestationRetracted or
            CompanyCommissionActivityKind.TermsAmended or
            CompanyCommissionActivityKind.CommissionOpened or
            CompanyCommissionActivityKind.CommissionClosed or
            CompanyCommissionActivityKind.ParticipantRecoveryRedeemed or
            CompanyCommissionActivityKind.MigratedFromTradeOrder or
            CompanyCommissionActivityKind.MigratedTradeOrderHistory or
            CompanyCommissionActivityKind.CommissionReopened =>
                DiscordNotificationAttentionClass.Routine,

            CompanyCommissionActivityKind.ClaimAccepted or
            CompanyCommissionActivityKind.ClaimRecovered or
            CompanyCommissionActivityKind.ProvisionalIdentitySubmitted or
            CompanyCommissionActivityKind.PaymentPolicyChangeRequested or
            CompanyCommissionActivityKind.CompanyMaterialsReceived or
            CompanyCommissionActivityKind.DeliveryReadinessDeclared or
            CompanyCommissionActivityKind.ParticipantRecoveryIssued =>
                DiscordNotificationAttentionClass.ActionRequired,

            CompanyCommissionActivityKind.ClaimRejected or
            CompanyCommissionActivityKind.ClaimReleased or
            CompanyCommissionActivityKind.ProvisionalIdentityRejected or
            CompanyCommissionActivityKind.DeliveryReturnedToWork or
            CompanyCommissionActivityKind.CommissionCanceled or
            CompanyCommissionActivityKind.CommissionPublicationRevoked or
            CompanyCommissionActivityKind.ClaimResolutionRequired =>
                DiscordNotificationAttentionClass.CriticalException,

            _ => throw new ArgumentOutOfRangeException(nameof(eventKind), eventKind, null)
        };

    public static DiscordNotificationMentionBehavior ResolveBehavior(
        DiscordNotificationRouteConfiguration route,
        DiscordNotificationAttentionClass attentionClass) =>
        attentionClass switch
        {
            DiscordNotificationAttentionClass.Routine => route.RoutineBehavior,
            DiscordNotificationAttentionClass.ActionRequired => route.ActionRequiredBehavior,
            DiscordNotificationAttentionClass.CriticalException => route.CriticalExceptionBehavior,
            _ => throw new ArgumentOutOfRangeException(nameof(attentionClass), attentionClass, null)
        };
}

public static class DiscordOriginContactCapture
{
    public static DiscordOriginContact? FromVerifiedInteraction(JsonElement interaction)
    {
        if (!interaction.TryGetProperty("member", out var member) ||
            !member.TryGetProperty("user", out var user))
        {
            return null;
        }

        var userId = ReadString(user, "id");
        if (!DiscordSnowflake.IsValid(userId))
        {
            return null;
        }

        var displayName = ReadString(member, "nick") ??
            ReadString(user, "global_name") ??
            ReadString(user, "username") ??
            "Discord claimant";
        return new DiscordOriginContact(
            userId!,
            DiscordProjectionSanitizer.Text(displayName, 120));
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal static class DiscordSnowflake
{
    public static bool IsValid(string? value) =>
        value is { Length: >= 17 and <= 20 } &&
        value.All(char.IsAsciiDigit);
}
