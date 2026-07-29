using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class SqliteTradeCompanyStore(
    TradeCompanyOptions options,
    TradeCompanyAccessKeyHasher accessKeyHasher) : ITradeCompanyStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim migrationGate = new(1, 1);
    private volatile bool migrated;

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select coalesce(max(version), 0) from trade_company_schema_migrations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<ProvisionedTradeCompany> CreateCompanyAsync(
        string displayName,
        string ownerKeyHash,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken);
        var companyId = new CompanyId(Guid.NewGuid());
        var grantId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var companyCommand = connection.CreateCommand())
        {
            companyCommand.Transaction = (SqliteTransaction)transaction;
            companyCommand.CommandText = """
                insert into trade_companies
                    (id, display_name, current_revision, created_at_utc, updated_at_utc)
                values
                    ($id, $displayName, 0, $createdAtUtc, $updatedAtUtc);
                """;
            companyCommand.Parameters.AddWithValue("$id", companyId.ToString());
            companyCommand.Parameters.AddWithValue("$displayName", displayName);
            companyCommand.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            companyCommand.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await companyCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertGrantAsync(
            connection,
            (SqliteTransaction)transaction,
            grantId,
            companyId,
            TradeCompanyRole.Owner,
            ownerKeyHash,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ProvisionedTradeCompany(
            new TradeCompanyIdentity(companyId, displayName, CompanyRevision.None, now, now),
            new TradeCompanyGrantRecord(
                grantId,
                companyId,
                TradeCompanyRole.Owner,
                now,
                null,
                null));
    }

    public async Task<TradeCompanyIdentity?> LoadCompanyAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select display_name, current_revision, created_at_utc, updated_at_utc
            from trade_companies
            where id = $companyId and disabled_at_utc is null;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCompany(companyId, reader)
            : null;
    }

    public async Task<TradeCompanyAccessContext?> AuthenticateAsync(
        string plaintextKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select g.id, g.company_id, g.role, g.key_hash
            from trade_company_grants g
            inner join trade_companies c on c.id = g.company_id
            where g.revoked_at_utc is null and c.disabled_at_utc is null;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var storedHash = reader.GetString(3);
            if (!accessKeyHasher.Verify(plaintextKey, storedHash))
            {
                continue;
            }

            var grantId = Guid.Parse(reader.GetString(0));
            var companyId = CompanyId.Parse(reader.GetString(1));
            var role = ParseRole(reader.GetString(2));
            await reader.DisposeAsync();
            await TouchGrantAsync(connection, grantId, cancellationToken);
            return new TradeCompanyAccessContext(companyId, grantId, role);
        }

        return null;
    }

    public async Task<IReadOnlyList<TradeCompanyGrantRecord>> LoadGrantsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id, role, created_at_utc, last_used_at_utc, revoked_at_utc
            from trade_company_grants
            where company_id = $companyId
            order by created_at_utc asc;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());

        var grants = new List<TradeCompanyGrantRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            grants.Add(new TradeCompanyGrantRecord(
                Guid.Parse(reader.GetString(0)),
                companyId,
                ParseRole(reader.GetString(1)),
                ParseTimestamp(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseTimestamp(reader.GetString(3)),
                reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4))));
        }

        return grants;
    }

    public async Task<TradeCompanyGrantRecord> CreateGrantAsync(
        CompanyId companyId,
        TradeCompanyRole role,
        string storedKeyHash,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken);
        var grantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertGrantAsync(
            connection,
            (SqliteTransaction)transaction,
            grantId,
            companyId,
            role,
            storedKeyHash,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TradeCompanyGrantRecord(grantId, companyId, role, now, null, null);
    }

    public async Task<TradeCompanyGrantRevokeStatus> RevokeGrantAsync(
        CompanyId companyId,
        Guid grantId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        TradeCompanyRole? targetRole;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = (SqliteTransaction)transaction;
            read.CommandText = """
                select role
                from trade_company_grants
                where id = $grantId and company_id = $companyId and revoked_at_utc is null;
                """;
            read.Parameters.AddWithValue("$grantId", grantId.ToString("D"));
            read.Parameters.AddWithValue("$companyId", companyId.ToString());
            var scalar = await read.ExecuteScalarAsync(cancellationToken) as string;
            targetRole = scalar == null ? null : ParseRole(scalar);
        }

        if (targetRole == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TradeCompanyGrantRevokeStatus.NotFound;
        }

        if (targetRole == TradeCompanyRole.Owner)
        {
            await using var count = connection.CreateCommand();
            count.Transaction = (SqliteTransaction)transaction;
            count.CommandText = """
                select count(*)
                from trade_company_grants
                where company_id = $companyId and role = 'Owner' and revoked_at_utc is null;
                """;
            count.Parameters.AddWithValue("$companyId", companyId.ToString());
            var owners = Convert.ToInt32(
                await count.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (owners <= 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return TradeCompanyGrantRevokeStatus.LastOwner;
            }
        }

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText = """
            update trade_company_grants
            set revoked_at_utc = $revokedAtUtc
            where id = $grantId and company_id = $companyId and revoked_at_utc is null;
            """;
        update.Parameters.AddWithValue("$grantId", grantId.ToString("D"));
        update.Parameters.AddWithValue("$companyId", companyId.ToString());
        update.Parameters.AddWithValue("$revokedAtUtc", DateTime.UtcNow.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TradeCompanyGrantRevokeStatus.Revoked;
    }

    public async Task<TradeCompanyChangeSet> LoadChangesAsync(
        CompanyId companyId,
        CompanyRevision afterRevision,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var company = await LoadCompanyAsync(connection, companyId, cancellationToken);
        if (company == null)
        {
            return new TradeCompanyChangeSet(companyId, CompanyRevision.None, []);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            select record_kind, record_id, payload_json, record_revision, company_revision,
                   updated_at_utc, deleted, deleted_at_utc
            from trade_company_records
            where company_id = $companyId and company_revision > $afterRevision
            order by company_revision asc;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$afterRevision", afterRevision.Value);

        var records = new List<TradeCompanyRecordEnvelope>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadRecord(companyId, reader));
        }

        return new TradeCompanyChangeSet(companyId, company.Revision, records);
    }

    public async Task<TradeCompanyMutationResult> ApplyMutationAsync(
        TradeCompanyMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken);
        var requestHash = HashRequest(request);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var replay = await LoadMutationAsync(
            connection,
            (SqliteTransaction)transaction,
            request.CompanyId,
            request.IdempotencyKey,
            cancellationToken);
        if (replay != null)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(replay.Value.RequestHash),
                    Encoding.ASCII.GetBytes(requestHash)))
            {
                return new TradeCompanyMutationResult(
                    TradeCompanyMutationStatus.Rejected,
                    null,
                    ErrorCode: "idempotency_key_reused",
                    ErrorMessage: "The idempotency key was already used for a different mutation.");
            }

            return replay.Value.Result with { Status = TradeCompanyMutationStatus.Replayed };
        }

        var company = await LoadCompanyAsync(
            connection,
            request.CompanyId,
            cancellationToken,
            (SqliteTransaction)transaction);
        if (company == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new TradeCompanyMutationResult(
                TradeCompanyMutationStatus.Rejected,
                null,
                ErrorCode: "company_not_found",
                ErrorMessage: "The company does not exist.");
        }

        var current = await LoadRecordAsync(
            connection,
            (SqliteTransaction)transaction,
            request.CompanyId,
            request.RecordKind,
            request.RecordId,
            cancellationToken);
        if (company.Revision != request.ExpectedCompanyRevision ||
            (current?.RecordRevision ?? CompanyRecordRevision.None) != request.ExpectedRecordRevision)
        {
            var conflict = new TradeCompanyMutationResult(
                TradeCompanyMutationStatus.Conflict,
                null,
                current,
                "revision_conflict",
                "The company or record revision changed.");
            await SaveMutationAsync(
                connection,
                (SqliteTransaction)transaction,
                request,
                requestHash,
                conflict,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return conflict;
        }

        var nextCompanyRevision = company.Revision.Next();
        var nextRecordRevision = new CompanyRecordRevision(
            (current?.RecordRevision.Value ?? 0) + 1);
        var now = DateTime.UtcNow;

        await using (var recordCommand = connection.CreateCommand())
        {
            recordCommand.Transaction = (SqliteTransaction)transaction;
            recordCommand.CommandText = """
                insert into trade_company_records
                    (company_id, record_kind, record_id, payload_json, record_revision,
                     company_revision, updated_at_utc, deleted, deleted_at_utc)
                values
                    ($companyId, $recordKind, $recordId, $payloadJson, $recordRevision,
                     $companyRevision, $updatedAtUtc, 0, null)
                on conflict(company_id, record_kind, record_id) do update set
                    payload_json = excluded.payload_json,
                    record_revision = excluded.record_revision,
                    company_revision = excluded.company_revision,
                    updated_at_utc = excluded.updated_at_utc,
                    deleted = 0,
                    deleted_at_utc = null;
                """;
            recordCommand.Parameters.AddWithValue("$companyId", request.CompanyId.ToString());
            recordCommand.Parameters.AddWithValue("$recordKind", request.RecordKind);
            recordCommand.Parameters.AddWithValue("$recordId", request.RecordId);
            recordCommand.Parameters.AddWithValue("$payloadJson", request.PayloadJson);
            recordCommand.Parameters.AddWithValue("$recordRevision", nextRecordRevision.Value);
            recordCommand.Parameters.AddWithValue("$companyRevision", nextCompanyRevision.Value);
            recordCommand.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await recordCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var companyCommand = connection.CreateCommand())
        {
            companyCommand.Transaction = (SqliteTransaction)transaction;
            companyCommand.CommandText = """
                update trade_companies
                set current_revision = $companyRevision, updated_at_utc = $updatedAtUtc
                where id = $companyId and current_revision = $expectedCompanyRevision;
                """;
            companyCommand.Parameters.AddWithValue("$companyId", request.CompanyId.ToString());
            companyCommand.Parameters.AddWithValue("$companyRevision", nextCompanyRevision.Value);
            companyCommand.Parameters.AddWithValue("$expectedCompanyRevision", company.Revision.Value);
            companyCommand.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            if (await companyCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new TradeCompanyMutationResult(
                    TradeCompanyMutationStatus.Conflict,
                    null,
                    current,
                    "revision_conflict",
                    "The company revision changed while applying the mutation.");
            }
        }

        var record = new TradeCompanyRecordEnvelope(
            request.CompanyId,
            request.RecordKind,
            request.RecordId,
            request.PayloadJson,
            nextRecordRevision,
            nextCompanyRevision,
            now);
        var applied = new TradeCompanyMutationResult(TradeCompanyMutationStatus.Applied, record);
        await SaveMutationAsync(
            connection,
            (SqliteTransaction)transaction,
            request,
            requestHash,
            applied,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return applied;
    }

    public async Task<TradeCompanyPublicationOwnership?> LoadPublicationOwnershipAsync(
        string publicId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select company_id, payload_json
            from trade_company_records
            where record_kind = $recordKind and record_id = $recordId and deleted = 0;
            """;
        command.Parameters.AddWithValue("$recordKind", TradeCompanyRecordKinds.Publication);
        command.Parameters.AddWithValue("$recordId", publicId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedCompanyId = CompanyId.Parse(reader.GetString(0));
        TradeCompanyPublicationOwnership? ownership;
        try
        {
            ownership = JsonSerializer.Deserialize<TradeCompanyPublicationOwnership>(
                reader.GetString(1),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        return ownership is
        {
            OrderId: var orderId,
            OrderRevision.Value: > 0
        } &&
            orderId != Guid.Empty &&
            ownership.CompanyId == storedCompanyId
                ? ownership
                : null;
    }

    private async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        if (migrated)
        {
            return;
        }

        await migrationGate.WaitAsync(cancellationToken);
        try
        {
            if (migrated)
            {
                return;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));
            Directory.CreateDirectory(directory!);
            await using var connection = await OpenAsync(cancellationToken);
            await using (var migrationTable = connection.CreateCommand())
            {
                migrationTable.CommandText = """
                    create table if not exists trade_company_schema_migrations (
                        version integer primary key,
                        applied_at_utc text not null
                    );
                    """;
                await migrationTable.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText =
                "select coalesce(max(version), 0) from trade_company_schema_migrations;";
            var currentVersion = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (currentVersion > CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Trade Company database schema {currentVersion} is newer than supported schema {CurrentSchemaVersion}.");
            }

            if (currentVersion == 0)
            {
                await ApplyVersionOneAsync(connection, cancellationToken);
                currentVersion = 1;
            }

            if (currentVersion != CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Trade Company database schema {currentVersion} is not supported.");
            }

            migrated = true;
        }
        finally
        {
            migrationGate.Release();
        }
    }

    private static async Task ApplyVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            create table trade_companies (
                id text primary key,
                display_name text not null,
                current_revision integer not null,
                created_at_utc text not null,
                updated_at_utc text not null,
                disabled_at_utc text null
            );

            create table trade_company_grants (
                id text primary key,
                company_id text not null,
                role text not null,
                key_hash text not null,
                created_at_utc text not null,
                last_used_at_utc text null,
                revoked_at_utc text null,
                foreign key(company_id) references trade_companies(id)
            );

            create index ix_trade_company_grants_company
                on trade_company_grants(company_id);

            create table trade_company_records (
                company_id text not null,
                record_kind text not null,
                record_id text not null,
                payload_json text not null,
                record_revision integer not null,
                company_revision integer not null,
                updated_at_utc text not null,
                deleted integer not null,
                deleted_at_utc text null,
                primary key(company_id, record_kind, record_id),
                foreign key(company_id) references trade_companies(id)
            );

            create index ix_trade_company_records_changes
                on trade_company_records(company_id, company_revision);

            create unique index ux_trade_company_publication
                on trade_company_records(record_id)
                where record_kind = 'publication' and deleted = 0;

            create table trade_company_mutations (
                company_id text not null,
                idempotency_key text not null,
                request_hash text not null,
                result_json text not null,
                created_at_utc text not null,
                primary key(company_id, idempotency_key),
                foreign key(company_id) references trade_companies(id)
            );

            insert into trade_company_schema_migrations(version, applied_at_utc)
            values (1, $appliedAtUtc);
            """;
        command.Parameters.AddWithValue("$appliedAtUtc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(options.DatabasePath),
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            pragma foreign_keys = on;
            pragma busy_timeout = 5000;
            """;
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task InsertGrantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid grantId,
        CompanyId companyId,
        TradeCompanyRole role,
        string storedKeyHash,
        DateTime createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into trade_company_grants
                (id, company_id, role, key_hash, created_at_utc)
            values
                ($id, $companyId, $role, $keyHash, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$id", grantId.ToString("D"));
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$role", role.ToString());
        command.Parameters.AddWithValue("$keyHash", storedKeyHash);
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TouchGrantAsync(
        SqliteConnection connection,
        Guid grantId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            update trade_company_grants
            set last_used_at_utc = $lastUsedAtUtc
            where id = $grantId and revoked_at_utc is null;
            """;
        command.Parameters.AddWithValue("$grantId", grantId.ToString("D"));
        command.Parameters.AddWithValue("$lastUsedAtUtc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<TradeCompanyIdentity?> LoadCompanyAsync(
        SqliteConnection connection,
        CompanyId companyId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select display_name, current_revision, created_at_utc, updated_at_utc
            from trade_companies
            where id = $companyId and disabled_at_utc is null;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCompany(companyId, reader)
            : null;
    }

    private static TradeCompanyIdentity ReadCompany(CompanyId companyId, SqliteDataReader reader) =>
        new(
            companyId,
            reader.GetString(0),
            new CompanyRevision(reader.GetInt64(1)),
            ParseTimestamp(reader.GetString(2)),
            ParseTimestamp(reader.GetString(3)));

    private static async Task<TradeCompanyRecordEnvelope?> LoadRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        string recordKind,
        string recordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select record_kind, record_id, payload_json, record_revision, company_revision,
                   updated_at_utc, deleted, deleted_at_utc
            from trade_company_records
            where company_id = $companyId and record_kind = $recordKind and record_id = $recordId;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$recordKind", recordKind);
        command.Parameters.AddWithValue("$recordId", recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadRecord(companyId, reader)
            : null;
    }

    private static TradeCompanyRecordEnvelope ReadRecord(CompanyId companyId, SqliteDataReader reader) =>
        new(
            companyId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            new CompanyRecordRevision(reader.GetInt64(3)),
            new CompanyRevision(reader.GetInt64(4)),
            ParseTimestamp(reader.GetString(5)),
            reader.GetInt64(6) == 1,
            reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)));

    private static async Task<(string RequestHash, TradeCompanyMutationResult Result)?> LoadMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select request_hash, result_json
            from trade_company_mutations
            where company_id = $companyId and idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var result = JsonSerializer.Deserialize<TradeCompanyMutationResult>(reader.GetString(1), JsonOptions)
            ?? throw new InvalidOperationException("Stored Trade Company mutation result is invalid.");
        return (reader.GetString(0), result);
    }

    private static async Task SaveMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TradeCompanyMutationRequest request,
        string requestHash,
        TradeCompanyMutationResult result,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into trade_company_mutations
                (company_id, idempotency_key, request_hash, result_json, created_at_utc)
            values
                ($companyId, $idempotencyKey, $requestHash, $resultJson, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$companyId", request.CompanyId.ToString());
        command.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$resultJson", JsonSerializer.Serialize(result, JsonOptions));
        command.Parameters.AddWithValue("$createdAtUtc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string HashRequest(TradeCompanyMutationRequest request) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions))));

    private static TradeCompanyRole ParseRole(string value)
    {
        return Enum.TryParse<TradeCompanyRole>(value, ignoreCase: false, out var role) &&
            Enum.IsDefined(role)
                ? role
                : throw new InvalidOperationException($"Stored Trade Company role '{value}' is invalid.");
    }

    private static DateTime ParseTimestamp(string value) =>
        DateTime.Parse(value, null, DateTimeStyles.RoundtripKind);
}
