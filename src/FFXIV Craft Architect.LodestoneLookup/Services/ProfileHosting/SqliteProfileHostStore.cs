using System.Data;
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

    public async Task<ProfileHostEnsureResult> EnsureProfileAsync(
        string profileId,
        string displayName,
        string plaintextKey,
        ProfileAccessKeyHasher hasher,
        CancellationToken ct)
    {
        if (!Guid.TryParseExact(profileId, "D", out var parsedProfileId) ||
            parsedProfileId == Guid.Empty ||
            string.IsNullOrWhiteSpace(displayName) ||
            displayName.Length > 120 ||
            string.IsNullOrWhiteSpace(plaintextKey) ||
            plaintextKey.Length > 256)
        {
            throw new InvalidOperationException("The profile identity, display name, or access key is invalid.");
        }

        profileId = parsedProfileId.ToString("D");
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);

        string? existingDisplayName = null;
        var existingDisabled = false;
        await using (var profile = connection.CreateCommand())
        {
            profile.Transaction = (SqliteTransaction)transaction;
            profile.CommandText =
                """
                SELECT display_name, disabled_at_utc
                FROM hosted_profiles
                WHERE id = $profileId;
                """;
            profile.Parameters.AddWithValue("$profileId", profileId);
            await using var reader = await profile.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                existingDisplayName = reader.GetString(0);
                existingDisabled = !reader.IsDBNull(1);
            }
        }

        if (existingDisplayName != null)
        {
            if (existingDisabled ||
                !string.Equals(existingDisplayName, displayName, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(ct);
                throw new InvalidOperationException(
                    "The existing profile does not match the requested active profile identity.");
            }

            var keyMatches = false;
            await using (var keys = connection.CreateCommand())
            {
                keys.Transaction = (SqliteTransaction)transaction;
                keys.CommandText =
                    """
                    SELECT key_hash
                    FROM profile_access_keys
                    WHERE profile_id = $profileId AND revoked_at_utc IS NULL;
                    """;
                keys.Parameters.AddWithValue("$profileId", profileId);
                await using var reader = await keys.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (hasher.Verify(plaintextKey, reader.GetString(0)))
                    {
                        keyMatches = true;
                    }
                }
            }

            var revision = await GetServerRevisionAsync(
                connection,
                profileId,
                ct,
                (SqliteTransaction)transaction);
            if (!keyMatches && revision != 0)
            {
                await transaction.RollbackAsync(ct);
                throw new InvalidOperationException(
                    "The supplied access key does not authenticate the existing profile.");
            }

            if (!keyMatches)
            {
                var reconciliationTimestamp = DateTime.UtcNow.ToString("O");
                await using (var revokeKeys = connection.CreateCommand())
                {
                    revokeKeys.Transaction = (SqliteTransaction)transaction;
                    revokeKeys.CommandText =
                        """
                        UPDATE profile_access_keys
                        SET revoked_at_utc = $revokedAtUtc
                        WHERE profile_id = $profileId AND revoked_at_utc IS NULL;
                        """;
                    revokeKeys.Parameters.AddWithValue("$profileId", profileId);
                    revokeKeys.Parameters.AddWithValue("$revokedAtUtc", reconciliationTimestamp);
                    await revokeKeys.ExecuteNonQueryAsync(ct);
                }

                await using var insertKey = connection.CreateCommand();
                insertKey.Transaction = (SqliteTransaction)transaction;
                insertKey.CommandText =
                    """
                    INSERT INTO profile_access_keys (id, profile_id, key_hash, created_at_utc)
                    VALUES ($id, $profileId, $keyHash, $createdAtUtc);
                    """;
                insertKey.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                insertKey.Parameters.AddWithValue("$profileId", profileId);
                insertKey.Parameters.AddWithValue("$keyHash", hasher.Hash(plaintextKey));
                insertKey.Parameters.AddWithValue("$createdAtUtc", reconciliationTimestamp);
                await insertKey.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return new ProfileHostEnsureResult(
                new ProfileHostProfileResponse
                {
                    ProfileId = profileId,
                    DisplayName = existingDisplayName,
                    ServerRevision = revision
                },
                Created: false);
        }

        var now = DateTime.UtcNow;
        await using (var insertProfile = connection.CreateCommand())
        {
            insertProfile.Transaction = (SqliteTransaction)transaction;
            insertProfile.CommandText =
                """
                INSERT INTO hosted_profiles (id, display_name, created_at_utc, updated_at_utc)
                VALUES ($id, $displayName, $createdAtUtc, $updatedAtUtc);
                """;
            insertProfile.Parameters.AddWithValue("$id", profileId);
            insertProfile.Parameters.AddWithValue("$displayName", displayName);
            insertProfile.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            insertProfile.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await insertProfile.ExecuteNonQueryAsync(ct);
        }

        await using (var insertKey = connection.CreateCommand())
        {
            insertKey.Transaction = (SqliteTransaction)transaction;
            insertKey.CommandText =
                """
                INSERT INTO profile_access_keys (id, profile_id, key_hash, created_at_utc)
                VALUES ($id, $profileId, $keyHash, $createdAtUtc);
                """;
            insertKey.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insertKey.Parameters.AddWithValue("$profileId", profileId);
            insertKey.Parameters.AddWithValue("$keyHash", hasher.Hash(plaintextKey));
            insertKey.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            await insertKey.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new ProfileHostEnsureResult(
            new ProfileHostProfileResponse
            {
                ProfileId = profileId,
                DisplayName = displayName,
                ServerRevision = 0
            },
            Created: true);
    }

    public async Task<ProfileSyncObjectEnvelope?> LoadObjectAsync(
        string profileId,
        string collection,
        string objectId,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        return await LoadObjectAsync(connection, profileId, collection, objectId, ct);
    }

    public async Task AddAccessKeyAsync(string profileId, string storedHash, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var now = DateTime.UtcNow;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into profile_access_keys (id, profile_id, key_hash, created_at_utc)
            values ($id, $profileId, $keyHash, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$keyHash", storedHash);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task CreatePairingCodeAsync(
        string profileId,
        string tokenHash,
        DateTime expiresAtUtc,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var now = DateTime.UtcNow;

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                delete from profile_pairing_codes
                where expires_at_utc <= $nowUtc or redeemed_at_utc is not null;
                """;
            cleanup.Parameters.AddWithValue("$nowUtc", now.ToString("O"));
            await cleanup.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                insert into profile_pairing_codes (
                    token_hash,
                    profile_id,
                    created_at_utc,
                    expires_at_utc)
                values ($tokenHash, $profileId, $createdAtUtc, $expiresAtUtc);
                """;
            insert.Parameters.AddWithValue("$tokenHash", tokenHash);
            insert.Parameters.AddWithValue("$profileId", profileId);
            insert.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            insert.Parameters.AddWithValue("$expiresAtUtc", expiresAtUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<ProfileHostProfileResponse?> RedeemPairingCodeAsync(
        string tokenHash,
        string accessKeyHash,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);

        string? profileId = null;
        string? displayName = null;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                select p.id, p.display_name
                from profile_pairing_codes c
                inner join hosted_profiles p on p.id = c.profile_id
                where c.token_hash = $tokenHash
                  and c.redeemed_at_utc is null
                  and c.expires_at_utc > $nowUtc
                  and p.disabled_at_utc is null;
                """;
            select.Parameters.AddWithValue("$tokenHash", tokenHash);
            select.Parameters.AddWithValue("$nowUtc", nowUtc.ToString("O"));
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                profileId = reader.GetString(0);
                displayName = reader.GetString(1);
            }
        }

        if (profileId == null || displayName == null)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        await using (var redeem = connection.CreateCommand())
        {
            redeem.Transaction = transaction;
            redeem.CommandText = """
                update profile_pairing_codes
                set redeemed_at_utc = $redeemedAtUtc
                where token_hash = $tokenHash and redeemed_at_utc is null;
                """;
            redeem.Parameters.AddWithValue("$tokenHash", tokenHash);
            redeem.Parameters.AddWithValue("$redeemedAtUtc", nowUtc.ToString("O"));
            if (await redeem.ExecuteNonQueryAsync(ct) != 1)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }
        }

        await using (var insertKey = connection.CreateCommand())
        {
            insertKey.Transaction = transaction;
            insertKey.CommandText = """
                insert into profile_access_keys (id, profile_id, key_hash, created_at_utc)
                values ($id, $profileId, $keyHash, $createdAtUtc);
                """;
            insertKey.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insertKey.Parameters.AddWithValue("$profileId", profileId);
            insertKey.Parameters.AddWithValue("$keyHash", accessKeyHash);
            insertKey.Parameters.AddWithValue("$createdAtUtc", nowUtc.ToString("O"));
            await insertKey.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new ProfileHostProfileResponse
        {
            ProfileId = profileId,
            DisplayName = displayName,
            ServerRevision = await GetServerRevisionAsync(connection, profileId, ct)
        };
    }

    public async Task<ProfileHostProfileResponse?> LoadProfileAsync(string profileId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, display_name
            from hosted_profiles
            where id = $profileId and disabled_at_utc is null;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var displayName = reader.GetString(1);
        await reader.DisposeAsync();

        return new ProfileHostProfileResponse
        {
            ProfileId = profileId,
            DisplayName = displayName,
            ServerRevision = await GetServerRevisionAsync(connection, profileId, ct)
        };
    }

    public async Task RevokeAccessKeysAsync(string profileId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update profile_access_keys
            set revoked_at_utc = $revokedAtUtc
            where profile_id = $profileId and revoked_at_utc is null;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$revokedAtUtc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DisableProfileAsync(string profileId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update hosted_profiles
            set disabled_at_utc = $disabledAtUtc,
                updated_at_utc = $disabledAtUtc
            where id = $profileId and disabled_at_utc is null;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$disabledAtUtc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProfileHostProfileResponse?> AuthenticateAsync(
        string plaintextKey,
        ProfileAccessKeyHasher hasher,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select p.id, p.display_name, k.id, k.key_hash
            from profile_access_keys k
            inner join hosted_profiles p on p.id = k.profile_id
            where k.revoked_at_utc is null and p.disabled_at_utc is null;
            """;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var profileId = reader.GetString(0);
            var displayName = reader.GetString(1);
            var keyId = reader.GetString(2);
            var storedHash = reader.GetString(3);
            if (!hasher.Verify(plaintextKey, storedHash))
            {
                continue;
            }

            await reader.DisposeAsync();
            await TouchAccessKeyAsync(connection, keyId, ct);
            var revision = await GetServerRevisionAsync(connection, profileId, ct);
            return new ProfileHostProfileResponse
            {
                ProfileId = profileId,
                DisplayName = displayName,
                ServerRevision = revision
            };
        }

        return null;
    }

    public async Task<ProfileSyncChangesResponse> LoadChangesAsync(
        string profileId,
        long sinceRevision,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select collection, object_id, payload_json, revision, updated_at_utc, deleted, deleted_at_utc
            from sync_objects
            where profile_id = $profileId and revision > $sinceRevision
            order by revision asc;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$sinceRevision", sinceRevision);

        var objects = new List<ProfileSyncObjectEnvelope>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                objects.Add(ReadObject(reader));
            }
        }

        var serverRevision = await GetServerRevisionAsync(
            connection,
            profileId,
            ct,
            transaction);
        await transaction.CommitAsync(ct);
        return new ProfileSyncChangesResponse
        {
            ServerRevision = serverRevision,
            Objects = objects
        };
    }

    public async Task<ProfileSyncPutResponse> PutObjectAsync(
        string profileId,
        string collection,
        string objectId,
        string payloadJson,
        long expectedRevision,
        CancellationToken ct,
        bool allowCompanyCollection = false,
        long? expectedServerRevision = null)
    {
        if (!allowCompanyCollection)
        {
            ValidateCollection(collection);
        }
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        var currentServerRevision = await GetServerRevisionAsync(
            connection,
            profileId,
            ct,
            transaction);
        if (expectedServerRevision is { } expectedCompanyRevision)
        {
            if (currentServerRevision != expectedCompanyRevision)
            {
                await transaction.RollbackAsync(ct);
                return new ProfileSyncPutResponse
                {
                    Success = false,
                    Conflict = true,
                    ServerRevision = currentServerRevision,
                    ErrorCode = "company_revision_conflict",
                    ErrorMessage = "The hosted company changed before the write completed."
                };
            }
        }

        var revision = await ReserveNextRevisionAsync(connection, transaction, profileId, ct);
        var existing = await LoadObjectAsync(
            connection,
            profileId,
            collection,
            objectId,
            ct,
            transaction);

        if (existing != null && existing.Revision != expectedRevision)
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                ServerRevision = currentServerRevision,
                RemoteObject = existing
            };
        }

        if (existing == null && expectedRevision != 0)
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                ServerRevision = currentServerRevision,
                ErrorCode = "missing_remote_object",
                ErrorMessage = "Remote object does not exist."
            };
        }

        var now = DateTime.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into sync_objects (profile_id, collection, object_id, payload_json, revision, updated_at_utc, deleted, deleted_at_utc)
            values ($profileId, $collection, $objectId, $payloadJson, $revision, $updatedAtUtc, 0, null)
            on conflict(profile_id, collection, object_id) do update set
                payload_json = excluded.payload_json,
                revision = excluded.revision,
                updated_at_utc = excluded.updated_at_utc,
                deleted = 0,
                deleted_at_utc = null
            where sync_objects.revision = $expectedRevision;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$objectId", objectId);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                ServerRevision = currentServerRevision,
                RemoteObject = existing,
                ErrorCode = "revision_conflict",
                ErrorMessage = "Remote object changed before the hosted write completed."
            };
        }
        await transaction.CommitAsync(ct);

        return new ProfileSyncPutResponse
        {
            Success = true,
            ServerRevision = revision,
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

    public async Task<HostedProfileObject?> FindObjectAsync(
        string collection,
        string objectId,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select o.profile_id,
                   o.payload_json,
                   o.revision,
                   o.updated_at_utc,
                   o.deleted,
                   o.deleted_at_utc
            from sync_objects o
            inner join hosted_profiles p on p.id = o.profile_id
            where p.disabled_at_utc is null
              and o.collection = $collection
              and o.object_id = $objectId
              and o.deleted = 0
            order by o.profile_id
            limit 2;
            """;
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$objectId", objectId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var found = ReadHostedObject(reader, collection, objectId);
        if (await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"Hosted object identity '{collection}/{objectId}' is duplicated across active profiles.");
        }

        return found;
    }

    public async Task<IReadOnlyList<HostedProfileObject>> LoadObjectsAsync(
        string collection,
        CancellationToken ct)
    {
        ValidateCollection(collection);
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select o.profile_id,
                   o.object_id,
                   o.payload_json,
                   o.revision,
                   o.updated_at_utc,
                   o.deleted,
                   o.deleted_at_utc
            from sync_objects o
            inner join hosted_profiles p on p.id = o.profile_id
            where p.disabled_at_utc is null
              and o.collection = $collection
              and o.deleted = 0
            order by o.profile_id, o.object_id;
            """;
        command.Parameters.AddWithValue("$collection", collection);
        var found = new List<HostedProfileObject>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            found.Add(new HostedProfileObject(
                reader.GetString(0),
                new ProfileSyncObjectEnvelope
                {
                    Collection = collection,
                    ObjectId = reader.GetString(1),
                    PayloadJson = reader.GetString(2),
                    Revision = reader.GetInt64(3),
                    UpdatedAtUtc = DateTime.Parse(
                        reader.GetString(4),
                        null,
                        DateTimeStyles.RoundtripKind),
                    Deleted = reader.GetInt64(5) == 1,
                    DeletedAtUtc = reader.IsDBNull(6)
                        ? null
                        : DateTime.Parse(
                            reader.GetString(6),
                            null,
                            DateTimeStyles.RoundtripKind)
                }));
        }

        return found;
    }

    public async Task<ProfileSyncPutResponse> DeleteObjectAsync(
        string profileId,
        string collection,
        string objectId,
        long expectedRevision,
        CancellationToken ct)
    {
        ValidateCollection(collection);
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        var revision = await ReserveNextRevisionAsync(connection, transaction, profileId, ct);
        var existing = await LoadObjectAsync(
            connection,
            profileId,
            collection,
            objectId,
            ct,
            transaction);

        if (existing != null && existing.Revision != expectedRevision)
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                RemoteObject = existing
            };
        }

        if (existing == null && expectedRevision != 0)
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                ErrorCode = "missing_remote_object",
                ErrorMessage = "Remote object does not exist."
            };
        }

        var now = DateTime.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into sync_objects (profile_id, collection, object_id, payload_json, revision, updated_at_utc, deleted, deleted_at_utc)
            values ($profileId, $collection, $objectId, '{}', $revision, $updatedAtUtc, 1, $deletedAtUtc)
            on conflict(profile_id, collection, object_id) do update set
                payload_json = '{}',
                revision = excluded.revision,
                updated_at_utc = excluded.updated_at_utc,
                deleted = 1,
                deleted_at_utc = excluded.deleted_at_utc
            where sync_objects.revision = $expectedRevision;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$objectId", objectId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$deletedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                RemoteObject = existing,
                ErrorCode = "revision_conflict",
                ErrorMessage = "Remote object changed before the hosted delete completed."
            };
        }
        await transaction.CommitAsync(ct);

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
        CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static void ValidateCollection(string collection)
    {
        if (!ProfileSyncCollections.All.Contains(collection))
        {
            throw new ArgumentException($"Collection '{collection}' is not syncable.", nameof(collection));
        }
    }

    private static ProfileSyncObjectEnvelope ReadObject(SqliteDataReader reader)
    {
        return new ProfileSyncObjectEnvelope
        {
            Collection = reader.GetString(0),
            ObjectId = reader.GetString(1),
            PayloadJson = reader.GetString(2),
            Revision = reader.GetInt64(3),
            UpdatedAtUtc = DateTime.Parse(reader.GetString(4), null, DateTimeStyles.RoundtripKind),
            Deleted = reader.GetInt64(5) == 1,
            DeletedAtUtc = reader.IsDBNull(6)
                ? null
                : DateTime.Parse(reader.GetString(6), null, DateTimeStyles.RoundtripKind)
        };
    }

    private static HostedProfileObject ReadHostedObject(
        SqliteDataReader reader,
        string collection,
        string objectId) =>
        new(
            reader.GetString(0),
            new ProfileSyncObjectEnvelope
            {
                Collection = collection,
                ObjectId = objectId,
                PayloadJson = reader.GetString(1),
                Revision = reader.GetInt64(2),
                UpdatedAtUtc = DateTime.Parse(
                    reader.GetString(3),
                    null,
                    DateTimeStyles.RoundtripKind),
                Deleted = reader.GetInt64(4) == 1,
                DeletedAtUtc = reader.IsDBNull(5)
                    ? null
                    : DateTime.Parse(
                        reader.GetString(5),
                        null,
                        DateTimeStyles.RoundtripKind)
            });

    private static async Task TouchAccessKeyAsync(SqliteConnection connection, string keyId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update profile_access_keys
            set last_used_at_utc = $lastUsedAtUtc
            where id = $id;
            """;
        command.Parameters.AddWithValue("$id", keyId);
        command.Parameters.AddWithValue("$lastUsedAtUtc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ReserveNextRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into profile_revisions (profile_id, revision)
            values ($profileId, 1)
            on conflict(profile_id) do update set
                revision = profile_revisions.revision + 1
            returning revision;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        var scalar = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<long> GetServerRevisionAsync(
        SqliteConnection connection,
        string profileId,
        CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select coalesce(
                (
                    select revision
                    from profile_revisions
                    where profile_id = $profileId
                ),
                0
            );
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        var scalar = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
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

            create table if not exists profile_pairing_codes (
                token_hash text primary key,
                profile_id text not null,
                created_at_utc text not null,
                expires_at_utc text not null,
                redeemed_at_utc text null,
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

            create table if not exists profile_revisions (
                profile_id text primary key,
                revision integer not null,
                foreign key(profile_id) references hosted_profiles(id)
            );

            insert into profile_revisions (profile_id, revision)
            select p.id, coalesce(max(o.revision), 0)
            from hosted_profiles p
            left join sync_objects o on o.profile_id = p.id
            group by p.id
            on conflict(profile_id) do update set
                revision = max(profile_revisions.revision, excluded.revision);
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
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }
}

public sealed record HostedProfileObject(
    string ProfileId,
    ProfileSyncObjectEnvelope Object);

public sealed record ProfileHostEnsureResult(
    ProfileHostProfileResponse Profile,
    bool Created);
