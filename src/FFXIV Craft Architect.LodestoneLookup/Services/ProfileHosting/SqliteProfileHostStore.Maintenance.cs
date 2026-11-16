using System.Data;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed record HostedSyncObject(
    string ProfileId,
    string Collection,
    string ObjectId,
    long Revision,
    string PayloadJson);

public sealed partial class SqliteProfileHostStore
{
    // Rewrites a deep-archived order in place (maintenance only). The summary is
    // re-derived so archive search results stay consistent with the payload, and the
    // write only applies when the stored payload is still the one the caller read.
    public async Task<bool> ReplaceDeepArchivedOrderPayloadAsync(
        string profileId,
        string objectId,
        string expectedPayloadJson,
        string payloadJson,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        var summary = TradeOrderArchiveSummaryCodec.TryCreate(payloadJson, objectId);
        if (summary == null)
        {
            return false;
        }

        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update deep_archived_trade_orders
            set payload_json = $payloadJson,
                summary_json = $summaryJson
            where profile_id = $profileId
              and object_id = $objectId
              and payload_json = $expectedPayloadJson;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$objectId", objectId);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$summaryJson", TradeOrderArchiveSummaryCodec.Serialize(summary));
        command.Parameters.AddWithValue("$expectedPayloadJson", expectedPayloadJson);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    // Every live object in every collection of every active profile, including the
    // company-scoped collections generic synchronization does not expose.
    public async Task<IReadOnlyList<HostedSyncObject>> LoadAllLiveObjectsAsync(CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select o.profile_id, o.collection, o.object_id, o.revision, o.payload_json
            from sync_objects o
            inner join hosted_profiles p on p.id = o.profile_id
            where p.disabled_at_utc is null
              and o.deleted = 0
            order by o.profile_id, o.collection, o.object_id;
            """;
        var objects = new List<HostedSyncObject>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            objects.Add(new HostedSyncObject(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4)));
        }

        return objects;
    }

    // Tombstones one object of a retired company through the normal revision path and
    // records it as retired in the same transaction, so later writes cannot restore it.
    public async Task<ProfileSyncPutResponse> RetireObjectAsync(
        string profileId,
        string collection,
        string objectId,
        long expectedRevision,
        CompanyId companyId,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        var existing = await LoadObjectAsync(connection, profileId, collection, objectId, ct, transaction);
        if (existing is not { Deleted: false } || existing.Revision != expectedRevision)
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                RemoteObject = existing,
                ErrorCode = "revision_conflict",
                ErrorMessage = "The record changed before it could be retired."
            };
        }

        var revision = await ReserveNextRevisionAsync(connection, transaction, profileId, ct);
        var now = DateTime.UtcNow.ToString("O");
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                update sync_objects
                set payload_json = '{}',
                    revision = $revision,
                    updated_at_utc = $now,
                    deleted = 1,
                    deleted_at_utc = $now
                where profile_id = $profileId
                  and collection = $collection
                  and object_id = $objectId
                  and revision = $expectedRevision;

                insert into retired_sync_objects (profile_id, collection, object_id, company_id, retired_at_utc)
                values ($profileId, $collection, $objectId, $companyId, $now)
                on conflict(profile_id, collection, object_id) do nothing;
                """;
            command.Parameters.AddWithValue("$profileId", profileId);
            command.Parameters.AddWithValue("$collection", collection);
            command.Parameters.AddWithValue("$objectId", objectId);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            command.Parameters.AddWithValue("$companyId", companyId.Value.ToString("D"));
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        _changeSignal?.Publish(profileId, revision);
        return new ProfileSyncPutResponse { Success = true, ServerRevision = revision };
    }

    private static async Task<bool> IsRetiredObjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        string collection,
        string objectId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select 1 from retired_sync_objects
            where profile_id = $profileId and collection = $collection and object_id = $objectId
            limit 1;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$objectId", objectId);
        return await command.ExecuteScalarAsync(ct) != null;
    }
}
