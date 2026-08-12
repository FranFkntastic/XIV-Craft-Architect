using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed class CompanyCommissionDiscordDeliveryService(
    SqliteDiscordCollaborationStore collaboration,
    SqliteDiscordNotificationStore notifications,
    SqliteMembershipStore? memberships,
    SqliteDiscordIdentityStore? identities,
    DiscordCommissionOptions options,
    TimeProvider timeProvider) : ICompanyCommissionDiscordDelivery
{
    public CompanyCommissionDiscordDeliveryService(
        SqliteDiscordCollaborationStore collaboration,
        SqliteDiscordNotificationStore notifications,
        DiscordCommissionOptions options,
        TimeProvider timeProvider)
        : this(collaboration, notifications, null, null, options, timeProvider)
    {
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<DiscordPublicationCreateResult> ProjectAsync(
        CommittedCompanyCommissionDiscordProjection projection,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidatePublicationProjection(projection);
        if (validationError != null)
        {
            return Conflict(validationError);
        }

        if (!options.CanPublishDirectly)
        {
            return Conflict("Discord direct publishing is not configured.");
        }

        var installation = await collaboration.ResolveCompanyInstallationAsync(
            projection.CompanyId,
            cancellationToken);
        if (installation == null)
        {
            return Conflict(
                "The company does not have a ready Discord trade-channel installation.");
        }

        var existing = await collaboration.LoadPublicationByOrderAsync(
            projection.CompanyId,
            projection.Commission.CommissionId,
            cancellationToken);
        var actionToken = existing?.ActionToken ??
            SqliteDiscordCollaborationStore.CreateActionToken();
        object payload;
        try
        {
            payload = CompanyCommissionDiscordMessage.CreatePublication(
                projection,
                actionToken);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(exception.Message);
        }

        var state = CompanyCommissionDiscordMessage.ResolveProjectionState(
            projection.Commission);
        if (existing != null)
        {
            if (!string.Equals(
                    existing.PublicId,
                    projection.Commission.PublicBriefId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    existing.ChannelId,
                    installation.ChannelId,
                    StringComparison.Ordinal))
            {
                return Conflict(
                    "The canonical commission projection conflicts with its persisted Discord binding.");
            }

            if (projection.Commission.ProjectionRevision <=
                existing.DesiredProjectionRevision)
            {
                return new DiscordPublicationCreateResult(
                    DiscordPublicationCreateStatus.Replayed,
                    existing);
            }

            try
            {
                await collaboration.EnqueueProjectionAsync(
                    existing.PublicationId,
                    state,
                    projection.Commission.ProjectionRevision,
                    JsonSerializer.Serialize(payload, JsonOptions),
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                return Conflict(exception.Message);
            }
            var updated = await collaboration.LoadPublicationByOrderAsync(
                projection.CompanyId,
                projection.Commission.CommissionId,
                cancellationToken);
            return new DiscordPublicationCreateResult(
                DiscordPublicationCreateStatus.Created,
                updated ?? existing);
        }

        return await collaboration.CreatePublicationAsync(
            new TradeCompanyPublicationOwnership(
                projection.CompanyId,
                projection.Commission.CommissionId,
                projection.ObjectRevision),
            projection.Commission.PublicBriefId,
            projection.Commission.Terms.Version,
            $"commission-projection:{projection.EventId:N}",
            actionToken,
            installation.ChannelId,
            state,
            JsonSerializer.Serialize(payload, JsonOptions),
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public async Task<DiscordNotificationEnqueueResult> NotifyAsync(
        CommittedCompanyCommissionNotification notification,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateNotification(notification);
        var attentionClass = CompanyCommissionNotificationPolicy.Classify(
            notification.EventKind);
        if (validationError != null)
        {
            return new DiscordNotificationEnqueueResult(
                DiscordNotificationEnqueueStatus.Invalid,
                attentionClass,
                [],
                validationError);
        }

        var route = await notifications.LoadRouteAsync(
            notification.CompanyId,
            cancellationToken);
        if (route == null)
        {
            return new DiscordNotificationEnqueueResult(
                DiscordNotificationEnqueueStatus.Unconfigured,
                attentionClass,
                [],
                "Commissioner Discord notification routing is not configured.");
        }

        var behavior = CompanyCommissionNotificationPolicy.ResolveBehavior(
            route,
            attentionClass);
        if (behavior == DiscordNotificationMentionBehavior.Off)
        {
            return new DiscordNotificationEnqueueResult(
                DiscordNotificationEnqueueStatus.Suppressed,
                attentionClass,
                []);
        }

        var directMessagePayload =
            CompanyCommissionDiscordMessage.CreateNotification(
                notification,
                attentionClass,
                behavior,
                DiscordNotificationDestinationKind.CommissionerDirectMessage,
                route.CommissionerDiscordUserId);
        var updateChannelPayload =
            CompanyCommissionDiscordMessage.CreateNotification(
                notification,
                attentionClass,
                behavior,
                DiscordNotificationDestinationKind.UpdateChannel,
                route.CommissionerDiscordUserId);
        return await notifications.EnqueueAsync(
            notification.CompanyId,
            notification.Commission.CommissionId,
            notification.EventId,
            notification.CommissionRevision,
            attentionClass,
            route.Revision,
            JsonSerializer.Serialize(directMessagePayload, JsonOptions),
            JsonSerializer.Serialize(updateChannelPayload, JsonOptions),
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public async Task<DiscordNotificationEnqueueResult> NotifyMembersAsync(
        CommittedCompanyCommissionNotification notification,
        TradeCompanyCommission commission,
        Uri publicUrl,
        CancellationToken cancellationToken = default)
    {
        var attentionClass = CompanyCommissionNotificationPolicy.Classify(
            notification.EventKind);
        var validationError = ValidateNotification(notification);
        if (validationError != null ||
            !options.TryBuildCommissionUrl(
                notification.Commission.PublicBriefId,
                out var canonicalPublicUrl) ||
            !string.Equals(
                canonicalPublicUrl,
                publicUrl.AbsoluteUri,
                StringComparison.Ordinal))
        {
            return new(
                DiscordNotificationEnqueueStatus.Invalid,
                attentionClass,
                [],
                validationError ?? "The member notification destination is not canonical.");
        }

        if (memberships == null || identities == null)
        {
            return new(DiscordNotificationEnqueueStatus.Suppressed,
                attentionClass, []);
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var contacts = await notifications.LoadCommittedClaimContactDiscordUserIdsAsync(
            notification.CompanyId,
            notification.Commission.CommissionId,
            cancellationToken);
        foreach (var contact in contacts)
        {
            var identity = await identities.LoadByDiscordUserAsync(contact, cancellationToken);
            var membership = identity == null
                ? null
                : await memberships.LoadForAccountAsync(
                    notification.CompanyId,
                    identity.ProfileId,
                    cancellationToken);
            if (membership is not { NotificationsOptedOut: true })
            {
                ids.Add(contact);
            }
        }
        if (commission.ActiveClaim?.CrafterId is { } crafterId)
        {
            var membership = await memberships.LoadForAccountAsync(
                notification.CompanyId,
                crafterId,
                cancellationToken);
            if (membership is { State: MembershipState.Active, NotificationsOptedOut: false })
            {
                var identity = await identities.LoadByProfileAsync(crafterId, cancellationToken);
                if (identity != null)
                {
                    ids.Add(identity.DiscordUserId);
                }
            }
        }
        var route = await notifications.LoadRouteAsync(notification.CompanyId, cancellationToken);
        if (route == null || ids.Count == 0)
        {
            return new(DiscordNotificationEnqueueStatus.Suppressed,
                attentionClass, []);
        }
        var memberActivityUrl = CompanyCommissionNotificationLinks.BuildMemberActivityUrl(
            publicUrl,
            notification.CompanyId,
            notification.Commission.CommissionId,
            notification.EventId);
        var payload = CompanyCommissionDiscordMessage.CreateNotification(
            notification with { ActivityUrl = memberActivityUrl },
            attentionClass,
            DiscordNotificationMentionBehavior.NoPing,
            DiscordNotificationDestinationKind.MemberDirectMessage,
            string.Empty);
        return await notifications.EnqueueMemberAsync(
            notification.CompanyId,
            notification.Commission.CommissionId,
            notification.EventId,
            notification.CommissionRevision,
            attentionClass,
            route.Revision,
            JsonSerializer.Serialize(payload, JsonOptions),
            ids,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public Task CaptureDiscordClaimContactAsync(
        CommittedDiscordClaimContact contact,
        CancellationToken cancellationToken = default) =>
        notifications.CaptureCommittedClaimContactAsync(contact, cancellationToken);

    public Task<DiscordNotificationRouteUpdateResult> PutRouteAsync(
        CompanyId companyId,
        DiscordNotificationRouteUpdate update,
        CancellationToken cancellationToken = default) =>
        notifications.PutRouteAsync(
            companyId,
            update,
            timeProvider.GetUtcNow(),
            cancellationToken);

    public Task<DiscordNotificationRouteConfiguration?> LoadRouteAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        notifications.LoadRouteAsync(companyId, cancellationToken);

    public Task<IReadOnlyList<DiscordNotificationDiagnostic>> LoadDiagnosticsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        notifications.LoadDiagnosticsAsync(companyId, cancellationToken);

    public Task<bool> RetryDiagnosticAsync(
        CompanyId companyId,
        Guid diagnosticId,
        CancellationToken cancellationToken = default) =>
        notifications.RetryFailedAsync(
            companyId,
            diagnosticId,
            timeProvider.GetUtcNow(),
            cancellationToken);

    private string? ValidatePublicationProjection(
        CommittedCompanyCommissionDiscordProjection projection)
    {
        if (projection.CompanyId == default ||
            projection.Commission.CommissionId == Guid.Empty ||
            projection.EventId == Guid.Empty ||
            projection.ObjectRevision.Value <= 0 ||
            projection.CommissionRevision <= 0 ||
            projection.Commission.ProjectionRevision <= 0 ||
            projection.Commission.Terms.Version <= 0 ||
            string.IsNullOrWhiteSpace(projection.Commission.PublicBriefId) ||
            projection.Commission.PublicBriefId.Length > 64)
        {
            return "A committed canonical commission projection is required.";
        }

        if (!IsSafePublicUrl(projection.PublicViewUrl) ||
            !options.TryBuildCommissionUrl(
                projection.Commission.PublicBriefId,
                out var canonicalPublicUrl) ||
            !string.Equals(
                canonicalPublicUrl,
                projection.PublicViewUrl.AbsoluteUri,
                StringComparison.Ordinal) ||
            projection.StateRequiresClaimUrl() &&
            !IsSafeClaimUrl(projection.PublicViewUrl, projection.ClaimUrl))
        {
            return "The canonical public or claim URL is missing or unsafe.";
        }

        if (projection.CommittedAtUtc == default ||
            projection.Commission.ProjectionRevision <
            projection.CommissionRevision)
        {
            return "The Discord projection is not backed by the committed canonical event.";
        }

        return null;
    }

    private string? ValidateNotification(
        CommittedCompanyCommissionNotification notification)
    {
        if (notification.CompanyId == default ||
            notification.Commission.CommissionId == Guid.Empty ||
            notification.EventId == Guid.Empty ||
            notification.CommissionRevision <= 0 ||
            notification.Commission.ProjectionRevision < notification.CommissionRevision ||
            notification.CommittedAtUtc == default ||
            string.IsNullOrWhiteSpace(notification.Summary) ||
            string.IsNullOrWhiteSpace(notification.ActionLabel) ||
            !options.TryBuildCommissionUrl(
                notification.Commission.PublicBriefId,
                out var canonicalPublicUrl) ||
            !Uri.TryCreate(canonicalPublicUrl, UriKind.Absolute, out var canonicalPublicUri) ||
            !CompanyCommissionNotificationLinks.IsCanonicalOperatorActivityUrl(
                canonicalPublicUri,
                notification.ActivityUrl,
                notification.Commission.CommissionId,
                notification.EventId))
        {
            return "A sanitized notification backed by a committed canonical event is required.";
        }

        return null;
    }

    private static bool IsSafePublicUrl(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme is "https" or "http" &&
        (uri.Scheme == "https" || uri.IsLoopback) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsSafeClaimUrl(Uri publicViewUrl, Uri? claimUrl) =>
        claimUrl is { IsAbsoluteUri: true } &&
        claimUrl.Scheme is "https" or "http" &&
        (claimUrl.Scheme == "https" || claimUrl.IsLoopback) &&
        string.IsNullOrEmpty(claimUrl.UserInfo) &&
        string.Equals(publicViewUrl.Scheme, claimUrl.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(publicViewUrl.Host, claimUrl.Host, StringComparison.OrdinalIgnoreCase) &&
        publicViewUrl.Port == claimUrl.Port &&
        string.Equals(publicViewUrl.AbsolutePath, claimUrl.AbsolutePath, StringComparison.Ordinal) &&
        string.Equals(publicViewUrl.Query, claimUrl.Query, StringComparison.Ordinal) &&
        IsBoundedClaimFragment(claimUrl.Fragment);

    private static bool IsBoundedClaimFragment(string fragment)
    {
        const string prefix = "#claim=";
        if (!fragment.StartsWith(prefix, StringComparison.Ordinal) ||
            fragment.Length is < 23 or > 263)
        {
            return false;
        }

        var capability = fragment[prefix.Length..];
        return capability.Length is >= 16 and <= 256 &&
            capability.All(character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_');
    }

    private static DiscordPublicationCreateResult Conflict(string error) =>
        new(DiscordPublicationCreateStatus.Conflict, null, error);
}

internal static class CompanyCommissionDiscordProjectionValidation
{
    public static bool StateRequiresClaimUrl(
        this CommittedCompanyCommissionDiscordProjection projection) =>
        CompanyCommissionDiscordMessage.ResolveProjectionState(projection.Commission) ==
        DiscordPublicationState.Open;

}
