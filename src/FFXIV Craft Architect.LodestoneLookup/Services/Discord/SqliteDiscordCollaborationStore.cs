using System.Data;
using System.Security.Cryptography;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed class SqliteDiscordCollaborationStore(
    DiscordCommissionOptions options)
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
            await EnsureCompatibleSchemaAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS discord_company_installations (
                    company_id TEXT PRIMARY KEY,
                    application_id TEXT NOT NULL,
                    guild_id TEXT NOT NULL,
                    channel_id TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS discord_publications (
                    publication_id TEXT PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    order_id TEXT NOT NULL,
                    source_order_revision INTEGER NOT NULL,
                    public_id TEXT NOT NULL,
                    brief_version INTEGER NOT NULL,
                    channel_id TEXT NOT NULL,
                    message_id TEXT NULL,
                    action_token TEXT NOT NULL UNIQUE,
                    state INTEGER NOT NULL,
                    desired_projection_revision INTEGER NOT NULL,
                    idempotency_key TEXT NOT NULL UNIQUE,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );

                DROP INDEX IF EXISTS ux_discord_publications_active_order;

                CREATE UNIQUE INDEX ux_discord_publications_active_order
                    ON discord_publications(company_id, order_id)
                    WHERE state IN (0, 1, 6);

                CREATE UNIQUE INDEX IF NOT EXISTS ux_discord_publications_message
                    ON discord_publications(channel_id, message_id)
                    WHERE message_id IS NOT NULL;

                CREATE TABLE IF NOT EXISTS discord_interest_claims (
                    claim_id TEXT PRIMARY KEY,
                    publication_id TEXT NOT NULL,
                    company_id TEXT NOT NULL,
                    order_id TEXT NOT NULL,
                    discord_user_id TEXT NOT NULL,
                    discord_display_name TEXT NOT NULL,
                    state INTEGER NOT NULL,
                    resolved_crafter_id TEXT NULL,
                    accepted_order_revision INTEGER NULL,
                    resolution_idempotency_key TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    resolved_at_utc TEXT NULL,
                    FOREIGN KEY (publication_id) REFERENCES discord_publications(publication_id),
                    UNIQUE (publication_id, discord_user_id)
                );

                CREATE TABLE IF NOT EXISTS discord_outbox (
                    work_item_id TEXT PRIMARY KEY,
                    publication_id TEXT NOT NULL,
                    dedupe_key TEXT NOT NULL UNIQUE,
                    operation INTEGER NOT NULL,
                    channel_id TEXT NOT NULL,
                    message_id TEXT NULL,
                    payload_json TEXT NOT NULL,
                    desired_projection_revision INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    attempt_count INTEGER NOT NULL,
                    next_attempt_at_utc TEXT NOT NULL,
                    lease_id TEXT NULL,
                    lease_expires_at_utc TEXT NULL,
                    last_error TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    FOREIGN KEY (publication_id) REFERENCES discord_publications(publication_id)
                );

                CREATE INDEX IF NOT EXISTS ix_discord_outbox_due
                    ON discord_outbox(state, next_attempt_at_utc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (options.HasBootstrapInstallation)
            {
                await UpsertCompanyInstallationAsync(
                    connection,
                    new DiscordCompanyInstallationBinding(
                        CompanyId.Parse(options.CompanyId),
                        options.ApplicationId,
                        options.AllowedGuildId,
                        options.AllowedChannelId,
                        DateTimeOffset.UtcNow),
                    cancellationToken);
            }
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public async Task<DiscordCompanyInstallationBinding?> ResolveCompanyInstallationAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadCompanyInstallationAsync(
            connection,
            companyId,
            cancellationToken);
    }

    public async Task<bool> MatchesCurrentCompanyInstallationAsync(
        CompanyId companyId,
        string channelId,
        CancellationToken cancellationToken = default)
    {
        var installation = await ResolveCompanyInstallationAsync(
            companyId,
            cancellationToken);
        return installation != null &&
            string.Equals(
                installation.ChannelId,
                channelId,
                StringComparison.Ordinal);
    }

    public async Task<DiscordPublicationCreateResult> CreatePublicationAsync(
        TradeCompanyPublicationOwnership ownership,
        string publicId,
        int briefVersion,
        string idempotencyKey,
        string actionToken,
        string channelId,
        DiscordPublicationState initialState,
        string initialPayloadJson,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialPayloadJson);
        if (!options.CanPublishDirectly ||
            !IsDiscordSnowflake(channelId) ||
            ownership.OrderId == Guid.Empty ||
            briefVersion <= 0 ||
            actionToken.Length > 100 ||
            !actionToken.StartsWith("ca:v1:", StringComparison.Ordinal))
        {
            return new DiscordPublicationCreateResult(
                DiscordPublicationCreateStatus.Conflict,
                null,
                "The canonical ownership and configured Discord destination do not match.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var installation = await LoadCompanyInstallationAsync(
            connection,
            ownership.CompanyId,
            cancellationToken,
            (SqliteTransaction)transaction);
        if (installation == null ||
            !string.Equals(
                installation.ChannelId,
                channelId,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordPublicationCreateResult(
                DiscordPublicationCreateStatus.Conflict,
                null,
                "The Discord destination does not match the server-owned company installation.");
        }

        var replay = await LoadPublicationByIdempotencyAsync(
            connection,
            (SqliteTransaction)transaction,
            idempotencyKey,
            cancellationToken);
        if (replay != null)
        {
            await transaction.RollbackAsync(cancellationToken);
            var matches =
                replay.CompanyId == ownership.CompanyId &&
                replay.OrderId == ownership.OrderId &&
                replay.SourceOrderRevision == ownership.OrderRevision &&
                string.Equals(replay.PublicId, publicId, StringComparison.Ordinal) &&
                string.Equals(replay.ChannelId, channelId, StringComparison.Ordinal);
            return new DiscordPublicationCreateResult(
                matches
                    ? DiscordPublicationCreateStatus.Replayed
                    : DiscordPublicationCreateStatus.Conflict,
                matches ? replay : null,
                matches ? null : "The idempotency key is already bound to another publication.");
        }

        var publicationId = Guid.NewGuid();
        var publication = new DiscordPublicationRecord(
            publicationId,
            ownership.CompanyId,
            ownership.OrderId,
            ownership.OrderRevision,
            publicId,
            briefVersion,
            channelId,
            null,
            actionToken,
            initialState,
            1,
            idempotencyKey,
            createdAt,
            createdAt);

        try
        {
            await InsertPublicationAsync(
                connection,
                (SqliteTransaction)transaction,
                publication,
                cancellationToken);
            await InsertOutboxAsync(
                connection,
                (SqliteTransaction)transaction,
                publication,
                DiscordOutboxOperation.CreateMessage,
                initialPayloadJson,
                "create:" + publicationId.ToString("N"),
                createdAt,
                createdAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DiscordPublicationCreateResult(
                DiscordPublicationCreateStatus.Created,
                publication);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordPublicationCreateResult(
                DiscordPublicationCreateStatus.Conflict,
                null,
                "An active Discord publication already owns this order.");
        }
    }

    public async Task<DiscordPublicationRecord?> LoadPublicationAsync(
        Guid publicationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreatePublicationSelect(connection);
        command.CommandText += " WHERE publication_id = $value;";
        command.Parameters.AddWithValue("$value", publicationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPublication(reader) : null;
    }

    public async Task<DiscordPublicationRecord?> LoadPublicationByPublicIdAsync(
        string publicId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreatePublicationSelect(connection);
        command.CommandText +=
            " WHERE public_id = $value ORDER BY created_at_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$value", publicId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPublication(reader) : null;
    }

    public async Task<DiscordPublicationRecord?> LoadPublicationByOrderAsync(
        CompanyId companyId,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreatePublicationSelect(connection);
        command.CommandText +=
            """
             WHERE company_id = $companyId AND order_id = $orderId
             ORDER BY created_at_utc DESC
             LIMIT 1;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$orderId", orderId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPublication(reader) : null;
    }

    public async Task<DiscordPublicationRetryResult> RetryFailedPublicationAsync(
        CompanyId companyId,
        Guid publicationId,
        string publicId,
        DiscordPublicationState restoredState,
        string payloadJson,
        DateTimeOffset queuedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (publicationId == Guid.Empty ||
            restoredState is DiscordPublicationState.Failed or
            DiscordPublicationState.ReconciliationRequired)
        {
            return new DiscordPublicationRetryResult(
                DiscordPublicationRetryStatus.Conflict,
                null,
                "A failed Discord publication cannot be retried into another failure state.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText =
            """
            SELECT
                p.publication_id,
                p.company_id,
                p.order_id,
                p.source_order_revision,
                p.public_id,
                p.brief_version,
                p.channel_id,
                p.message_id,
                p.action_token,
                p.state,
                p.desired_projection_revision,
                p.idempotency_key,
                p.created_at_utc,
                p.updated_at_utc,
                o.work_item_id,
                o.operation,
                o.channel_id,
                o.message_id,
                o.state
            FROM discord_publications p
            LEFT JOIN discord_outbox o ON o.publication_id = p.publication_id
            WHERE p.company_id = $companyId
              AND p.publication_id = $publicationId
              AND p.public_id = $publicId
            ORDER BY o.created_at_utc;
            """;
        select.Parameters.AddWithValue("$companyId", companyId.ToString());
        select.Parameters.AddWithValue("$publicationId", publicationId.ToString("D"));
        select.Parameters.AddWithValue("$publicId", publicId);

        DiscordPublicationRecord? publication = null;
        var failed = new List<(
            Guid WorkItemId,
            DiscordOutboxOperation Operation,
            string ChannelId,
            string? MessageId)>();
        var hasUnsafeState = false;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                publication ??= ReadPublication(reader);
                if (reader.IsDBNull(14))
                {
                    continue;
                }

                var outboxState = (DiscordOutboxState)reader.GetInt32(18);
                if (outboxState == DiscordOutboxState.Failed)
                {
                    failed.Add((
                        Guid.Parse(reader.GetString(14)),
                        (DiscordOutboxOperation)reader.GetInt32(15),
                        reader.GetString(16),
                        reader.IsDBNull(17) ? null : reader.GetString(17)));
                }
                else if (outboxState != DiscordOutboxState.Succeeded)
                {
                    hasUnsafeState = true;
                }
            }
        }

        if (publication == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordPublicationRetryResult(
                DiscordPublicationRetryStatus.Missing,
                null,
                "The Discord publication was not found for this company.");
        }

        var installation = await LoadCompanyInstallationAsync(
            connection,
            companyId,
            cancellationToken,
            (SqliteTransaction)transaction);
        if (installation == null ||
            !string.Equals(
                installation.ChannelId,
                publication.ChannelId,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordPublicationRetryResult(
                DiscordPublicationRetryStatus.Conflict,
                publication,
                "The failed publication no longer matches the server-owned company installation.");
        }

        if (publication.State != DiscordPublicationState.Failed ||
            hasUnsafeState ||
            failed.Count != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordPublicationRetryResult(
                DiscordPublicationRetryStatus.Conflict,
                publication,
                "Only one unambiguous terminal Discord failure can be retried.");
        }

        var workItem = failed[0];
        var validOperation = string.Equals(
                publication.ChannelId,
                workItem.ChannelId,
                StringComparison.Ordinal) &&
            (workItem.Operation switch
            {
                DiscordOutboxOperation.CreateMessage =>
                    string.IsNullOrWhiteSpace(publication.MessageId) &&
                    string.IsNullOrWhiteSpace(workItem.MessageId),
                DiscordOutboxOperation.EditMessage =>
                    !string.IsNullOrWhiteSpace(publication.MessageId) &&
                    string.Equals(
                        publication.MessageId,
                        workItem.MessageId,
                        StringComparison.Ordinal),
                _ => false
            });
        if (!validOperation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordPublicationRetryResult(
                DiscordPublicationRetryStatus.Conflict,
                publication,
                "The failed Discord work item does not match the persisted message identity.");
        }

        await using var requeue = connection.CreateCommand();
        requeue.Transaction = (SqliteTransaction)transaction;
        requeue.CommandText =
            """
            UPDATE discord_outbox
            SET
                state = $pending,
                payload_json = $payloadJson,
                attempt_count = 0,
                next_attempt_at_utc = $queuedAt,
                lease_id = NULL,
                lease_expires_at_utc = NULL,
                last_error = NULL,
                updated_at_utc = $queuedAt
            WHERE work_item_id = $workItemId
              AND state = $failed;
            """;
        requeue.Parameters.AddWithValue("$pending", (int)DiscordOutboxState.Pending);
        requeue.Parameters.AddWithValue("$payloadJson", payloadJson);
        requeue.Parameters.AddWithValue("$queuedAt", queuedAt.ToString("O"));
        requeue.Parameters.AddWithValue("$workItemId", workItem.WorkItemId.ToString("D"));
        requeue.Parameters.AddWithValue("$failed", (int)DiscordOutboxState.Failed);
        if (await requeue.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordPublicationRetryResult(
                DiscordPublicationRetryStatus.Conflict,
                publication,
                "The failed Discord work item changed before it could be requeued.");
        }

        await using var restore = connection.CreateCommand();
        restore.Transaction = (SqliteTransaction)transaction;
        restore.CommandText =
            """
            UPDATE discord_publications
            SET state = $state, updated_at_utc = $queuedAt
            WHERE publication_id = $publicationId
              AND state = $failed;
            """;
        restore.Parameters.AddWithValue("$state", (int)restoredState);
        restore.Parameters.AddWithValue("$queuedAt", queuedAt.ToString("O"));
        restore.Parameters.AddWithValue(
            "$publicationId",
            publication.PublicationId.ToString("D"));
        restore.Parameters.AddWithValue("$failed", (int)DiscordPublicationState.Failed);
        if (await restore.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordPublicationRetryResult(
                DiscordPublicationRetryStatus.Conflict,
                publication,
                "The Discord publication changed before its failed work item could be requeued.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new DiscordPublicationRetryResult(
            DiscordPublicationRetryStatus.Queued,
            publication with
            {
                State = restoredState,
                UpdatedAt = queuedAt
            });
    }

    public async Task<IReadOnlyList<DiscordInterestClaim>> LoadPendingClaimsAsync(
        CompanyId companyId,
        Guid? orderId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                claim_id,
                publication_id,
                company_id,
                order_id,
                discord_user_id,
                discord_display_name,
                state,
                resolved_crafter_id,
                accepted_order_revision,
                resolution_idempotency_key,
                created_at_utc,
                resolved_at_utc
            FROM discord_interest_claims
            WHERE company_id = $companyId
              AND state IN ($pending, $assignmentPending)
            """ +
            (orderId.HasValue ? " AND order_id = $orderId" : string.Empty) +
            " ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$pending", (int)DiscordInterestClaimState.Pending);
        command.Parameters.AddWithValue(
            "$assignmentPending",
            (int)DiscordInterestClaimState.AssignmentPending);
        if (orderId.HasValue)
        {
            command.Parameters.AddWithValue("$orderId", orderId.Value.ToString("D"));
        }

        var claims = new List<DiscordInterestClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(ReadClaim(reader));
        }

        return claims;
    }

    public async Task<DiscordClaimTransitionResult> BeginClaimAcceptanceAsync(
        CompanyId companyId,
        Guid claimId,
        string resolutionIdempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolutionIdempotencyKey);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var claim = await LoadClaimAsync(
            connection,
            (SqliteTransaction)transaction,
            companyId,
            claimId,
            cancellationToken);
        if (claim == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(DiscordClaimTransitionStatus.Missing, null);
        }

        if (claim.State == DiscordInterestClaimState.AssignmentPending &&
            string.Equals(
                claim.ResolutionIdempotencyKey,
                resolutionIdempotencyKey,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(
                DiscordClaimTransitionStatus.Replayed,
                claim);
        }

        if (claim.State == DiscordInterestClaimState.Accepted &&
            string.Equals(
                claim.ResolutionIdempotencyKey,
                resolutionIdempotencyKey,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(
                DiscordClaimTransitionStatus.Replayed,
                claim);
        }

        if (claim.State != DiscordInterestClaimState.Pending)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(
                DiscordClaimTransitionStatus.Conflict,
                claim,
                "The claim is no longer pending.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            UPDATE discord_interest_claims
            SET state = $state, resolution_idempotency_key = $key
            WHERE claim_id = $claimId AND state = $pending;
            """;
        command.Parameters.AddWithValue("$state", (int)DiscordInterestClaimState.AssignmentPending);
        command.Parameters.AddWithValue("$key", resolutionIdempotencyKey);
        command.Parameters.AddWithValue("$claimId", claimId.ToString("D"));
        command.Parameters.AddWithValue("$pending", (int)DiscordInterestClaimState.Pending);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(
                DiscordClaimTransitionStatus.Conflict,
                claim);
        }

        await transaction.CommitAsync(cancellationToken);
        return new DiscordClaimTransitionResult(
            DiscordClaimTransitionStatus.Applied,
            claim with
            {
                State = DiscordInterestClaimState.AssignmentPending,
                ResolutionIdempotencyKey = resolutionIdempotencyKey
            });
    }

    public async Task<DiscordClaimTransitionResult> CompleteClaimAcceptanceAsync(
        CompanyId companyId,
        Guid claimId,
        string resolutionIdempotencyKey,
        Guid crafterId,
        CompanyRecordRevision acceptedOrderRevision,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var claim = await LoadClaimAsync(
            connection,
            (SqliteTransaction)transaction,
            companyId,
            claimId,
            cancellationToken);
        if (claim == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(DiscordClaimTransitionStatus.Missing, null);
        }

        if (claim.State == DiscordInterestClaimState.Accepted &&
            claim.ResolvedCrafterId == crafterId &&
            string.Equals(
                claim.ResolutionIdempotencyKey,
                resolutionIdempotencyKey,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(
                DiscordClaimTransitionStatus.Replayed,
                claim);
        }

        if (claim.State != DiscordInterestClaimState.AssignmentPending ||
            !string.Equals(
                claim.ResolutionIdempotencyKey,
                resolutionIdempotencyKey,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(
                DiscordClaimTransitionStatus.Conflict,
                claim);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE discord_interest_claims
            SET
                state = $state,
                resolved_crafter_id = $crafterId,
                accepted_order_revision = $acceptedRevision,
                resolved_at_utc = $resolvedAt
            WHERE claim_id = $claimId;
            """;
        update.Parameters.AddWithValue("$state", (int)DiscordInterestClaimState.Accepted);
        update.Parameters.AddWithValue("$crafterId", crafterId.ToString("D"));
        update.Parameters.AddWithValue("$acceptedRevision", acceptedOrderRevision.Value);
        update.Parameters.AddWithValue("$resolvedAt", resolvedAt.ToString("O"));
        update.Parameters.AddWithValue("$claimId", claimId.ToString("D"));
        await update.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new DiscordClaimTransitionResult(
            DiscordClaimTransitionStatus.Applied,
            claim with
            {
                State = DiscordInterestClaimState.Accepted,
                ResolvedCrafterId = crafterId,
                AcceptedOrderRevision = acceptedOrderRevision,
                ResolvedAt = resolvedAt
            });
    }

    public Task ResetClaimAcceptanceAsync(
        CompanyId companyId,
        Guid claimId,
        string resolutionIdempotencyKey,
        CancellationToken cancellationToken = default) =>
        ChangeClaimStateAsync(
            companyId,
            claimId,
            DiscordInterestClaimState.AssignmentPending,
            DiscordInterestClaimState.Pending,
            resolutionIdempotencyKey,
            resolvedAt: null,
            cancellationToken);

    public async Task<DiscordClaimTransitionResult> DeclineClaimAsync(
        CompanyId companyId,
        Guid claimId,
        string resolutionIdempotencyKey,
        DateTimeOffset declinedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolutionIdempotencyKey);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var claim = await LoadClaimAsync(
            connection,
            (SqliteTransaction)transaction,
            companyId,
            claimId,
            cancellationToken);
        if (claim == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(
                DiscordClaimTransitionStatus.Missing,
                null);
        }

        if (claim.State == DiscordInterestClaimState.Declined &&
            string.Equals(
                claim.ResolutionIdempotencyKey,
                resolutionIdempotencyKey,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(
                DiscordClaimTransitionStatus.Replayed,
                claim);
        }

        if (claim.State != DiscordInterestClaimState.Pending)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(
                DiscordClaimTransitionStatus.Conflict,
                claim,
                "The claim is no longer pending.");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE discord_interest_claims
            SET
                state = $state,
                resolution_idempotency_key = $key,
                resolved_at_utc = $resolvedAt
            WHERE company_id = $companyId
              AND claim_id = $claimId
              AND state = $pending;
            """;
        update.Parameters.AddWithValue("$state", (int)DiscordInterestClaimState.Declined);
        update.Parameters.AddWithValue("$key", resolutionIdempotencyKey);
        update.Parameters.AddWithValue("$resolvedAt", declinedAt.ToString("O"));
        update.Parameters.AddWithValue("$companyId", companyId.ToString());
        update.Parameters.AddWithValue("$claimId", claimId.ToString("D"));
        update.Parameters.AddWithValue("$pending", (int)DiscordInterestClaimState.Pending);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordClaimTransitionResult(
                DiscordClaimTransitionStatus.Conflict,
                claim);
        }

        await transaction.CommitAsync(cancellationToken);
        return new DiscordClaimTransitionResult(
            DiscordClaimTransitionStatus.Applied,
            claim with
            {
                State = DiscordInterestClaimState.Declined,
                ResolutionIdempotencyKey = resolutionIdempotencyKey,
                ResolvedAt = declinedAt
            });
    }

    public async Task EnqueueProjectionAsync(
        Guid publicationId,
        DiscordPublicationState state,
        long desiredProjectionRevision,
        string payloadJson,
        DateTimeOffset queuedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        if (desiredProjectionRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desiredProjectionRevision));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var publication = await LoadPublicationAsync(
            connection,
            (SqliteTransaction)transaction,
            publicationId,
            cancellationToken) ??
            throw new InvalidOperationException("Discord publication was not found.");
        if (publication.State == DiscordPublicationState.ReconciliationRequired &&
            string.IsNullOrWhiteSpace(publication.MessageId))
        {
            throw new InvalidOperationException(
                "Discord message creation has an ambiguous result and requires explicit reconciliation.");
        }

        if (desiredProjectionRevision <= publication.DesiredProjectionRevision)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var activeCreate = string.IsNullOrWhiteSpace(publication.MessageId)
            ? await LoadActiveCreateAsync(
                connection,
                (SqliteTransaction)transaction,
                publicationId,
                cancellationToken)
            : null;
        await using var discardStale = connection.CreateCommand();
        discardStale.Transaction = (SqliteTransaction)transaction;
        discardStale.CommandText =
            """
            DELETE FROM discord_outbox
            WHERE publication_id = $publicationId
              AND state IN ($pending, $retry)
              AND desired_projection_revision < $desiredRevision
              AND ($preservedWorkItemId IS NULL OR work_item_id <> $preservedWorkItemId);
            """;
        discardStale.Parameters.AddWithValue("$publicationId", publicationId.ToString("D"));
        discardStale.Parameters.AddWithValue("$pending", (int)DiscordOutboxState.Pending);
        discardStale.Parameters.AddWithValue("$retry", (int)DiscordOutboxState.Retry);
        discardStale.Parameters.AddWithValue("$desiredRevision", desiredProjectionRevision);
        discardStale.Parameters.AddWithValue(
            "$preservedWorkItemId",
            activeCreate is null ? DBNull.Value : activeCreate.Value.WorkItemId.ToString("D"));
        await discardStale.ExecuteNonQueryAsync(cancellationToken);

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE discord_publications
            SET
                state = $state,
                desired_projection_revision = $desiredRevision,
                updated_at_utc = $now
            WHERE publication_id = $publicationId;
            """;
        update.Parameters.AddWithValue("$state", (int)state);
        update.Parameters.AddWithValue("$desiredRevision", desiredProjectionRevision);
        update.Parameters.AddWithValue("$now", queuedAt.ToString("O"));
        update.Parameters.AddWithValue("$publicationId", publicationId.ToString("D"));
        await update.ExecuteNonQueryAsync(cancellationToken);

        var updated = publication with
        {
            State = state,
            DesiredProjectionRevision = desiredProjectionRevision,
            UpdatedAt = queuedAt
        };
        if (activeCreate.HasValue &&
            activeCreate.Value.State is DiscordOutboxState.Pending or DiscordOutboxState.Retry)
        {
            await CoalesceQueuedCreateAsync(
                connection,
                (SqliteTransaction)transaction,
                activeCreate.Value.WorkItemId,
                desiredProjectionRevision,
                payloadJson,
                queuedAt,
                cancellationToken);
        }
        else
        {
            var createIsInFlight =
                activeCreate?.State == DiscordOutboxState.InFlight;
            var operation = string.IsNullOrWhiteSpace(publication.MessageId) && !createIsInFlight
                ? DiscordOutboxOperation.CreateMessage
                : DiscordOutboxOperation.EditMessage;
            await InsertOutboxAsync(
                connection,
                (SqliteTransaction)transaction,
                updated,
                operation,
                payloadJson,
                $"projection:{publicationId:N}:{desiredProjectionRevision}",
                queuedAt,
                createIsInFlight ? DateTimeOffset.MaxValue : queuedAt,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<DiscordVolunteerInteractionResult> RecordInterestAsync(
        DiscordVolunteerInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        if (!IsDiscordSnowflake(interaction.InteractionId) ||
            !IsDiscordSnowflake(interaction.ApplicationId) ||
            !IsDiscordSnowflake(interaction.GuildId) ||
            !IsDiscordSnowflake(interaction.ChannelId) ||
            !IsDiscordSnowflake(interaction.MessageId) ||
            !IsDiscordSnowflake(interaction.DiscordUserId) ||
            string.IsNullOrWhiteSpace(interaction.ActionToken) ||
            interaction.ActionToken.Length > 100)
        {
            return new DiscordVolunteerInteractionResult(
                DiscordVolunteerInteractionStatus.Rejected,
                "This Volunteer action is invalid.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var publication = await LoadPublicationByActionTokenAsync(
            connection,
            (SqliteTransaction)transaction,
            interaction.ActionToken,
            cancellationToken);
        var installation = publication == null
            ? null
            : await LoadCompanyInstallationAsync(
                connection,
                publication.CompanyId,
                cancellationToken,
                (SqliteTransaction)transaction);
        if (publication == null ||
            installation == null ||
            publication.State != DiscordPublicationState.Open ||
            !string.Equals(installation.ApplicationId, interaction.ApplicationId, StringComparison.Ordinal) ||
            !string.Equals(installation.GuildId, interaction.GuildId, StringComparison.Ordinal) ||
            !string.Equals(installation.ChannelId, interaction.ChannelId, StringComparison.Ordinal) ||
            !string.Equals(publication.ChannelId, interaction.ChannelId, StringComparison.Ordinal) ||
            !string.Equals(publication.MessageId, interaction.MessageId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordVolunteerInteractionResult(
                DiscordVolunteerInteractionStatus.NoLongerOpen,
                "This commission is no longer accepting volunteers.");
        }

        var existingClaim = await LoadClaimByUserAsync(
            connection,
            (SqliteTransaction)transaction,
            publication.PublicationId,
            interaction.DiscordUserId,
            cancellationToken);
        if (existingClaim != null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordVolunteerInteractionResult(
                DiscordVolunteerInteractionStatus.Replayed,
                ClaimMessage(existingClaim));
        }

        var now = DateTimeOffset.UtcNow;
        var claim = new DiscordInterestClaim(
            Guid.NewGuid(),
            publication.PublicationId,
            publication.CompanyId,
            publication.OrderId,
            interaction.DiscordUserId,
            Truncate(interaction.DiscordUserDisplayName, 120),
            DiscordInterestClaimState.Pending,
            null,
            null,
            null,
            now,
            null);
        await InsertClaimAsync(
            connection,
            (SqliteTransaction)transaction,
            claim,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DiscordVolunteerInteractionResult(
            DiscordVolunteerInteractionStatus.Recorded,
            "Your interest was recorded. The company operator must confirm the assignment in Craft Architect.");
    }

    public async Task<IReadOnlyList<DiscordOutboxWorkItem>> LeaseDueAsync(
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
                o.work_item_id,
                p.company_id,
                o.operation,
                o.channel_id,
                o.message_id,
                o.payload_json,
                o.attempt_count
            FROM discord_outbox o
            INNER JOIN discord_publications p
                ON p.publication_id = o.publication_id
            WHERE (
                    o.state IN ($pending, $retry)
                    AND o.next_attempt_at_utc <= $now
                )
                OR (
                    o.state = $inFlight
                    AND o.lease_expires_at_utc <= $now
                )
            ORDER BY o.created_at_utc
            LIMIT $maximumCount;
            """;
        select.Parameters.AddWithValue("$pending", (int)DiscordOutboxState.Pending);
        select.Parameters.AddWithValue("$retry", (int)DiscordOutboxState.Retry);
        select.Parameters.AddWithValue("$inFlight", (int)DiscordOutboxState.InFlight);
        select.Parameters.AddWithValue("$now", now.ToString("O"));
        select.Parameters.AddWithValue("$maximumCount", Math.Clamp(maximumCount, 1, 100));
        var candidates = new List<(
            Guid Id,
            CompanyId CompanyId,
            DiscordOutboxOperation Operation,
            string Channel,
            string? Message,
            string Payload,
            int Attempts)>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((
                    Guid.Parse(reader.GetString(0)),
                    CompanyId.Parse(reader.GetString(1)),
                    (DiscordOutboxOperation)reader.GetInt32(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6)));
            }
        }

        var leased = new List<DiscordOutboxWorkItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var leaseId = Guid.NewGuid().ToString("N");
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE discord_outbox
                SET
                    state = $inFlight,
                    attempt_count = attempt_count + 1,
                    lease_id = $leaseId,
                    lease_expires_at_utc = $leaseExpiresAt,
                    updated_at_utc = $now
                WHERE work_item_id = $workItemId;
                """;
            update.Parameters.AddWithValue("$inFlight", (int)DiscordOutboxState.InFlight);
            update.Parameters.AddWithValue("$leaseId", leaseId);
            update.Parameters.AddWithValue("$leaseExpiresAt", (now + leaseDuration).ToString("O"));
            update.Parameters.AddWithValue("$now", now.ToString("O"));
            update.Parameters.AddWithValue("$workItemId", candidate.Id.ToString("D"));
            await update.ExecuteNonQueryAsync(cancellationToken);
            leased.Add(new DiscordOutboxWorkItem(
                candidate.Id,
                leaseId,
                candidate.CompanyId,
                candidate.Operation,
                candidate.Channel,
                candidate.Message,
                candidate.Payload,
                candidate.Attempts + 1));
        }

        await transaction.CommitAsync(cancellationToken);
        return leased;
    }

    public async Task CompleteAsync(
        Guid workItemId,
        string leaseId,
        string? messageId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var metadata = await LoadOutboxMetadataAsync(
            connection,
            (SqliteTransaction)transaction,
            workItemId,
            leaseId,
            cancellationToken);
        if (metadata == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        await UpdateOutboxTerminalAsync(
            connection,
            (SqliteTransaction)transaction,
            workItemId,
            leaseId,
            DiscordOutboxState.Succeeded,
            null,
            completedAt,
            cancellationToken);
        await using var publication = connection.CreateCommand();
        publication.Transaction = (SqliteTransaction)transaction;
        publication.CommandText =
            """
            UPDATE discord_publications
            SET
                message_id = COALESCE($messageId, message_id),
                updated_at_utc = $now
            WHERE publication_id = $publicationId;
            """;
        publication.Parameters.AddWithValue(
            "$messageId",
            string.IsNullOrWhiteSpace(messageId) ? DBNull.Value : messageId);
        publication.Parameters.AddWithValue("$now", completedAt.ToString("O"));
        publication.Parameters.AddWithValue("$publicationId", metadata.Value.ToString("D"));
        await publication.ExecuteNonQueryAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            await using var releaseDeferred = connection.CreateCommand();
            releaseDeferred.Transaction = (SqliteTransaction)transaction;
            releaseDeferred.CommandText =
                """
                UPDATE discord_outbox
                SET
                    message_id = $messageId,
                    next_attempt_at_utc = $completedAt,
                    updated_at_utc = $completedAt
                WHERE publication_id = $publicationId
                  AND operation = $edit
                  AND message_id IS NULL
                  AND state IN ($pending, $retry);
                """;
            releaseDeferred.Parameters.AddWithValue("$messageId", messageId);
            releaseDeferred.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
            releaseDeferred.Parameters.AddWithValue("$publicationId", metadata.Value.ToString("D"));
            releaseDeferred.Parameters.AddWithValue("$edit", (int)DiscordOutboxOperation.EditMessage);
            releaseDeferred.Parameters.AddWithValue("$pending", (int)DiscordOutboxState.Pending);
            releaseDeferred.Parameters.AddWithValue("$retry", (int)DiscordOutboxState.Retry);
            await releaseDeferred.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task RetryAsync(
        Guid workItemId,
        string leaseId,
        DateTimeOffset nextAttemptAt,
        string error,
        CancellationToken cancellationToken = default) =>
        UpdateOutboxRetryAsync(
            workItemId,
            leaseId,
            nextAttemptAt,
            error,
            cancellationToken);

    public Task RequireReconciliationAsync(
        Guid workItemId,
        string leaseId,
        string error,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default) =>
        FailOutboxAsync(
            workItemId,
            leaseId,
            DiscordOutboxState.ReconciliationRequired,
            DiscordPublicationState.ReconciliationRequired,
            error,
            failedAt,
            cancellationToken);

    public Task ExhaustAsync(
        Guid workItemId,
        string leaseId,
        string error,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default) =>
        FailOutboxAsync(
            workItemId,
            leaseId,
            DiscordOutboxState.Failed,
            DiscordPublicationState.Failed,
            error,
            failedAt,
            cancellationToken);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await OpenConnectionAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
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

    private static async Task UpsertCompanyInstallationAsync(
        SqliteConnection connection,
        DiscordCompanyInstallationBinding installation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO discord_company_installations (
                company_id,
                application_id,
                guild_id,
                channel_id,
                updated_at_utc
            )
            VALUES (
                $companyId,
                $applicationId,
                $guildId,
                $channelId,
                $updatedAt
            )
            ON CONFLICT(company_id) DO UPDATE SET
                application_id = excluded.application_id,
                guild_id = excluded.guild_id,
                channel_id = excluded.channel_id,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$companyId", installation.CompanyId.ToString());
        command.Parameters.AddWithValue("$applicationId", installation.ApplicationId);
        command.Parameters.AddWithValue("$guildId", installation.GuildId);
        command.Parameters.AddWithValue("$channelId", installation.ChannelId);
        command.Parameters.AddWithValue("$updatedAt", installation.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DiscordCompanyInstallationBinding?> LoadCompanyInstallationAsync(
        SqliteConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT application_id, guild_id, channel_id, updated_at_utc
            FROM discord_company_installations
            WHERE company_id = $companyId;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DiscordCompanyInstallationBinding(
                companyId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)))
            : null;
    }

    private static async Task EnsureCompatibleSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(discord_publications);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), "installation_id", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Discord collaboration database uses the incompatible pre-release " +
                    "installation-bound schema. No data was changed; archive or explicitly " +
                    "convert that pre-release database before starting this version.");
            }
        }
    }

    private static async Task InsertPublicationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscordPublicationRecord publication,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO discord_publications (
                publication_id,
                company_id,
                order_id,
                source_order_revision,
                public_id,
                brief_version,
                channel_id,
                message_id,
                action_token,
                state,
                desired_projection_revision,
                idempotency_key,
                created_at_utc,
                updated_at_utc
            )
            VALUES (
                $publicationId,
                $companyId,
                $orderId,
                $sourceOrderRevision,
                $publicId,
                $briefVersion,
                $channelId,
                $messageId,
                $actionToken,
                $state,
                $desiredProjectionRevision,
                $idempotencyKey,
                $createdAt,
                $updatedAt
            );
            """;
        AddPublicationParameters(command, publication);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOutboxAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscordPublicationRecord publication,
        DiscordOutboxOperation operation,
        string payloadJson,
        string dedupeKey,
        DateTimeOffset queuedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO discord_outbox (
                work_item_id,
                publication_id,
                dedupe_key,
                operation,
                channel_id,
                message_id,
                payload_json,
                desired_projection_revision,
                state,
                attempt_count,
                next_attempt_at_utc,
                lease_id,
                lease_expires_at_utc,
                last_error,
                created_at_utc,
                updated_at_utc
            )
            VALUES (
                $workItemId,
                $publicationId,
                $dedupeKey,
                $operation,
                $channelId,
                $messageId,
                $payloadJson,
                $desiredProjectionRevision,
                $state,
                0,
                $nextAttemptAt,
                NULL,
                NULL,
                NULL,
                $createdAt,
                $updatedAt
            );
            """;
        command.Parameters.AddWithValue("$workItemId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$publicationId", publication.PublicationId.ToString("D"));
        command.Parameters.AddWithValue("$dedupeKey", dedupeKey);
        command.Parameters.AddWithValue("$operation", (int)operation);
        command.Parameters.AddWithValue("$channelId", publication.ChannelId);
        command.Parameters.AddWithValue(
            "$messageId",
            string.IsNullOrWhiteSpace(publication.MessageId)
                ? DBNull.Value
                : publication.MessageId);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue(
            "$desiredProjectionRevision",
            publication.DesiredProjectionRevision);
        command.Parameters.AddWithValue("$state", (int)DiscordOutboxState.Pending);
        command.Parameters.AddWithValue("$nextAttemptAt", nextAttemptAt.ToString("O"));
        command.Parameters.AddWithValue("$createdAt", queuedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", queuedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(Guid WorkItemId, DiscordOutboxState State)?> LoadActiveCreateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT work_item_id, state
            FROM discord_outbox
            WHERE publication_id = $publicationId
              AND operation = $create
              AND state IN ($pending, $inFlight, $retry)
            ORDER BY
                CASE WHEN state = $inFlight THEN 0 ELSE 1 END,
                created_at_utc
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$publicationId", publicationId.ToString("D"));
        command.Parameters.AddWithValue("$create", (int)DiscordOutboxOperation.CreateMessage);
        command.Parameters.AddWithValue("$pending", (int)DiscordOutboxState.Pending);
        command.Parameters.AddWithValue("$inFlight", (int)DiscordOutboxState.InFlight);
        command.Parameters.AddWithValue("$retry", (int)DiscordOutboxState.Retry);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (Guid.Parse(reader.GetString(0)), (DiscordOutboxState)reader.GetInt32(1))
            : null;
    }

    private static async Task CoalesceQueuedCreateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workItemId,
        long desiredProjectionRevision,
        string payloadJson,
        DateTimeOffset queuedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE discord_outbox
            SET
                payload_json = $payloadJson,
                desired_projection_revision = $desiredRevision,
                state = $pending,
                next_attempt_at_utc = $queuedAt,
                last_error = NULL,
                updated_at_utc = $queuedAt
            WHERE work_item_id = $workItemId
              AND state IN ($pending, $retry);
            """;
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$desiredRevision", desiredProjectionRevision);
        command.Parameters.AddWithValue("$pending", (int)DiscordOutboxState.Pending);
        command.Parameters.AddWithValue("$retry", (int)DiscordOutboxState.Retry);
        command.Parameters.AddWithValue("$queuedAt", queuedAt.ToString("O"));
        command.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The Discord message create changed while its newer projection was being coalesced.");
        }
    }

    private static async Task<DiscordPublicationRecord?> LoadPublicationByIdempotencyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = CreatePublicationSelect(connection, transaction);
        command.CommandText += " WHERE idempotency_key = $value;";
        command.Parameters.AddWithValue("$value", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPublication(reader) : null;
    }

    private static async Task<DiscordPublicationRecord?> LoadPublicationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        await using var command = CreatePublicationSelect(connection, transaction);
        command.CommandText += " WHERE publication_id = $value;";
        command.Parameters.AddWithValue("$value", publicationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPublication(reader) : null;
    }

    private static async Task<DiscordPublicationRecord?> LoadPublicationByActionTokenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actionToken,
        CancellationToken cancellationToken)
    {
        await using var command = CreatePublicationSelect(connection, transaction);
        command.CommandText += " WHERE action_token = $value;";
        command.Parameters.AddWithValue("$value", actionToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPublication(reader) : null;
    }

    private static SqliteCommand CreatePublicationSelect(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                publication_id,
                company_id,
                order_id,
                source_order_revision,
                public_id,
                brief_version,
                channel_id,
                message_id,
                action_token,
                state,
                desired_projection_revision,
                idempotency_key,
                created_at_utc,
                updated_at_utc
            FROM discord_publications
            """;
        return command;
    }

    private static DiscordPublicationRecord ReadPublication(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            CompanyId.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            new CompanyRecordRevision(reader.GetInt64(3)),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            (DiscordPublicationState)reader.GetInt32(9),
            reader.GetInt64(10),
            reader.GetString(11),
            DateTimeOffset.Parse(reader.GetString(12)),
            DateTimeOffset.Parse(reader.GetString(13)));

    private static void AddPublicationParameters(
        SqliteCommand command,
        DiscordPublicationRecord publication)
    {
        command.Parameters.AddWithValue("$publicationId", publication.PublicationId.ToString("D"));
        command.Parameters.AddWithValue("$companyId", publication.CompanyId.ToString());
        command.Parameters.AddWithValue("$orderId", publication.OrderId.ToString("D"));
        command.Parameters.AddWithValue("$sourceOrderRevision", publication.SourceOrderRevision.Value);
        command.Parameters.AddWithValue("$publicId", publication.PublicId);
        command.Parameters.AddWithValue("$briefVersion", publication.BriefVersion);
        command.Parameters.AddWithValue("$channelId", publication.ChannelId);
        command.Parameters.AddWithValue(
            "$messageId",
            string.IsNullOrWhiteSpace(publication.MessageId)
                ? DBNull.Value
                : publication.MessageId);
        command.Parameters.AddWithValue("$actionToken", publication.ActionToken);
        command.Parameters.AddWithValue("$state", (int)publication.State);
        command.Parameters.AddWithValue(
            "$desiredProjectionRevision",
            publication.DesiredProjectionRevision);
        command.Parameters.AddWithValue("$idempotencyKey", publication.IdempotencyKey);
        command.Parameters.AddWithValue("$createdAt", publication.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", publication.UpdatedAt.ToString("O"));
    }

    private static async Task<DiscordInterestClaim?> LoadClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        Guid claimId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateClaimSelect(connection, transaction);
        command.CommandText += " WHERE company_id = $companyId AND claim_id = $claimId;";
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$claimId", claimId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadClaim(reader) : null;
    }

    private static async Task<DiscordInterestClaim?> LoadClaimByUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid publicationId,
        string discordUserId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateClaimSelect(connection, transaction);
        command.CommandText +=
            " WHERE publication_id = $publicationId AND discord_user_id = $discordUserId;";
        command.Parameters.AddWithValue("$publicationId", publicationId.ToString("D"));
        command.Parameters.AddWithValue("$discordUserId", discordUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadClaim(reader) : null;
    }

    private static SqliteCommand CreateClaimSelect(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                claim_id,
                publication_id,
                company_id,
                order_id,
                discord_user_id,
                discord_display_name,
                state,
                resolved_crafter_id,
                accepted_order_revision,
                resolution_idempotency_key,
                created_at_utc,
                resolved_at_utc
            FROM discord_interest_claims
            """;
        return command;
    }

    private static DiscordInterestClaim ReadClaim(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            CompanyId.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            (DiscordInterestClaimState)reader.GetInt32(6),
            reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : new CompanyRecordRevision(reader.GetInt64(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            DateTimeOffset.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)));

    private static async Task InsertClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DiscordInterestClaim claim,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO discord_interest_claims (
                claim_id,
                publication_id,
                company_id,
                order_id,
                discord_user_id,
                discord_display_name,
                state,
                resolved_crafter_id,
                accepted_order_revision,
                resolution_idempotency_key,
                created_at_utc,
                resolved_at_utc
            )
            VALUES (
                $claimId,
                $publicationId,
                $companyId,
                $orderId,
                $discordUserId,
                $discordDisplayName,
                $state,
                NULL,
                NULL,
                NULL,
                $createdAt,
                NULL
            );
            """;
        command.Parameters.AddWithValue("$claimId", claim.ClaimId.ToString("D"));
        command.Parameters.AddWithValue("$publicationId", claim.PublicationId.ToString("D"));
        command.Parameters.AddWithValue("$companyId", claim.CompanyId.ToString());
        command.Parameters.AddWithValue("$orderId", claim.OrderId.ToString("D"));
        command.Parameters.AddWithValue("$discordUserId", claim.DiscordUserId);
        command.Parameters.AddWithValue("$discordDisplayName", claim.DiscordDisplayName);
        command.Parameters.AddWithValue("$state", (int)claim.State);
        command.Parameters.AddWithValue("$createdAt", claim.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ChangeClaimStateAsync(
        CompanyId companyId,
        Guid claimId,
        DiscordInterestClaimState fromState,
        DiscordInterestClaimState toState,
        string? resolutionIdempotencyKey,
        DateTimeOffset? resolvedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE discord_interest_claims
            SET
                state = $toState,
                resolution_idempotency_key = CASE
                    WHEN $toState = $pending THEN NULL
                    ELSE resolution_idempotency_key
                END,
                resolved_at_utc = $resolvedAt
            WHERE company_id = $companyId
              AND claim_id = $claimId
              AND state = $fromState
              AND (
                    $resolutionKey IS NULL
                    OR resolution_idempotency_key = $resolutionKey
              );
            """;
        command.Parameters.AddWithValue("$toState", (int)toState);
        command.Parameters.AddWithValue("$pending", (int)DiscordInterestClaimState.Pending);
        command.Parameters.AddWithValue(
            "$resolvedAt",
            resolvedAt.HasValue ? resolvedAt.Value.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$claimId", claimId.ToString("D"));
        command.Parameters.AddWithValue("$fromState", (int)fromState);
        command.Parameters.AddWithValue(
            "$resolutionKey",
            resolutionIdempotencyKey is null ? DBNull.Value : resolutionIdempotencyKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateOutboxRetryAsync(
        Guid workItemId,
        string leaseId,
        DateTimeOffset nextAttemptAt,
        string error,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE discord_outbox
            SET
                state = $state,
                next_attempt_at_utc = $nextAttemptAt,
                lease_id = NULL,
                lease_expires_at_utc = NULL,
                last_error = $error,
                updated_at_utc = $updatedAt
            WHERE work_item_id = $workItemId
              AND lease_id = $leaseId
              AND state = $inFlight;
            """;
        command.Parameters.AddWithValue("$state", (int)DiscordOutboxState.Retry);
        command.Parameters.AddWithValue("$nextAttemptAt", nextAttemptAt.ToString("O"));
        command.Parameters.AddWithValue("$error", Truncate(error, 512));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));
        command.Parameters.AddWithValue("$leaseId", leaseId);
        command.Parameters.AddWithValue("$inFlight", (int)DiscordOutboxState.InFlight);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task FailOutboxAsync(
        Guid workItemId,
        string leaseId,
        DiscordOutboxState outboxState,
        DiscordPublicationState publicationState,
        string error,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var metadata = await LoadOutboxMetadataAsync(
            connection,
            (SqliteTransaction)transaction,
            workItemId,
            leaseId,
            cancellationToken);
        if (metadata == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        await UpdateOutboxTerminalAsync(
            connection,
            (SqliteTransaction)transaction,
            workItemId,
            leaseId,
            outboxState,
            error,
            failedAt,
            cancellationToken);
        await using var failDeferred = connection.CreateCommand();
        failDeferred.Transaction = (SqliteTransaction)transaction;
        failDeferred.CommandText =
            """
            UPDATE discord_outbox
            SET
                state = $state,
                last_error = $error,
                updated_at_utc = $failedAt
            WHERE publication_id = $publicationId
              AND operation = $edit
              AND message_id IS NULL
              AND state IN ($pending, $retry);
            """;
        failDeferred.Parameters.AddWithValue("$state", (int)outboxState);
        failDeferred.Parameters.AddWithValue("$error", Truncate(error, 512));
        failDeferred.Parameters.AddWithValue("$failedAt", failedAt.ToString("O"));
        failDeferred.Parameters.AddWithValue("$publicationId", metadata.Value.ToString("D"));
        failDeferred.Parameters.AddWithValue("$edit", (int)DiscordOutboxOperation.EditMessage);
        failDeferred.Parameters.AddWithValue("$pending", (int)DiscordOutboxState.Pending);
        failDeferred.Parameters.AddWithValue("$retry", (int)DiscordOutboxState.Retry);
        await failDeferred.ExecuteNonQueryAsync(cancellationToken);
        await using var publication = connection.CreateCommand();
        publication.Transaction = (SqliteTransaction)transaction;
        publication.CommandText =
            """
            UPDATE discord_publications
            SET state = $state, updated_at_utc = $updatedAt
            WHERE publication_id = $publicationId;
            """;
        publication.Parameters.AddWithValue("$state", (int)publicationState);
        publication.Parameters.AddWithValue("$updatedAt", failedAt.ToString("O"));
        publication.Parameters.AddWithValue("$publicationId", metadata.Value.ToString("D"));
        await publication.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<Guid?> LoadOutboxMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workItemId,
        string leaseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT publication_id
            FROM discord_outbox
            WHERE work_item_id = $workItemId
              AND lease_id = $leaseId
              AND state = $inFlight;
            """;
        command.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));
        command.Parameters.AddWithValue("$leaseId", leaseId);
        command.Parameters.AddWithValue("$inFlight", (int)DiscordOutboxState.InFlight);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Guid.Parse(reader.GetString(0))
            : null;
    }

    private static async Task UpdateOutboxTerminalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid workItemId,
        string leaseId,
        DiscordOutboxState state,
        string? error,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE discord_outbox
            SET
                state = $state,
                lease_id = NULL,
                lease_expires_at_utc = NULL,
                last_error = $error,
                updated_at_utc = $updatedAt
            WHERE work_item_id = $workItemId
              AND lease_id = $leaseId
              AND state = $inFlight;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue(
            "$error",
            string.IsNullOrWhiteSpace(error) ? DBNull.Value : Truncate(error, 512));
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));
        command.Parameters.AddWithValue("$leaseId", leaseId);
        command.Parameters.AddWithValue("$inFlight", (int)DiscordOutboxState.InFlight);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsDiscordSnowflake(string value) =>
        value.Length is >= 1 and <= 32 &&
        value.All(char.IsAsciiDigit);

    private static string CreateToken(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static string CreateActionToken() =>
        "ca:v1:" + CreateToken(18);

    private static string ClaimMessage(DiscordInterestClaim claim) =>
        claim.State switch
        {
            DiscordInterestClaimState.Pending or DiscordInterestClaimState.AssignmentPending =>
                "Your interest is already recorded. The company operator must confirm the assignment in Craft Architect.",
            DiscordInterestClaimState.Accepted =>
                "The company operator accepted this assignment.",
            DiscordInterestClaimState.Declined =>
                "The company operator declined this request.",
            _ => "This Volunteer request is no longer active."
        };

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
