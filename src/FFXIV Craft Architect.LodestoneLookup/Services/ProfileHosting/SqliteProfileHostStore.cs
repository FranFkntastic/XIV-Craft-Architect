using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class SqliteProfileHostStore
{
    private const int MaximumImportedActiveAccessKeys = 64;
    private sealed record ImportedAccessKey(
        string Id,
        string ProfileId,
        string StoredHash,
        string CreatedAtUtc,
        string? RevokedAtUtc);
    private sealed record ActiveProfileMetadata(
        string DisplayName,
        long MetadataRevision);
    private sealed record AccessKeyAuthenticationCandidate(
        string ProfileId,
        string DisplayName,
        long MetadataRevision,
        string KeyId,
        string StoredHash);
    private sealed record CachedAccessKeyAuthentication(
        string[] StoredHashes,
        DateTimeOffset ExpiresAt);

    private const int MaximumCachedAccessKeys = 256;
    private static readonly TimeSpan AccessKeyCacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AccessKeyUsageTouchInterval = TimeSpan.FromMinutes(5);

    private readonly ProfileHostOptions _options;
    private readonly ProfileHostChangeSignal? _changeSignal;
    private readonly ITradeCompanyFounderBinder? _founderBinder;
    private readonly SqliteDiscordIdentityStore? _identityStore;
    private readonly ILogger<SqliteProfileHostStore>? _logger;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedAccessKeyAuthentication> _accessKeyCache = new();
    private volatile bool _schemaReady;

    public SqliteProfileHostStore(
        ProfileHostOptions options,
        ProfileHostChangeSignal? changeSignal = null,
        ITradeCompanyFounderBinder? founderBinder = null,
        ILogger<SqliteProfileHostStore>? logger = null,
        SqliteDiscordIdentityStore? identityStore = null)
    {
        _options = options;
        _changeSignal = changeSignal;
        _founderBinder = founderBinder;
        _logger = logger;
        _identityStore = identityStore;
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
        var existingMetadataRevision = 0L;
        var existingDisabled = false;
        await using (var profile = connection.CreateCommand())
        {
            profile.Transaction = (SqliteTransaction)transaction;
            profile.CommandText =
                """
                SELECT display_name, metadata_revision, disabled_at_utc
                FROM hosted_profiles
                WHERE id = $profileId;
                """;
            profile.Parameters.AddWithValue("$profileId", profileId);
            await using var reader = await profile.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                existingDisplayName = reader.GetString(0);
                existingMetadataRevision = reader.GetInt64(1);
                existingDisabled = !reader.IsDBNull(2);
            }
        }

        if (existingDisplayName != null)
        {
            if (existingDisabled)
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
            if (!keyMatches && (revision != 0 || existingMetadataRevision != 0))
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
                    INSERT INTO profile_access_keys (
                        id,
                        profile_id,
                        key_hash,
                        key_fingerprint,
                        created_at_utc)
                    VALUES ($id, $profileId, $keyHash, $keyFingerprint, $createdAtUtc);
                    """;
                insertKey.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                insertKey.Parameters.AddWithValue("$profileId", profileId);
                insertKey.Parameters.AddWithValue("$keyHash", hasher.Hash(plaintextKey));
                insertKey.Parameters.AddWithValue("$keyFingerprint", hasher.Fingerprint(plaintextKey));
                insertKey.Parameters.AddWithValue("$createdAtUtc", reconciliationTimestamp);
                await insertKey.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return new ProfileHostEnsureResult(
                new ProfileHostProfileResponse
                {
                    ProfileId = profileId,
                    DisplayName = existingDisplayName,
                    MetadataRevision = existingMetadataRevision,
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
                INSERT INTO profile_access_keys (
                    id,
                    profile_id,
                    key_hash,
                    key_fingerprint,
                    created_at_utc)
                VALUES ($id, $profileId, $keyHash, $keyFingerprint, $createdAtUtc);
                """;
            insertKey.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insertKey.Parameters.AddWithValue("$profileId", profileId);
            insertKey.Parameters.AddWithValue("$keyHash", hasher.Hash(plaintextKey));
            insertKey.Parameters.AddWithValue("$keyFingerprint", hasher.Fingerprint(plaintextKey));
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

    public async Task<ProfileHostEnsureResult> ProvisionProfileIfMissingAsync(
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
        await using (var connection = await OpenAsync(ct))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT p.display_name, p.metadata_revision, p.disabled_at_utc,
                       coalesce(r.revision, 0)
                FROM hosted_profiles p
                LEFT JOIN profile_revisions r ON r.profile_id = p.id
                WHERE p.id = $profileId;
                """;
            command.Parameters.AddWithValue("$profileId", profileId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(2))
                {
                    throw new InvalidOperationException(
                        "The existing profile does not match the requested active profile identity.");
                }

                return new ProfileHostEnsureResult(
                    new ProfileHostProfileResponse
                    {
                        ProfileId = profileId,
                        DisplayName = reader.GetString(0),
                        MetadataRevision = reader.GetInt64(1),
                        ServerRevision = reader.GetInt64(3)
                    },
                    Created: false);
            }
        }

        return await EnsureProfileAsync(profileId, displayName, plaintextKey, hasher, ct);
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
        await AddAccessKeyAsync(profileId, storedHash, fingerprint: null, ct);
    }

    public async Task AddAccessKeyAsync(
        string profileId,
        CreatedProfileAccessKey accessKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accessKey);
        await AddAccessKeyAsync(
            profileId,
            accessKey.StoredHash,
            accessKey.Fingerprint,
            ct);
    }

    private async Task AddAccessKeyAsync(
        string profileId,
        string storedHash,
        string? fingerprint,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        var now = DateTime.UtcNow;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into profile_access_keys (
                id,
                profile_id,
                key_hash,
                key_fingerprint,
                created_at_utc)
            values ($id, $profileId, $keyHash, $keyFingerprint, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$keyHash", storedHash);
        command.Parameters.AddWithValue("$keyFingerprint", (object?)fingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProfileAccessKeyImportResult> ImportActiveAccessKeysAsync(
        string sourceDatabasePath,
        string profileId,
        string expectedDisplayName,
        ProfileAccessKeyHasher hasher,
        CancellationToken ct)
    {
        if (!Guid.TryParseExact(profileId, "D", out var parsedProfileId) ||
            parsedProfileId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Profile id must be a non-empty UUID in canonical form.");
        }

        if (string.IsNullOrWhiteSpace(expectedDisplayName) || expectedDisplayName.Length > 120)
        {
            throw new InvalidOperationException(
                "Expected display name must contain 1 to 120 characters.");
        }

        if (string.IsNullOrWhiteSpace(sourceDatabasePath))
        {
            throw new InvalidOperationException("Source database path is required.");
        }

        profileId = parsedProfileId.ToString("D");
        var sourcePath = Path.GetFullPath(sourceDatabasePath);
        var targetPath = Path.GetFullPath(_options.DatabasePath);
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source and target databases must be distinct.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException("The source profile database does not exist.");
        }

        if (!File.Exists(targetPath))
        {
            throw new InvalidOperationException("The target profile database does not exist.");
        }

        // Hold an immediate transaction on the source through the target commit.
        // That freezes credential revocation/rotation and also makes a filesystem
        // alias of the target fail on the second write reservation.
        await using var source = await OpenDatabaseAsync(sourcePath, SqliteOpenMode.ReadWrite, ct);
        await EnsureProfileMetadataRevisionSchemaAsync(source, ct);
        await using var sourceTransaction = source.BeginTransaction(
            IsolationLevel.Serializable,
            deferred: false);
        var sourceProfile = await LoadActiveProfileMetadataAsync(
            source,
            sourceTransaction,
            profileId,
            "source",
            ct);
        var sourceKeys = await LoadActiveAccessKeysAsync(
            source,
            sourceTransaction,
            profileId,
            ct);
        ValidateSourceAccessKeys(sourceKeys, hasher);

        await using var target = await OpenDatabaseAsync(targetPath, SqliteOpenMode.ReadWrite, ct);
        await using var targetTransaction = (SqliteTransaction)await target.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        var targetProfile = await LoadActiveProfileMetadataAsync(
            target,
            targetTransaction,
            profileId,
            "target",
            ct);
        var importedMetadataRevision = string.Equals(
            targetProfile.DisplayName,
            sourceProfile.DisplayName,
            StringComparison.Ordinal)
                ? Math.Max(targetProfile.MetadataRevision, sourceProfile.MetadataRevision)
                : Math.Max(
                    checked(targetProfile.MetadataRevision + 1),
                    sourceProfile.MetadataRevision);

        var profileMetadataChanged = false;
        await using (var copyProfileMetadata = target.CreateCommand())
        {
            copyProfileMetadata.Transaction = targetTransaction;
            copyProfileMetadata.CommandText =
                """
                UPDATE hosted_profiles
                SET display_name = $displayName,
                    metadata_revision = $metadataRevision,
                    updated_at_utc = $updatedAtUtc
                WHERE id = $profileId
                  AND disabled_at_utc IS NULL
                  AND (
                    display_name <> $displayName OR
                    metadata_revision <> $metadataRevision
                  );
                """;
            copyProfileMetadata.Parameters.AddWithValue("$profileId", profileId);
            copyProfileMetadata.Parameters.AddWithValue("$displayName", sourceProfile.DisplayName);
            copyProfileMetadata.Parameters.AddWithValue(
                "$metadataRevision",
                importedMetadataRevision);
            copyProfileMetadata.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O"));
            profileMetadataChanged = await copyProfileMetadata.ExecuteNonQueryAsync(ct) == 1;
        }
        var importedServerRevision = profileMetadataChanged
            ? await ReserveNextRevisionAsync(target, targetTransaction, profileId, ct)
            : (long?)null;

        var targetKeys = await LoadRelevantTargetAccessKeysAsync(
            target,
            targetTransaction,
            sourceKeys,
            ct);
        var alreadyPresentIds = new List<string>();
        var keysToInsert = new List<ImportedAccessKey>();
        foreach (var sourceKey in sourceKeys)
        {
            var idMatches = targetKeys
                .Where(key => string.Equals(key.Id, sourceKey.Id, StringComparison.Ordinal))
                .ToArray();
            var hashMatches = targetKeys
                .Where(key => string.Equals(key.StoredHash, sourceKey.StoredHash, StringComparison.Ordinal))
                .ToArray();

            if (idMatches.Length == 1 &&
                hashMatches.Length == 1 &&
                ReferenceEquals(idMatches[0], hashMatches[0]) &&
                string.Equals(idMatches[0].ProfileId, profileId, StringComparison.Ordinal) &&
                string.Equals(idMatches[0].CreatedAtUtc, sourceKey.CreatedAtUtc, StringComparison.Ordinal) &&
                idMatches[0].RevokedAtUtc is null)
            {
                alreadyPresentIds.Add(sourceKey.Id);
                continue;
            }

            if (idMatches.Length != 0 || hashMatches.Length != 0)
            {
                await targetTransaction.RollbackAsync(ct);
                throw new InvalidOperationException(
                    "An imported access key conflicts with existing target credential metadata.");
            }

            keysToInsert.Add(sourceKey);
        }

        foreach (var sourceKey in keysToInsert)
        {
            await using var insert = target.CreateCommand();
            insert.Transaction = targetTransaction;
            insert.CommandText =
                """
                INSERT INTO profile_access_keys (id, profile_id, key_hash, created_at_utc, last_used_at_utc, revoked_at_utc)
                VALUES ($id, $profileId, $keyHash, $createdAtUtc, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$id", sourceKey.Id);
            insert.Parameters.AddWithValue("$profileId", profileId);
            insert.Parameters.AddWithValue("$keyHash", sourceKey.StoredHash);
            insert.Parameters.AddWithValue("$createdAtUtc", sourceKey.CreatedAtUtc);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await targetTransaction.CommitAsync(ct);
        await sourceTransaction.CommitAsync(ct);
        if (importedServerRevision.HasValue)
        {
            _changeSignal?.Publish(profileId, importedServerRevision.Value);
        }
        return new ProfileAccessKeyImportResult(
            profileId,
            sourceKeys.Count,
            keysToInsert.Select(key => key.Id).ToArray(),
            alreadyPresentIds.ToArray());
    }

    private static async Task<ActiveProfileMetadata> LoadActiveProfileMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        string databaseRole,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT display_name, metadata_revision, disabled_at_utc
            FROM hosted_profiles
            WHERE id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !reader.IsDBNull(2))
        {
            throw new InvalidOperationException(
                $"The {databaseRole} database does not contain the expected active profile identity.");
        }

        return new ActiveProfileMetadata(reader.GetString(0), reader.GetInt64(1));
    }

    private static async Task<List<ImportedAccessKey>> LoadActiveAccessKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, key_hash, created_at_utc
            FROM profile_access_keys
            WHERE profile_id = $profileId AND revoked_at_utc IS NULL
            ORDER BY id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$limit", MaximumImportedActiveAccessKeys + 1);

        var keys = new List<ImportedAccessKey>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            keys.Add(new ImportedAccessKey(
                reader.GetString(0),
                profileId,
                reader.GetString(1),
                reader.GetString(2),
                null));
        }

        return keys;
    }

    private static void ValidateSourceAccessKeys(
        IReadOnlyCollection<ImportedAccessKey> sourceKeys,
        ProfileAccessKeyHasher hasher)
    {
        if (sourceKeys.Count == 0)
        {
            throw new InvalidOperationException("The source profile has no active access keys to import.");
        }

        if (sourceKeys.Count > MaximumImportedActiveAccessKeys)
        {
            throw new InvalidOperationException(
                $"The source profile exceeds the {MaximumImportedActiveAccessKeys}-key import limit.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in sourceKeys)
        {
            if (!Guid.TryParseExact(key.Id, "D", out var parsedKeyId) ||
                parsedKeyId == Guid.Empty ||
                !string.Equals(key.Id, parsedKeyId.ToString("D"), StringComparison.Ordinal) ||
                !hasher.IsSupportedStoredHash(key.StoredHash) ||
                !DateTimeOffset.TryParse(
                    key.CreatedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _) ||
                !ids.Add(key.Id) ||
                !hashes.Add(key.StoredHash))
            {
                throw new InvalidOperationException(
                    "The source profile contains malformed or duplicate active credential metadata.");
            }
        }
    }

    private static async Task<List<ImportedAccessKey>> LoadRelevantTargetAccessKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<ImportedAccessKey> sourceKeys,
        CancellationToken ct)
    {
        var sourceIds = sourceKeys.Select(key => key.Id).ToHashSet(StringComparer.Ordinal);
        var sourceHashes = sourceKeys.Select(key => key.StoredHash).ToHashSet(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, profile_id, key_hash, created_at_utc, revoked_at_utc
            FROM profile_access_keys;
            """;

        var keys = new List<ImportedAccessKey>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var storedHash = reader.GetString(2);
            if (!sourceIds.Contains(id) && !sourceHashes.Contains(storedHash))
            {
                continue;
            }

            keys.Add(new ImportedAccessKey(
                id,
                reader.GetString(1),
                storedHash,
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return keys;
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
        string accessKeyFingerprint,
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
        var metadataRevision = 0L;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                select p.id, p.display_name, p.metadata_revision
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
                metadataRevision = reader.GetInt64(2);
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
                insert into profile_access_keys (
                    id,
                    profile_id,
                    key_hash,
                    key_fingerprint,
                    created_at_utc)
                values ($id, $profileId, $keyHash, $keyFingerprint, $createdAtUtc);
                """;
            insertKey.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insertKey.Parameters.AddWithValue("$profileId", profileId);
            insertKey.Parameters.AddWithValue("$keyHash", accessKeyHash);
            insertKey.Parameters.AddWithValue("$keyFingerprint", accessKeyFingerprint);
            insertKey.Parameters.AddWithValue("$createdAtUtc", nowUtc.ToString("O"));
            await insertKey.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new ProfileHostProfileResponse
        {
            ProfileId = profileId,
            DisplayName = displayName,
            MetadataRevision = metadataRevision,
            ServerRevision = await GetServerRevisionAsync(connection, profileId, ct)
        };
    }

    public async Task<ProfileHostProfileResponse?> LoadProfileAsync(string profileId, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, display_name, metadata_revision
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
        var metadataRevision = reader.GetInt64(2);
        await reader.DisposeAsync();

        return new ProfileHostProfileResponse
        {
            ProfileId = profileId,
            DisplayName = displayName,
            MetadataRevision = metadataRevision,
            ServerRevision = await GetServerRevisionAsync(connection, profileId, ct)
        };
    }

    public async Task<ProfileHostDisplayNameUpdateResponse> UpdateProfileDisplayNameAsync(
        string profileId,
        long expectedMetadataRevision,
        string displayName,
        CancellationToken ct)
    {
        if (expectedMetadataRevision < 0 ||
            expectedMetadataRevision == long.MaxValue ||
            !ProfileHostDisplayNamePolicy.TryNormalize(displayName, out var normalizedDisplayName))
        {
            return new ProfileHostDisplayNameUpdateResponse
            {
                ErrorCode = "invalid_profile_name",
                ErrorMessage = "Account names must contain 1 to 120 visible characters."
            };
        }

        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        var updated = 0;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                update hosted_profiles
                set display_name = $displayName,
                    metadata_revision = metadata_revision + 1,
                    updated_at_utc = $updatedAtUtc
                where id = $profileId
                  and disabled_at_utc is null
                  and metadata_revision = $expectedMetadataRevision
                  and display_name <> $displayName;
                """;
            update.Parameters.AddWithValue("$displayName", normalizedDisplayName);
            update.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$profileId", profileId);
            update.Parameters.AddWithValue("$expectedMetadataRevision", expectedMetadataRevision);
            updated = await update.ExecuteNonQueryAsync(ct);
        }

        string? currentDisplayName = null;
        var currentMetadataRevision = 0L;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                select display_name, metadata_revision
                from hosted_profiles
                where id = $profileId and disabled_at_utc is null;
                """;
            current.Parameters.AddWithValue("$profileId", profileId);
            await using var reader = await current.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                currentDisplayName = reader.GetString(0);
                currentMetadataRevision = reader.GetInt64(1);
            }
        }

        if (currentDisplayName == null)
        {
            await transaction.RollbackAsync(ct);
            return new ProfileHostDisplayNameUpdateResponse
            {
                ErrorCode = "profile_unavailable",
                ErrorMessage = "This hosted profile is no longer available."
            };
        }

        var publishedRevision = updated == 1
            ? await ReserveNextRevisionAsync(connection, transaction, profileId, ct)
            : (long?)null;
        var currentProfile = new ProfileHostProfileResponse
        {
            ProfileId = profileId,
            DisplayName = currentDisplayName,
            MetadataRevision = currentMetadataRevision,
            ServerRevision = publishedRevision ?? await GetServerRevisionAsync(
                    connection,
                    profileId,
                    ct,
                    transaction)
        };
        if (updated == 1 ||
            ((currentMetadataRevision == expectedMetadataRevision ||
             currentMetadataRevision == expectedMetadataRevision + 1) &&
             string.Equals(currentDisplayName, normalizedDisplayName, StringComparison.Ordinal)))
        {
            await transaction.CommitAsync(ct);
            if (publishedRevision.HasValue)
            {
                _changeSignal?.Publish(profileId, publishedRevision.Value);
            }
            return new ProfileHostDisplayNameUpdateResponse
            {
                Success = true,
                Profile = currentProfile
            };
        }

        await transaction.CommitAsync(ct);
        return new ProfileHostDisplayNameUpdateResponse
        {
            Conflict = true,
            Profile = currentProfile,
            ErrorCode = "profile_name_conflict",
            ErrorMessage = "The account name changed in another browser."
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

    public async Task<IReadOnlyList<ProfileHostAccessKeyMetadata>> LoadActiveAccessKeysAsync(
        string profileId,
        IReadOnlyCollection<string> currentKeyIds,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, created_at_utc, last_used_at_utc
            from profile_access_keys
            where profile_id = $profileId and revoked_at_utc is null
            order by created_at_utc, id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);

        var keys = new List<ProfileHostAccessKeyMetadata>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            keys.Add(new ProfileHostAccessKeyMetadata
            {
                Id = reader.GetString(0),
                CreatedAtUtc = DateTime.Parse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                LastUsedAtUtc = reader.IsDBNull(2)
                    ? null
                    : DateTime.Parse(
                        reader.GetString(2),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                IsCurrent = currentKeyIds.Contains(reader.GetString(0), StringComparer.Ordinal)
            });
        }

        return keys;
    }

    public async Task<bool> RevokeAccessKeyAsync(
        string profileId,
        string keyId,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update profile_access_keys
            set revoked_at_utc = $revokedAtUtc
            where id = $keyId
              and profile_id = $profileId
              and revoked_at_utc is null;
            """;
        command.Parameters.AddWithValue("$keyId", keyId);
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$revokedAtUtc", DateTime.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
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
        CancellationToken ct) =>
        (await AuthenticateAccessKeyAsync(plaintextKey, hasher, ct))?.Profile;

    public async Task<ProfileHostProfileResponse?> TryAuthenticateCachedAsync(
        string plaintextKey,
        ProfileAccessKeyHasher hasher,
        CancellationToken ct) =>
        (await TryAuthenticateCachedAccessKeyAsync(plaintextKey, hasher, ct))?.Profile;

    public async Task<AuthenticatedProfileAccessKey?> TryAuthenticateCachedAccessKeyAsync(
        string plaintextKey,
        ProfileAccessKeyHasher hasher,
        CancellationToken ct)
    {
        var fingerprint = hasher.Fingerprint(plaintextKey);
        if (!_accessKeyCache.TryGetValue(fingerprint, out var cached) ||
            cached.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _accessKeyCache.TryRemove(fingerprint, out _);
            return null;
        }

        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        return await TryAuthenticateCachedAccessKeyAsync(
            connection,
            fingerprint,
            ct);
    }

    public async Task<AuthenticatedProfileAccessKey?> AuthenticateAccessKeyAsync(
        string plaintextKey,
        ProfileAccessKeyHasher hasher,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        var fingerprint = hasher.Fingerprint(plaintextKey);
        var cached = await TryAuthenticateCachedAccessKeyAsync(
            connection,
            fingerprint,
            ct);
        if (cached != null)
        {
            return cached;
        }

        var candidates = await LoadAuthenticationCandidatesAsync(
            connection,
            fingerprint,
            ct);
        var usedLegacyFallback = candidates.Count == 0;
        if (usedLegacyFallback)
        {
            candidates = await LoadAuthenticationCandidatesAsync(
                connection,
                fingerprint: null,
                ct);
        }

        var matchingCandidates = candidates
            .Where(candidate => hasher.Verify(plaintextKey, candidate.StoredHash))
            .ToList();
        if (!usedLegacyFallback && matchingCandidates.Count > 0)
        {
            var importedAliases = await LoadAuthenticationCandidatesByStoredHashAsync(
                connection,
                matchingCandidates.Select(candidate => candidate.StoredHash).Distinct().ToArray(),
                ct);
            matchingCandidates.AddRange(importedAliases.Where(alias =>
                matchingCandidates.All(candidate => candidate.KeyId != alias.KeyId) &&
                hasher.Verify(plaintextKey, alias.StoredHash)));
        }

        var matches = matchingCandidates
            .Select(candidate => (
                candidate.ProfileId,
                candidate.DisplayName,
                candidate.MetadataRevision,
                candidate.KeyId))
            .ToList();

        if (matches.Count == 0 || matches.Any(match => match.ProfileId != matches[0].ProfileId))
        {
            return null;
        }

        foreach (var match in matches)
        {
            await SaveAccessKeyFingerprintAsync(
                connection,
                match.KeyId,
                fingerprint,
                ct);
            await TouchAccessKeyAsync(connection, match.KeyId, ct);
        }
        var profileId = matches[0].ProfileId;
        var revision = await GetServerRevisionAsync(connection, profileId, ct);
        var authenticated = new AuthenticatedProfileAccessKey(
            new ProfileHostProfileResponse
            {
                ProfileId = profileId,
                DisplayName = matches[0].DisplayName,
                MetadataRevision = matches[0].MetadataRevision,
                ServerRevision = revision
            },
            matches.Select(match => match.KeyId).ToArray());
        CacheSuccessfulAccessKey(
            fingerprint,
            matchingCandidates.Select(candidate => candidate.StoredHash));
        return authenticated;
    }

    private async Task<AuthenticatedProfileAccessKey?> TryAuthenticateCachedAccessKeyAsync(
        SqliteConnection connection,
        string fingerprint,
        CancellationToken ct)
    {
        if (!_accessKeyCache.TryGetValue(fingerprint, out var cached) ||
            cached.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _accessKeyCache.TryRemove(fingerprint, out _);
            return null;
        }

        var candidates = await LoadAuthenticationCandidatesByStoredHashAsync(
            connection,
            cached.StoredHashes,
            ct);
        if (candidates.Count == 0 ||
            candidates.Any(candidate => candidate.ProfileId != candidates[0].ProfileId))
        {
            _accessKeyCache.TryRemove(fingerprint, out _);
            return null;
        }

        foreach (var candidate in candidates)
        {
            await SaveAccessKeyFingerprintAsync(
                connection,
                candidate.KeyId,
                fingerprint,
                ct);
            await TouchAccessKeyAsync(connection, candidate.KeyId, ct);
        }

        var profileId = candidates[0].ProfileId;
        var revision = await GetServerRevisionAsync(connection, profileId, ct);
        return new AuthenticatedProfileAccessKey(
            new ProfileHostProfileResponse
            {
                ProfileId = profileId,
                DisplayName = candidates[0].DisplayName,
                MetadataRevision = candidates[0].MetadataRevision,
                ServerRevision = revision
            },
            candidates.Select(candidate => candidate.KeyId).ToArray());
    }

    private void CacheSuccessfulAccessKey(
        string fingerprint,
        IEnumerable<string> storedHashes)
    {
        var now = DateTimeOffset.UtcNow;
        if (_accessKeyCache.Count >= MaximumCachedAccessKeys)
        {
            foreach (var expired in _accessKeyCache.Where(entry => entry.Value.ExpiresAt <= now))
            {
                _accessKeyCache.TryRemove(expired.Key, out _);
            }

            if (_accessKeyCache.Count >= MaximumCachedAccessKeys)
            {
                var oldest = _accessKeyCache.MinBy(entry => entry.Value.ExpiresAt);
                if (!string.IsNullOrEmpty(oldest.Key))
                {
                    _accessKeyCache.TryRemove(oldest.Key, out _);
                }
            }
        }

        _accessKeyCache[fingerprint] = new CachedAccessKeyAuthentication(
            storedHashes.Distinct(StringComparer.Ordinal).ToArray(),
            now.Add(AccessKeyCacheLifetime));
    }

    private static async Task<List<AccessKeyAuthenticationCandidate>> LoadAuthenticationCandidatesAsync(
        SqliteConnection connection,
        string? fingerprint,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = fingerprint == null
            ? """
                select p.id, p.display_name, p.metadata_revision, k.id, k.key_hash
                from profile_access_keys k
                inner join hosted_profiles p on p.id = k.profile_id
                where k.key_fingerprint is null
                  and k.revoked_at_utc is null
                  and p.disabled_at_utc is null;
                """
            : """
                select p.id, p.display_name, p.metadata_revision, k.id, k.key_hash
                from profile_access_keys k
                inner join hosted_profiles p on p.id = k.profile_id
                where k.key_fingerprint = $fingerprint
                  and k.revoked_at_utc is null
                  and p.disabled_at_utc is null;
                """;
        if (fingerprint != null)
        {
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
        }

        var candidates = new List<AccessKeyAuthenticationCandidate>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            candidates.Add(new AccessKeyAuthenticationCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return candidates;
    }

    private static async Task SaveAccessKeyFingerprintAsync(
        SqliteConnection connection,
        string keyId,
        string fingerprint,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update profile_access_keys
            set key_fingerprint = $fingerprint
            where id = $id and key_fingerprint is null;
            """;
        command.Parameters.AddWithValue("$id", keyId);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<AccessKeyAuthenticationCandidate>> LoadAuthenticationCandidatesByStoredHashAsync(
        SqliteConnection connection,
        IReadOnlyList<string> storedHashes,
        CancellationToken ct)
    {
        if (storedHashes.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var parameters = new string[storedHashes.Count];
        for (var index = 0; index < storedHashes.Count; index++)
        {
            parameters[index] = $"$hash{index}";
            command.Parameters.AddWithValue(parameters[index], storedHashes[index]);
        }

        command.CommandText = $"""
            select p.id, p.display_name, p.metadata_revision, k.id, k.key_hash
            from profile_access_keys k
            inner join hosted_profiles p on p.id = k.profile_id
            where k.key_hash in ({string.Join(", ", parameters)})
              and k.revoked_at_utc is null
              and p.disabled_at_utc is null;
            """;
        var candidates = new List<AccessKeyAuthenticationCandidate>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            candidates.Add(new AccessKeyAuthenticationCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return candidates;
    }

    public async Task<ProfileSyncChangesResponse> LoadChangesAsync(
        string profileId,
        long sinceRevision,
        CancellationToken ct,
        int? limit = null,
        IReadOnlyCollection<string>? collections = null)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var collectionFilter = collections is { Count: > 0 }
            ? " and collection in (select value from json_each($collections))"
            : string.Empty;
        command.CommandText = limit.HasValue
            ? $"""
            select collection, object_id, payload_json, revision, updated_at_utc, deleted, deleted_at_utc
            from sync_objects
            where profile_id = $profileId and revision > $sinceRevision{collectionFilter}
            order by revision asc
            limit $limit;
            """
            : $"""
            select collection, object_id, payload_json, revision, updated_at_utc, deleted, deleted_at_utc
            from sync_objects
            where profile_id = $profileId and revision > $sinceRevision{collectionFilter}
            order by revision asc;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$sinceRevision", sinceRevision);
        if (collections is { Count: > 0 })
        {
            command.Parameters.AddWithValue(
                "$collections",
                JsonSerializer.Serialize(collections));
        }
        if (limit.HasValue)
        {
            command.Parameters.AddWithValue("$limit", checked(limit.Value + 1));
        }

        var objects = new List<ProfileSyncObjectEnvelope>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                objects.Add(ReadObject(reader));
            }
        }

        var hasMore = limit.HasValue && objects.Count > limit.Value;
        if (hasMore)
        {
            objects.RemoveAt(objects.Count - 1);
        }

        var currentServerRevision = await GetServerRevisionAsync(
            connection,
            profileId,
            ct,
            transaction);
        var serverRevision = hasMore
            ? objects[^1].Revision
            : currentServerRevision;
        await transaction.CommitAsync(ct);
        return new ProfileSyncChangesResponse
        {
            ServerRevision = serverRevision,
            HasMore = hasMore,
            Objects = objects
        };
    }

    public async Task<long> LoadServerRevisionAsync(
        string profileId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        return await GetServerRevisionAsync(connection, profileId, ct);
    }

    public async Task<ProfileSyncObjectEnvelope?> LoadHostedObjectAsync(
        string profileId,
        string collection,
        string objectId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ValidateCollection(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        return await LoadObjectAsync(connection, profileId, collection, objectId, ct);
    }

    public async Task<IReadOnlyList<string>> LoadActiveProfileIdsAsync(
        CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id
            from hosted_profiles
            where disabled_at_utc is null
            order by id;
            """;
        var profileIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            profileIds.Add(reader.GetString(0));
        }

        return profileIds;
    }

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadRetentionCandidatesAsync(
        string profileId,
        DateTime modifiedBeforeUtc,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select collection, object_id, payload_json, revision, updated_at_utc, deleted, deleted_at_utc
            from sync_objects
            where profile_id = $profileId
              and collection = $collection
              and deleted = 0
              and updated_at_utc < $cutoff
            order by updated_at_utc asc;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$collection", ProfileSyncCollections.TradeOrders);
        command.Parameters.AddWithValue("$cutoff", modifiedBeforeUtc.ToString("O"));
        var objects = new List<ProfileSyncObjectEnvelope>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            objects.Add(ReadObject(reader));
        }

        return objects;
    }

    public async Task<bool> MoveOrderToDeepArchiveAsync(
        string profileId,
        ProfileSyncObjectEnvelope candidate,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(candidate);
        var summary = TradeOrderArchiveSummaryCodec.TryCreate(
            candidate.PayloadJson,
            candidate.ObjectId);
        if (summary == null)
        {
            return false;
        }

        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);
        var current = await LoadObjectAsync(
            connection,
            profileId,
            ProfileSyncCollections.TradeOrders,
            candidate.ObjectId,
            ct,
            transaction);
        if (current is not { Deleted: false } || current.Revision != candidate.Revision)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var currentSummary = TradeOrderArchiveSummaryCodec.TryCreate(
            current.PayloadJson,
            current.ObjectId);
        if (currentSummary == null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var tombstoneRevision = await ReserveNextRevisionAsync(
            connection,
            transaction,
            profileId,
            ct);
        var archivedAtUtc = DateTime.UtcNow;
        var summaryJson = TradeOrderArchiveSummaryCodec.Serialize(currentSummary);
        var searchText = string.Join(
            "\n",
            new[] { currentSummary.Title }
                .Concat(currentSummary.Outputs.Select(output => output.Name)))
            .ToLowerInvariant();

        await using (var archive = connection.CreateCommand())
        {
            archive.Transaction = transaction;
            archive.CommandText = """
                insert into deep_archived_trade_orders (
                    profile_id, object_id, payload_json, summary_json, search_text,
                    source_revision, tombstone_revision, archived_at_utc)
                values (
                    $profileId, $objectId, $payloadJson, $summaryJson, $searchText,
                    $sourceRevision, $tombstoneRevision, $archivedAtUtc)
                on conflict(profile_id, object_id) do update set
                    payload_json = excluded.payload_json,
                    summary_json = excluded.summary_json,
                    search_text = excluded.search_text,
                    source_revision = excluded.source_revision,
                    tombstone_revision = excluded.tombstone_revision,
                    archived_at_utc = excluded.archived_at_utc;
                """;
            archive.Parameters.AddWithValue("$profileId", profileId);
            archive.Parameters.AddWithValue("$objectId", candidate.ObjectId);
            archive.Parameters.AddWithValue("$payloadJson", current.PayloadJson);
            archive.Parameters.AddWithValue("$summaryJson", summaryJson);
            archive.Parameters.AddWithValue("$searchText", searchText);
            archive.Parameters.AddWithValue("$sourceRevision", current.Revision);
            archive.Parameters.AddWithValue("$tombstoneRevision", tombstoneRevision);
            archive.Parameters.AddWithValue("$archivedAtUtc", archivedAtUtc.ToString("O"));
            await archive.ExecuteNonQueryAsync(ct);
        }

        await using (var tombstone = connection.CreateCommand())
        {
            tombstone.Transaction = transaction;
            tombstone.CommandText = """
                update sync_objects
                set payload_json = '{}',
                    revision = $revision,
                    updated_at_utc = $updatedAtUtc,
                    deleted = 1,
                    deleted_at_utc = $updatedAtUtc
                where profile_id = $profileId
                  and collection = $collection
                  and object_id = $objectId
                  and revision = $expectedRevision
                  and deleted = 0;
                """;
            tombstone.Parameters.AddWithValue("$profileId", profileId);
            tombstone.Parameters.AddWithValue("$collection", ProfileSyncCollections.TradeOrders);
            tombstone.Parameters.AddWithValue("$objectId", candidate.ObjectId);
            tombstone.Parameters.AddWithValue("$revision", tombstoneRevision);
            tombstone.Parameters.AddWithValue("$updatedAtUtc", archivedAtUtc.ToString("O"));
            tombstone.Parameters.AddWithValue("$expectedRevision", current.Revision);
            if (await tombstone.ExecuteNonQueryAsync(ct) != 1)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }
        }

        await transaction.CommitAsync(ct);
        _changeSignal?.Publish(profileId, tombstoneRevision);
        return true;
    }

    public async Task<TradeOrderDeepArchivePage> SearchDeepArchivedOrdersAsync(
        string profileId,
        string query,
        int offset,
        int limit,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select object_id, summary_json, tombstone_revision, archived_at_utc
            from deep_archived_trade_orders
            where profile_id = $profileId
              and search_text like $query escape '\'
            order by archived_at_utc desc, object_id
            limit $limitPlusOne offset $offset;
            """;
        var escapedQuery = query.Trim().ToLowerInvariant()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$query", $"%{escapedQuery}%");
        command.Parameters.AddWithValue("$limitPlusOne", checked(limit + 1));
        command.Parameters.AddWithValue("$offset", offset);

        var orders = new List<TradeOrderDeepArchiveRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var objectId = reader.GetString(0);
            orders.Add(new TradeOrderDeepArchiveRecord
            {
                OrderId = Guid.Parse(objectId),
                Summary = TradeOrderArchiveSummaryCodec.Deserialize(reader.GetString(1), objectId),
                HostedRevision = reader.GetInt64(2),
                ArchivedAtUtc = DateTime.Parse(
                    reader.GetString(3),
                    null,
                    DateTimeStyles.RoundtripKind)
            });
        }

        var hasMore = orders.Count > limit;
        if (hasMore)
        {
            orders.RemoveAt(orders.Count - 1);
        }
        return new TradeOrderDeepArchivePage
        {
            Offset = offset,
            HasMore = hasMore,
            Orders = orders
        };
    }

    public async Task<ProfileSyncObjectEnvelope?> LoadDeepArchivedOrderAsync(
        string profileId,
        string objectId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select payload_json, tombstone_revision, archived_at_utc
            from deep_archived_trade_orders
            where profile_id = $profileId and object_id = $objectId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$objectId", objectId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.TradeOrders,
            ObjectId = objectId,
            PayloadJson = reader.GetString(0),
            Revision = reader.GetInt64(1),
            DeepArchived = true,
            UpdatedAtUtc = DateTime.Parse(
                reader.GetString(2),
                null,
                DateTimeStyles.RoundtripKind)
        };
    }

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadAllDeepArchivedOrdersAsync(
        string profileId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select object_id, payload_json, tombstone_revision, archived_at_utc
            from deep_archived_trade_orders
            where profile_id = $profileId
            order by archived_at_utc, object_id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        var orders = new List<ProfileSyncObjectEnvelope>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            orders.Add(new ProfileSyncObjectEnvelope
            {
                Collection = ProfileSyncCollections.TradeOrders,
                ObjectId = reader.GetString(0),
                PayloadJson = reader.GetString(1),
                Revision = reader.GetInt64(2),
                DeepArchived = true,
                UpdatedAtUtc = DateTime.Parse(
                    reader.GetString(3),
                    null,
                    DateTimeStyles.RoundtripKind)
            });
        }
        return orders;
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
        payloadJson = ProfileSyncPlanPayloadCodec.CompactIfPlan(
            collection,
            objectId,
            payloadJson);
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
        var isCompany = string.Equals(
            collection,
            ProfileSyncCollections.TradeCompanyProfiles,
            StringComparison.Ordinal);
        var introducesCompany = isCompany && existing == null;

        if (introducesCompany &&
            _identityStore != null &&
            await _identityStore.LoadByProfileAsync(Guid.Parse(profileId), ct) == null)
        {
            await transaction.RollbackAsync(ct);
            _logger?.LogError(
                "Hosted company creation refused for unclaimed profile {ProfileId}: company {CompanyId} requires an account.",
                profileId,
                objectId);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                ServerRevision = currentServerRevision,
                ErrorCode = "company_account_required",
                ErrorMessage = "Creating a hosted company requires a claimed account."
            };
        }

        if (existing is { Deleted: false } &&
            IsIdenticalLinkedPlanSnapshot(collection, objectId, existing.PayloadJson, payloadJson))
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = true,
                ServerRevision = currentServerRevision,
                Object = existing
            };
        }

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

        var linkedPlanError = await ValidateTradeOrderLinkedPlanAsync(
            connection,
            transaction,
            profileId,
            collection,
            objectId,
            payloadJson,
            existing,
            ct);
        if (linkedPlanError != null)
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                ServerRevision = currentServerRevision,
                RemoteObject = existing,
                ErrorCode = "linked_plan_invalid",
                ErrorMessage = linkedPlanError
            };
        }

        if (existing is { Deleted: false } &&
            IsUnsafeLinkedPlanSealPromotion(
                collection,
                objectId,
                existing.PayloadJson,
                payloadJson))
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                ServerRevision = currentServerRevision,
                RemoteObject = existing,
                ErrorCode = "linked_plan_promotion_mismatch",
                ErrorMessage = "The legacy hosted plan differs from the proposed linked snapshot seal."
            };
        }

        if (existing is { Deleted: false } &&
            IsLinkedPlanSnapshot(collection, objectId, existing.PayloadJson))
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                ServerRevision = currentServerRevision,
                RemoteObject = existing,
                ErrorCode = "immutable_plan_snapshot",
                ErrorMessage = "A linked order plan snapshot is immutable. Publish a new plan identity instead."
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
        if (string.Equals(collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal))
        {
            await using var restore = connection.CreateCommand();
            restore.Transaction = transaction;
            restore.CommandText = """
                delete from deep_archived_trade_orders
                where profile_id = $profileId
                  and object_id = $objectId
                  and tombstone_revision = $expectedRevision;
                """;
            restore.Parameters.AddWithValue("$profileId", profileId);
            restore.Parameters.AddWithValue("$objectId", objectId);
            restore.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            await restore.ExecuteNonQueryAsync(ct);
        }
        if (_founderBinder != null && isCompany)
        {
            if (!FounderMembershipBinding.TryRead(
                    profileId,
                    objectId,
                    payloadJson,
                    out var companyId,
                    out var accountProfileId))
            {
                if (introducesCompany)
                {
                    await transaction.RollbackAsync(ct);
                    _logger?.LogError(
                        "Founder membership binding refused hosted company {CompanyId} on profile {ProfileId}: object and payload identities do not match.",
                        objectId,
                        profileId);
                    return new ProfileSyncPutResponse
                    {
                        Success = false,
                        Conflict = true,
                        ServerRevision = currentServerRevision,
                        ErrorCode = "founder_identity_mismatch",
                        ErrorMessage = "The hosted company founder identity is invalid."
                    };
                }

                _logger?.LogError(
                    "Founder membership binding skipped hosted company {CompanyId} on profile {ProfileId}: object and payload identities do not match.",
                    objectId,
                    profileId);
            }
            else
            {
                try
                {
                    var binding = await _founderBinder.BindFounderAsync(companyId, accountProfileId, ct);
                    if (introducesCompany && binding.Status == FounderBindingStatus.ConflictingOwner)
                    {
                        await transaction.RollbackAsync(ct);
                        return new ProfileSyncPutResponse
                        {
                            Success = false,
                            Conflict = true,
                            ServerRevision = currentServerRevision,
                            ErrorCode = "founder_owner_conflict",
                            ErrorMessage = "The hosted company already has a different founder."
                        };
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (introducesCompany)
                    {
                        await transaction.RollbackAsync(ct);
                        _logger?.LogError(
                            exception,
                            "Founder membership binding refused new hosted company {CompanyId} on profile {ProfileId}.",
                            companyId,
                            profileId);
                        return new ProfileSyncPutResponse
                        {
                            Success = false,
                            Conflict = true,
                            ServerRevision = currentServerRevision,
                            ErrorCode = "founder_binding_failed",
                            ErrorMessage = "Founder membership could not be created."
                        };
                    }

                    _logger?.LogError(
                        exception,
                        "Founder membership binding failed for existing hosted company {CompanyId} on profile {ProfileId}; periodic reconciliation will retry.",
                        companyId,
                        profileId);
                }
            }
        }

        await transaction.CommitAsync(ct);
        _changeSignal?.Publish(profileId, revision);

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

    private static bool IsLinkedPlanSnapshot(
        string collection,
        string objectId,
        string payloadJson) =>
        string.Equals(collection, ProfileSyncCollections.Plans, StringComparison.OrdinalIgnoreCase) &&
        ProfileSyncPlanPayloadCodec.Deserialize(payloadJson, objectId).LinkedOrderId.HasValue;

    private static bool IsIdenticalLinkedPlanSnapshot(
        string collection,
        string objectId,
        string existingPayloadJson,
        string candidatePayloadJson)
    {
        if (!string.Equals(collection, ProfileSyncCollections.Plans, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var existing = ProfileSyncPlanPayloadCodec.Deserialize(existingPayloadJson, objectId);
        return existing.LinkedOrderId.HasValue &&
               string.Equals(
                   ProfileSyncPlanPayloadCodec.Serialize(existing),
                   ProfileSyncPlanPayloadCodec.Serialize(
                       ProfileSyncPlanPayloadCodec.Deserialize(candidatePayloadJson, objectId)),
                   StringComparison.Ordinal);
    }

    private static bool IsUnsafeLinkedPlanSealPromotion(
        string collection,
        string objectId,
        string existingPayloadJson,
        string candidatePayloadJson)
    {
        if (!string.Equals(collection, ProfileSyncCollections.Plans, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var existing = ProfileSyncPlanPayloadCodec.Deserialize(existingPayloadJson, objectId);
        var candidate = ProfileSyncPlanPayloadCodec.Deserialize(candidatePayloadJson, objectId);
        return !existing.LinkedOrderId.HasValue &&
               candidate.LinkedOrderId.HasValue &&
               !ProfileSyncPlanPayloadCodec.HasSameRevisionContent(existing, candidate);
    }

    private static async Task<bool> IsLinkedPlanStillReferencedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        string planId,
        string planPayloadJson,
        CancellationToken cancellationToken)
    {
        var linkedOrderId = ProfileSyncPlanPayloadCodec.Deserialize(
            planPayloadJson,
            planId).LinkedOrderId;
        if (!linkedOrderId.HasValue)
        {
            return false;
        }

        var order = await LoadObjectAsync(
            connection,
            profileId,
            ProfileSyncCollections.TradeOrders,
            linkedOrderId.Value.ToString("D"),
            cancellationToken,
            transaction);
        if (order is not { Deleted: false })
        {
            return false;
        }

        return true;
    }

    private static async Task<string?> ValidateTradeOrderLinkedPlanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        string collection,
        string objectId,
        string payloadJson,
        ProfileSyncObjectEnvelope? existing,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                collection,
                ProfileSyncCollections.TradeOrders,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidate = JsonSerializer.Deserialize<TradeOrder>(
            payloadJson,
            ProfileSyncJson.CreateOptions());
        if (candidate == null ||
            !string.Equals(candidate.Id.ToString("D"), objectId, StringComparison.Ordinal))
        {
            return "The Trade order payload does not match its hosted object identity.";
        }
        if (candidate.CraftPlanLinkKind != TradeOrderCraftPlanLinkKind.OrderGenerated)
        {
            return null;
        }

        var current = existing is { Deleted: false }
            ? JsonSerializer.Deserialize<TradeOrder>(
                existing.PayloadJson,
                ProfileSyncJson.CreateOptions())
            : null;
        if (current?.Id == candidate.Id &&
            current.CraftPlanLinkKind == candidate.CraftPlanLinkKind &&
            string.Equals(current.CraftPlanId, candidate.CraftPlanId, StringComparison.Ordinal) &&
            current.CraftPlanSavedAtUtc == candidate.CraftPlanSavedAtUtc)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(candidate.CraftPlanId) ||
            !candidate.CraftPlanSavedAtUtc.HasValue)
        {
            return "A changed generated-plan link requires both plan identity and saved timestamp.";
        }

        var hostedPlan = await LoadObjectAsync(
            connection,
            profileId,
            ProfileSyncCollections.Plans,
            candidate.CraftPlanId,
            cancellationToken,
            transaction);
        if (hostedPlan is not { Deleted: false })
        {
            return "The referenced generated plan snapshot is not hosted in this profile.";
        }
        var plan = ProfileSyncPlanPayloadCodec.Deserialize(
            hostedPlan.PayloadJson,
            candidate.CraftPlanId);
        return plan.LinkedOrderId == candidate.Id &&
               plan.SavedAt == candidate.CraftPlanSavedAtUtc.Value
            ? null
            : "The referenced generated plan does not match this order and saved revision.";
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
            throw new DuplicateHostedObjectIdentityException(collection, objectId);
        }

        return found;
    }

    public async Task<IReadOnlyList<HostedProfileObject>> LoadObjectsAsync(
        string collection,
        CancellationToken ct)
        => await LoadObjectsAsync(collection, includeDeleted: false, ct);

    public async Task<IReadOnlyList<HostedProfileObject>> LoadProfileObjectsAsync(
        string profileId,
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
              and o.profile_id = $profileId
              and o.collection = $collection
              and o.deleted = 0
            order by o.object_id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
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
                    PayloadJson = NormalizePortablePayload(
                        collection,
                        reader.GetString(1),
                        reader.GetString(2),
                        deleted: false),
                    Revision = reader.GetInt64(3),
                    UpdatedAtUtc = DateTime.Parse(
                        reader.GetString(4),
                        null,
                        DateTimeStyles.RoundtripKind),
                    Deleted = false,
                    DeletedAtUtc = null
                }));
        }

        return found;
    }

    public async Task<IReadOnlyList<HostedProfileObject>> LoadObjectsAsync(
        string collection,
        bool includeDeleted,
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
              and ($includeDeleted = 1 or o.deleted = 0)
            order by o.profile_id, o.object_id;
            """;
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);
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
                    PayloadJson = NormalizePortablePayload(
                        collection,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(5) == 1),
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

        if (existing is { Deleted: false } &&
            IsLinkedPlanSnapshot(collection, objectId, existing.PayloadJson) &&
            await IsLinkedPlanStillReferencedAsync(
                connection,
                transaction,
                profileId,
                objectId,
                existing.PayloadJson,
                ct))
        {
            await transaction.RollbackAsync(ct);
            return new ProfileSyncPutResponse
            {
                Success = false,
                Conflict = true,
                RemoteObject = existing,
                ErrorCode = "immutable_plan_snapshot",
                ErrorMessage = "Linked order plan snapshots cannot be deleted by generic profile synchronization."
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
        if (string.Equals(collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal))
        {
            await using var deleteArchived = connection.CreateCommand();
            deleteArchived.Transaction = transaction;
            deleteArchived.CommandText = """
                delete from deep_archived_trade_orders
                where profile_id = $profileId
                  and object_id = $objectId
                  and tombstone_revision = $expectedRevision;
                """;
            deleteArchived.Parameters.AddWithValue("$profileId", profileId);
            deleteArchived.Parameters.AddWithValue("$objectId", objectId);
            deleteArchived.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            await deleteArchived.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        _changeSignal?.Publish(profileId, revision);

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
            PayloadJson = NormalizePortablePayload(
                collection,
                objectId,
                reader.GetString(0),
                reader.GetInt64(3) == 1),
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
        var collection = reader.GetString(0);
        var objectId = reader.GetString(1);
        var deleted = reader.GetInt64(5) == 1;
        return new ProfileSyncObjectEnvelope
        {
            Collection = collection,
            ObjectId = objectId,
            PayloadJson = NormalizePortablePayload(
                collection,
                objectId,
                reader.GetString(2),
                deleted),
            Revision = reader.GetInt64(3),
            UpdatedAtUtc = DateTime.Parse(reader.GetString(4), null, DateTimeStyles.RoundtripKind),
            Deleted = deleted,
            DeletedAtUtc = reader.IsDBNull(6)
                ? null
                : DateTime.Parse(reader.GetString(6), null, DateTimeStyles.RoundtripKind)
        };
    }

    private static HostedProfileObject ReadHostedObject(
        SqliteDataReader reader,
        string collection,
        string objectId)
    {
        var deleted = reader.GetInt64(4) == 1;
        return new HostedProfileObject(
            reader.GetString(0),
            new ProfileSyncObjectEnvelope
            {
                Collection = collection,
                ObjectId = objectId,
                PayloadJson = NormalizePortablePayload(
                    collection,
                    objectId,
                    reader.GetString(1),
                    deleted),
                Revision = reader.GetInt64(2),
                UpdatedAtUtc = DateTime.Parse(
                    reader.GetString(3),
                    null,
                    DateTimeStyles.RoundtripKind),
                Deleted = deleted,
                DeletedAtUtc = reader.IsDBNull(5)
                    ? null
                    : DateTime.Parse(
                        reader.GetString(5),
                        null,
                        DateTimeStyles.RoundtripKind)
            });
    }

    private static string NormalizePortablePayload(
        string collection,
        string objectId,
        string payloadJson,
        bool deleted)
    {
        return deleted
            ? payloadJson
            : ProfileSyncPlanPayloadCodec.CompactIfPlan(
                collection,
                objectId,
                payloadJson);
    }

    private static async Task TouchAccessKeyAsync(SqliteConnection connection, string keyId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update profile_access_keys
            set last_used_at_utc = $lastUsedAtUtc
            where id = $id
              and (last_used_at_utc is null or last_used_at_utc < $touchBeforeUtc);
            """;
        command.Parameters.AddWithValue("$id", keyId);
        command.Parameters.AddWithValue("$lastUsedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue(
            "$touchBeforeUtc",
            now.Subtract(AccessKeyUsageTouchInterval).ToString("O"));
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
        if (_schemaReady)
        {
            return;
        }

        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady)
            {
                return;
            }

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
                    metadata_revision integer not null default 0,
                    created_at_utc text not null,
                    updated_at_utc text not null,
                    disabled_at_utc text null
                );

                create table if not exists profile_access_keys (
                    id text primary key,
                    profile_id text not null,
                    key_hash text not null,
                    key_fingerprint text null,
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

                create table if not exists deep_archived_trade_orders (
                    profile_id text not null,
                    object_id text not null,
                    payload_json text not null,
                    summary_json text not null,
                    search_text text not null,
                    source_revision integer not null,
                    tombstone_revision integer not null,
                    archived_at_utc text not null,
                    primary key(profile_id, object_id),
                    foreign key(profile_id) references hosted_profiles(id)
                );

                create index if not exists ix_deep_archived_trade_orders_profile_archived
                on deep_archived_trade_orders(profile_id, archived_at_utc desc);

                insert into profile_revisions (profile_id, revision)
                select p.id, coalesce(max(o.revision), 0)
                from hosted_profiles p
                left join sync_objects o on o.profile_id = p.id
                group by p.id
                on conflict(profile_id) do update set
                    revision = max(profile_revisions.revision, excluded.revision);
                """;
            await command.ExecuteNonQueryAsync(ct);
            await EnsureProfileMetadataRevisionSchemaAsync(connection, ct);
            await EnsureAccessKeyFingerprintSchemaAsync(connection, ct);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static async Task EnsureProfileMetadataRevisionSchemaAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        var hasMetadataRevision = false;
        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "pragma table_info(hosted_profiles);";
            await using var reader = await columns.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(
                        reader.GetString(1),
                        "metadata_revision",
                        StringComparison.Ordinal))
                {
                    hasMetadataRevision = true;
                    break;
                }
            }
        }

        if (!hasMetadataRevision)
        {
            await using var addColumn = connection.CreateCommand();
            addColumn.CommandText =
                "alter table hosted_profiles add column metadata_revision integer not null default 0;";
            await addColumn.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task EnsureAccessKeyFingerprintSchemaAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        var hasFingerprint = false;
        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "pragma table_info(profile_access_keys);";
            await using var reader = await columns.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (string.Equals(
                        reader.GetString(1),
                        "key_fingerprint",
                        StringComparison.Ordinal))
                {
                    hasFingerprint = true;
                    break;
                }
            }
        }

        if (!hasFingerprint)
        {
            await using var addColumn = connection.CreateCommand();
            addColumn.CommandText =
                "alter table profile_access_keys add column key_fingerprint text null;";
            await addColumn.ExecuteNonQueryAsync(ct);
        }

        await using var createIndex = connection.CreateCommand();
        createIndex.CommandText = """
            create index if not exists ix_profile_access_keys_fingerprint
            on profile_access_keys(key_fingerprint)
            where key_fingerprint is not null;

            create index if not exists ix_profile_access_keys_hash
            on profile_access_keys(key_hash);
            """;
        await createIndex.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        return await OpenDatabaseAsync(_options.DatabasePath, SqliteOpenMode.ReadWriteCreate, ct);
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync(
        string databasePath,
        SqliteOpenMode mode,
        CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
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

public sealed class DuplicateHostedObjectIdentityException(
    string collection,
    string objectId) : InvalidOperationException(
        $"Hosted object identity '{collection}/{objectId}' is duplicated across active profiles.");

public sealed record HostedProfileObject(
    string ProfileId,
    ProfileSyncObjectEnvelope Object);

public sealed record ProfileHostEnsureResult(
    ProfileHostProfileResponse Profile,
    bool Created);

public sealed record ProfileAccessKeyImportResult(
    string ProfileId,
    int SourceActiveKeyCount,
    IReadOnlyList<string> InsertedKeyIds,
    IReadOnlyList<string> AlreadyPresentKeyIds);

public sealed record AuthenticatedProfileAccessKey(
    ProfileHostProfileResponse Profile,
    IReadOnlyList<string> KeyIds);
