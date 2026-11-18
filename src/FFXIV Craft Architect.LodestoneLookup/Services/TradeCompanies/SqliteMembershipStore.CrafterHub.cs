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

public sealed partial class SqliteMembershipStore
{
    /// <summary>
    /// Replaces the projected owners of one commission. An empty owner list removes the
    /// commission from every account's lookup (released, rejected, or cancelled claims).
    /// </summary>
    public async Task SetCommissionClaimOwnersAsync(
        CompanyId companyId,
        Guid commissionId,
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
        await ReplaceCommissionClaimOwnersAsync(
            connection,
            transaction,
            companyId,
            commissionId,
            owners,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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

    /// <summary>
    /// Makes the lookup equal to a full rebuild from canonical commissions, in one
    /// transaction. Rows for commissions outside <paramref name="expected"/> are removed.
    /// </summary>
    public async Task ReplaceAllAccountCommissionClaimsAsync(
        IReadOnlyCollection<AccountCommissionClaim> expected,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM account_commission_claims;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var group in expected.GroupBy(item => (item.CompanyId, item.CommissionId)))
        {
            await ReplaceCommissionClaimOwnersAsync(
                connection,
                transaction,
                group.Key.CompanyId,
                group.Key.CommissionId,
                group.Select(item => new AccountCommissionClaimOwner(
                        item.AccountProfileId,
                        item.ClaimId,
                        item.ClaimedAtUtc))
                    .ToArray(),
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
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
