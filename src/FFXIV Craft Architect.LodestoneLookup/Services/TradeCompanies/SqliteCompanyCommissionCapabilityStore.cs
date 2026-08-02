using System.Data;
using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public enum CompanyCommissionCapabilityKind
{
    Claim,
    Participant,
    Recovery
}

public sealed record CompanyCommissionCapabilityResolution(
    CompanyId CompanyId,
    Guid CommissionId,
    string PublicBriefId,
    CompanyCommissionCapabilityKind Kind,
    Guid? GrantId,
    long CapabilityRevision);

public sealed record IssuedCompanyCommissionCapability(
    CompanyCommissionCapabilityResolution Resolution,
    string PlaintextToken);

public sealed class SqliteCompanyCommissionCapabilityStore(CommissionBriefOptions options)
{
    public const int MaximumCapabilityLength = 512;
    public const int MaximumPublicBriefIdLength = 128;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public async Task<IssuedCompanyCommissionCapability> IssueAsync(
        CompanyId companyId,
        Guid commissionId,
        string publicBriefId,
        CompanyCommissionCapabilityKind kind,
        Guid? grantId,
        long capabilityRevision,
        DateTime issuedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (commissionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(publicBriefId) ||
            publicBriefId.Length > MaximumPublicBriefIdLength ||
            capabilityRevision <= 0 ||
            (kind is CompanyCommissionCapabilityKind.Participant or
                CompanyCommissionCapabilityKind.Recovery) &&
            (!grantId.HasValue || grantId.Value == Guid.Empty) ||
            kind == CompanyCommissionCapabilityKind.Claim && grantId.HasValue)
        {
            throw new ArgumentException("A valid commission capability identity is required.");
        }

        var plaintext = CreateToken();
        var tokenHash = HashToken(plaintext);
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (kind != CompanyCommissionCapabilityKind.Claim)
        {
            await using var revoke = connection.CreateCommand();
            revoke.Transaction = transaction;
            revoke.CommandText = """
                    UPDATE company_commission_capabilities
                    SET revoked_at_utc = COALESCE(revoked_at_utc, $revokedAtUtc)
                    WHERE company_id = $companyId
                      AND commission_id = $commissionId
                      AND capability_kind = $kind
                      AND grant_id = $grantId
                      AND revoked_at_utc IS NULL;
                    """;
            revoke.Parameters.AddWithValue("$revokedAtUtc", issuedAtUtc.ToString("O"));
            revoke.Parameters.AddWithValue("$companyId", companyId.ToString());
            revoke.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
            revoke.Parameters.AddWithValue("$kind", kind.ToString());
            revoke.Parameters.AddWithValue("$grantId", grantId?.ToString("D") ?? string.Empty);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = kind == CompanyCommissionCapabilityKind.Claim
                ? """
                INSERT INTO company_commission_capabilities
                    (
                        capability_id,
                        company_id,
                        commission_id,
                        public_brief_id,
                        capability_kind,
                        grant_id,
                        capability_revision,
                        token_hash,
                        issued_at_utc
                    )
                VALUES
                    (
                        $capabilityId,
                        $companyId,
                        $commissionId,
                        $publicBriefId,
                        $kind,
                        $grantId,
                        $capabilityRevision,
                        $tokenHash,
                        $issuedAtUtc
                    );
                """
                : """
                INSERT INTO company_commission_capabilities
                    (
                        capability_id,
                        company_id,
                        commission_id,
                        public_brief_id,
                        capability_kind,
                        grant_id,
                        capability_revision,
                        token_hash,
                        issued_at_utc
                    )
                VALUES
                    (
                        $capabilityId,
                        $companyId,
                        $commissionId,
                        $publicBriefId,
                        $kind,
                        $grantId,
                        $capabilityRevision,
                        $tokenHash,
                        $issuedAtUtc
                    )
                ON CONFLICT (
                    company_id,
                    commission_id,
                    capability_kind,
                    grant_id,
                    capability_revision
                )
                WHERE capability_kind <> 'Claim'
                DO UPDATE SET
                    public_brief_id = excluded.public_brief_id,
                    token_hash = excluded.token_hash,
                    issued_at_utc = excluded.issued_at_utc,
                    revoked_at_utc = NULL,
                    consumed_by_command_id = NULL;
                """;
            insert.Parameters.AddWithValue("$capabilityId", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$companyId", companyId.ToString());
            insert.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
            insert.Parameters.AddWithValue("$publicBriefId", publicBriefId);
            insert.Parameters.AddWithValue("$kind", kind.ToString());
            insert.Parameters.AddWithValue("$grantId", grantId?.ToString("D") ?? string.Empty);
            insert.Parameters.AddWithValue("$capabilityRevision", capabilityRevision);
            insert.Parameters.AddWithValue("$tokenHash", tokenHash);
            insert.Parameters.AddWithValue("$issuedAtUtc", issuedAtUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new IssuedCompanyCommissionCapability(
            new CompanyCommissionCapabilityResolution(
                companyId,
                commissionId,
                publicBriefId,
                kind,
                grantId,
                capabilityRevision),
            plaintext);
    }

    public async Task<CompanyCommissionCapabilityResolution> InstallLinkedParticipantAsync(
        CompanyId companyId,
        Guid commissionId,
        string publicBriefId,
        Guid participantGrantId,
        long participantCapabilityRevision,
        string participantPlaintextToken,
        DateTime installedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (commissionId == Guid.Empty ||
            participantGrantId == Guid.Empty ||
            participantCapabilityRevision <= 0 ||
            string.IsNullOrWhiteSpace(publicBriefId) ||
            publicBriefId.Length > MaximumPublicBriefIdLength ||
            !IsValidCapability(participantPlaintextToken))
        {
            throw new ArgumentException(
                "A valid linked participant credential is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = """
                UPDATE company_commission_capabilities
                SET revoked_at_utc = COALESCE(revoked_at_utc, $installedAtUtc)
                WHERE company_id = $companyId
                  AND commission_id = $commissionId
                  AND capability_kind = $kind
                  AND grant_id = $grantId
                  AND revoked_at_utc IS NULL;
                """;
            revoke.Parameters.AddWithValue("$installedAtUtc", installedAtUtc.ToString("O"));
            revoke.Parameters.AddWithValue("$companyId", companyId.ToString());
            revoke.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
            revoke.Parameters.AddWithValue(
                "$kind",
                CompanyCommissionCapabilityKind.Participant.ToString());
            revoke.Parameters.AddWithValue("$grantId", participantGrantId.ToString("D"));
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var install = connection.CreateCommand())
        {
            install.Transaction = transaction;
            install.CommandText = """
                INSERT INTO company_commission_capabilities (
                    capability_id, company_id, commission_id, public_brief_id,
                    capability_kind, grant_id, capability_revision, token_hash,
                    issued_at_utc, revoked_at_utc, consumed_by_command_id)
                VALUES (
                    $capabilityId, $companyId, $commissionId, $publicBriefId,
                    $kind, $grantId, $capabilityRevision, $tokenHash,
                    $installedAtUtc, NULL, NULL)
                ON CONFLICT (
                    company_id, commission_id, capability_kind, grant_id, capability_revision)
                WHERE capability_kind <> 'Claim'
                DO UPDATE SET
                    public_brief_id = excluded.public_brief_id,
                    token_hash = excluded.token_hash,
                    issued_at_utc = excluded.issued_at_utc,
                    revoked_at_utc = NULL,
                    consumed_by_command_id = NULL;
                """;
            install.Parameters.AddWithValue("$capabilityId", Guid.NewGuid().ToString("D"));
            install.Parameters.AddWithValue("$companyId", companyId.ToString());
            install.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
            install.Parameters.AddWithValue("$publicBriefId", publicBriefId);
            install.Parameters.AddWithValue(
                "$kind",
                CompanyCommissionCapabilityKind.Participant.ToString());
            install.Parameters.AddWithValue("$grantId", participantGrantId.ToString("D"));
            install.Parameters.AddWithValue("$capabilityRevision", participantCapabilityRevision);
            install.Parameters.AddWithValue("$tokenHash", HashToken(participantPlaintextToken));
            install.Parameters.AddWithValue("$installedAtUtc", installedAtUtc.ToString("O"));
            await install.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CompanyCommissionCapabilityResolution(
            companyId,
            commissionId,
            publicBriefId,
            CompanyCommissionCapabilityKind.Participant,
            participantGrantId,
            participantCapabilityRevision);
    }

    public async Task<CompanyCommissionCapabilityResolution?> ResolveAsync(
        string publicBriefId,
        CompanyCommissionCapabilityKind kind,
        string plaintextToken,
        CancellationToken cancellationToken = default)
        => await ResolveCoreAsync(
            publicBriefId,
            kind,
            plaintextToken,
            consumedByCommandId: null,
            cancellationToken);

    public async Task<CompanyCommissionCapabilityResolution?> ResolveForCommandAsync(
        string publicBriefId,
        CompanyCommissionCapabilityKind kind,
        string plaintextToken,
        Guid commandId,
        CancellationToken cancellationToken = default)
        => commandId == Guid.Empty
            ? null
            : await ResolveCoreAsync(
                publicBriefId,
                kind,
                plaintextToken,
                commandId,
                cancellationToken);

    private async Task<CompanyCommissionCapabilityResolution?> ResolveCoreAsync(
        string publicBriefId,
        CompanyCommissionCapabilityKind kind,
        string plaintextToken,
        Guid? consumedByCommandId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publicBriefId) ||
            publicBriefId.Length > MaximumPublicBriefIdLength ||
            !IsValidCapability(plaintextToken))
        {
            return null;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT company_id,
                   commission_id,
                   grant_id,
                   capability_revision,
                   token_hash
            FROM company_commission_capabilities
            WHERE public_brief_id = $publicBriefId
              AND capability_kind = $kind
              AND (
                    revoked_at_utc IS NULL
                    OR consumed_by_command_id = $consumedByCommandId
                  );
            """;
        command.Parameters.AddWithValue("$publicBriefId", publicBriefId);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue(
            "$consumedByCommandId",
            consumedByCommandId?.ToString("D") ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        CompanyCommissionCapabilityResolution? resolved = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!TokenMatches(plaintextToken, reader.GetString(4)))
            {
                continue;
            }
            if (resolved != null ||
                !CompanyId.TryParse(reader.GetString(0), out var companyId) ||
                !Guid.TryParse(reader.GetString(1), out var commissionId) ||
                commissionId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "Commission capability ownership is duplicated or invalid.");
            }

            var rawGrantId = reader.GetString(2);
            resolved = new CompanyCommissionCapabilityResolution(
                companyId,
                commissionId,
                publicBriefId,
                kind,
                Guid.TryParse(rawGrantId, out var parsedGrantId) ? parsedGrantId : null,
                reader.GetInt64(3));
        }

        return resolved;
    }

    public async Task<CompanyCommissionCapabilityResolution> FinalizeAuthorityExchangeAsync(
        CompanyCommissionCapabilityResolution authority,
        string authorityPlaintextToken,
        Guid commandId,
        Guid participantGrantId,
        long participantCapabilityRevision,
        string newParticipantPlaintextToken,
        DateTime finalizedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (authority.Kind is not (
                CompanyCommissionCapabilityKind.Claim or
                CompanyCommissionCapabilityKind.Recovery) ||
            authority.Kind == CompanyCommissionCapabilityKind.Recovery &&
            authority.GrantId == null ||
            commandId == Guid.Empty ||
            participantGrantId == Guid.Empty ||
            participantCapabilityRevision <= 0 ||
            !IsValidCapability(authorityPlaintextToken) ||
            !IsValidCapability(newParticipantPlaintextToken))
        {
            throw new ArgumentException("A valid authority exchange is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using (var authorize = connection.CreateCommand())
        {
            authorize.Transaction = transaction;
            authorize.CommandText = """
                SELECT COUNT(*)
                FROM company_commission_capabilities
                WHERE company_id = $companyId
                  AND commission_id = $commissionId
                  AND public_brief_id = $publicBriefId
                  AND capability_kind = $kind
                  AND grant_id = $authorityGrantId
                  AND capability_revision = $authorityRevision
                  AND token_hash = $authorityTokenHash
                  AND (
                        revoked_at_utc IS NULL
                        OR consumed_by_command_id = $commandId
                      );
                """;
            authorize.Parameters.AddWithValue("$companyId", authority.CompanyId.ToString());
            authorize.Parameters.AddWithValue("$commissionId", authority.CommissionId.ToString("D"));
            authorize.Parameters.AddWithValue("$publicBriefId", authority.PublicBriefId);
            authorize.Parameters.AddWithValue("$kind", authority.Kind.ToString());
            authorize.Parameters.AddWithValue(
                "$authorityGrantId",
                authority.GrantId?.ToString("D") ?? string.Empty);
            authorize.Parameters.AddWithValue("$authorityRevision", authority.CapabilityRevision);
            authorize.Parameters.AddWithValue(
                "$authorityTokenHash",
                HashToken(authorityPlaintextToken));
            authorize.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
            if (Convert.ToInt64(
                    await authorize.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new UnauthorizedAccessException(
                    "The claim or recovery capability is invalid, expired, or already used.");
            }
        }

        var participantTokenHash = HashToken(newParticipantPlaintextToken);
        await using (var replay = connection.CreateCommand())
        {
            replay.Transaction = transaction;
            replay.CommandText = """
                SELECT grant_id,
                       capability_revision,
                       token_hash
                FROM company_commission_capabilities
                WHERE company_id = $companyId
                  AND commission_id = $commissionId
                  AND capability_kind = $participantKind
                  AND consumed_by_command_id = $commandId
                ORDER BY capability_id
                LIMIT 2;
                """;
            replay.Parameters.AddWithValue("$companyId", authority.CompanyId.ToString());
            replay.Parameters.AddWithValue("$commissionId", authority.CommissionId.ToString("D"));
            replay.Parameters.AddWithValue(
                "$participantKind",
                CompanyCommissionCapabilityKind.Participant.ToString());
            replay.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
            await using var reader = await replay.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var installedGrantId = Guid.Parse(reader.GetString(0));
                var installedRevision = reader.GetInt64(1);
                var installedHash = reader.GetString(2);
                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The authority command installed more than one participant credential.");
                }
                if (!string.Equals(
                        installedHash,
                        participantTokenHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The authority command was replayed with different participant secret material.");
                }

                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return new CompanyCommissionCapabilityResolution(
                    authority.CompanyId,
                    authority.CommissionId,
                    authority.PublicBriefId,
                    CompanyCommissionCapabilityKind.Participant,
                    installedGrantId,
                    installedRevision);
            }
        }

        await using (var inspectParticipant = connection.CreateCommand())
        {
            inspectParticipant.Transaction = transaction;
            inspectParticipant.CommandText = """
                SELECT token_hash
                FROM company_commission_capabilities
                WHERE company_id = $companyId
                  AND commission_id = $commissionId
                  AND capability_kind = $participantKind
                  AND grant_id = $participantGrantId
                  AND capability_revision = $participantRevision;
                """;
            inspectParticipant.Parameters.AddWithValue("$companyId", authority.CompanyId.ToString());
            inspectParticipant.Parameters.AddWithValue("$commissionId", authority.CommissionId.ToString("D"));
            inspectParticipant.Parameters.AddWithValue(
                "$participantKind",
                CompanyCommissionCapabilityKind.Participant.ToString());
            inspectParticipant.Parameters.AddWithValue("$participantGrantId", participantGrantId.ToString("D"));
            inspectParticipant.Parameters.AddWithValue("$participantRevision", participantCapabilityRevision);
            var existingHash = await inspectParticipant.ExecuteScalarAsync(cancellationToken) as string;
            if (existingHash != null &&
                !string.Equals(existingHash, participantTokenHash, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    "The participant credential revision was already installed with different secret material.");
            }
        }

        await using (var revokeParticipant = connection.CreateCommand())
        {
            revokeParticipant.Transaction = transaction;
            revokeParticipant.CommandText = """
                UPDATE company_commission_capabilities
                SET revoked_at_utc = COALESCE(revoked_at_utc, $finalizedAtUtc)
                WHERE company_id = $companyId
                  AND commission_id = $commissionId
                  AND capability_kind = $participantKind
                  AND grant_id = $participantGrantId
                  AND capability_revision <> $participantRevision
                  AND revoked_at_utc IS NULL;
                """;
            revokeParticipant.Parameters.AddWithValue("$finalizedAtUtc", finalizedAtUtc.ToString("O"));
            revokeParticipant.Parameters.AddWithValue("$companyId", authority.CompanyId.ToString());
            revokeParticipant.Parameters.AddWithValue("$commissionId", authority.CommissionId.ToString("D"));
            revokeParticipant.Parameters.AddWithValue(
                "$participantKind",
                CompanyCommissionCapabilityKind.Participant.ToString());
            revokeParticipant.Parameters.AddWithValue("$participantGrantId", participantGrantId.ToString("D"));
            revokeParticipant.Parameters.AddWithValue("$participantRevision", participantCapabilityRevision);
            await revokeParticipant.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertParticipant = connection.CreateCommand())
        {
            insertParticipant.Transaction = transaction;
            insertParticipant.CommandText = """
                INSERT OR IGNORE INTO company_commission_capabilities
                    (
                        capability_id,
                        company_id,
                        commission_id,
                        public_brief_id,
                        capability_kind,
                        grant_id,
                        capability_revision,
                        token_hash,
                        issued_at_utc,
                        consumed_by_command_id
                    )
                VALUES
                    (
                        $capabilityId,
                        $companyId,
                        $commissionId,
                        $publicBriefId,
                        $participantKind,
                        $participantGrantId,
                        $participantRevision,
                        $participantTokenHash,
                        $finalizedAtUtc,
                        $commandId
                    );
                """;
            insertParticipant.Parameters.AddWithValue("$capabilityId", Guid.NewGuid().ToString("D"));
            insertParticipant.Parameters.AddWithValue("$companyId", authority.CompanyId.ToString());
            insertParticipant.Parameters.AddWithValue("$commissionId", authority.CommissionId.ToString("D"));
            insertParticipant.Parameters.AddWithValue("$publicBriefId", authority.PublicBriefId);
            insertParticipant.Parameters.AddWithValue(
                "$participantKind",
                CompanyCommissionCapabilityKind.Participant.ToString());
            insertParticipant.Parameters.AddWithValue("$participantGrantId", participantGrantId.ToString("D"));
            insertParticipant.Parameters.AddWithValue("$participantRevision", participantCapabilityRevision);
            insertParticipant.Parameters.AddWithValue("$participantTokenHash", participantTokenHash);
            insertParticipant.Parameters.AddWithValue("$finalizedAtUtc", finalizedAtUtc.ToString("O"));
            insertParticipant.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
            await insertParticipant.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = """
                UPDATE company_commission_capabilities
                SET revoked_at_utc = COALESCE(revoked_at_utc, $finalizedAtUtc),
                    consumed_by_command_id = $commandId
                WHERE company_id = $companyId
                  AND commission_id = $commissionId
                  AND public_brief_id = $publicBriefId
                  AND capability_kind = $kind
                  AND grant_id = $authorityGrantId
                  AND capability_revision = $authorityRevision
                  AND token_hash = $authorityTokenHash
                  AND (
                        revoked_at_utc IS NULL
                        OR consumed_by_command_id = $commandId
                      );
                """;
            consume.Parameters.AddWithValue("$finalizedAtUtc", finalizedAtUtc.ToString("O"));
            consume.Parameters.AddWithValue("$commandId", commandId.ToString("D"));
            consume.Parameters.AddWithValue("$companyId", authority.CompanyId.ToString());
            consume.Parameters.AddWithValue("$commissionId", authority.CommissionId.ToString("D"));
            consume.Parameters.AddWithValue("$publicBriefId", authority.PublicBriefId);
            consume.Parameters.AddWithValue("$kind", authority.Kind.ToString());
            consume.Parameters.AddWithValue(
                "$authorityGrantId",
                authority.GrantId?.ToString("D") ?? string.Empty);
            consume.Parameters.AddWithValue("$authorityRevision", authority.CapabilityRevision);
            consume.Parameters.AddWithValue(
                "$authorityTokenHash",
                HashToken(authorityPlaintextToken));
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new UnauthorizedAccessException(
                    "The authority exchange could not be finalized.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new CompanyCommissionCapabilityResolution(
            authority.CompanyId,
            authority.CommissionId,
            authority.PublicBriefId,
            CompanyCommissionCapabilityKind.Participant,
            participantGrantId,
            participantCapabilityRevision);
    }

    public async Task RevokeAsync(
        CompanyCommissionCapabilityResolution resolution,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE company_commission_capabilities
            SET revoked_at_utc = COALESCE(revoked_at_utc, $revokedAtUtc)
            WHERE company_id = $companyId
              AND commission_id = $commissionId
              AND public_brief_id = $publicBriefId
              AND capability_kind = $kind
              AND grant_id = $grantId
              AND capability_revision = $capabilityRevision;
            """;
        command.Parameters.AddWithValue("$revokedAtUtc", revokedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$companyId", resolution.CompanyId.ToString());
        command.Parameters.AddWithValue("$commissionId", resolution.CommissionId.ToString("D"));
        command.Parameters.AddWithValue("$publicBriefId", resolution.PublicBriefId);
        command.Parameters.AddWithValue("$kind", resolution.Kind.ToString());
        command.Parameters.AddWithValue("$grantId", resolution.GrantId?.ToString("D") ?? string.Empty);
        command.Parameters.AddWithValue("$capabilityRevision", resolution.CapabilityRevision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RevokeAllAsync(
        CompanyId companyId,
        Guid commissionId,
        CompanyCommissionCapabilityKind kind,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE company_commission_capabilities
            SET revoked_at_utc = COALESCE(revoked_at_utc, $revokedAtUtc)
            WHERE company_id = $companyId
              AND commission_id = $commissionId
              AND capability_kind = $kind
              AND revoked_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$revokedAtUtc", revokedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
        command.Parameters.AddWithValue("$kind", kind.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string BuildFragmentUrl(string publicUrl, string fragmentKey, string plaintextToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(fragmentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextToken);
        var builder = new UriBuilder(publicUrl)
        {
            Fragment =
                $"{Uri.EscapeDataString(fragmentKey)}={Uri.EscapeDataString(plaintextToken)}"
        };
        return builder.Uri.AbsoluteUri;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var absolutePath = Path.GetFullPath(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = absolutePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS company_commission_capabilities (
                    capability_id TEXT PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    commission_id TEXT NOT NULL,
                    public_brief_id TEXT NOT NULL,
                    capability_kind TEXT NOT NULL,
                    grant_id TEXT NOT NULL,
                    capability_revision INTEGER NOT NULL,
                    token_hash TEXT NOT NULL,
                    issued_at_utc TEXT NOT NULL,
                    revoked_at_utc TEXT NULL,
                    consumed_by_command_id TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_company_commission_capabilities_public
                ON company_commission_capabilities (
                    public_brief_id,
                    capability_kind,
                    revoked_at_utc
                );

                DROP INDEX IF EXISTS ux_company_commission_capabilities_revision;

                CREATE UNIQUE INDEX IF NOT EXISTS ux_company_commission_capabilities_grant_revision
                ON company_commission_capabilities (
                    company_id,
                    commission_id,
                    capability_kind,
                    grant_id,
                    capability_revision
                )
                WHERE capability_kind <> 'Claim';
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AddColumnIfMissingAsync(
                connection,
                "consumed_by_command_id",
                "TEXT NULL",
                cancellationToken);
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
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(company_commission_capabilities);";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText =
            $"ALTER TABLE company_commission_capabilities ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool IsValidCapability(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        token.Length is >= 32 and <= MaximumCapabilityLength &&
        token.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_');

    private static bool TokenMatches(string token, string storedHash)
    {
        var actual = Encoding.ASCII.GetBytes(HashToken(token));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return actual.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
