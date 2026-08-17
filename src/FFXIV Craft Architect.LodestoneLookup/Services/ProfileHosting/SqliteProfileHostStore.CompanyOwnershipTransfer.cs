using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed partial class SqliteProfileHostStore
{
    private sealed record TransferObject(
        string ProfileId,
        string Collection,
        string ObjectId,
        string PayloadJson,
        long Revision,
        string UpdatedAtUtc);

    private sealed record TransferArchive(
        string ProfileId,
        string ObjectId,
        string PayloadJson,
        string SummaryJson,
        string SearchText,
        long SourceRevision,
        long TombstoneRevision,
        string ArchivedAtUtc);

    private sealed record TransferScope(
        IReadOnlyList<TransferObject> SourceObjects,
        IReadOnlyList<TransferObject> TargetObjects,
        IReadOnlyList<TransferArchive> SourceArchives,
        IReadOnlyList<TransferArchive> TargetArchives,
        CompanyOwnershipTransferCounts Counts,
        string Fingerprint);

    public async Task<CompanyOwnershipTransferPreview?> PreviewCompanyOwnershipTransferAsync(
        CompanyId companyId,
        Guid sourceProfileId,
        Guid targetProfileId,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        if (!await IsActiveProfileAsync(connection, targetProfileId, null, cancellationToken))
        {
            return null;
        }

        var scope = await LoadTransferScopeAsync(
            connection,
            null,
            companyId,
            sourceProfileId,
            targetProfileId,
            cancellationToken);
        if (!scope.SourceObjects.Any(IsCompanyProfile))
        {
            return null;
        }

        var target = await LoadProfileAsync(targetProfileId.ToString("D"), cancellationToken);
        return target == null
            ? null
            : new CompanyOwnershipTransferPreview(
                companyId,
                sourceProfileId,
                targetProfileId,
                target.DisplayName,
                scope.Fingerprint,
                scope.Counts);
    }

    public async Task<CompanyOwnershipTransferReceipt?> LoadCompanyOwnershipTransferAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        return await LoadTransferReceiptAsync(connection, null, idempotencyKey, cancellationToken);
    }

    public async Task<IReadOnlyList<CompanyOwnershipTransferReceipt>> LoadPendingOwnershipTransfersAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select idempotency_key from company_ownership_transfers where membership_projected_at_utc is null order by committed_at_utc;";
        var keys = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) keys.Add(Guid.Parse(reader.GetString(0)));
        }
        var receipts = new List<CompanyOwnershipTransferReceipt>(keys.Count);
        foreach (var key in keys)
        {
            var receipt = await LoadTransferReceiptAsync(connection, null, key, cancellationToken);
            if (receipt != null) receipts.Add(receipt);
        }
        return receipts;
    }

    public async Task<CompanyOwnershipTransferResult> CommitCompanyOwnershipTransferAsync(
        CompanyId companyId,
        Guid sourceProfileId,
        Guid targetProfileId,
        PreviousOwnerDisposition disposition,
        Guid idempotencyKey,
        string expectedScopeFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (sourceProfileId == Guid.Empty || targetProfileId == Guid.Empty ||
            sourceProfileId == targetProfileId || idempotencyKey == Guid.Empty ||
            string.IsNullOrWhiteSpace(expectedScopeFingerprint))
        {
            return new CompanyOwnershipTransferResult(
                CompanyOwnershipTransferStatus.InvalidTarget,
                Error: "The ownership transfer request is incomplete.");
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var replay = await LoadTransferReceiptAsync(
            connection,
            transaction,
            idempotencyKey,
            cancellationToken);
        if (replay != null)
        {
            await transaction.CommitAsync(cancellationToken);
            var matches = replay.CompanyId == companyId &&
                          replay.SourceProfileId == sourceProfileId &&
                          replay.TargetProfileId == targetProfileId &&
                          replay.PreviousOwnerDisposition == disposition;
            return matches
                ? new CompanyOwnershipTransferResult(CompanyOwnershipTransferStatus.Replayed, replay)
                : new CompanyOwnershipTransferResult(
                    CompanyOwnershipTransferStatus.Conflict,
                    Error: "That transfer key already belongs to a different request.");
        }

        if (!await IsActiveProfileAsync(connection, targetProfileId, transaction, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CompanyOwnershipTransferResult(
                CompanyOwnershipTransferStatus.InvalidTarget,
                Error: "The selected member no longer has an active account.");
        }

        var scope = await LoadTransferScopeAsync(
            connection,
            transaction,
            companyId,
            sourceProfileId,
            targetProfileId,
            cancellationToken);
        if (!scope.SourceObjects.Any(IsCompanyProfile))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CompanyOwnershipTransferResult(CompanyOwnershipTransferStatus.NotFound);
        }
        if (!string.Equals(scope.Fingerprint, expectedScopeFingerprint, StringComparison.Ordinal))
        {
            var currentPreview = await CreatePreviewAsync(
                connection,
                transaction,
                companyId,
                sourceProfileId,
                targetProfileId,
                scope,
                cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return new CompanyOwnershipTransferResult(
                CompanyOwnershipTransferStatus.Conflict,
                Preview: currentPreview,
                Error: "The company changed after the transfer was reviewed. Review the current scope and try again.");
        }

        var sourceRevision = await ReserveNextRevisionAsync(
            connection, transaction, sourceProfileId.ToString("D"), cancellationToken);
        var targetRevision = await ReserveNextRevisionAsync(
            connection, transaction, targetProfileId.ToString("D"), cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var item in scope.SourceObjects)
        {
            await UpsertTransferredObjectAsync(
                connection, transaction, targetProfileId, item, targetRevision, now, cancellationToken);
            if (string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal))
            {
                await DeleteTransferredArchiveAsync(
                    connection, transaction, targetProfileId, item.ObjectId, cancellationToken);
            }
            await TombstoneTransferredObjectAsync(
                connection, transaction, sourceProfileId, item, sourceRevision, now, cancellationToken);
        }

        foreach (var archive in scope.SourceArchives)
        {
            await UpsertTransferredArchiveAsync(
                connection, transaction, targetProfileId, archive, targetRevision, cancellationToken);
            await EnsureTransferredArchiveTombstoneAsync(
                connection, transaction, targetProfileId, archive.ObjectId, targetRevision, now, cancellationToken);
            await DeleteTransferredArchiveAsync(
                connection, transaction, sourceProfileId, archive.ObjectId, cancellationToken);
        }

        var receipt = new CompanyOwnershipTransferReceipt(
            Guid.NewGuid(),
            idempotencyKey,
            companyId,
            sourceProfileId,
            targetProfileId,
            disposition,
            scope.Fingerprint,
            scope.Counts,
            now,
            null);
        await InsertTransferReceiptAsync(connection, transaction, receipt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _changeSignal?.Publish(sourceProfileId.ToString("D"), sourceRevision);
        _changeSignal?.Publish(targetProfileId.ToString("D"), targetRevision);
        return new CompanyOwnershipTransferResult(CompanyOwnershipTransferStatus.Applied, receipt);
    }

    public async Task<CompanyOwnershipTransferReceipt?> MarkOwnershipMembershipProjectedAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var receipt = await LoadTransferReceiptAsync(connection, transaction, idempotencyKey, cancellationToken);
        if (receipt == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        if (receipt.MembershipProjectedAtUtc.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return receipt;
        }
        var projectedAt = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "update company_ownership_transfers set membership_projected_at_utc = $at where idempotency_key = $key;";
        command.Parameters.AddWithValue("$at", projectedAt.ToString("O"));
        command.Parameters.AddWithValue("$key", idempotencyKey.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return receipt with { MembershipProjectedAtUtc = projectedAt };
    }

    private static async Task<CompanyOwnershipTransferPreview> CreatePreviewAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        Guid sourceProfileId,
        Guid targetProfileId,
        TransferScope scope,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select display_name from hosted_profiles where id = $id and disabled_at_utc is null;";
        command.Parameters.AddWithValue("$id", targetProfileId.ToString("D"));
        var displayName = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? "Member";
        return new(companyId, sourceProfileId, targetProfileId, displayName, scope.Fingerprint, scope.Counts);
    }

    private static async Task<TransferScope> LoadTransferScopeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CompanyId companyId,
        Guid sourceProfileId,
        Guid targetProfileId,
        CancellationToken cancellationToken)
    {
        var sourceAll = await LoadActiveTransferObjectsAsync(connection, transaction, sourceProfileId, cancellationToken);
        var targetAll = await LoadActiveTransferObjectsAsync(connection, transaction, targetProfileId, cancellationToken);
        var company = companyId.ToString();
        var sourceDirect = sourceAll.Where(item => BelongsToCompany(item, company)).ToList();
        var targetDirect = targetAll.Where(item => BelongsToCompany(item, company)).ToList();
        var sourceArchives = (await LoadTransferArchivesAsync(connection, transaction, sourceProfileId, cancellationToken))
            .Where(item => PayloadBelongsToCompany(item.PayloadJson, company)).ToArray();
        var targetArchives = (await LoadTransferArchivesAsync(connection, transaction, targetProfileId, cancellationToken))
            .Where(item => PayloadBelongsToCompany(item.PayloadJson, company)).ToArray();
        var companyOrderIds = sourceDirect.Concat(targetDirect)
            .Where(item => string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal))
            .Select(item => item.ObjectId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        companyOrderIds.UnionWith(sourceArchives.Select(item => item.ObjectId));
        companyOrderIds.UnionWith(targetArchives.Select(item => item.ObjectId));
        var sourceObjects = sourceDirect.Concat(sourceAll.Where(item => IsLinkedPlan(item, companyOrderIds)))
            .DistinctBy(item => (item.Collection.ToLowerInvariant(), item.ObjectId.ToLowerInvariant()))
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToArray();
        var targetObjects = targetDirect.Concat(targetAll.Where(item => IsLinkedPlan(item, companyOrderIds)))
            .DistinctBy(item => (item.Collection.ToLowerInvariant(), item.ObjectId.ToLowerInvariant()))
            .OrderBy(item => item.Collection, StringComparer.Ordinal)
            .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToArray();
        var sourceKeys = sourceObjects.Select(ObjectKey)
            .Concat(sourceArchives.Select(ArchiveKey))
            .ToHashSet(StringComparer.Ordinal);
        var targetKeys = targetObjects.Select(ObjectKey)
            .Concat(targetArchives.Select(ArchiveKey))
            .ToHashSet(StringComparer.Ordinal);
        var collisions = sourceKeys.Intersect(targetKeys).Count();
        var targetOnly = targetKeys.Except(sourceKeys).Count();
        var counts = new CompanyOwnershipTransferCounts(
            sourceObjects.Count(IsCompanyProfile),
            sourceObjects.Count(item => item.Collection == ProfileSyncCollections.TradeOrders),
            sourceObjects.Count(item => item.Collection == ProfileSyncCollections.TradeCrafters),
            sourceObjects.Count(item => item.Collection == "tradeCompany.publication"),
            sourceObjects.Count(item => item.Collection == ProfileSyncCollections.TradePayrollDrafts),
            sourceObjects.Count(item => item.Collection == ProfileSyncCollections.Plans),
            sourceArchives.Length,
            collisions,
            targetOnly);
        var fingerprint = ComputeScopeFingerprint(sourceObjects, targetObjects, sourceArchives, targetArchives);
        return new(sourceObjects, targetObjects, sourceArchives, targetArchives, counts, fingerprint);
    }

    private static async Task<IReadOnlyList<TransferObject>> LoadActiveTransferObjectsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid profileId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select collection, object_id, payload_json, revision, updated_at_utc from sync_objects where profile_id = $id and deleted = 0;";
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        var result = new List<TransferObject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(profileId.ToString("D"), reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<TransferArchive>> LoadTransferArchivesAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid profileId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select object_id, payload_json, summary_json, search_text, source_revision, tombstone_revision, archived_at_utc from deep_archived_trade_orders where profile_id = $id;";
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        var result = new List<TransferArchive>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(profileId.ToString("D"), reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6)));
        }
        return result;
    }

    private static bool BelongsToCompany(TransferObject item, string companyId) =>
        IsCompanyProfile(item) && string.Equals(item.ObjectId, companyId, StringComparison.OrdinalIgnoreCase) ||
        item.Collection != ProfileSyncCollections.Plans && PayloadBelongsToCompany(item.PayloadJson, companyId);

    private static bool IsCompanyProfile(TransferObject item) =>
        string.Equals(item.Collection, ProfileSyncCollections.TradeCompanyProfiles, StringComparison.Ordinal);

    private static bool IsLinkedPlan(TransferObject item, IReadOnlySet<string> orderIds)
    {
        if (!string.Equals(item.Collection, ProfileSyncCollections.Plans, StringComparison.Ordinal)) return false;
        try
        {
            var linkedOrder = ProfileSyncPlanPayloadCodec.Deserialize(item.PayloadJson, item.ObjectId).LinkedOrderId;
            return linkedOrder.HasValue && orderIds.Contains(linkedOrder.Value.ToString("D"));
        }
        catch (JsonException) { return false; }
    }

    private static bool PayloadBelongsToCompany(string payloadJson, string companyId)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return TryString(document.RootElement, "companyProfileId", out var profileId) &&
                       string.Equals(profileId, companyId, StringComparison.OrdinalIgnoreCase) ||
                   TryString(document.RootElement, "companyId", out var directId) &&
                       string.Equals(directId, companyId, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryString(JsonElement element, string name, out string? value)
    {
        foreach (var property in element.ValueKind == JsonValueKind.Object ? element.EnumerateObject() : [])
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString();
                return true;
            }
        }
        value = null;
        return false;
    }

    private static string ObjectKey(TransferObject item) => $"{item.Collection.ToLowerInvariant()}\n{item.ObjectId.ToLowerInvariant()}";
    private static string ArchiveKey(TransferArchive item) => $"{ProfileSyncCollections.TradeOrders.ToLowerInvariant()}\n{item.ObjectId.ToLowerInvariant()}";

    private static string ComputeScopeFingerprint(
        IEnumerable<TransferObject> sourceObjects,
        IEnumerable<TransferObject> targetObjects,
        IEnumerable<TransferArchive> sourceArchives,
        IEnumerable<TransferArchive> targetArchives)
    {
        var lines = sourceObjects.Select(item => $"S|{ObjectKey(item)}|{item.Revision}|{Hash(item.PayloadJson)}")
            .Concat(targetObjects.Select(item => $"T|{ObjectKey(item)}|{item.Revision}|{Hash(item.PayloadJson)}"))
            .Concat(sourceArchives.Select(item => $"SA|{item.ObjectId}|{item.TombstoneRevision}|{Hash(item.PayloadJson)}"))
            .Concat(targetArchives.Select(item => $"TA|{item.ObjectId}|{item.TombstoneRevision}|{Hash(item.PayloadJson)}"))
            .OrderBy(item => item, StringComparer.Ordinal);
        return Hash(string.Join("\n", lines));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<bool> IsActiveProfileAsync(SqliteConnection connection, Guid profileId, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select count(*) from hosted_profiles where id = $id and disabled_at_utc is null;";
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task UpsertTransferredObjectAsync(SqliteConnection connection, SqliteTransaction transaction, Guid targetProfileId, TransferObject item, long revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "insert into sync_objects(profile_id,collection,object_id,payload_json,revision,updated_at_utc,deleted,deleted_at_utc) values($p,$c,$o,$j,$r,$at,0,null) on conflict(profile_id,collection,object_id) do update set payload_json=excluded.payload_json,revision=excluded.revision,updated_at_utc=excluded.updated_at_utc,deleted=0,deleted_at_utc=null;";
        command.Parameters.AddWithValue("$p", targetProfileId.ToString("D")); command.Parameters.AddWithValue("$c", item.Collection); command.Parameters.AddWithValue("$o", item.ObjectId); command.Parameters.AddWithValue("$j", item.PayloadJson); command.Parameters.AddWithValue("$r", revision); command.Parameters.AddWithValue("$at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TombstoneTransferredObjectAsync(SqliteConnection connection, SqliteTransaction transaction, Guid sourceProfileId, TransferObject item, long revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "update sync_objects set payload_json='{}',revision=$r,updated_at_utc=$at,deleted=1,deleted_at_utc=$at where profile_id=$p and collection=$c and object_id=$o and deleted=0;";
        command.Parameters.AddWithValue("$p", sourceProfileId.ToString("D")); command.Parameters.AddWithValue("$c", item.Collection); command.Parameters.AddWithValue("$o", item.ObjectId); command.Parameters.AddWithValue("$r", revision); command.Parameters.AddWithValue("$at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertTransferredArchiveAsync(SqliteConnection connection, SqliteTransaction transaction, Guid targetProfileId, TransferArchive item, long targetRevision, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "insert into deep_archived_trade_orders(profile_id,object_id,payload_json,summary_json,search_text,source_revision,tombstone_revision,archived_at_utc) values($p,$o,$j,$s,$search,$source,$tombstone,$at) on conflict(profile_id,object_id) do update set payload_json=excluded.payload_json,summary_json=excluded.summary_json,search_text=excluded.search_text,source_revision=excluded.source_revision,tombstone_revision=excluded.tombstone_revision,archived_at_utc=excluded.archived_at_utc;";
        command.Parameters.AddWithValue("$p", targetProfileId.ToString("D")); command.Parameters.AddWithValue("$o", item.ObjectId); command.Parameters.AddWithValue("$j", item.PayloadJson); command.Parameters.AddWithValue("$s", item.SummaryJson); command.Parameters.AddWithValue("$search", item.SearchText); command.Parameters.AddWithValue("$source", item.SourceRevision); command.Parameters.AddWithValue("$tombstone", targetRevision); command.Parameters.AddWithValue("$at", item.ArchivedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureTransferredArchiveTombstoneAsync(SqliteConnection connection, SqliteTransaction transaction, Guid targetProfileId, string objectId, long revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "insert into sync_objects(profile_id,collection,object_id,payload_json,revision,updated_at_utc,deleted,deleted_at_utc) values($p,$c,$o,'{}',$r,$at,1,$at) on conflict(profile_id,collection,object_id) do update set payload_json='{}',revision=excluded.revision,updated_at_utc=excluded.updated_at_utc,deleted=1,deleted_at_utc=excluded.deleted_at_utc;";
        command.Parameters.AddWithValue("$p", targetProfileId.ToString("D")); command.Parameters.AddWithValue("$c", ProfileSyncCollections.TradeOrders); command.Parameters.AddWithValue("$o", objectId); command.Parameters.AddWithValue("$r", revision); command.Parameters.AddWithValue("$at", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteTransferredArchiveAsync(SqliteConnection connection, SqliteTransaction transaction, Guid sourceProfileId, string objectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "delete from deep_archived_trade_orders where profile_id=$p and object_id=$o;";
        command.Parameters.AddWithValue("$p", sourceProfileId.ToString("D")); command.Parameters.AddWithValue("$o", objectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertTransferReceiptAsync(SqliteConnection connection, SqliteTransaction transaction, CompanyOwnershipTransferReceipt receipt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "insert into company_ownership_transfers(transfer_id,idempotency_key,company_id,source_profile_id,target_profile_id,previous_owner_disposition,scope_fingerprint,counts_json,committed_at_utc,membership_projected_at_utc) values($id,$key,$company,$source,$target,$disposition,$fingerprint,$counts,$at,null);";
        command.Parameters.AddWithValue("$id", receipt.TransferId.ToString("D")); command.Parameters.AddWithValue("$key", receipt.IdempotencyKey.ToString("D")); command.Parameters.AddWithValue("$company", receipt.CompanyId.ToString()); command.Parameters.AddWithValue("$source", receipt.SourceProfileId.ToString("D")); command.Parameters.AddWithValue("$target", receipt.TargetProfileId.ToString("D")); command.Parameters.AddWithValue("$disposition", receipt.PreviousOwnerDisposition.ToString().ToLowerInvariant()); command.Parameters.AddWithValue("$fingerprint", receipt.ScopeFingerprint); command.Parameters.AddWithValue("$counts", JsonSerializer.Serialize(receipt.Counts)); command.Parameters.AddWithValue("$at", receipt.CommittedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<CompanyOwnershipTransferReceipt?> LoadTransferReceiptAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "select transfer_id,company_id,source_profile_id,target_profile_id,previous_owner_disposition,scope_fingerprint,counts_json,committed_at_utc,membership_projected_at_utc from company_ownership_transfers where idempotency_key=$key;";
        command.Parameters.AddWithValue("$key", idempotencyKey.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(
            Guid.Parse(reader.GetString(0)), idempotencyKey, CompanyId.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)),
            Enum.Parse<PreviousOwnerDisposition>(reader.GetString(4), true), reader.GetString(5),
            JsonSerializer.Deserialize<CompanyOwnershipTransferCounts>(reader.GetString(6))!, DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }
}
