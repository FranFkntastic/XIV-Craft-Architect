using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

public sealed class SqliteCommissionBriefStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CommissionBriefOptions _options;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public SqliteCommissionBriefStore(CommissionBriefOptions options)
    {
        _options = options;
    }

    public async Task<(PublishedCommissionBrief Published, string EditorToken)> CreateAsync(
        CommissionBriefDocument brief,
        CancellationToken ct)
    {
        return await CreateAsync(brief, ownership: null, ct);
    }

    public async Task<(PublishedCommissionBrief Published, string EditorToken)> CreateAsync(
        CommissionBriefDocument brief,
        TradeCompanyPublicationOwnership? ownership,
        CancellationToken ct)
    {
        return await CreateAsync(brief, ownership, publicId: null, ct);
    }

    public async Task<PublishedCommissionBrief> CreateCompanyOwnedAsync(
        CommissionBriefDocument brief,
        TradeCompanyPublicationOwnership ownership,
        string idempotencyKey,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var publicId = CreateCompanyPublicId(ownership, idempotencyKey);
        var existing = await LoadAsync(publicId, ct);
        if (existing != null)
        {
            if (existing.Ownership != ownership ||
                !string.Equals(
                    JsonSerializer.Serialize(existing.Brief, JsonOptions),
                    JsonSerializer.Serialize(brief, JsonOptions),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The company publication idempotency key was reused for different terms.");
            }

            return existing;
        }

        try
        {
            var created = await CreateAsync(brief, ownership, publicId, ct);
            return created.Published;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            existing = await LoadAsync(publicId, ct);
            if (existing?.Ownership == ownership &&
                string.Equals(
                    JsonSerializer.Serialize(existing.Brief, JsonOptions),
                    JsonSerializer.Serialize(brief, JsonOptions),
                    StringComparison.Ordinal))
            {
                return existing;
            }

            if (existing != null)
            {
                throw new InvalidOperationException(
                    "The company publication idempotency key was reused for different terms.");
            }

            throw;
        }
    }

    private async Task<(PublishedCommissionBrief Published, string EditorToken)> CreateAsync(
        CommissionBriefDocument brief,
        TradeCompanyPublicationOwnership? ownership,
        string? publicId,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);

        publicId ??= CreateToken(12);
        var editorToken = CreateToken(32);
        var publishedAt = DateTime.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO commission_briefs
                (
                    public_id,
                    editor_token_hash,
                    version,
                    payload_json,
                    published_at_utc,
                    company_id,
                    order_id,
                    order_revision
                )
            VALUES
                (
                    $publicId,
                    $editorTokenHash,
                    1,
                    $payloadJson,
                    $publishedAtUtc,
                    $companyId,
                    $orderId,
                    $orderRevision
                );
            """;
        command.Parameters.AddWithValue("$publicId", publicId);
        command.Parameters.AddWithValue("$editorTokenHash", HashToken(editorToken));
        command.Parameters.AddWithValue("$payloadJson", JsonSerializer.Serialize(brief, JsonOptions));
        command.Parameters.AddWithValue("$publishedAtUtc", publishedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$companyId",
            ownership is null ? DBNull.Value : ownership.CompanyId.ToString());
        command.Parameters.AddWithValue(
            "$orderId",
            ownership is null ? DBNull.Value : ownership.OrderId.ToString("D"));
        command.Parameters.AddWithValue(
            "$orderRevision",
            ownership is null ? DBNull.Value : ownership.OrderRevision.Value);
        await command.ExecuteNonQueryAsync(ct);

        return (
            new PublishedCommissionBrief
            {
                PublicId = publicId,
                Version = 1,
                PublishedAtUtc = publishedAt,
                Brief = brief,
                Ownership = ownership
            },
            editorToken);
    }

    public async Task<PublishedCommissionBrief?> LoadAsync(string publicId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                version,
                payload_json,
                published_at_utc,
                company_id,
                order_id,
                order_revision
            FROM commission_briefs
            WHERE public_id = $publicId AND revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$publicId", publicId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var brief = JsonSerializer.Deserialize<CommissionBriefDocument>(reader.GetString(1), JsonOptions);
        if (brief == null)
        {
            return null;
        }

        TradeCompanyPublicationOwnership? ownership = null;
        if (!reader.IsDBNull(3) &&
            !reader.IsDBNull(4) &&
            !reader.IsDBNull(5) &&
            CompanyId.TryParse(reader.GetString(3), out var companyId) &&
            Guid.TryParse(reader.GetString(4), out var orderId))
        {
            ownership = new TradeCompanyPublicationOwnership(
                companyId,
                orderId,
                new CompanyRecordRevision(reader.GetInt64(5)));
        }

        return new PublishedCommissionBrief
        {
            PublicId = publicId,
            Version = reader.GetInt32(0),
            PublishedAtUtc = DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Brief = brief,
            Ownership = ownership
        };
    }

    public async Task<bool> RevokeAsync(string publicId, string editorToken, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT editor_token_hash FROM commission_briefs WHERE public_id = $publicId;";
        read.Parameters.AddWithValue("$publicId", publicId);
        var storedHash = await read.ExecuteScalarAsync(ct) as string;
        if (storedHash == null || !TokenMatches(editorToken, storedHash))
        {
            return false;
        }

        await using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE commission_briefs
            SET revoked_at_utc = COALESCE(revoked_at_utc, $revokedAtUtc)
            WHERE public_id = $publicId;
            """;
        update.Parameters.AddWithValue("$revokedAtUtc", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$publicId", publicId);
        return await update.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RevokeCompanyOwnedAsync(
        string publicId,
        CompanyId companyId,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT company_id FROM commission_briefs WHERE public_id = $publicId;";
        read.Parameters.AddWithValue("$publicId", publicId);
        var storedCompanyId = await read.ExecuteScalarAsync(ct) as string;
        if (!CompanyId.TryParse(storedCompanyId, out var parsedCompanyId) ||
            parsedCompanyId != companyId)
        {
            return false;
        }

        await using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE commission_briefs
            SET revoked_at_utc = COALESCE(revoked_at_utc, $revokedAtUtc)
            WHERE public_id = $publicId AND company_id = $companyId;
            """;
        update.Parameters.AddWithValue("$revokedAtUtc", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$publicId", publicId);
        update.Parameters.AddWithValue("$companyId", companyId.ToString());
        return await update.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DiscardCompanyOwnedAsync(
        string publicId,
        TradeCompanyPublicationOwnership ownership,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM commission_briefs
            WHERE public_id = $publicId
              AND company_id = $companyId
              AND order_id = $orderId
              AND order_revision = $orderRevision;
            """;
        command.Parameters.AddWithValue("$publicId", publicId);
        command.Parameters.AddWithValue("$companyId", ownership.CompanyId.ToString());
        command.Parameters.AddWithValue("$orderId", ownership.OrderId.ToString("D"));
        command.Parameters.AddWithValue("$orderRevision", ownership.OrderRevision.Value);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var absolutePath = Path.GetFullPath(_options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = absolutePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await connection.OpenAsync(ct);
        return connection;
    }

    private async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
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

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS commission_briefs (
                    public_id TEXT PRIMARY KEY,
                    editor_token_hash TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    payload_json TEXT NOT NULL,
                    published_at_utc TEXT NOT NULL,
                    revoked_at_utc TEXT NULL,
                    company_id TEXT NULL,
                    order_id TEXT NULL,
                    order_revision INTEGER NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct);

            await AddColumnIfMissingAsync(connection, "company_id", "TEXT NULL", ct);
            await AddColumnIfMissingAsync(connection, "order_id", "TEXT NULL", ct);
            await AddColumnIfMissingAsync(connection, "order_revision", "INTEGER NULL", ct);
            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string columnName,
        string definition,
        CancellationToken ct)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(commission_briefs);";
        await using var reader = await inspect.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE commission_briefs ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private static string CreateToken(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static string CreateCompanyPublicId(
        TradeCompanyPublicationOwnership ownership,
        string idempotencyKey)
    {
        var material = string.Join(
            ":",
            ownership.CompanyId,
            ownership.OrderId.ToString("D"),
            ownership.OrderRevision.Value,
            idempotencyKey);
        return Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(material))[..15])
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool TokenMatches(string token, string storedHash)
    {
        var actual = Encoding.ASCII.GetBytes(HashToken(token));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return actual.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
