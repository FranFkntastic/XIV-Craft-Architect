using System.Globalization;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class SqliteProfileHostStore
{
    private readonly ProfileHostOptions _options;

    public SqliteProfileHostStore(ProfileHostOptions options)
    {
        _options = options;
    }

    public async Task<ProfileHostProfileResponse> CreateProfileAsync(string displayName, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var profileId = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into hosted_profiles (id, display_name, created_at_utc, updated_at_utc)
            values ($id, $displayName, $createdAtUtc, $updatedAtUtc);
            """;
        command.Parameters.AddWithValue("$id", profileId);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);

        return new ProfileHostProfileResponse
        {
            ProfileId = profileId,
            DisplayName = displayName,
            ServerRevision = 0
        };
    }

    public async Task<ProfileSyncPutResponse> PutObjectAsync(
        string profileId,
        string collection,
        string objectId,
        string payloadJson,
        long expectedRevision,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        var existing = await LoadObjectAsync(connection, profileId, collection, objectId, ct);

        if (existing != null && existing.Revision != expectedRevision)
        {
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                RemoteObject = existing
            };
        }

        if (existing == null && expectedRevision != 0)
        {
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                ErrorCode = "missing_remote_object",
                ErrorMessage = "Remote object does not exist."
            };
        }

        var revision = (existing?.Revision ?? 0) + 1;
        var now = DateTime.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into sync_objects (profile_id, collection, object_id, payload_json, revision, updated_at_utc, deleted, deleted_at_utc)
            values ($profileId, $collection, $objectId, $payloadJson, $revision, $updatedAtUtc, 0, null)
            on conflict(profile_id, collection, object_id) do update set
                payload_json = excluded.payload_json,
                revision = excluded.revision,
                updated_at_utc = excluded.updated_at_utc,
                deleted = 0,
                deleted_at_utc = null;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$objectId", objectId);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);

        return new ProfileSyncPutResponse
        {
            Success = true,
            Object = new ProfileSyncObjectEnvelope
            {
                Collection = collection,
                ObjectId = objectId,
                PayloadJson = payloadJson,
                Revision = revision,
                UpdatedAtUtc = now
            }
        };
    }

    public async Task<ProfileSyncPutResponse> DeleteObjectAsync(
        string profileId,
        string collection,
        string objectId,
        long expectedRevision,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        var existing = await LoadObjectAsync(connection, profileId, collection, objectId, ct);

        if (existing != null && existing.Revision != expectedRevision)
        {
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                RemoteObject = existing
            };
        }

        var revision = (existing?.Revision ?? 0) + 1;
        var now = DateTime.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into sync_objects (profile_id, collection, object_id, payload_json, revision, updated_at_utc, deleted, deleted_at_utc)
            values ($profileId, $collection, $objectId, '{}', $revision, $updatedAtUtc, 1, $deletedAtUtc)
            on conflict(profile_id, collection, object_id) do update set
                payload_json = '{}',
                revision = excluded.revision,
                updated_at_utc = excluded.updated_at_utc,
                deleted = 1,
                deleted_at_utc = excluded.deleted_at_utc;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$objectId", objectId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$deletedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);

        return new ProfileSyncPutResponse
        {
            Success = true,
            Object = new ProfileSyncObjectEnvelope
            {
                Collection = collection,
                ObjectId = objectId,
                PayloadJson = "{}",
                Revision = revision,
                UpdatedAtUtc = now,
                Deleted = true,
                DeletedAtUtc = now
            }
        };
    }

    private static async Task<ProfileSyncObjectEnvelope?> LoadObjectAsync(
        SqliteConnection connection,
        string profileId,
        string collection,
        string objectId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select payload_json, revision, updated_at_utc, deleted, deleted_at_utc
            from sync_objects
            where profile_id = $profileId and collection = $collection and object_id = $objectId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$objectId", objectId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ProfileSyncObjectEnvelope
        {
            Collection = collection,
            ObjectId = objectId,
            PayloadJson = reader.GetString(0),
            Revision = reader.GetInt64(1),
            UpdatedAtUtc = DateTime.Parse(reader.GetString(2), null, DateTimeStyles.RoundtripKind),
            Deleted = reader.GetInt64(3) == 1,
            DeletedAtUtc = reader.IsDBNull(4)
                ? null
                : DateTime.Parse(reader.GetString(4), null, DateTimeStyles.RoundtripKind)
        };
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_options.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists hosted_profiles (
                id text primary key,
                display_name text not null,
                created_at_utc text not null,
                updated_at_utc text not null,
                disabled_at_utc text null
            );

            create table if not exists profile_access_keys (
                id text primary key,
                profile_id text not null,
                key_hash text not null,
                created_at_utc text not null,
                last_used_at_utc text null,
                revoked_at_utc text null,
                foreign key(profile_id) references hosted_profiles(id)
            );

            create table if not exists sync_objects (
                profile_id text not null,
                collection text not null,
                object_id text not null,
                payload_json text not null,
                revision integer not null,
                updated_at_utc text not null,
                deleted integer not null,
                deleted_at_utc text null,
                primary key(profile_id, collection, object_id),
                foreign key(profile_id) references hosted_profiles(id)
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
