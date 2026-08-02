using System.Globalization;
using System.Runtime.InteropServices;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class ProfileHostCredentialImportContractTests
{
    [Fact]
    public void ProvisioningCommand_ParsesExplicitCredentialImport()
    {
        var command = ProfileHostProvisioningCommand.TryParse(
        [
            "profile-host",
            "import-active-credentials",
            @"C:\staging\profile-host.db",
            "a82eff20-e796-4f75-b621-58c259174c44",
            "Sapphire",
            "Avenue"
        ]);

        Assert.NotNull(command);
        Assert.Equal(ProfileHostProvisioningAction.ImportActiveCredentials, command.Action);
        Assert.Equal(@"C:\staging\profile-host.db", command.SourceDatabasePath);
        Assert.Equal("a82eff20-e796-4f75-b621-58c259174c44", command.ProfileId);
        Assert.Equal("Sapphire Avenue", command.DisplayName);
    }

    [Fact]
    public async Task ImportActiveAccessKeys_IsAdditiveIdempotentAndCredentialOnly()
    {
        await using var fixture = await CredentialDatabaseFixture.CreateAsync();
        var sourceRows = await ReadActiveKeysAsync(fixture.SourcePath, fixture.ProfileId);
        var targetStateBefore = await ReadNonCredentialStateAsync(fixture.TargetPath);
        var targetSignal = fixture.Signal.Observe(fixture.ProfileId);

        var first = await fixture.TargetStore.ImportActiveAccessKeysAsync(
            fixture.SourcePath,
            fixture.ProfileId,
            fixture.DisplayName,
            fixture.Hasher,
            CancellationToken.None);

        Assert.Equal(2, first.SourceActiveKeyCount);
        Assert.Equal(sourceRows.Select(row => row.Id), first.InsertedKeyIds);
        Assert.Empty(first.AlreadyPresentKeyIds);
        var importedRows = (await ReadActiveKeysAsync(fixture.TargetPath, fixture.ProfileId))
            .Where(row => first.InsertedKeyIds.Contains(row.Id, StringComparer.Ordinal))
            .ToArray();
        Assert.Equal(sourceRows, importedRows);
        Assert.All(importedRows, row => Assert.Null(row.LastUsedAtUtc));
        Assert.Equal(targetStateBefore, await ReadNonCredentialStateAsync(fixture.TargetPath));
        Assert.False(targetSignal.Changed.IsCompleted);

        Assert.Equal(
            fixture.ProfileId,
            (await fixture.TargetStore.AuthenticateAsync(
                fixture.SourceKeyOne.PlaintextKey,
                fixture.Hasher,
                CancellationToken.None))?.ProfileId);
        Assert.Equal(
            fixture.ProfileId,
            (await fixture.TargetStore.AuthenticateAsync(
                fixture.SourceKeyTwo.PlaintextKey,
                fixture.Hasher,
                CancellationToken.None))?.ProfileId);
        Assert.Equal(
            fixture.ProfileId,
            (await fixture.TargetStore.AuthenticateAsync(
                fixture.CanonicalKey.PlaintextKey,
                fixture.Hasher,
                CancellationToken.None))?.ProfileId);
        Assert.Null(await fixture.TargetStore.AuthenticateAsync(
            fixture.RevokedSourceKey.PlaintextKey,
            fixture.Hasher,
            CancellationToken.None));

        var second = await fixture.TargetStore.ImportActiveAccessKeysAsync(
            fixture.SourcePath,
            fixture.ProfileId,
            fixture.DisplayName,
            fixture.Hasher,
            CancellationToken.None);

        Assert.Empty(second.InsertedKeyIds);
        Assert.Equal(sourceRows.Select(row => row.Id), second.AlreadyPresentKeyIds);
        Assert.Equal(targetStateBefore, await ReadNonCredentialStateAsync(fixture.TargetPath));
        Assert.False(targetSignal.Changed.IsCompleted);
    }

    [Fact]
    public async Task ImportActiveAccessKeys_MalformedBatchWritesNothing()
    {
        await using var fixture = await CredentialDatabaseFixture.CreateAsync();
        var sourceRows = await ReadActiveKeysAsync(fixture.SourcePath, fixture.ProfileId);
        await ExecuteAsync(
            fixture.SourcePath,
            "UPDATE profile_access_keys SET key_hash = 'not-a-supported-hash' WHERE id = $id;",
            ("$id", sourceRows[1].Id));
        var targetKeysBefore = await ReadAllKeyIdsAsync(fixture.TargetPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.TargetStore.ImportActiveAccessKeysAsync(
                fixture.SourcePath,
                fixture.ProfileId,
                fixture.DisplayName,
                fixture.Hasher,
                CancellationToken.None));

        Assert.Equal(targetKeysBefore, await ReadAllKeyIdsAsync(fixture.TargetPath));
    }

    [Fact]
    public async Task ImportActiveAccessKeys_TargetKeyConflictRollsBackWholeBatch()
    {
        await using var fixture = await CredentialDatabaseFixture.CreateAsync();
        var sourceRows = await ReadActiveKeysAsync(fixture.SourcePath, fixture.ProfileId);
        var targetRows = await ReadActiveKeysAsync(fixture.TargetPath, fixture.ProfileId);
        await ExecuteAsync(
            fixture.SourcePath,
            "UPDATE profile_access_keys SET id = $targetId WHERE id = $sourceId;",
            ("$targetId", targetRows[0].Id),
            ("$sourceId", sourceRows[1].Id));
        var targetKeysBefore = await ReadAllKeyIdsAsync(fixture.TargetPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.TargetStore.ImportActiveAccessKeysAsync(
                fixture.SourcePath,
                fixture.ProfileId,
                fixture.DisplayName,
                fixture.Hasher,
                CancellationToken.None));

        Assert.Equal(targetKeysBefore, await ReadAllKeyIdsAsync(fixture.TargetPath));
    }

    [Theory]
    [InlineData("same-database")]
    [InlineData("source-disabled")]
    [InlineData("target-disabled")]
    [InlineData("display-mismatch")]
    [InlineData("profile-id-mismatch")]
    public async Task ImportActiveAccessKeys_IdentityMismatchWritesNothing(string mismatch)
    {
        await using var fixture = await CredentialDatabaseFixture.CreateAsync();
        var sourcePath = fixture.SourcePath;
        var profileId = fixture.ProfileId;
        var displayName = fixture.DisplayName;
        switch (mismatch)
        {
            case "same-database":
                sourcePath = fixture.TargetPath;
                break;
            case "source-disabled":
                await fixture.SourceStore.DisableProfileAsync(profileId, CancellationToken.None);
                break;
            case "target-disabled":
                await fixture.TargetStore.DisableProfileAsync(profileId, CancellationToken.None);
                break;
            case "display-mismatch":
                displayName += " Other";
                break;
            case "profile-id-mismatch":
                profileId = Guid.NewGuid().ToString("D");
                break;
        }

        var targetKeysBefore = await ReadAllKeyIdsAsync(fixture.TargetPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.TargetStore.ImportActiveAccessKeysAsync(
                sourcePath,
                profileId,
                displayName,
                fixture.Hasher,
                CancellationToken.None));

        Assert.Equal(targetKeysBefore, await ReadAllKeyIdsAsync(fixture.TargetPath));
    }

    [Fact]
    public async Task ImportActiveAccessKeys_SymbolicAliasOfTargetFailsClosed()
    {
        await using var fixture = await CredentialDatabaseFixture.CreateAsync();
        var aliasPath = fixture.TargetPath + ".alias";
        CreateFileAlias(aliasPath, fixture.TargetPath);
        try
        {
            var targetKeysBefore = await ReadAllKeyIdsAsync(fixture.TargetPath);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                fixture.TargetStore.ImportActiveAccessKeysAsync(
                    aliasPath,
                    fixture.ProfileId,
                    fixture.DisplayName,
                    fixture.Hasher,
                    CancellationToken.None));

            Assert.Equal(targetKeysBefore, await ReadAllKeyIdsAsync(fixture.TargetPath));
        }
        finally
        {
            File.Delete(aliasPath);
        }
    }

    private static async Task<IReadOnlyList<AccessKeyRow>> ReadActiveKeysAsync(
        string path,
        string profileId)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, key_hash, created_at_utc, last_used_at_utc
            FROM profile_access_keys
            WHERE profile_id = $profileId AND revoked_at_utc IS NULL
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        var rows = new List<AccessKeyRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AccessKeyRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    private static void CreateFileAlias(string aliasPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(aliasPath, targetPath);
            return;
        }

        if (!CreateHardLink(aliasPath, targetPath, IntPtr.Zero))
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static async Task<IReadOnlyList<string>> ReadAllKeyIdsAsync(string path)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM profile_access_keys ORDER BY id;";
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<string> ReadNonCredentialStateAsync(string path)
    {
        await using var connection = await OpenAsync(path);
        var queries = new[]
        {
            "SELECT id, display_name, created_at_utc, updated_at_utc, disabled_at_utc FROM hosted_profiles ORDER BY id;",
            "SELECT token_hash, profile_id, created_at_utc, expires_at_utc, redeemed_at_utc FROM profile_pairing_codes ORDER BY token_hash;",
            "SELECT profile_id, collection, object_id, payload_json, revision, updated_at_utc, deleted, deleted_at_utc FROM sync_objects ORDER BY profile_id, collection, object_id;",
            "SELECT profile_id, revision FROM profile_revisions ORDER BY profile_id;"
        };
        var state = new List<string>();
        foreach (var query in queries)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                state.Add(string.Join(
                    "|",
                    Enumerable.Range(0, reader.FieldCount)
                        .Select(index => reader.IsDBNull(index)
                            ? "<null>"
                            : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
            }
        }

        return string.Join("\n", state);
    }

    private static async Task ExecuteAsync(
        string path,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private sealed record AccessKeyRow(
        string Id,
        string StoredHash,
        string CreatedAtUtc,
        string? LastUsedAtUtc);

    private sealed class CredentialDatabaseFixture : IAsyncDisposable
    {
        private CredentialDatabaseFixture(
            string sourcePath,
            string targetPath,
            string profileId,
            ProfileAccessKeyHasher hasher,
            ProfileHostChangeSignal signal,
            SqliteProfileHostStore sourceStore,
            SqliteProfileHostStore targetStore,
            CreatedProfileAccessKey sourceKeyOne,
            CreatedProfileAccessKey sourceKeyTwo,
            CreatedProfileAccessKey revokedSourceKey,
            CreatedProfileAccessKey canonicalKey)
        {
            SourcePath = sourcePath;
            TargetPath = targetPath;
            ProfileId = profileId;
            Hasher = hasher;
            Signal = signal;
            SourceStore = sourceStore;
            TargetStore = targetStore;
            SourceKeyOne = sourceKeyOne;
            SourceKeyTwo = sourceKeyTwo;
            RevokedSourceKey = revokedSourceKey;
            CanonicalKey = canonicalKey;
        }

        public string SourcePath { get; }
        public string TargetPath { get; }
        public string ProfileId { get; }
        public string DisplayName => "Sapphire Avenue";
        public ProfileAccessKeyHasher Hasher { get; }
        public ProfileHostChangeSignal Signal { get; }
        public SqliteProfileHostStore SourceStore { get; }
        public SqliteProfileHostStore TargetStore { get; }
        public CreatedProfileAccessKey SourceKeyOne { get; }
        public CreatedProfileAccessKey SourceKeyTwo { get; }
        public CreatedProfileAccessKey RevokedSourceKey { get; }
        public CreatedProfileAccessKey CanonicalKey { get; }

        public static async Task<CredentialDatabaseFixture> CreateAsync()
        {
            var sourcePath = Path.Combine(Path.GetTempPath(), $"ca-import-source-{Guid.NewGuid():N}.db");
            var targetPath = Path.Combine(Path.GetTempPath(), $"ca-import-target-{Guid.NewGuid():N}.db");
            var profileId = Guid.NewGuid().ToString("D");
            var hasher = new ProfileAccessKeyHasher();
            var signal = new ProfileHostChangeSignal();
            var sourceStore = new SqliteProfileHostStore(
                new ProfileHostOptions { DatabasePath = sourcePath });
            var targetStore = new SqliteProfileHostStore(
                new ProfileHostOptions { DatabasePath = targetPath },
                signal);
            await sourceStore.EnsureProfileAsync(
                profileId,
                "Sapphire Avenue",
                "cap_source-bootstrap",
                hasher,
                CancellationToken.None);
            var revokedSourceKey = hasher.CreateAccessKey();
            await sourceStore.AddAccessKeyAsync(
                profileId,
                revokedSourceKey.StoredHash,
                CancellationToken.None);
            await sourceStore.RevokeAccessKeysAsync(profileId, CancellationToken.None);
            var sourceKeyOne = hasher.CreateAccessKey();
            var sourceKeyTwo = hasher.CreateAccessKey();
            await sourceStore.AddAccessKeyAsync(profileId, sourceKeyOne.StoredHash, CancellationToken.None);
            await sourceStore.AddAccessKeyAsync(profileId, sourceKeyTwo.StoredHash, CancellationToken.None);

            var canonicalKey = hasher.CreateAccessKey();
            await targetStore.EnsureProfileAsync(
                profileId,
                "Sapphire Avenue",
                canonicalKey.PlaintextKey,
                hasher,
                CancellationToken.None);
            await targetStore.CreatePairingCodeAsync(
                profileId,
                "target-pairing-code",
                DateTime.UtcNow.AddHours(1),
                CancellationToken.None);
            await targetStore.PutObjectAsync(
                profileId,
                ProfileSyncCollections.Settings,
                "credential-import-sentinel",
                "{\"preserved\":true}",
                0,
                CancellationToken.None);

            return new CredentialDatabaseFixture(
                sourcePath,
                targetPath,
                profileId,
                hasher,
                signal,
                sourceStore,
                targetStore,
                sourceKeyOne,
                sourceKeyTwo,
                revokedSourceKey,
                canonicalKey);
        }

        public ValueTask DisposeAsync()
        {
            DeleteDatabase(SourcePath);
            DeleteDatabase(TargetPath);
            return ValueTask.CompletedTask;
        }

        private static void DeleteDatabase(string path)
        {
            foreach (var candidate in new[] { path, path + "-shm", path + "-wal" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
    }
}
