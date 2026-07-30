using System.Data;
using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

internal sealed record DiscordNotificationOutboxWorkItem(
    Guid WorkItemId,
    string LeaseId,
    CompanyId CompanyId,
    Guid CommissionId,
    Guid EventId,
    long DesiredProjectionRevision,
    DiscordNotificationAttentionClass AttentionClass,
    long RouteRevision,
    DiscordNotificationDestinationKind DestinationKind,
    string DestinationKey,
    string CommissionerDiscordUserId,
    string? AllowedMentionUserId,
    string? ChannelId,
    string PayloadJson,
    string? FallbackPayloadJson,
    string? FallbackAllowedMentionUserId,
    string? FallbackChannelId,
    bool IsFallback,
    int AttemptCount);

public sealed class SqliteDiscordNotificationStore(DiscordCommissionOptions options)
{
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS discord_notification_routes (
                    company_id TEXT PRIMARY KEY,
                    commissioner_user_id TEXT NOT NULL,
                    destination_mode INTEGER NOT NULL,
                    update_channel_id TEXT NULL,
                    dm_fallback INTEGER NOT NULL,
                    routine_behavior INTEGER NOT NULL,
                    action_required_behavior INTEGER NOT NULL,
                    critical_exception_behavior INTEGER NOT NULL,
                    revision INTEGER NOT NULL,
                    idempotency_key TEXT NOT NULL,
                    fingerprint TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS discord_notification_outbox (
                    work_item_id TEXT PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    commission_id TEXT NOT NULL,
                    event_id TEXT NOT NULL,
                    desired_projection_revision INTEGER NOT NULL,
                    attention_class INTEGER NOT NULL,
                    route_revision INTEGER NOT NULL,
                    destination_kind INTEGER NOT NULL,
                    destination_key TEXT NOT NULL,
                    commissioner_user_id TEXT NOT NULL,
                    allowed_mention_user_id TEXT NULL,
                    channel_id TEXT NULL,
                    payload_json TEXT NOT NULL,
                    fallback_payload_json TEXT NULL,
                    fallback_allowed_mention_user_id TEXT NULL,
                    fallback_channel_id TEXT NULL,
                    state INTEGER NOT NULL,
                    attempt_count INTEGER NOT NULL,
                    next_attempt_at_utc TEXT NOT NULL,
                    lease_id TEXT NULL,
                    lease_expires_at_utc TEXT NULL,
                    message_id TEXT NULL,
                    last_error TEXT NULL,
                    failure_code TEXT NULL,
                    fallback_source_id TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    UNIQUE (
                        event_id,
                        destination_kind,
                        destination_key,
                        desired_projection_revision
                    )
                );

                CREATE INDEX IF NOT EXISTS ix_discord_notification_outbox_due
                    ON discord_notification_outbox(state, next_attempt_at_utc);

                CREATE INDEX IF NOT EXISTS ix_discord_notification_outbox_diagnostics
                    ON discord_notification_outbox(company_id, state, updated_at_utc);

                CREATE TABLE IF NOT EXISTS discord_claim_contacts (
                    interaction_id TEXT PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    commission_id TEXT NOT NULL,
                    claim_event_id TEXT NOT NULL,
                    commission_revision INTEGER NOT NULL,
                    discord_user_id TEXT NOT NULL,
                    display_name_snapshot TEXT NOT NULL,
                    committed_at_utc TEXT NOT NULL,
                    UNIQUE(company_id, commission_id, discord_user_id)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public async Task<DiscordNotificationRouteConfiguration?> LoadRouteAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadRouteAsync(connection, companyId, cancellationToken);
    }

    public async Task<DiscordNotificationRouteUpdateResult> PutRouteAsync(
        CompanyId companyId,
        DiscordNotificationRouteUpdate update,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateRoute(update);
        if (validationError != null)
        {
            return new DiscordNotificationRouteUpdateResult(
                DiscordNotificationRouteUpdateStatus.Invalid,
                null,
                validationError);
        }

        var fingerprint = RouteFingerprint(update);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var existing = await LoadRouteStateAsync(
            connection,
            (SqliteTransaction)transaction,
            companyId,
            cancellationToken);
        if (existing != null &&
            string.Equals(existing.Value.IdempotencyKey, update.IdempotencyKey, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return string.Equals(existing.Value.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? new DiscordNotificationRouteUpdateResult(
                    DiscordNotificationRouteUpdateStatus.Replayed,
                    existing.Value.Configuration)
                : new DiscordNotificationRouteUpdateResult(
                    DiscordNotificationRouteUpdateStatus.Conflict,
                    existing.Value.Configuration,
                    "The route idempotency key is already bound to different settings.");
        }

        var currentRevision = existing?.Configuration.Revision ?? 0;
        if (currentRevision != update.ExpectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordNotificationRouteUpdateResult(
                DiscordNotificationRouteUpdateStatus.Conflict,
                existing?.Configuration,
                "The notification route changed before this update was applied.");
        }

        var configuration = new DiscordNotificationRouteConfiguration(
            companyId,
            update.CommissionerDiscordUserId.Trim(),
            update.DestinationMode,
            string.IsNullOrWhiteSpace(update.UpdateChannelId)
                ? null
                : update.UpdateChannelId.Trim(),
            update.DirectMessageFallback,
            update.RoutineBehavior,
            update.ActionRequiredBehavior,
            update.CriticalExceptionBehavior,
            checked(currentRevision + 1),
            now);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO discord_notification_routes (
                company_id,
                commissioner_user_id,
                destination_mode,
                update_channel_id,
                dm_fallback,
                routine_behavior,
                action_required_behavior,
                critical_exception_behavior,
                revision,
                idempotency_key,
                fingerprint,
                updated_at_utc
            )
            VALUES (
                $companyId,
                $commissionerUserId,
                $destinationMode,
                $updateChannelId,
                $dmFallback,
                $routineBehavior,
                $actionRequiredBehavior,
                $criticalExceptionBehavior,
                $revision,
                $idempotencyKey,
                $fingerprint,
                $updatedAt
            )
            ON CONFLICT(company_id) DO UPDATE SET
                commissioner_user_id = excluded.commissioner_user_id,
                destination_mode = excluded.destination_mode,
                update_channel_id = excluded.update_channel_id,
                dm_fallback = excluded.dm_fallback,
                routine_behavior = excluded.routine_behavior,
                action_required_behavior = excluded.action_required_behavior,
                critical_exception_behavior = excluded.critical_exception_behavior,
                revision = excluded.revision,
                idempotency_key = excluded.idempotency_key,
                fingerprint = excluded.fingerprint,
                updated_at_utc = excluded.updated_at_utc
            WHERE discord_notification_routes.revision = $expectedRevision;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue(
            "$commissionerUserId",
            configuration.CommissionerDiscordUserId);
        command.Parameters.AddWithValue("$destinationMode", (int)configuration.DestinationMode);
        command.Parameters.AddWithValue(
            "$updateChannelId",
            configuration.UpdateChannelId is { } channelId
                ? channelId
                : DBNull.Value);
        command.Parameters.AddWithValue("$dmFallback", (int)configuration.DirectMessageFallback);
        command.Parameters.AddWithValue("$routineBehavior", (int)configuration.RoutineBehavior);
        command.Parameters.AddWithValue(
            "$actionRequiredBehavior",
            (int)configuration.ActionRequiredBehavior);
        command.Parameters.AddWithValue(
            "$criticalExceptionBehavior",
            (int)configuration.CriticalExceptionBehavior);
        command.Parameters.AddWithValue("$revision", configuration.Revision);
        command.Parameters.AddWithValue("$idempotencyKey", update.IdempotencyKey.Trim());
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        command.Parameters.AddWithValue("$expectedRevision", update.ExpectedRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordNotificationRouteUpdateResult(
                DiscordNotificationRouteUpdateStatus.Conflict,
                existing?.Configuration,
                "The notification route changed before this update was applied.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new DiscordNotificationRouteUpdateResult(
            DiscordNotificationRouteUpdateStatus.Applied,
            configuration);
    }

    internal async Task<DiscordNotificationEnqueueResult> EnqueueAsync(
        CompanyId companyId,
        Guid commissionId,
        Guid eventId,
        long desiredProjectionRevision,
        DiscordNotificationAttentionClass attentionClass,
        long expectedRouteRevision,
        string directMessagePayloadJson,
        string updateChannelPayloadJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (commissionId == Guid.Empty ||
            eventId == Guid.Empty ||
            desiredProjectionRevision <= 0 ||
            string.IsNullOrWhiteSpace(directMessagePayloadJson) ||
            string.IsNullOrWhiteSpace(updateChannelPayloadJson))
        {
            return new DiscordNotificationEnqueueResult(
                DiscordNotificationEnqueueStatus.Invalid,
                attentionClass,
                [],
                "A committed commission event, projection revision, and payload are required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var route = await LoadRouteAsync(
            connection,
            companyId,
            cancellationToken,
            (SqliteTransaction)transaction);
        if (route == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordNotificationEnqueueResult(
                DiscordNotificationEnqueueStatus.Unconfigured,
                attentionClass,
                [],
                "Commissioner Discord notification routing is not configured.");
        }

        if (route.Revision != expectedRouteRevision)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordNotificationEnqueueResult(
                DiscordNotificationEnqueueStatus.Invalid,
                attentionClass,
                [],
                "The notification route changed while this event was being projected.");
        }

        var behavior = CompanyCommissionNotificationPolicy.ResolveBehavior(
            route,
            attentionClass);
        if (behavior == DiscordNotificationMentionBehavior.Off)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordNotificationEnqueueResult(
                DiscordNotificationEnqueueStatus.Suppressed,
                attentionClass,
                []);
        }

        var destinations = ResolveDestinations(route);
        if (destinations.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordNotificationEnqueueResult(
                DiscordNotificationEnqueueStatus.Invalid,
                attentionClass,
                [],
                "The configured notification route does not have a usable destination.");
        }

        var workItemIds = new List<Guid>(destinations.Count);
        var inserted = false;
        foreach (var destination in destinations)
        {
            var workItemId = Guid.NewGuid();
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO discord_notification_outbox (
                    work_item_id,
                    company_id,
                    commission_id,
                    event_id,
                    desired_projection_revision,
                    attention_class,
                    route_revision,
                    destination_kind,
                    destination_key,
                    commissioner_user_id,
                    allowed_mention_user_id,
                    channel_id,
                    payload_json,
                    fallback_payload_json,
                    fallback_allowed_mention_user_id,
                    fallback_channel_id,
                    state,
                    attempt_count,
                    next_attempt_at_utc,
                    created_at_utc,
                    updated_at_utc
                )
                VALUES (
                    $workItemId,
                    $companyId,
                    $commissionId,
                    $eventId,
                    $desiredProjectionRevision,
                    $attentionClass,
                    $routeRevision,
                    $destinationKind,
                    $destinationKey,
                    $commissionerUserId,
                    $allowedMentionUserId,
                    $channelId,
                    $payloadJson,
                    $fallbackPayloadJson,
                    $fallbackAllowedMentionUserId,
                    $fallbackChannelId,
                    $state,
                    0,
                    $now,
                    $now,
                    $now
                );
                """;
            command.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));
            command.Parameters.AddWithValue("$companyId", companyId.ToString());
            command.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
            command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
            command.Parameters.AddWithValue(
                "$desiredProjectionRevision",
                desiredProjectionRevision);
            command.Parameters.AddWithValue("$attentionClass", (int)attentionClass);
            command.Parameters.AddWithValue("$routeRevision", route.Revision);
            command.Parameters.AddWithValue("$destinationKind", (int)destination.Kind);
            command.Parameters.AddWithValue("$destinationKey", destination.Key);
            command.Parameters.AddWithValue(
                "$commissionerUserId",
                route.CommissionerDiscordUserId);
            command.Parameters.AddWithValue(
                "$allowedMentionUserId",
                destination.Kind == DiscordNotificationDestinationKind.UpdateChannel &&
                (behavior is
                    DiscordNotificationMentionBehavior.Push or
                    DiscordNotificationMentionBehavior.SilentPing)
                        ? route.CommissionerDiscordUserId
                        : DBNull.Value);
            command.Parameters.AddWithValue(
                "$channelId",
                destination.ChannelId is { } channelId ? channelId : DBNull.Value);
            command.Parameters.AddWithValue(
                "$payloadJson",
                destination.Kind ==
                    DiscordNotificationDestinationKind.CommissionerDirectMessage
                        ? directMessagePayloadJson
                        : updateChannelPayloadJson);
            command.Parameters.AddWithValue(
                "$fallbackPayloadJson",
                destination.Kind ==
                    DiscordNotificationDestinationKind.CommissionerDirectMessage
                        ? updateChannelPayloadJson
                        : DBNull.Value);
            command.Parameters.AddWithValue(
                "$fallbackAllowedMentionUserId",
                destination.Kind ==
                    DiscordNotificationDestinationKind.CommissionerDirectMessage &&
                (behavior is
                    DiscordNotificationMentionBehavior.Push or
                    DiscordNotificationMentionBehavior.SilentPing)
                        ? route.CommissionerDiscordUserId
                        : DBNull.Value);
            command.Parameters.AddWithValue(
                "$fallbackChannelId",
                destination.Kind ==
                    DiscordNotificationDestinationKind.CommissionerDirectMessage &&
                route.DirectMessageFallback == DiscordDirectMessageFallback.UpdateChannel &&
                route.UpdateChannelId is { } fallbackChannelId
                    ? fallbackChannelId
                    : DBNull.Value);
            command.Parameters.AddWithValue("$state", (int)DiscordOutboxState.Pending);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                inserted = true;
                workItemIds.Add(workItemId);
            }
            else
            {
                var existingId = await LoadExistingWorkItemIdAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    eventId,
                    desiredProjectionRevision,
                    destination.Kind,
                    destination.Key,
                    cancellationToken);
                if (existingId.HasValue)
                {
                    workItemIds.Add(existingId.Value);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new DiscordNotificationEnqueueResult(
            inserted
                ? DiscordNotificationEnqueueStatus.Queued
                : DiscordNotificationEnqueueStatus.Replayed,
            attentionClass,
            workItemIds);
    }

    public async Task CaptureCommittedClaimContactAsync(
        CommittedDiscordClaimContact contact,
        CancellationToken cancellationToken = default)
    {
        if (contact.EventKind != CompanyCommissionActivityKind.ClaimAccepted ||
            contact.ClaimEventId == Guid.Empty ||
            contact.CommissionId == Guid.Empty ||
            contact.CommissionRevision <= 0 ||
            !DiscordSnowflake.IsValid(contact.InteractionId) ||
            !DiscordSnowflake.IsValid(contact.Contact.DiscordUserId))
        {
            throw new InvalidOperationException(
                "Discord contact capture requires an already-committed canonical claim event.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO discord_claim_contacts (
                interaction_id,
                company_id,
                commission_id,
                claim_event_id,
                commission_revision,
                discord_user_id,
                display_name_snapshot,
                committed_at_utc
            )
            VALUES (
                $interactionId,
                $companyId,
                $commissionId,
                $claimEventId,
                $commissionRevision,
                $discordUserId,
                $displayNameSnapshot,
                $committedAt
            )
            ON CONFLICT(interaction_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$interactionId", contact.InteractionId);
        command.Parameters.AddWithValue("$companyId", contact.CompanyId.ToString());
        command.Parameters.AddWithValue(
            "$commissionId",
            contact.CommissionId.ToString("D"));
        command.Parameters.AddWithValue(
            "$claimEventId",
            contact.ClaimEventId.ToString("D"));
        command.Parameters.AddWithValue(
            "$commissionRevision",
            contact.CommissionRevision);
        command.Parameters.AddWithValue(
            "$discordUserId",
            contact.Contact.DiscordUserId);
        command.Parameters.AddWithValue(
            "$displayNameSnapshot",
            DiscordProjectionSanitizer.Text(contact.Contact.DisplayNameSnapshot, 120));
        command.Parameters.AddWithValue(
            "$committedAt",
            contact.CommittedAtUtc.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<DiscordNotificationOutboxWorkItem>> LeaseDueAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText =
            """
            SELECT
                work_item_id,
                company_id,
                commission_id,
                event_id,
                desired_projection_revision,
                attention_class,
                route_revision,
                destination_kind,
                destination_key,
                commissioner_user_id,
                allowed_mention_user_id,
                channel_id,
                payload_json,
                fallback_payload_json,
                fallback_allowed_mention_user_id,
                fallback_channel_id,
                fallback_source_id IS NOT NULL,
                attempt_count
            FROM discord_notification_outbox
            WHERE (
                    state IN ($pending, $retry)
                    AND next_attempt_at_utc <= $now
                )
                OR (
                    state = $inFlight
                    AND lease_expires_at_utc <= $now
                )
            ORDER BY created_at_utc
            LIMIT $maximumCount;
            """;
        select.Parameters.AddWithValue("$pending", (int)DiscordOutboxState.Pending);
        select.Parameters.AddWithValue("$retry", (int)DiscordOutboxState.Retry);
        select.Parameters.AddWithValue("$inFlight", (int)DiscordOutboxState.InFlight);
        select.Parameters.AddWithValue("$now", now.ToString("O"));
        select.Parameters.AddWithValue("$maximumCount", Math.Clamp(maximumCount, 1, 100));
        var candidates = new List<DiscordNotificationOutboxWorkItem>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new DiscordNotificationOutboxWorkItem(
                    Guid.Parse(reader.GetString(0)),
                    string.Empty,
                    CompanyId.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)),
                    Guid.Parse(reader.GetString(3)),
                    reader.GetInt64(4),
                    (DiscordNotificationAttentionClass)reader.GetInt32(5),
                    reader.GetInt64(6),
                    (DiscordNotificationDestinationKind)reader.GetInt32(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15),
                    reader.GetInt64(16) != 0,
                    reader.GetInt32(17)));
            }
        }

        var leased = new List<DiscordNotificationOutboxWorkItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var leaseId = Guid.NewGuid().ToString("N");
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE discord_notification_outbox
                SET
                    state = $inFlight,
                    attempt_count = attempt_count + 1,
                    lease_id = $leaseId,
                    lease_expires_at_utc = $leaseExpiresAt,
                    updated_at_utc = $now
                WHERE work_item_id = $workItemId
                  AND state IN ($pending, $retry, $alreadyInFlight);
                """;
            update.Parameters.AddWithValue("$inFlight", (int)DiscordOutboxState.InFlight);
            update.Parameters.AddWithValue("$leaseId", leaseId);
            update.Parameters.AddWithValue(
                "$leaseExpiresAt",
                (now + leaseDuration).ToString("O"));
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$workItemId", candidate.WorkItemId.ToString("D"));
            update.Parameters.AddWithValue("$pending", (int)DiscordOutboxState.Pending);
            update.Parameters.AddWithValue("$retry", (int)DiscordOutboxState.Retry);
            update.Parameters.AddWithValue(
                "$alreadyInFlight",
                (int)DiscordOutboxState.InFlight);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                leased.Add(candidate with
                {
                    LeaseId = leaseId,
                    AttemptCount = candidate.AttemptCount + 1
                });
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return leased;
    }

    internal async Task<bool> MatchesCurrentRouteAsync(
        DiscordNotificationOutboxWorkItem workItem,
        CancellationToken cancellationToken = default)
    {
        var route = await LoadRouteAsync(workItem.CompanyId, cancellationToken);
        if (route == null ||
            route.Revision != workItem.RouteRevision ||
            !string.Equals(
                route.CommissionerDiscordUserId,
                workItem.CommissionerDiscordUserId,
                StringComparison.Ordinal) ||
            CompanyCommissionNotificationPolicy.ResolveBehavior(
                route,
                workItem.AttentionClass) == DiscordNotificationMentionBehavior.Off)
        {
            return false;
        }

        return workItem.DestinationKind switch
        {
            DiscordNotificationDestinationKind.CommissionerDirectMessage =>
                route.DestinationMode is
                    DiscordNotificationDestinationMode.CommissionerDirectMessage or
                    DiscordNotificationDestinationMode.Both,
            DiscordNotificationDestinationKind.UpdateChannel =>
                ((route.DestinationMode is
                        DiscordNotificationDestinationMode.UpdateChannel or
                        DiscordNotificationDestinationMode.Both) ||
                    workItem.IsFallback &&
                    route.DirectMessageFallback == DiscordDirectMessageFallback.UpdateChannel) &&
                string.Equals(
                    route.UpdateChannelId,
                    workItem.ChannelId,
                    StringComparison.Ordinal),
            _ => false
        };
    }

    internal async Task SetResolvedChannelAsync(
        Guid workItemId,
        string leaseId,
        string channelId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE discord_notification_outbox
            SET channel_id = $channelId, updated_at_utc = $now
            WHERE work_item_id = $workItemId
              AND state = $inFlight
              AND lease_id = $leaseId;
            """;
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));
        command.Parameters.AddWithValue("$inFlight", (int)DiscordOutboxState.InFlight);
        command.Parameters.AddWithValue("$leaseId", leaseId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal Task CompleteAsync(
        Guid workItemId,
        string leaseId,
        string? messageId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        UpdateStateAsync(
            workItemId,
            leaseId,
            DiscordOutboxState.Succeeded,
            now,
            messageId,
            null,
            null,
            cancellationToken);

    internal Task RetryAsync(
        Guid workItemId,
        string leaseId,
        DateTimeOffset nextAttemptAt,
        string error,
        CancellationToken cancellationToken = default) =>
        UpdateStateAsync(
            workItemId,
            leaseId,
            DiscordOutboxState.Retry,
            nextAttemptAt,
            null,
            "transient_delivery_failure",
            error,
            cancellationToken);

    internal async Task FailAsync(
        DiscordNotificationOutboxWorkItem workItem,
        DiscordOutboxState state,
        string failureCode,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (!await UpdateStateAsync(
                connection,
                (SqliteTransaction)transaction,
                workItem.WorkItemId,
                workItem.LeaseId,
                state,
                now,
                null,
                failureCode,
                error,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        if (workItem.DestinationKind ==
                DiscordNotificationDestinationKind.CommissionerDirectMessage &&
            state == DiscordOutboxState.Failed &&
            (failureCode is
                "dm_channel_unavailable" or
                "discord_delivery_failed" or
                "retry_budget_exhausted") &&
            DiscordSnowflake.IsValid(workItem.FallbackChannelId))
        {
            await InsertFallbackAsync(
                connection,
                (SqliteTransaction)transaction,
                workItem,
                workItem.FallbackChannelId!,
                now,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DiscordNotificationDiagnostic>> LoadDiagnosticsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                o.work_item_id,
                o.commission_id,
                o.event_id,
                o.desired_projection_revision,
                o.destination_kind,
                o.state,
                o.failure_code,
                o.last_error,
                o.updated_at_utc,
                EXISTS (
                    SELECT 1
                    FROM discord_notification_outbox fallback
                    WHERE fallback.event_id = o.event_id
                      AND fallback.desired_projection_revision =
                          o.desired_projection_revision
                      AND fallback.destination_kind = $updateChannel
                      AND fallback.work_item_id <> o.work_item_id
                )
            FROM discord_notification_outbox o
            WHERE o.company_id = $companyId
              AND o.state IN ($failed, $reconciliationRequired)
            ORDER BY o.updated_at_utc DESC;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$failed", (int)DiscordOutboxState.Failed);
        command.Parameters.AddWithValue(
            "$reconciliationRequired",
            (int)DiscordOutboxState.ReconciliationRequired);
        command.Parameters.AddWithValue(
            "$updateChannel",
            (int)DiscordNotificationDestinationKind.UpdateChannel);
        var diagnostics = new List<DiscordNotificationDiagnostic>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = (DiscordOutboxState)reader.GetInt32(5);
            var fallbackQueued = reader.GetInt64(9) != 0;
            var destination =
                (DiscordNotificationDestinationKind)reader.GetInt32(4);
            diagnostics.Add(new DiscordNotificationDiagnostic(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetInt64(3),
                destination,
                state == DiscordOutboxState.ReconciliationRequired
                    ? DiscordNotificationDiagnosticState.ReconciliationRequired
                    : DiscordNotificationDiagnosticState.Failed,
                DiagnosticSummary(
                    destination,
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    fallbackQueued),
                reader.IsDBNull(7)
                    ? "Discord delivery failed without a response detail."
                    : reader.GetString(7),
                RecommendedAction(state, fallbackQueued),
                state == DiscordOutboxState.Failed,
                fallbackQueued,
                DateTimeOffset.Parse(reader.GetString(8))));
        }

        return diagnostics;
    }

    public async Task<bool> RetryFailedAsync(
        CompanyId companyId,
        Guid diagnosticId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE discord_notification_outbox
            SET
                state = $pending,
                attempt_count = 0,
                next_attempt_at_utc = $now,
                lease_id = NULL,
                lease_expires_at_utc = NULL,
                last_error = NULL,
                failure_code = NULL,
                updated_at_utc = $now
            WHERE work_item_id = $workItemId
              AND company_id = $companyId
              AND state = $failed;
            """;
        command.Parameters.AddWithValue("$pending", (int)DiscordOutboxState.Pending);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$workItemId", diagnosticId.ToString("D"));
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$failed", (int)DiscordOutboxState.Failed);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task UpdateStateAsync(
        Guid workItemId,
        string leaseId,
        DiscordOutboxState state,
        DateTimeOffset now,
        string? messageId,
        string? failureCode,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await UpdateStateAsync(
            connection,
            (SqliteTransaction)transaction,
            workItemId,
            leaseId,
            state,
            now,
            messageId,
            failureCode,
            error,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> UpdateStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workItemId,
        string leaseId,
        DiscordOutboxState state,
        DateTimeOffset now,
        string? messageId,
        string? failureCode,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE discord_notification_outbox
            SET
                state = $state,
                next_attempt_at_utc = $now,
                lease_id = NULL,
                lease_expires_at_utc = NULL,
                message_id = COALESCE($messageId, message_id),
                failure_code = $failureCode,
                last_error = $lastError,
                updated_at_utc = $now
            WHERE work_item_id = $workItemId
              AND state = $inFlight
              AND lease_id = $leaseId;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue(
            "$messageId",
            string.IsNullOrWhiteSpace(messageId) ? DBNull.Value : messageId);
        command.Parameters.AddWithValue(
            "$failureCode",
            string.IsNullOrWhiteSpace(failureCode) ? DBNull.Value : failureCode);
        command.Parameters.AddWithValue(
            "$lastError",
            string.IsNullOrWhiteSpace(error)
                ? DBNull.Value
                : DiscordProjectionSanitizer.Text(error, 512));
        command.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));
        command.Parameters.AddWithValue("$inFlight", (int)DiscordOutboxState.InFlight);
        command.Parameters.AddWithValue("$leaseId", leaseId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task InsertFallbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscordNotificationOutboxWorkItem source,
        string channelId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO discord_notification_outbox (
                work_item_id,
                company_id,
                commission_id,
                event_id,
                desired_projection_revision,
                attention_class,
                route_revision,
                destination_kind,
                destination_key,
                commissioner_user_id,
                allowed_mention_user_id,
                channel_id,
                payload_json,
                state,
                attempt_count,
                next_attempt_at_utc,
                fallback_source_id,
                created_at_utc,
                updated_at_utc
            )
            VALUES (
                $workItemId,
                $companyId,
                $commissionId,
                $eventId,
                $desiredProjectionRevision,
                $attentionClass,
                $routeRevision,
                $destinationKind,
                $destinationKey,
                $commissionerUserId,
                $allowedMentionUserId,
                $channelId,
                $payloadJson,
                $state,
                0,
                $now,
                $fallbackSourceId,
                $now,
                $now
            );
            """;
        command.Parameters.AddWithValue("$workItemId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$companyId", source.CompanyId.ToString());
        command.Parameters.AddWithValue("$commissionId", source.CommissionId.ToString("D"));
        command.Parameters.AddWithValue("$eventId", source.EventId.ToString("D"));
        command.Parameters.AddWithValue(
            "$desiredProjectionRevision",
            source.DesiredProjectionRevision);
        command.Parameters.AddWithValue("$attentionClass", (int)source.AttentionClass);
        command.Parameters.AddWithValue("$routeRevision", source.RouteRevision);
        command.Parameters.AddWithValue(
            "$destinationKind",
            (int)DiscordNotificationDestinationKind.UpdateChannel);
        command.Parameters.AddWithValue("$destinationKey", "channel:" + channelId);
        command.Parameters.AddWithValue(
            "$commissionerUserId",
            source.CommissionerDiscordUserId);
        command.Parameters.AddWithValue(
            "$allowedMentionUserId",
            source.FallbackAllowedMentionUserId is { } allowedMentionUserId
                ? allowedMentionUserId
                : DBNull.Value);
        command.Parameters.AddWithValue("$channelId", channelId);
        command.Parameters.AddWithValue(
            "$payloadJson",
            source.FallbackPayloadJson ?? source.PayloadJson);
        command.Parameters.AddWithValue("$state", (int)DiscordOutboxState.Pending);
        command.Parameters.AddWithValue(
            "$fallbackSourceId",
            source.WorkItemId.ToString("D"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await OpenConnectionAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var absolutePath = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = absolutePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA journal_mode = WAL;
            """;
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<DiscordNotificationRouteConfiguration?> LoadRouteAsync(
        SqliteConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var state = await LoadRouteStateAsync(
            connection,
            transaction,
            companyId,
            cancellationToken);
        return state?.Configuration;
    }

    private static async Task<(
        DiscordNotificationRouteConfiguration Configuration,
        string IdempotencyKey,
        string Fingerprint)?> LoadRouteStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                commissioner_user_id,
                destination_mode,
                update_channel_id,
                dm_fallback,
                routine_behavior,
                action_required_behavior,
                critical_exception_behavior,
                revision,
                idempotency_key,
                fingerprint,
                updated_at_utc
            FROM discord_notification_routes
            WHERE company_id = $companyId;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            new DiscordNotificationRouteConfiguration(
                companyId,
                reader.GetString(0),
                (DiscordNotificationDestinationMode)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                (DiscordDirectMessageFallback)reader.GetInt32(3),
                (DiscordNotificationMentionBehavior)reader.GetInt32(4),
                (DiscordNotificationMentionBehavior)reader.GetInt32(5),
                (DiscordNotificationMentionBehavior)reader.GetInt32(6),
                reader.GetInt64(7),
                DateTimeOffset.Parse(reader.GetString(10))),
            reader.GetString(8),
            reader.GetString(9));
    }

    private static async Task<Guid?> LoadExistingWorkItemIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid eventId,
        long desiredProjectionRevision,
        DiscordNotificationDestinationKind destinationKind,
        string destinationKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT work_item_id
            FROM discord_notification_outbox
            WHERE event_id = $eventId
              AND desired_projection_revision = $desiredProjectionRevision
              AND destination_kind = $destinationKind
              AND destination_key = $destinationKey;
            """;
        command.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        command.Parameters.AddWithValue(
            "$desiredProjectionRevision",
            desiredProjectionRevision);
        command.Parameters.AddWithValue("$destinationKind", (int)destinationKind);
        command.Parameters.AddWithValue("$destinationKey", destinationKey);
        return Guid.TryParse(
            (string?)await command.ExecuteScalarAsync(cancellationToken),
            out var id)
                ? id
                : null;
    }

    private static string? ValidateRoute(DiscordNotificationRouteUpdate update)
    {
        if (!DiscordSnowflake.IsValid(update.CommissionerDiscordUserId))
        {
            return "A stable commissioner Discord user ID is required.";
        }

        if (update.ExpectedRevision < 0 ||
            string.IsNullOrWhiteSpace(update.IdempotencyKey) ||
            update.IdempotencyKey.Length > 120)
        {
            return "A non-negative expected revision and bounded idempotency key are required.";
        }

        var needsChannel =
            update.DestinationMode is
                DiscordNotificationDestinationMode.UpdateChannel or
                DiscordNotificationDestinationMode.Both ||
            update.DirectMessageFallback == DiscordDirectMessageFallback.UpdateChannel;
        if (needsChannel && !DiscordSnowflake.IsValid(update.UpdateChannelId))
        {
            return "The selected route or DM fallback requires a configured update channel.";
        }

        if (update.DirectMessageFallback == DiscordDirectMessageFallback.UpdateChannel &&
            update.DestinationMode == DiscordNotificationDestinationMode.UpdateChannel)
        {
            return "A DM fallback requires a route that actually attempts commissioner DM delivery.";
        }

        if (!Enum.IsDefined(update.DestinationMode) ||
            !Enum.IsDefined(update.DirectMessageFallback) ||
            !Enum.IsDefined(update.RoutineBehavior) ||
            !Enum.IsDefined(update.ActionRequiredBehavior) ||
            !Enum.IsDefined(update.CriticalExceptionBehavior))
        {
            return "The notification route contains an unsupported policy value.";
        }

        return null;
    }

    private static string RouteFingerprint(DiscordNotificationRouteUpdate update)
    {
        var canonical = string.Join(
            "|",
            update.CommissionerDiscordUserId.Trim(),
            (int)update.DestinationMode,
            update.UpdateChannelId?.Trim() ?? string.Empty,
            (int)update.DirectMessageFallback,
            (int)update.RoutineBehavior,
            (int)update.ActionRequiredBehavior,
            (int)update.CriticalExceptionBehavior,
            update.ExpectedRevision);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static IReadOnlyList<(
        DiscordNotificationDestinationKind Kind,
        string Key,
        string? ChannelId)> ResolveDestinations(
        DiscordNotificationRouteConfiguration route)
    {
        var destinations = new List<(
            DiscordNotificationDestinationKind,
            string,
            string?)>(2);
        if (route.DestinationMode is
            DiscordNotificationDestinationMode.CommissionerDirectMessage or
            DiscordNotificationDestinationMode.Both)
        {
            destinations.Add((
                DiscordNotificationDestinationKind.CommissionerDirectMessage,
                "dm:" + route.CommissionerDiscordUserId,
                null));
        }

        if ((route.DestinationMode is
                DiscordNotificationDestinationMode.UpdateChannel or
                DiscordNotificationDestinationMode.Both) &&
            route.UpdateChannelId is { } channelId)
        {
            destinations.Add((
                DiscordNotificationDestinationKind.UpdateChannel,
                "channel:" + channelId,
                channelId));
        }

        return destinations;
    }

    private static string DiagnosticSummary(
        DiscordNotificationDestinationKind destination,
        string? failureCode,
        bool fallbackQueued)
    {
        var target = destination ==
            DiscordNotificationDestinationKind.CommissionerDirectMessage
                ? "Commissioner DM delivery"
                : "Commission update-channel delivery";
        var result = $"{target} failed ({failureCode ?? "delivery_failure"}).";
        return fallbackQueued
            ? result + " The explicitly configured update-channel fallback was queued."
            : result;
    }

    private static string RecommendedAction(
        DiscordOutboxState state,
        bool fallbackQueued)
    {
        if (state == DiscordOutboxState.ReconciliationRequired)
        {
            return "Confirm whether Discord created the message before attempting any resend.";
        }

        return fallbackQueued
            ? "Review the DM route; the configured fallback is handling this event."
            : "Correct the recipient, channel, or bot permissions, then retry this delivery.";
    }
}
