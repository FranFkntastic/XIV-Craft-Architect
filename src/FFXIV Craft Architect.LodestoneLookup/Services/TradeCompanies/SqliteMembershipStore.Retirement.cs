using System.Data;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record CompanyMembershipFootprint(
    int Memberships,
    int OpenMemberships,
    int OpenInvitations,
    int CrafterBindings,
    int BindingSuppressions,
    int CommissionAttentionRows,
    bool AlreadyRetired);

public sealed record CompanyMembershipRetirementResult(
    int MembershipsRevoked,
    int InvitationsRevoked,
    int BindingsRemoved,
    int SuppressionsRemoved,
    int AttentionRowsRemoved);

// Operator-run retirement of stale company containers.
public sealed partial class SqliteMembershipStore
{
    public async Task<CompanyMembershipFootprint> LoadCompanyFootprintAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        var id = companyId.ToString();
        return new CompanyMembershipFootprint(
            await CountAsync(connection, "SELECT count(*) FROM company_memberships WHERE company_id = $companyId;", id, cancellationToken),
            await CountAsync(connection, "SELECT count(*) FROM company_memberships WHERE company_id = $companyId AND state IN ('pending', 'active');", id, cancellationToken),
            await CountAsync(connection, "SELECT count(*) FROM company_membership_invitations WHERE company_id = $companyId AND revoked_at_utc IS NULL AND consumed_at_utc IS NULL;", id, cancellationToken),
            await CountAsync(connection, "SELECT count(*) FROM company_crafter_account_bindings WHERE company_id = $companyId;", id, cancellationToken),
            await CountAsync(connection, "SELECT count(*) FROM company_crafter_account_binding_suppressions WHERE company_id = $companyId;", id, cancellationToken),
            await CountAsync(connection, "SELECT count(*) FROM membership_commission_attention WHERE company_id = $companyId;", id, cancellationToken),
            await CountAsync(connection, "SELECT count(*) FROM company_retirement_receipts WHERE company_id = $companyId;", id, cancellationToken) > 0);
    }

    public async Task<bool> IsCompanyRetiredAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        return await CountAsync(
            connection,
            "SELECT count(*) FROM company_retirement_receipts WHERE company_id = $companyId;",
            companyId.ToString(),
            cancellationToken) > 0;
    }

    // Closes every membership-side door into a retired company: memberships are
    // revoked, open invitations revoked, crafter bindings unbound, and the receipt
    // recorded. Events are written so the audit trail explains each change.
    public async Task<CompanyMembershipRetirementResult> RetireCompanyAsync(
        CompanyId companyId,
        Guid actorProfileId,
        string actor,
        string detailsJson,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, actorProfileId);
        const string Reason = "The company was retired by an administrator.";
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var id = companyId.ToString();
        var actorId = actorProfileId.ToString("D");

        var openMemberships = new List<(string Account, string State, string Role)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT account_profile_id, state, role FROM company_memberships
                WHERE company_id = $companyId AND state IN ('pending', 'active');
                """;
            select.Parameters.AddWithValue("$companyId", id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                openMemberships.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var membership in openMemberships)
        {
            await ExecuteAsync(
                """
                UPDATE company_memberships
                SET state = 'revoked', decided_at_utc = $now, decided_by_profile_id = $actor
                WHERE company_id = $companyId AND account_profile_id = $account;
                """,
                ("$account", membership.Account));
            await ExecuteAsync(
                """
                INSERT INTO membership_events (
                    event_id, company_id, account_profile_id, from_state, to_state,
                    actor_profile_id, created_at_utc, role, decided_at_utc,
                    decided_by_profile_id, reason)
                VALUES (
                    $eventId, $companyId, $account, $fromState, 'revoked',
                    $actor, $now, $role, $now, $actor, $reason);
                """,
                ("$eventId", Guid.NewGuid().ToString("D")),
                ("$account", membership.Account),
                ("$fromState", membership.State),
                ("$role", membership.Role),
                ("$reason", Reason));
        }

        var openInvitations = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT invitation_id FROM company_membership_invitations
                WHERE company_id = $companyId AND revoked_at_utc IS NULL AND consumed_at_utc IS NULL;
                """;
            select.Parameters.AddWithValue("$companyId", id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                openInvitations.Add(reader.GetString(0));
            }
        }

        foreach (var invitation in openInvitations)
        {
            await ExecuteAsync(
                """
                UPDATE company_membership_invitations
                SET revoked_at_utc = $now, revoked_by_profile_id = $actor
                WHERE invitation_id = $invitation;
                """,
                ("$invitation", invitation));
            await ExecuteAsync(
                """
                INSERT INTO company_membership_invitation_events (
                    event_id, invitation_id, company_id, action, actor_profile_id, created_at_utc)
                VALUES ($eventId, $invitation, $companyId, 'revoked', $actor, $now);
                """,
                ("$eventId", Guid.NewGuid().ToString("D")),
                ("$invitation", invitation));
        }

        var bindings = new List<(string Crafter, string Account, string Evidence)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT legacy_crafter_id, account_profile_id, evidence_kind
                FROM company_crafter_account_bindings WHERE company_id = $companyId;
                """;
            select.Parameters.AddWithValue("$companyId", id);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                bindings.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var binding in bindings)
        {
            await ExecuteAsync(
                """
                INSERT INTO company_crafter_account_binding_events (
                    event_id, company_id, legacy_crafter_id, account_profile_id,
                    action, evidence_kind, actor_profile_id, created_at_utc)
                VALUES ($eventId, $companyId, $crafter, $account, 'unbound', $evidence, $actor, $now);
                """,
                ("$eventId", Guid.NewGuid().ToString("D")),
                ("$crafter", binding.Crafter),
                ("$account", binding.Account),
                ("$evidence", binding.Evidence));
        }

        var bindingsRemoved = await ExecuteAsync(
            "DELETE FROM company_crafter_account_bindings WHERE company_id = $companyId;");
        var suppressionsRemoved = await ExecuteAsync(
            "DELETE FROM company_crafter_account_binding_suppressions WHERE company_id = $companyId;");
        var attentionRemoved = await ExecuteAsync(
            "DELETE FROM membership_commission_attention WHERE company_id = $companyId;");
        await ExecuteAsync(
            """
            INSERT INTO company_retirement_receipts (company_id, actor, details_json, retired_at_utc)
            VALUES ($companyId, $actorName, $details, $now)
            ON CONFLICT(company_id) DO NOTHING;
            """,
            ("$actorName", actor),
            ("$details", detailsJson));

        await transaction.CommitAsync(cancellationToken);
        return new CompanyMembershipRetirementResult(
            openMemberships.Count,
            openInvitations.Count,
            bindingsRemoved,
            suppressionsRemoved,
            attentionRemoved);

        async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$companyId", id);
            command.Parameters.AddWithValue("$actor", actorId);
            command.Parameters.AddWithValue("$now", now);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string sql,
        string companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$companyId", companyId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
}
