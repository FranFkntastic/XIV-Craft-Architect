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

    public SqliteCommissionBriefStore(CommissionBriefOptions options)
    {
        _options = options;
    }

    public async Task<(PublishedCommissionBrief Published, string EditorToken)> CreateAsync(
        CommissionBriefDocument brief,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);

        var publicId = CreateToken(12);
        var editorToken = CreateToken(32);
        var publishedAt = DateTime.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO commission_briefs
                (public_id, editor_token_hash, version, payload_json, published_at_utc)
            VALUES
                ($publicId, $editorTokenHash, 1, $payloadJson, $publishedAtUtc);
            """;
        command.Parameters.AddWithValue("$publicId", publicId);
        command.Parameters.AddWithValue("$editorTokenHash", HashToken(editorToken));
        command.Parameters.AddWithValue("$payloadJson", JsonSerializer.Serialize(brief, JsonOptions));
        command.Parameters.AddWithValue("$publishedAtUtc", publishedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);

        return (
            new PublishedCommissionBrief
            {
                PublicId = publicId,
                Version = 1,
                PublishedAtUtc = publishedAt,
                Brief = brief
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
            SELECT version, payload_json, published_at_utc
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

        return new PublishedCommissionBrief
        {
            PublicId = publicId,
            Version = reader.GetInt32(0),
            PublishedAtUtc = DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            Brief = brief
        };
    }

    public async Task<bool> RevokeAsync(string publicId, string editorToken, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureSchemaAsync(connection, ct);
        await using var read = connection.CreateCommand();
        read.CommandText =
            "SELECT editor_token_hash FROM commission_briefs WHERE public_id = $publicId AND revoked_at_utc IS NULL;";
        read.Parameters.AddWithValue("$publicId", publicId);
        var storedHash = await read.ExecuteScalarAsync(ct) as string;
        if (storedHash == null || !TokenMatches(editorToken, storedHash))
        {
            return false;
        }

        await using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE commission_briefs SET revoked_at_utc = $revokedAtUtc WHERE public_id = $publicId AND revoked_at_utc IS NULL;";
        update.Parameters.AddWithValue("$revokedAtUtc", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$publicId", publicId);
        return await update.ExecuteNonQueryAsync(ct) == 1;
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

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS commission_briefs (
                public_id TEXT PRIMARY KEY,
                editor_token_hash TEXT NOT NULL,
                version INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                published_at_utc TEXT NOT NULL,
                revoked_at_utc TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string CreateToken(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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
