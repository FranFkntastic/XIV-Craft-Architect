using System.Data;
using System.Globalization;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

/// <summary>
/// One row of the account→claims lookup. It only says where to look: the canonical
/// commission's active claim decides ownership every time it is read.
/// </summary>
public sealed record AccountCommissionClaim(
    Guid AccountProfileId,
    CompanyId CompanyId,
    Guid CommissionId,
    Guid ClaimId,
    DateTimeOffset ClaimedAtUtc);

public sealed record AccountCommissionClaimOwner(
    Guid AccountProfileId,
    Guid ClaimId,
    DateTimeOffset ClaimedAtUtc);

public sealed record AccountCommissionClaimRebuildResult(
    int AddedRows,
    int RemovedRows,
    int SkippedCommissions);

public sealed partial class SqliteMembershipStore
{
    private const string ProjectionVersionKey = "projection_version";

    /// <summary>
    /// Replaces the projected owners of one commission as of the canonical
    /// <paramref name="commissionRevision"/>. An empty owner list removes the commission
    /// from every account's lookup (released, rejected, or cancelled claims). Post-commit
    /// projections are not serialized across commands, so a write older than the
    /// commission's recorded revision is ignored; an equal revision rewrites (same
    /// canonical state, possibly newer owner resolution). Returns whether it applied.
    /// </summary>
    public async Task<bool> SetCommissionClaimOwnersAsync(
        CompanyId companyId,
        Guid commissionId,
        long commissionRevision,
        IReadOnlyCollection<AccountCommissionClaimOwner> owners,
        CancellationToken cancellationToken = default)
    {
        if (companyId.Value == Guid.Empty || commissionId == Guid.Empty)
        {
            throw new ArgumentException("A valid company and commission identity are required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using (var stored = connection.CreateCommand())
        {
            stored.Transaction = transaction;
            stored.CommandText = """
                SELECT commission_revision FROM account_commission_claim_marks
                WHERE company_id = $companyId AND commission_id = $commissionId;
                """;
            stored.Parameters.AddWithValue("$companyId", companyId.ToString());
            stored.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
            if (await stored.ExecuteScalarAsync(cancellationToken) is long storedRevision &&
                storedRevision > commissionRevision)
            {
                await transaction.CommitAsync(cancellationToken);
                return false;
            }
        }
        await ReplaceCommissionClaimOwnersAsync(
            connection,
            transaction,
            companyId,
            commissionId,
            owners,
            cancellationToken);
        await UpsertCommissionClaimMarkAsync(
            connection,
            transaction,
            companyId.ToString(),
            commissionId.ToString("D"),
            commissionRevision,
            touchProjectedAt: true,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AccountCommissionClaim>> LoadAccountCommissionClaimsAsync(
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        if (accountProfileId == Guid.Empty)
        {
            return [];
        }

        return await LoadCommissionClaimsAsync(
            "WHERE account_profile_id = $accountProfileId",
            command => command.Parameters.AddWithValue(
                "$accountProfileId",
                accountProfileId.ToString("D")),
            cancellationToken);
    }

    public async Task<IReadOnlyList<AccountCommissionClaim>> LoadAllAccountCommissionClaimsAsync(
        CancellationToken cancellationToken = default) =>
        await LoadCommissionClaimsAsync(string.Empty, _ => { }, cancellationToken);

    public async Task<bool> HasAccountCommissionClaimsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM account_commission_claims);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>The projection version the lookup was last fully rebuilt with, if any.</summary>
    public async Task<int?> LoadAccountCommissionClaimProjectionVersionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM account_commission_claim_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", ProjectionVersionKey);
        return await command.ExecuteScalarAsync(cancellationToken) is string value &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
                ? version
                : null;
    }

    /// <summary>
    /// Brings the lookup in line with a full rebuild from canonical commissions read since
    /// <paramref name="snapshotStartedAtUtc"/>. <paramref name="snapshotRevisions"/> holds
    /// the canonical revision of every commission the rebuild read. Only commissions whose
    /// owners differ are rewritten; one already projected at a newer revision than the
    /// rebuild read is left untouched (a commission the rebuild did not read falls back to
    /// whether it was projected after the snapshot started). Every commission read is
    /// marked at its snapshot revision so a delayed older projection cannot overwrite it.
    /// Records <paramref name="projectionVersion"/> as the lookup's version.
    /// </summary>
    public async Task<AccountCommissionClaimRebuildResult> ApplyAccountCommissionClaimRebuildAsync(
        IReadOnlyCollection<AccountCommissionClaim> expected,
        IReadOnlyDictionary<(CompanyId CompanyId, Guid CommissionId), long> snapshotRevisions,
        DateTimeOffset snapshotStartedAtUtc,
        int projectionVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var marks = new Dictionary<(string, string), (bool ProjectedSinceSnapshot, long? Revision)>();
        await using (var markRows = connection.CreateCommand())
        {
            markRows.Transaction = transaction;
            markRows.CommandText = """
                SELECT company_id, commission_id, projected_at_utc, commission_revision
                FROM account_commission_claim_marks;
                """;
            await using var reader = await markRows.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var projectedSinceSnapshot = DateTimeOffset.TryParse(
                        reader.GetString(2),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var projectedAt) &&
                    projectedAt >= snapshotStartedAtUtc;
                marks[(reader.GetString(0), reader.GetString(1))] = (
                    projectedSinceSnapshot,
                    reader.IsDBNull(3) ? null : reader.GetInt64(3));
            }
        }
        var revisions = snapshotRevisions.ToDictionary(
            item => (item.Key.CompanyId.ToString(), item.Key.CommissionId.ToString("D")),
            item => item.Value);

        var current = new Dictionary<(string, string), HashSet<(Guid, Guid)>>();
        await using (var rows = connection.CreateCommand())
        {
            rows.Transaction = transaction;
            rows.CommandText = "SELECT company_id, commission_id, account_profile_id, claim_id FROM account_commission_claims;";
            await using var reader = await rows.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!current.TryGetValue(key, out var owners))
                {
                    current[key] = owners = [];
                }
                owners.Add((Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3))));
            }
        }

        var wanted = expected
            .GroupBy(item => (item.CompanyId.ToString(), item.CommissionId.ToString("D")))
            .ToDictionary(group => group.Key, group => group.ToArray());
        int added = 0, removed = 0, skipped = 0;
        foreach (var key in wanted.Keys.Union(current.Keys).Union(revisions.Keys).ToArray())
        {
            var want = wanted.TryGetValue(key, out var items) ? items : [];
            var wantSet = want.Select(item => (item.AccountProfileId, item.ClaimId)).ToHashSet();
            var have = current.TryGetValue(key, out var existing) ? existing : [];
            var hasMark = marks.TryGetValue(key, out var mark);
            var hasRevision = revisions.TryGetValue(key, out var snapshotRevision);
            // Projected at a newer canonical revision than the rebuild read (or, without
            // comparable revisions, projected after the snapshot started): keep it.
            var newer = hasMark &&
                (hasRevision && mark.Revision is { } markRevision
                    ? markRevision > snapshotRevision
                    : mark.ProjectedSinceSnapshot);
            if (newer)
            {
                if (!wantSet.SetEquals(have))
                {
                    skipped++;
                }
                continue;
            }
            if (hasRevision && !(hasMark && mark.Revision >= snapshotRevision))
            {
                await UpsertCommissionClaimMarkAsync(
                    connection,
                    transaction,
                    key.Item1,
                    key.Item2,
                    snapshotRevision,
                    touchProjectedAt: false,
                    cancellationToken);
            }
            if (wantSet.SetEquals(have))
            {
                continue;
            }
            added += wantSet.Count(item => !have.Contains(item));
            removed += have.Count(item => !wantSet.Contains(item));
            await ReplaceCommissionClaimOwnersAsync(
                connection,
                transaction,
                ParseCompanyId(key.Item1),
                Guid.Parse(key.Item2),
                want.Select(item => new AccountCommissionClaimOwner(
                        item.AccountProfileId,
                        item.ClaimId,
                        item.ClaimedAtUtc))
                    .ToArray(),
                cancellationToken);
        }

        await using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = """
                INSERT INTO account_commission_claim_meta (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            version.Parameters.AddWithValue("$key", ProjectionVersionKey);
            version.Parameters.AddWithValue("$value", projectionVersion.ToString(CultureInfo.InvariantCulture));
            await version.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new AccountCommissionClaimRebuildResult(added, removed, skipped);
    }

    // Records the canonical revision a commission's owners were projected at. A live
    // projection also stamps the time, so a rebuild that did not read the commission
    // (created after its snapshot started) leaves it alone; a rebuild only raises the
    // revision and keeps any earlier stamp.
    private static async Task UpsertCommissionClaimMarkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string companyId,
        string commissionId,
        long commissionRevision,
        bool touchProjectedAt,
        CancellationToken cancellationToken)
    {
        await using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = touchProjectedAt
            ? """
                INSERT INTO account_commission_claim_marks (
                    company_id, commission_id, projected_at_utc, commission_revision)
                VALUES ($companyId, $commissionId, $projectedAt, $revision)
                ON CONFLICT(company_id, commission_id) DO UPDATE SET
                    projected_at_utc = excluded.projected_at_utc,
                    commission_revision = excluded.commission_revision;
                """
            : """
                INSERT INTO account_commission_claim_marks (
                    company_id, commission_id, projected_at_utc, commission_revision)
                VALUES ($companyId, $commissionId, $projectedAt, $revision)
                ON CONFLICT(company_id, commission_id) DO UPDATE SET
                    commission_revision = excluded.commission_revision;
                """;
        mark.Parameters.AddWithValue("$companyId", companyId);
        mark.Parameters.AddWithValue("$commissionId", commissionId);
        mark.Parameters.AddWithValue(
            "$projectedAt",
            (touchProjectedAt ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue).ToString("O"));
        mark.Parameters.AddWithValue("$revision", commissionRevision);
        await mark.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CrafterAccountBinding>> LoadCrafterBindingsForAccountAsync(
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        if (accountProfileId == Guid.Empty)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT company_id, legacy_crafter_id, account_profile_id, evidence_kind,
                   actor_profile_id, created_at_utc, updated_at_utc
            FROM company_crafter_account_bindings
            WHERE account_profile_id = $accountProfileId
            ORDER BY company_id, created_at_utc, legacy_crafter_id;
            """;
        command.Parameters.AddWithValue("$accountProfileId", accountProfileId.ToString("D"));
        var bindings = new List<CrafterAccountBinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bindings.Add(ReadCrafterBinding(reader));
        }
        return bindings;
    }

    /// <summary>
    /// Active invitations that the host delivered on Discord to this Discord user.
    /// </summary>
    public async Task<IReadOnlyList<CompanyMembershipInvitation>> LoadDiscordRecipientInvitationsAsync(
        string discordUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(discordUserId))
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT invitation_id, company_id, legacy_crafter_id, issued_by_profile_id,
                   issued_at_utc, expires_at_utc, revoked_at_utc, revoked_by_profile_id,
                   consumed_at_utc, consumed_by_profile_id,
                   discord_delivery_state, discord_recipient_name, discord_delivery_code
            FROM company_membership_invitations
            WHERE discord_recipient_user_id = $discordUserId
              AND discord_delivery_state = 'sent'
              AND revoked_at_utc IS NULL
              AND consumed_at_utc IS NULL
            ORDER BY issued_at_utc DESC;
            """;
        command.Parameters.AddWithValue("$discordUserId", discordUserId);
        var now = timeProvider.GetUtcNow();
        var invitations = new List<CompanyMembershipInvitation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var invitation = ReadInvitation(reader, now);
            if (invitation.State == MembershipInvitationState.Active)
            {
                invitations.Add(invitation);
            }
        }
        return invitations;
    }

    /// <summary>
    /// The invitee declines an invitation addressed to their Discord user. It is revoked
    /// with the invitee as actor, exactly as if an administrator had withdrawn it.
    /// </summary>
    public async Task<bool> DeclineInvitationForRecipientAsync(
        Guid invitationId,
        Guid accountProfileId,
        string discordUserId,
        CancellationToken cancellationToken = default)
    {
        if (invitationId == Guid.Empty || accountProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(discordUserId))
        {
            return false;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var invitation = await LoadRecipientInvitationAsync(
            connection,
            transaction,
            invitationId,
            discordUserId,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (invitation == null ||
            invitation.RevokedAtUtc != null ||
            invitation.ConsumedAtUtc != null ||
            invitation.ExpiresAtUtc <= now)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE company_membership_invitations
                SET revoked_at_utc = $revokedAt, revoked_by_profile_id = $revokedBy
                WHERE invitation_id = $invitationId
                  AND revoked_at_utc IS NULL AND consumed_at_utc IS NULL;
                """;
            command.Parameters.AddWithValue("$revokedAt", now.ToString("O"));
            command.Parameters.AddWithValue("$revokedBy", accountProfileId.ToString("D"));
            command.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }
        await InsertInvitationEventAsync(
            connection,
            transaction,
            invitationId,
            invitation.CompanyId,
            "revoked",
            accountProfileId,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<CompanyMembershipInvitation?> LoadRecipientInvitationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid invitationId,
        string discordUserId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT invitation_id, company_id, legacy_crafter_id, issued_by_profile_id,
                   issued_at_utc, expires_at_utc, revoked_at_utc, revoked_by_profile_id,
                   consumed_at_utc, consumed_by_profile_id,
                   discord_delivery_state, discord_recipient_name, discord_delivery_code
            FROM company_membership_invitations
            WHERE invitation_id = $invitationId
              AND discord_recipient_user_id = $discordUserId
              AND discord_delivery_state = 'sent';
            """;
        command.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
        command.Parameters.AddWithValue("$discordUserId", discordUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadInvitation(reader, DateTimeOffset.MinValue)
            : null;
    }

    private static async Task ReplaceCommissionClaimOwnersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        Guid commissionId,
        IReadOnlyCollection<AccountCommissionClaimOwner> owners,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM account_commission_claims
                WHERE company_id = $companyId AND commission_id = $commissionId;
                """;
            delete.Parameters.AddWithValue("$companyId", companyId.ToString());
            delete.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        var updatedAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var owner in owners
                     .Where(item => item.AccountProfileId != Guid.Empty)
                     .GroupBy(item => item.AccountProfileId)
                     .Select(group => group.First()))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO account_commission_claims (
                    account_profile_id, company_id, commission_id, claim_id,
                    claimed_at_utc, updated_at_utc)
                VALUES (
                    $accountProfileId, $companyId, $commissionId, $claimId,
                    $claimedAtUtc, $updatedAtUtc);
                """;
            insert.Parameters.AddWithValue("$accountProfileId", owner.AccountProfileId.ToString("D"));
            insert.Parameters.AddWithValue("$companyId", companyId.ToString());
            insert.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
            insert.Parameters.AddWithValue("$claimId", owner.ClaimId.ToString("D"));
            insert.Parameters.AddWithValue("$claimedAtUtc", owner.ClaimedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$updatedAtUtc", updatedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<AccountCommissionClaim>> LoadCommissionClaimsAsync(
        string whereClause,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT account_profile_id, company_id, commission_id, claim_id, claimed_at_utc
            FROM account_commission_claims
            {whereClause}
            ORDER BY account_profile_id, company_id, claimed_at_utc, commission_id;
            """;
        bind(command);
        var claims = new List<AccountCommissionClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(new AccountCommissionClaim(
                Guid.Parse(reader.GetString(0)),
                ParseCompanyId(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        }
        return claims;
    }
}
