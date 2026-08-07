using System.Data;
using System.Globalization;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public enum MembershipRole
{
    Owner,
    Operator,
    Crafter
}

public enum MembershipState
{
    Pending,
    Active,
    Denied,
    Revoked
}

public enum MembershipMutationStatus
{
    Applied,
    Replayed,
    NotFound,
    InvalidState,
    LastOwner
}

public sealed record CompanyMembership(
    CompanyId CompanyId,
    Guid AccountProfileId,
    MembershipRole Role,
    MembershipState State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    Guid? DecidedByProfileId,
    string? RequestNote);

public sealed record MembershipEvent(
    Guid EventId,
    CompanyId CompanyId,
    Guid AccountProfileId,
    MembershipState? FromState,
    MembershipState ToState,
    Guid ActorProfileId,
    DateTimeOffset CreatedAtUtc);

public sealed record MembershipMutationResult(
    MembershipMutationStatus Status,
    CompanyMembership? Membership = null);

public interface ITradeCompanyFounderBinder
{
    Task BindFounderAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default);
}

public sealed class SqliteMembershipStore(
    TradeMembershipOptions options,
    TimeProvider timeProvider) : ITradeCompanyFounderBinder
{
    public const int MaximumRequestNoteLength = 500;
    private readonly SemaphoreSlim schemaGate = new(1, 1);
    private bool schemaReady;

    public async Task BindFounderAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default) =>
        _ = await EnsureFounderAsync(companyId, accountProfileId, cancellationToken);

    public async Task<CompanyMembership> EnsureFounderAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, accountProfileId);
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var existing = await LoadAsync(
            connection,
            transaction,
            companyId,
            accountProfileId,
            cancellationToken);
        if (existing is { Role: MembershipRole.Owner, State: MembershipState.Active })
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var now = timeProvider.GetUtcNow();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO company_memberships (
                    company_id, account_profile_id, role, state, requested_at_utc,
                    decided_at_utc, decided_by_profile_id, request_note)
                VALUES (
                    $companyId, $accountProfileId, 'owner', 'active', $requestedAtUtc,
                    $decidedAtUtc, $decidedByProfileId, NULL)
                ON CONFLICT(company_id, account_profile_id) DO UPDATE SET
                    role = 'owner',
                    state = 'active',
                    decided_at_utc = excluded.decided_at_utc,
                    decided_by_profile_id = excluded.decided_by_profile_id,
                    request_note = NULL;
                """;
            command.Parameters.AddWithValue("$companyId", companyId.ToString());
            command.Parameters.AddWithValue("$accountProfileId", accountProfileId.ToString("D"));
            command.Parameters.AddWithValue("$requestedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$decidedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$decidedByProfileId", accountProfileId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertEventAsync(
            connection,
            transaction,
            companyId,
            accountProfileId,
            existing?.State,
            MembershipState.Active,
            accountProfileId,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await LoadAsync(companyId, accountProfileId, cancellationToken))!;
    }

    public async Task<MembershipMutationResult> RequestAsync(
        CompanyId companyId,
        Guid accountProfileId,
        string? requestNote,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, accountProfileId);
        requestNote = NormalizeNote(requestNote);
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var existing = await LoadAsync(
            connection,
            transaction,
            companyId,
            accountProfileId,
            cancellationToken);
        if (existing is { State: MembershipState.Pending or MembershipState.Active })
        {
            await transaction.CommitAsync(cancellationToken);
            return new MembershipMutationResult(MembershipMutationStatus.Replayed, existing);
        }

        var now = timeProvider.GetUtcNow();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO company_memberships (
                    company_id, account_profile_id, role, state, requested_at_utc,
                    decided_at_utc, decided_by_profile_id, request_note)
                VALUES (
                    $companyId, $accountProfileId, 'crafter', 'pending', $requestedAtUtc,
                    NULL, NULL, $requestNote)
                ON CONFLICT(company_id, account_profile_id) DO UPDATE SET
                    role = 'crafter',
                    state = 'pending',
                    requested_at_utc = excluded.requested_at_utc,
                    decided_at_utc = NULL,
                    decided_by_profile_id = NULL,
                    request_note = excluded.request_note;
                """;
            AddMembershipIdentity(command, companyId, accountProfileId);
            command.Parameters.AddWithValue("$requestedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$requestNote", (object?)requestNote ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertEventAsync(
            connection,
            transaction,
            companyId,
            accountProfileId,
            existing?.State,
            MembershipState.Pending,
            accountProfileId,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MembershipMutationResult(
            MembershipMutationStatus.Applied,
            await LoadAsync(companyId, accountProfileId, cancellationToken));
    }

    public Task<MembershipMutationResult> ApproveAsync(
        CompanyId companyId,
        Guid accountProfileId,
        Guid actorProfileId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            companyId,
            accountProfileId,
            actorProfileId,
            MembershipState.Pending,
            MembershipState.Active,
            cancellationToken);

    public Task<MembershipMutationResult> DenyAsync(
        CompanyId companyId,
        Guid accountProfileId,
        Guid actorProfileId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            companyId,
            accountProfileId,
            actorProfileId,
            MembershipState.Pending,
            MembershipState.Denied,
            cancellationToken);

    public Task<MembershipMutationResult> RevokeAsync(
        CompanyId companyId,
        Guid accountProfileId,
        Guid actorProfileId,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            companyId,
            accountProfileId,
            actorProfileId,
            MembershipState.Active,
            MembershipState.Revoked,
            cancellationToken);

    public async Task<CompanyMembership?> LoadAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, accountProfileId);
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        return await LoadAsync(
            connection,
            null,
            companyId,
            accountProfileId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<CompanyMembership>> LoadPendingAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        await LoadManyAsync(
            "company_id = $identity AND state = 'pending'",
            companyId.ToString(),
            cancellationToken);

    public async Task<IReadOnlyList<CompanyMembership>> LoadCurrentForAccountAsync(
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        if (accountProfileId == Guid.Empty)
        {
            throw new ArgumentException("A valid account profile identity is required.");
        }

        return await LoadManyAsync(
            "account_profile_id = $identity AND state IN ('pending', 'active')",
            accountProfileId.ToString("D"),
            cancellationToken);
    }

    public async Task<IReadOnlyList<MembershipEvent>> LoadEventsAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, accountProfileId);
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, company_id, account_profile_id, from_state, to_state,
                   actor_profile_id, created_at_utc
            FROM membership_events
            WHERE company_id = $companyId AND account_profile_id = $accountProfileId
            ORDER BY created_at_utc, event_id;
            """;
        AddMembershipIdentity(command, companyId, accountProfileId);
        var events = new List<MembershipEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new MembershipEvent(
                Guid.Parse(reader.GetString(0)),
                ParseCompanyId(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseState(reader.GetString(3)),
                ParseState(reader.GetString(4)),
                Guid.Parse(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }
        return events;
    }

    private async Task<MembershipMutationResult> TransitionAsync(
        CompanyId companyId,
        Guid accountProfileId,
        Guid actorProfileId,
        MembershipState expectedState,
        MembershipState targetState,
        CancellationToken cancellationToken)
    {
        RequireIdentity(companyId, accountProfileId);
        if (actorProfileId == Guid.Empty)
        {
            throw new ArgumentException("A valid actor profile identity is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var current = await LoadAsync(
            connection,
            transaction,
            companyId,
            accountProfileId,
            cancellationToken);
        if (current == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MembershipMutationResult(MembershipMutationStatus.NotFound);
        }
        if (current.State != expectedState)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MembershipMutationResult(MembershipMutationStatus.InvalidState, current);
        }
        if (targetState == MembershipState.Revoked &&
            current.Role == MembershipRole.Owner &&
            await CountActiveOwnersAsync(connection, transaction, companyId, cancellationToken) <= 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MembershipMutationResult(MembershipMutationStatus.LastOwner, current);
        }

        var now = timeProvider.GetUtcNow();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE company_memberships
                SET state = $state,
                    decided_at_utc = $decidedAtUtc,
                    decided_by_profile_id = $actorProfileId
                WHERE company_id = $companyId
                  AND account_profile_id = $accountProfileId
                  AND state = $expectedState;
                """;
            AddMembershipIdentity(command, companyId, accountProfileId);
            command.Parameters.AddWithValue("$state", ToStorage(targetState));
            command.Parameters.AddWithValue("$expectedState", ToStorage(expectedState));
            command.Parameters.AddWithValue("$decidedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$actorProfileId", actorProfileId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new MembershipMutationResult(MembershipMutationStatus.InvalidState, current);
            }
        }
        await InsertEventAsync(
            connection,
            transaction,
            companyId,
            accountProfileId,
            current.State,
            targetState,
            actorProfileId,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MembershipMutationResult(
            MembershipMutationStatus.Applied,
            await LoadAsync(companyId, accountProfileId, cancellationToken));
    }

    private async Task<IReadOnlyList<CompanyMembership>> LoadManyAsync(
        string predicate,
        string identity,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT company_id, account_profile_id, role, state, requested_at_utc,
                   decided_at_utc, decided_by_profile_id, request_note
            FROM company_memberships
            WHERE {predicate}
            ORDER BY requested_at_utc, company_id, account_profile_id;
            """;
        command.Parameters.AddWithValue("$identity", identity);
        var memberships = new List<CompanyMembership>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            memberships.Add(ReadMembership(reader));
        }
        return memberships;
    }

    private static async Task<CompanyMembership?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT company_id, account_profile_id, role, state, requested_at_utc,
                   decided_at_utc, decided_by_profile_id, request_note
            FROM company_memberships
            WHERE company_id = $companyId AND account_profile_id = $accountProfileId;
            """;
        AddMembershipIdentity(command, companyId, accountProfileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMembership(reader) : null;
    }

    private static async Task<long> CountActiveOwnersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM company_memberships
            WHERE company_id = $companyId AND role = 'owner' AND state = 'active';
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        Guid accountProfileId,
        MembershipState? fromState,
        MembershipState toState,
        Guid actorProfileId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO membership_events (
                event_id, company_id, account_profile_id, from_state, to_state,
                actor_profile_id, created_at_utc)
            VALUES (
                $eventId, $companyId, $accountProfileId, $fromState, $toState,
                $actorProfileId, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        AddMembershipIdentity(command, companyId, accountProfileId);
        command.Parameters.AddWithValue(
            "$fromState",
            fromState.HasValue ? ToStorage(fromState.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$toState", ToStorage(toState));
        command.Parameters.AddWithValue("$actorProfileId", actorProfileId.ToString("D"));
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (schemaReady)
        {
            return;
        }

        await schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (schemaReady)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS company_memberships (
                    company_id TEXT NOT NULL,
                    account_profile_id TEXT NOT NULL,
                    role TEXT NOT NULL CHECK(role IN ('owner', 'operator', 'crafter')),
                    state TEXT NOT NULL CHECK(state IN ('pending', 'active', 'denied', 'revoked')),
                    requested_at_utc TEXT NOT NULL,
                    decided_at_utc TEXT NULL,
                    decided_by_profile_id TEXT NULL,
                    request_note TEXT NULL CHECK(request_note IS NULL OR length(request_note) <= 500),
                    PRIMARY KEY(company_id, account_profile_id),
                    CHECK(role <> 'owner' OR state IN ('active', 'revoked'))
                );

                CREATE TABLE IF NOT EXISTS membership_events (
                    event_id TEXT PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    account_profile_id TEXT NOT NULL,
                    from_state TEXT NULL CHECK(from_state IS NULL OR from_state IN ('pending', 'active', 'denied', 'revoked')),
                    to_state TEXT NOT NULL CHECK(to_state IN ('pending', 'active', 'denied', 'revoked')),
                    actor_profile_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_company_memberships_account_state
                    ON company_memberships(account_profile_id, state);
                CREATE INDEX IF NOT EXISTS ix_membership_events_company_account
                    ON membership_events(company_id, account_profile_id, created_at_utc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            schemaReady = true;
        }
        finally
        {
            schemaGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(options.DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static CompanyMembership ReadMembership(SqliteDataReader reader) =>
        new(
            ParseCompanyId(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            ParseRole(reader.GetString(2)),
            ParseState(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            reader.IsDBNull(5)
                ? null
                : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7));

    private static void AddMembershipIdentity(
        SqliteCommand command,
        CompanyId companyId,
        Guid accountProfileId)
    {
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$accountProfileId", accountProfileId.ToString("D"));
    }

    private static void RequireIdentity(CompanyId companyId, Guid accountProfileId)
    {
        if (companyId.Value == Guid.Empty || accountProfileId == Guid.Empty)
        {
            throw new ArgumentException("Valid company and account profile identities are required.");
        }
    }

    private static string? NormalizeNote(string? note)
    {
        note = note?.Trim();
        if (string.IsNullOrEmpty(note))
        {
            return null;
        }
        if (note.Length > MaximumRequestNoteLength)
        {
            throw new ArgumentException($"Request note cannot exceed {MaximumRequestNoteLength} characters.");
        }
        return note;
    }

    private static CompanyId ParseCompanyId(string value) =>
        CompanyId.TryParse(value, out var companyId)
            ? companyId
            : throw new InvalidDataException("Stored company identity is invalid.");

    private static MembershipRole ParseRole(string value) => value switch
    {
        "owner" => MembershipRole.Owner,
        "operator" => MembershipRole.Operator,
        "crafter" => MembershipRole.Crafter,
        _ => throw new InvalidDataException("Stored membership role is invalid.")
    };

    private static MembershipState ParseState(string value) => value switch
    {
        "pending" => MembershipState.Pending,
        "active" => MembershipState.Active,
        "denied" => MembershipState.Denied,
        "revoked" => MembershipState.Revoked,
        _ => throw new InvalidDataException("Stored membership state is invalid.")
    };

    private static string ToStorage(MembershipState state) => state switch
    {
        MembershipState.Pending => "pending",
        MembershipState.Active => "active",
        MembershipState.Denied => "denied",
        MembershipState.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
