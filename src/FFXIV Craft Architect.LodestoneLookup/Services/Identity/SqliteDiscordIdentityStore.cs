using System.Data;
using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

public sealed class SqliteDiscordIdentityStore(DiscordIdentityOptions options)
{
    private readonly SemaphoreSlim _linkGate = new(1, 1);

    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public async Task CreateOAuthStateAsync(
        Guid profileId,
        string plaintextState,
        string pkceVerifier,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty ||
            !IsSecret(plaintextState, 32, 256) ||
            !IsSecret(pkceVerifier, 43, 128) ||
            expiresAt <= createdAt)
        {
            throw new ArgumentException("A valid OAuth state transaction is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using (var expire = connection.CreateCommand())
        {
            expire.Transaction = transaction;
            expire.CommandText = """
                UPDATE discord_oauth_states
                SET consumed_at_utc = COALESCE(consumed_at_utc, $createdAt),
                    pkce_verifier = ''
                WHERE purpose = 'link' AND profile_id = $profileId AND consumed_at_utc IS NULL;
                """;
            expire.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
            expire.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO discord_oauth_states (
                    state_hash,
                    purpose,
                    profile_id,
                    pkce_verifier,
                    created_at_utc,
                    expires_at_utc,
                    consumed_at_utc)
                VALUES ($stateHash, 'link', $profileId, $pkceVerifier, $createdAt, $expiresAt, NULL);
                """;
            insert.Parameters.AddWithValue("$stateHash", HashSecret(plaintextState));
            insert.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            insert.Parameters.AddWithValue("$pkceVerifier", pkceVerifier);
            insert.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
            insert.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            profileId,
            "oauth_started",
            discordUserId: null,
            createdAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CreateSignInOAuthStateAsync(
        string plaintextState,
        string pkceVerifier,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        if (!IsSecret(plaintextState, 32, 256) ||
            !IsSecret(pkceVerifier, 43, 128) ||
            expiresAt <= createdAt)
        {
            throw new ArgumentException("A valid OAuth state transaction is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO discord_oauth_states (
                state_hash, purpose, profile_id, pkce_verifier,
                created_at_utc, expires_at_utc, consumed_at_utc)
            VALUES ($stateHash, 'signin', NULL, $pkceVerifier, $createdAt, $expiresAt, NULL);
            """;
        insert.Parameters.AddWithValue("$stateHash", HashSecret(plaintextState));
        insert.Parameters.AddWithValue("$pkceVerifier", pkceVerifier);
        insert.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
        insert.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await InsertAuditAsync(
            connection,
            transaction,
            profileId: null,
            "signin_oauth_started",
            discordUserId: null,
            createdAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<DiscordOAuthStateConsumption> ConsumeOAuthStateAsync(
        string plaintextState,
        DateTimeOffset consumedAt,
        CancellationToken cancellationToken = default)
    {
        if (!IsSecret(plaintextState, 32, 256))
        {
            return new DiscordOAuthStateConsumption(DiscordOAuthStateStatus.Unknown);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT profile_id, pkce_verifier, expires_at_utc, consumed_at_utc, purpose
            FROM discord_oauth_states
            WHERE state_hash = $stateHash;
            """;
        select.Parameters.AddWithValue("$stateHash", HashSecret(plaintextState));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordOAuthStateConsumption(DiscordOAuthStateStatus.Unknown);
        }

        var profileId = reader.IsDBNull(0) ? (Guid?)null : Guid.Parse(reader.GetString(0));
        var verifier = reader.GetString(1);
        var expiresAt = DateTimeOffset.Parse(reader.GetString(2));
        var alreadyConsumed = !reader.IsDBNull(3);
        var purpose = ParsePurpose(reader.GetString(4));
        await reader.DisposeAsync();
        if (alreadyConsumed)
        {
            await InsertAuditAsync(
                connection,
                transaction,
                profileId,
                "oauth_replay_rejected",
                null,
                consumedAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DiscordOAuthStateConsumption(
                DiscordOAuthStateStatus.Replayed,
                profileId,
                Purpose: purpose);
        }

        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = """
                UPDATE discord_oauth_states
                SET consumed_at_utc = $consumedAt,
                    pkce_verifier = ''
                WHERE state_hash = $stateHash AND consumed_at_utc IS NULL;
                """;
            consume.Parameters.AddWithValue("$consumedAt", consumedAt.ToString("O"));
            consume.Parameters.AddWithValue("$stateHash", HashSecret(plaintextState));
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DiscordOAuthStateConsumption(
                    DiscordOAuthStateStatus.Replayed,
                    profileId,
                    Purpose: purpose);
            }
        }

        var expired = consumedAt >= expiresAt;
        await InsertAuditAsync(
            connection,
            transaction,
            profileId,
            expired ? "oauth_expired" : "oauth_consumed",
            null,
            consumedAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DiscordOAuthStateConsumption(
            expired ? DiscordOAuthStateStatus.Expired : DiscordOAuthStateStatus.Consumed,
            profileId,
            expired ? null : verifier,
            purpose);
    }

    public async Task RecordSignInAuditAsync(
        Guid? profileId,
        string eventKind,
        string discordUserId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(eventKind) ||
            !DiscordIdentityValue.IsSnowflake(discordUserId))
        {
            throw new ArgumentException("A valid Discord sign-in audit event is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await InsertAuditAsync(
            connection,
            transaction,
            profileId,
            eventKind,
            discordUserId,
            createdAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<DiscordIdentityLinkResult> LinkAsync(
        Guid profileId,
        string discordUserId,
        string displayName,
        DateTimeOffset linkedAt,
        CancellationToken cancellationToken = default)
    {
        await _linkGate.WaitAsync(cancellationToken);
        try
        {
            return await LinkCoreAsync(
                profileId,
                discordUserId,
                displayName,
                linkedAt,
                cancellationToken);
        }
        finally
        {
            _linkGate.Release();
        }
    }

    private async Task<DiscordIdentityLinkResult> LinkCoreAsync(
        Guid profileId,
        string discordUserId,
        string displayName,
        DateTimeOffset linkedAt,
        CancellationToken cancellationToken)
    {
        displayName = NormalizeDisplayName(displayName);
        if (profileId == Guid.Empty ||
            !DiscordIdentityValue.IsSnowflake(discordUserId) ||
            string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("A valid Discord identity is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var profileLink = await LoadLinkAsync(
            connection,
            transaction,
            "profile_id",
            profileId.ToString("D"),
            cancellationToken);
        var discordLink = await LoadLinkAsync(
            connection,
            transaction,
            "discord_user_id",
            discordUserId,
            cancellationToken);
        if (profileLink != null || discordLink != null)
        {
            if (profileLink?.LinkId == discordLink?.LinkId &&
                profileLink?.DiscordUserId == discordUserId)
            {
                await using var refresh = connection.CreateCommand();
                refresh.Transaction = transaction;
                refresh.CommandText = """
                    UPDATE discord_identity_links
                    SET display_name_snapshot = $displayName, updated_at_utc = $updatedAt
                    WHERE link_id = $linkId AND revoked_at_utc IS NULL;
                    """;
                refresh.Parameters.AddWithValue("$displayName", displayName);
                refresh.Parameters.AddWithValue("$updatedAt", linkedAt.ToString("O"));
                refresh.Parameters.AddWithValue("$linkId", profileLink.LinkId.ToString("D"));
                await refresh.ExecuteNonQueryAsync(cancellationToken);
                await InsertAuditAsync(
                    connection,
                    transaction,
                    profileId,
                    "link_refreshed",
                    discordUserId,
                    linkedAt,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new DiscordIdentityLinkResult(
                    DiscordIdentityLinkResultStatus.Refreshed,
                    profileLink with { DisplayNameSnapshot = displayName, UpdatedAt = linkedAt });
            }

            var status = profileLink != null
                ? DiscordIdentityLinkResultStatus.ProfileConflict
                : DiscordIdentityLinkResultStatus.DiscordConflict;
            await InsertAuditAsync(
                connection,
                transaction,
                profileId,
                status == DiscordIdentityLinkResultStatus.ProfileConflict
                    ? "profile_link_conflict"
                    : "discord_link_conflict",
                discordUserId,
                linkedAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DiscordIdentityLinkResult(status);
        }

        var link = new DiscordIdentityLink(
            Guid.NewGuid(),
            profileId,
            discordUserId,
            displayName,
            linkedAt,
            linkedAt);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO discord_identity_links (
                    link_id,
                    profile_id,
                    discord_user_id,
                    display_name_snapshot,
                    linked_at_utc,
                    updated_at_utc,
                    revoked_at_utc)
                VALUES ($linkId, $profileId, $discordUserId, $displayName, $linkedAt, $updatedAt, NULL);
                """;
            insert.Parameters.AddWithValue("$linkId", link.LinkId.ToString("D"));
            insert.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            insert.Parameters.AddWithValue("$discordUserId", discordUserId);
            insert.Parameters.AddWithValue("$displayName", displayName);
            insert.Parameters.AddWithValue("$linkedAt", linkedAt.ToString("O"));
            insert.Parameters.AddWithValue("$updatedAt", linkedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            profileId,
            "linked",
            discordUserId,
            linkedAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DiscordIdentityLinkResult(DiscordIdentityLinkResultStatus.Linked, link);
    }

    public async Task<DiscordIdentityLink?> LoadByProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadLinkAsync(
            connection,
            transaction: null,
            "profile_id",
            profileId.ToString("D"),
            cancellationToken);
    }

    public async Task<DiscordIdentityLink?> LoadByDiscordUserAsync(
        string discordUserId,
        CancellationToken cancellationToken = default)
    {
        if (!DiscordIdentityValue.IsSnowflake(discordUserId))
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken);
        return await LoadLinkAsync(
            connection,
            transaction: null,
            "discord_user_id",
            discordUserId,
            cancellationToken);
    }

    public async Task<bool> UnlinkAsync(
        Guid profileId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var link = await LoadLinkAsync(
            connection,
            transaction,
            "profile_id",
            profileId.ToString("D"),
            cancellationToken);
        if (link == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE discord_identity_links
            SET revoked_at_utc = $revokedAt, updated_at_utc = $revokedAt
            WHERE link_id = $linkId AND revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$revokedAt", revokedAt.ToString("O"));
        command.Parameters.AddWithValue("$linkId", link.LinkId.ToString("D"));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (changed)
        {
            await InsertAuditAsync(
                connection,
                transaction,
                profileId,
                "unlinked",
                link.DiscordUserId,
                revokedAt,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<IReadOnlyList<DiscordIdentityAuditEvent>> LoadAuditAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, profile_id, event_kind, discord_user_id, created_at_utc
            FROM discord_identity_audit
            WHERE profile_id = $profileId
            ORDER BY created_at_utc, event_id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
        var events = new List<DiscordIdentityAuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new DiscordIdentityAuditEvent(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return events;
    }

    internal async Task IssueBootstrapAsync(
        DiscordParticipantBootstrapBinding binding,
        string plaintextToken,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken = default)
    {
        if (!IsSecret(plaintextToken, 32, 256) ||
            !DiscordIdentityValue.IsSnowflake(binding.ProviderEventId) ||
            binding.ExpiresAt <= issuedAt)
        {
            throw new ArgumentException("A valid participant bootstrap is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using var inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        inspect.CommandText = """
            SELECT token_hash, profile_id, discord_user_id, company_id, commission_id,
                   public_brief_id, participant_grant_id, participant_revision, expires_at_utc
            FROM discord_participant_bootstraps
            WHERE provider_event_id = $providerEventId;
            """;
        inspect.Parameters.AddWithValue("$providerEventId", binding.ProviderEventId);
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var same = reader.GetString(0) == HashSecret(plaintextToken) &&
                reader.GetString(1) == binding.ProfileId.ToString("D") &&
                reader.GetString(2) == binding.DiscordUserId &&
                reader.GetString(3) == binding.CompanyId.ToString() &&
                reader.GetString(4) == binding.CommissionId.ToString("D") &&
                reader.GetString(5) == binding.PublicBriefId &&
                reader.GetString(6) == binding.ParticipantGrantId.ToString("D") &&
                reader.GetInt64(7) == binding.ParticipantCapabilityRevision &&
                DateTimeOffset.Parse(reader.GetString(8)) == binding.ExpiresAt;
            await reader.DisposeAsync();
            if (!same)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    "The Discord interaction was already bound to different participant authority.");
            }

            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await reader.DisposeAsync();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO discord_participant_bootstraps (
                provider_event_id, token_hash, profile_id, discord_user_id, company_id,
                commission_id, public_brief_id, participant_grant_id, participant_revision,
                issued_at_utc, expires_at_utc, redeemed_at_utc, participant_credential_hash)
            VALUES (
                $providerEventId, $tokenHash, $profileId, $discordUserId, $companyId,
                $commissionId, $publicBriefId, $participantGrantId, $participantRevision,
                $issuedAt, $expiresAt, NULL, NULL);
            """;
        insert.Parameters.AddWithValue("$providerEventId", binding.ProviderEventId);
        insert.Parameters.AddWithValue("$tokenHash", HashSecret(plaintextToken));
        insert.Parameters.AddWithValue("$profileId", binding.ProfileId.ToString("D"));
        insert.Parameters.AddWithValue("$discordUserId", binding.DiscordUserId);
        insert.Parameters.AddWithValue("$companyId", binding.CompanyId.ToString());
        insert.Parameters.AddWithValue("$commissionId", binding.CommissionId.ToString("D"));
        insert.Parameters.AddWithValue("$publicBriefId", binding.PublicBriefId);
        insert.Parameters.AddWithValue("$participantGrantId", binding.ParticipantGrantId.ToString("D"));
        insert.Parameters.AddWithValue("$participantRevision", binding.ParticipantCapabilityRevision);
        insert.Parameters.AddWithValue("$issuedAt", issuedAt.ToString("O"));
        insert.Parameters.AddWithValue("$expiresAt", binding.ExpiresAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await InsertAuditAsync(
            connection,
            transaction,
            binding.ProfileId,
            "participant_bootstrap_issued",
            binding.DiscordUserId,
            issuedAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<DiscordParticipantBootstrapBinding?> LoadBootstrapAsync(
        string providerEventId,
        CancellationToken cancellationToken = default)
    {
        if (!DiscordIdentityValue.IsSnowflake(providerEventId))
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, discord_user_id, company_id, commission_id,
                   public_brief_id, participant_grant_id, participant_revision, expires_at_utc
            FROM discord_participant_bootstraps
            WHERE provider_event_id = $providerEventId;
            """;
        command.Parameters.AddWithValue("$providerEventId", providerEventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DiscordParticipantBootstrapBinding(
                providerEventId,
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                CompanyId.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                Guid.Parse(reader.GetString(5)),
                reader.GetInt64(6),
                DateTimeOffset.Parse(reader.GetString(7)))
            : null;
    }

    internal async Task<DiscordParticipantBootstrapRedemption> RedeemBootstrapAsync(
        string plaintextToken,
        string participantCredential,
        DateTimeOffset redeemedAt,
        CancellationToken cancellationToken = default)
    {
        if (!IsSecret(plaintextToken, 32, 256) ||
            !IsSecret(participantCredential, 32, 512))
        {
            return new DiscordParticipantBootstrapRedemption(
                DiscordParticipantBootstrapRedemptionStatus.Unknown);
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT provider_event_id, profile_id, discord_user_id, company_id, commission_id,
                   public_brief_id, participant_grant_id, participant_revision, expires_at_utc,
                   redeemed_at_utc, participant_credential_hash
            FROM discord_participant_bootstraps
            WHERE token_hash = $tokenHash;
            """;
        command.Parameters.AddWithValue("$tokenHash", HashSecret(plaintextToken));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordParticipantBootstrapRedemption(
                DiscordParticipantBootstrapRedemptionStatus.Unknown);
        }

        var binding = new DiscordParticipantBootstrapBinding(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            CompanyId.Parse(reader.GetString(3)),
            Guid.Parse(reader.GetString(4)),
            reader.GetString(5),
            Guid.Parse(reader.GetString(6)),
            reader.GetInt64(7),
            DateTimeOffset.Parse(reader.GetString(8)));
        var alreadyRedeemed = !reader.IsDBNull(9);
        var storedCredentialHash = reader.IsDBNull(10) ? null : reader.GetString(10);
        await reader.DisposeAsync();
        var credentialHash = HashSecret(participantCredential);
        if (alreadyRedeemed)
        {
            var replay = string.Equals(
                storedCredentialHash,
                credentialHash,
                StringComparison.Ordinal);
            await InsertAuditAsync(
                connection,
                transaction,
                binding.ProfileId,
                replay ? "participant_bootstrap_replayed" : "participant_bootstrap_replay_rejected",
                binding.DiscordUserId,
                redeemedAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DiscordParticipantBootstrapRedemption(
                replay
                    ? DiscordParticipantBootstrapRedemptionStatus.Replayed
                    : DiscordParticipantBootstrapRedemptionStatus.ReplayRejected,
                replay ? binding : null);
        }

        if (redeemedAt >= binding.ExpiresAt)
        {
            await InsertAuditAsync(
                connection,
                transaction,
                binding.ProfileId,
                "participant_bootstrap_expired",
                binding.DiscordUserId,
                redeemedAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DiscordParticipantBootstrapRedemption(
                DiscordParticipantBootstrapRedemptionStatus.Expired);
        }

        await using var redeem = connection.CreateCommand();
        redeem.Transaction = transaction;
        redeem.CommandText = """
            UPDATE discord_participant_bootstraps
            SET redeemed_at_utc = $redeemedAt,
                participant_credential_hash = $credentialHash
            WHERE token_hash = $tokenHash AND redeemed_at_utc IS NULL;
            """;
        redeem.Parameters.AddWithValue("$redeemedAt", redeemedAt.ToString("O"));
        redeem.Parameters.AddWithValue("$credentialHash", credentialHash);
        redeem.Parameters.AddWithValue("$tokenHash", HashSecret(plaintextToken));
        if (await redeem.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DiscordParticipantBootstrapRedemption(
                DiscordParticipantBootstrapRedemptionStatus.ReplayRejected);
        }

        await InsertAuditAsync(
            connection,
            transaction,
            binding.ProfileId,
            "participant_bootstrap_redeemed",
            binding.DiscordUserId,
            redeemedAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DiscordParticipantBootstrapRedemption(
            DiscordParticipantBootstrapRedemptionStatus.Redeemed,
            binding);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var absolutePath = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = absolutePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        return connection;
    }

    private async Task EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
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

            await using (var version = connection.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version;";
                var schemaVersion = Convert.ToInt32(
                    await version.ExecuteScalarAsync(cancellationToken));
                if (schemaVersion < 2)
                {
                    var hasV1Tables = false;
                    await using (var probe = connection.CreateCommand())
                    {
                        probe.CommandText = """
                            SELECT COUNT(*) FROM sqlite_master
                            WHERE type = 'table' AND name IN (
                                'discord_oauth_states', 'discord_identity_audit');
                            """;
                        hasV1Tables = Convert.ToInt32(
                            await probe.ExecuteScalarAsync(cancellationToken)) == 2;
                    }

                    if (hasV1Tables)
                    {
                        await using var recreate = connection.CreateCommand();
                        recreate.CommandText = """
                            BEGIN IMMEDIATE;
                            CREATE TABLE IF NOT EXISTS discord_oauth_states_v2 (
                                state_hash TEXT PRIMARY KEY,
                                purpose TEXT NOT NULL CHECK (purpose IN ('link', 'signin')),
                                profile_id TEXT NULL,
                                pkce_verifier TEXT NOT NULL,
                                created_at_utc TEXT NOT NULL,
                                expires_at_utc TEXT NOT NULL,
                                consumed_at_utc TEXT NULL
                            );
                            INSERT INTO discord_oauth_states_v2
                                SELECT state_hash, 'link', profile_id, pkce_verifier,
                                       created_at_utc, expires_at_utc, consumed_at_utc
                                FROM discord_oauth_states;
                            DROP TABLE discord_oauth_states;
                            ALTER TABLE discord_oauth_states_v2 RENAME TO discord_oauth_states;

                            CREATE TABLE IF NOT EXISTS discord_identity_audit_v2 (
                                event_id TEXT PRIMARY KEY,
                                profile_id TEXT NULL,
                                event_kind TEXT NOT NULL,
                                discord_user_id TEXT NULL,
                                created_at_utc TEXT NOT NULL
                            );
                            INSERT INTO discord_identity_audit_v2
                                SELECT event_id, profile_id, event_kind, discord_user_id, created_at_utc
                                FROM discord_identity_audit;
                            DROP TABLE discord_identity_audit;
                            ALTER TABLE discord_identity_audit_v2 RENAME TO discord_identity_audit;
                            COMMIT;
                            """;
                        await recreate.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using var mark = connection.CreateCommand();
                    mark.CommandText = "PRAGMA user_version = 2;";
                    await mark.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS discord_identity_links (
                    link_id TEXT PRIMARY KEY,
                    profile_id TEXT NOT NULL,
                    discord_user_id TEXT NOT NULL,
                    display_name_snapshot TEXT NOT NULL,
                    linked_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    revoked_at_utc TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_discord_identity_links_profile
                ON discord_identity_links(profile_id) WHERE revoked_at_utc IS NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS ux_discord_identity_links_user
                ON discord_identity_links(discord_user_id) WHERE revoked_at_utc IS NULL;

                CREATE TABLE IF NOT EXISTS discord_oauth_states (
                    state_hash TEXT PRIMARY KEY,
                    purpose TEXT NOT NULL CHECK (purpose IN ('link', 'signin')),
                    profile_id TEXT NULL,
                    pkce_verifier TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    consumed_at_utc TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_discord_oauth_states_profile
                ON discord_oauth_states(profile_id, consumed_at_utc);

                CREATE TABLE IF NOT EXISTS discord_identity_audit (
                    event_id TEXT PRIMARY KEY,
                    profile_id TEXT NULL,
                    event_kind TEXT NOT NULL,
                    discord_user_id TEXT NULL,
                    created_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_discord_identity_audit_profile
                ON discord_identity_audit(profile_id, created_at_utc);

                CREATE TABLE IF NOT EXISTS discord_participant_bootstraps (
                    provider_event_id TEXT PRIMARY KEY,
                    token_hash TEXT NOT NULL UNIQUE,
                    profile_id TEXT NOT NULL,
                    discord_user_id TEXT NOT NULL,
                    company_id TEXT NOT NULL,
                    commission_id TEXT NOT NULL,
                    public_brief_id TEXT NOT NULL,
                    participant_grant_id TEXT NOT NULL,
                    participant_revision INTEGER NOT NULL,
                    issued_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    redeemed_at_utc TEXT NULL,
                    participant_credential_hash TEXT NULL
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

    private static async Task<DiscordIdentityLink?> LoadLinkAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string column,
        string value,
        CancellationToken cancellationToken)
    {
        if (column is not ("profile_id" or "discord_user_id"))
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT link_id, profile_id, discord_user_id, display_name_snapshot,
                   linked_at_utc, updated_at_utc
            FROM discord_identity_links
            WHERE {column} = $value AND revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DiscordIdentityLink(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5)))
            : null;
    }

    private static async Task InsertAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid? profileId,
        string eventKind,
        string? discordUserId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO discord_identity_audit (
                event_id, profile_id, event_kind, discord_user_id, created_at_utc)
            VALUES ($eventId, $profileId, $eventKind, $discordUserId, $createdAt);
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue(
            "$profileId",
            profileId is { } value ? value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$eventKind", eventKind);
        command.Parameters.AddWithValue(
            "$discordUserId",
            discordUserId is null ? DBNull.Value : discordUserId);
        command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeDisplayName(string value)
    {
        var normalized = string.Join(' ', value.Trim().Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    internal static string HashSecret(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSecret(string? value, int minimum, int maximum) =>
        value is not null &&
        value.Length >= minimum &&
        value.Length <= maximum &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static DiscordOAuthPurpose ParsePurpose(string value) => value switch
    {
        "link" => DiscordOAuthPurpose.Link,
        "signin" => DiscordOAuthPurpose.SignIn,
        _ => throw new InvalidOperationException("The OAuth state purpose is invalid.")
    };
}
