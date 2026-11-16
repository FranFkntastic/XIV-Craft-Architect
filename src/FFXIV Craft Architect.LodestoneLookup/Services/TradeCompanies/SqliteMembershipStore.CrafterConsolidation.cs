using System.Data;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record CompanyCrafterMergeReceipt(
    CompanyId CompanyId,
    Guid LoserCrafterId,
    Guid SurvivorCrafterId,
    string LodestoneCharacterId,
    Guid? AccountProfileId,
    string LoserPayloadJson,
    string DetailsJson,
    DateTimeOffset MergedAtUtc);

public sealed record CrafterReferenceRepointResult(
    int BindingsMoved,
    int BindingsDropped,
    int BindingEventsRepointed,
    int SuppressionsMoved,
    int SuppressionsDropped,
    int InvitationsRepointed);

// Crafter consolidation: one person, one crafter per company.
public sealed partial class SqliteMembershipStore
{
    public async Task<IReadOnlyList<CompanyCrafterMergeReceipt>> LoadCrafterMergeReceiptsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT loser_crafter_id, survivor_crafter_id, lodestone_character_id,
                   account_profile_id, loser_payload_json, details_json, merged_at_utc
            FROM company_crafter_merge_receipts
            WHERE company_id = $companyId
            ORDER BY merged_at_utc, loser_crafter_id;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        var receipts = new List<CompanyCrafterMergeReceipt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            receipts.Add(new CompanyCrafterMergeReceipt(
                companyId,
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture)));
        }

        return receipts;
    }

    // Moves every membership-side reference from the merged-away crafter to the
    // survivor and records the receipt in the same transaction. Rerunning is a no-op
    // apart from refreshing the receipt details.
    public async Task<CrafterReferenceRepointResult> RepointCrafterReferencesAsync(
        CompanyCrafterMergeReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        if (receipt.CompanyId.Value == Guid.Empty ||
            receipt.LoserCrafterId == Guid.Empty ||
            receipt.SurvivorCrafterId == Guid.Empty ||
            receipt.LoserCrafterId == receipt.SurvivorCrafterId)
        {
            throw new ArgumentException("A valid crafter merge is required.", nameof(receipt));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var survivorBound = await ExistsAsync(
            "SELECT 1 FROM company_crafter_account_bindings WHERE company_id = $companyId AND legacy_crafter_id = $survivor LIMIT 1;");
        var survivorSuppressed = await ExistsAsync(
            "SELECT 1 FROM company_crafter_account_binding_suppressions WHERE company_id = $companyId AND legacy_crafter_id = $survivor LIMIT 1;");

        var bindingsMoved = survivorBound
            ? 0
            : await ExecuteAsync(
                "UPDATE company_crafter_account_bindings SET legacy_crafter_id = $survivor WHERE company_id = $companyId AND legacy_crafter_id = $loser;");
        var bindingsDropped = await ExecuteAsync(
            "DELETE FROM company_crafter_account_bindings WHERE company_id = $companyId AND legacy_crafter_id = $loser;");
        var eventsRepointed = await ExecuteAsync(
            "UPDATE company_crafter_account_binding_events SET legacy_crafter_id = $survivor WHERE company_id = $companyId AND legacy_crafter_id = $loser;");
        // A suppression only carries over to a survivor that has no binding decision yet.
        var suppressionsMoved = survivorBound || survivorSuppressed || bindingsMoved > 0
            ? 0
            : await ExecuteAsync(
                "UPDATE company_crafter_account_binding_suppressions SET legacy_crafter_id = $survivor WHERE company_id = $companyId AND legacy_crafter_id = $loser;");
        var suppressionsDropped = await ExecuteAsync(
            "DELETE FROM company_crafter_account_binding_suppressions WHERE company_id = $companyId AND legacy_crafter_id = $loser;");
        var invitationsRepointed = await ExecuteAsync(
            "UPDATE company_membership_invitations SET legacy_crafter_id = $survivor WHERE company_id = $companyId AND legacy_crafter_id = $loser;");

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO company_crafter_merge_receipts (
                    company_id, loser_crafter_id, survivor_crafter_id, lodestone_character_id,
                    account_profile_id, loser_payload_json, details_json, merged_at_utc)
                VALUES (
                    $companyId, $loser, $survivor, $lodestone,
                    $account, $loserPayload, $details, $mergedAt)
                ON CONFLICT(company_id, loser_crafter_id) DO UPDATE SET
                    details_json = excluded.details_json;
                """;
            AddMergeParameters(command);
            command.Parameters.AddWithValue("$lodestone", receipt.LodestoneCharacterId);
            command.Parameters.AddWithValue(
                "$account",
                receipt.AccountProfileId?.ToString("D") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$loserPayload", receipt.LoserPayloadJson);
            command.Parameters.AddWithValue("$details", receipt.DetailsJson);
            command.Parameters.AddWithValue("$mergedAt", receipt.MergedAtUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CrafterReferenceRepointResult(
            bindingsMoved,
            bindingsDropped,
            eventsRepointed,
            suppressionsMoved,
            suppressionsDropped,
            invitationsRepointed);

        void AddMergeParameters(SqliteCommand command)
        {
            command.Parameters.AddWithValue("$companyId", receipt.CompanyId.ToString());
            command.Parameters.AddWithValue("$loser", receipt.LoserCrafterId.ToString("D"));
            command.Parameters.AddWithValue("$survivor", receipt.SurvivorCrafterId.ToString("D"));
        }

        async Task<bool> ExistsAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            AddMergeParameters(command);
            return await command.ExecuteScalarAsync(cancellationToken) != null;
        }

        async Task<int> ExecuteAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            AddMergeParameters(command);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
