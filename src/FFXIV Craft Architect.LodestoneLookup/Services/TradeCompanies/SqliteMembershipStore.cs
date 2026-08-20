using System.Data;
using System.Globalization;
using System.Security.Cryptography;
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
    string? RequestNote,
    bool NotificationsOptedOut = false,
    bool NotifyActionRequired = true,
    bool NotifyCommissionerMessages = true,
    bool NotifyProgressAndStatus = true);

public sealed record MemberNotificationPreferences(
    bool ActionRequired,
    bool CommissionerMessages,
    bool ProgressAndStatus)
{
    public bool AllowsAny => ActionRequired || CommissionerMessages || ProgressAndStatus;
}

public sealed record MembershipEvent(
    Guid EventId,
    CompanyId CompanyId,
    Guid AccountProfileId,
    MembershipState? FromState,
    MembershipState ToState,
    Guid ActorProfileId,
    DateTimeOffset CreatedAtUtc,
    MembershipRole? Role,
    DateTimeOffset? RequestedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    Guid? DecidedByProfileId,
    string? RequestNote,
    string? Reason);

public sealed record MembershipMutationResult(
    MembershipMutationStatus Status,
    CompanyMembership? Membership = null);

public enum MembershipInvitationState
{
    Active,
    Expired,
    Revoked,
    Consumed
}

public enum MembershipInvitationConsumptionStatus
{
    Applied,
    Replayed,
    Unavailable,
    BindingConflict
}

public sealed record CompanyMembershipInvitation(
    Guid InvitationId,
    CompanyId CompanyId,
    Guid? LegacyCrafterId,
    Guid IssuedByProfileId,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    Guid? RevokedByProfileId,
    DateTimeOffset? ConsumedAtUtc,
    Guid? ConsumedByProfileId,
    MembershipInvitationState State,
    string? Token = null);

public sealed record MembershipInvitationConsumptionResult(
    MembershipInvitationConsumptionStatus Status,
    CompanyMembership? Membership = null);

public enum CrafterAccountBindingEvidence
{
    CommittedDiscordClaim,
    OperatorConfirmed
}

public enum CrafterAccountBindingMutationStatus
{
    Applied,
    Replayed,
    Conflict,
    Suppressed,
    NotFound
}

public sealed record CrafterAccountBinding(
    CompanyId CompanyId,
    Guid LegacyCrafterId,
    Guid AccountProfileId,
    CrafterAccountBindingEvidence Evidence,
    Guid? ActorProfileId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CrafterAccountBindingMutationResult(
    CrafterAccountBindingMutationStatus Status,
    CrafterAccountBinding? Binding = null);

public enum FounderBindingStatus
{
    Bound,
    ExistingMembership,
    ConflictingOwner
}

public sealed record FounderBindingResult(
    FounderBindingStatus Status,
    CompanyMembership? Membership = null,
    Guid? ConflictingOwnerProfileId = null);

public interface ITradeCompanyFounderBinder
{
    Task<FounderBindingResult> BindFounderAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default);
}

public sealed class SqliteMembershipStore(
    TradeMembershipOptions options,
    TimeProvider timeProvider,
    ILogger<SqliteMembershipStore> logger) : ITradeCompanyFounderBinder
{
    public const int MaximumRequestNoteLength = 500;
    public static readonly TimeSpan DefaultInvitationLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan MinimumInvitationLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumInvitationLifetime = TimeSpan.FromDays(366);
    private readonly SemaphoreSlim schemaGate = new(1, 1);
    private bool schemaReady;

    public async Task<bool> ProjectOwnershipTransferAsync(
        Guid transferId,
        CompanyId companyId,
        Guid sourceProfileId,
        Guid targetProfileId,
        PreviousOwnerDisposition sourceDisposition,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, sourceProfileId);
        if (transferId == Guid.Empty || targetProfileId == Guid.Empty || sourceProfileId == targetProfileId)
        {
            throw new ArgumentException("A valid ownership transfer identity is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using (var replay = connection.CreateCommand())
        {
            replay.Transaction = transaction;
            replay.CommandText = "SELECT company_id, source_profile_id, target_profile_id, source_disposition FROM membership_ownership_transfers WHERE transfer_id = $id;";
            replay.Parameters.AddWithValue("$id", transferId.ToString("D"));
            await using var reader = await replay.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var matches = reader.GetString(0) == companyId.ToString() &&
                              reader.GetString(1) == sourceProfileId.ToString("D") &&
                              reader.GetString(2) == targetProfileId.ToString("D") &&
                              reader.GetString(3) == sourceDisposition.ToString().ToLowerInvariant();
                await transaction.CommitAsync(cancellationToken);
                return matches;
            }
        }

        var target = await LoadAsync(connection, transaction, companyId, targetProfileId, cancellationToken);
        if (target == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var source = await LoadAsync(connection, transaction, companyId, sourceProfileId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var targetOwner = target with
        {
            Role = MembershipRole.Owner,
            State = MembershipState.Active,
            DecidedAtUtc = now,
            DecidedByProfileId = sourceProfileId
        };
        var sourceProjected = (source ?? new CompanyMembership(
            companyId,
            sourceProfileId,
            MembershipRole.Owner,
            MembershipState.Active,
            now,
            now,
            sourceProfileId,
            null)) with
        {
            Role = sourceDisposition == PreviousOwnerDisposition.Operator
                ? MembershipRole.Operator
                : MembershipRole.Owner,
            State = sourceDisposition == PreviousOwnerDisposition.Operator
                ? MembershipState.Active
                : MembershipState.Revoked,
            DecidedAtUtc = now,
            DecidedByProfileId = sourceProfileId
        };

        await UpsertMembershipAsync(connection, transaction, targetOwner, cancellationToken);
        await UpsertMembershipAsync(connection, transaction, sourceProjected, cancellationToken);
        await InsertEventAsync(connection, transaction, target?.State, targetOwner, sourceProfileId, now, "Ownership transferred to this member.", cancellationToken);
        await InsertEventAsync(connection, transaction, source?.State, sourceProjected, sourceProfileId, now, "Ownership transferred to another member.", cancellationToken);
        await using (var record = connection.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO membership_ownership_transfers(transfer_id,company_id,source_profile_id,target_profile_id,source_disposition,projected_at_utc) VALUES($id,$company,$source,$target,$disposition,$at);";
            record.Parameters.AddWithValue("$id", transferId.ToString("D"));
            record.Parameters.AddWithValue("$company", companyId.ToString());
            record.Parameters.AddWithValue("$source", sourceProfileId.ToString("D"));
            record.Parameters.AddWithValue("$target", targetProfileId.ToString("D"));
            record.Parameters.AddWithValue("$disposition", sourceDisposition.ToString().ToLowerInvariant());
            record.Parameters.AddWithValue("$at", now.ToString("O"));
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<CompanyMembershipInvitation> IssueInvitationAsync(
        CompanyId companyId,
        Guid issuedByProfileId,
        Guid? legacyCrafterId,
        DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, issuedByProfileId);
        var invitationId = Guid.NewGuid();
        var token = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(token);
        var issuedAt = timeProvider.GetUtcNow();
        var expiresAt = (expiresAtUtc ?? issuedAt + DefaultInvitationLifetime).ToUniversalTime();
        if (expiresAt < issuedAt + MinimumInvitationLifetime ||
            expiresAt > issuedAt + MaximumInvitationLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                $"Invitation expiry must be between {MinimumInvitationLifetime.TotalMinutes:N0} minutes and {MaximumInvitationLifetime.TotalDays:N0} days from now.");
        }
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO company_membership_invitations (
                    invitation_id, token_hash, company_id, legacy_crafter_id,
                    issued_by_profile_id, issued_at_utc, expires_at_utc)
                VALUES (
                    $invitationId, $tokenHash, $companyId, $legacyCrafterId,
                    $issuedBy, $issuedAt, $expiresAt);
                """;
            command.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
            command.Parameters.AddWithValue("$tokenHash", tokenHash);
            command.Parameters.AddWithValue("$companyId", companyId.ToString());
            command.Parameters.AddWithValue("$legacyCrafterId", legacyCrafterId?.ToString("D") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$issuedBy", issuedByProfileId.ToString("D"));
            command.Parameters.AddWithValue("$issuedAt", issuedAt.ToString("O"));
            command.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertInvitationEventAsync(
            connection,
            transaction,
            invitationId,
            companyId,
            "issued",
            issuedByProfileId,
            issuedAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CompanyMembershipInvitation(
            invitationId,
            companyId,
            legacyCrafterId,
            issuedByProfileId,
            issuedAt,
            expiresAt,
            null,
            null,
            null,
            null,
            MembershipInvitationState.Active,
            token);
    }

    public async Task<IReadOnlyList<CompanyMembershipInvitation>> LoadInvitationsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT invitation_id, company_id, legacy_crafter_id, issued_by_profile_id,
                   issued_at_utc, expires_at_utc, revoked_at_utc, revoked_by_profile_id,
                   consumed_at_utc, consumed_by_profile_id
            FROM company_membership_invitations
            WHERE company_id = $companyId
            ORDER BY issued_at_utc DESC;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        var invitations = new List<CompanyMembershipInvitation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            invitations.Add(ReadInvitation(reader, timeProvider.GetUtcNow()));
        }
        return invitations;
    }

    public async Task<CompanyMembershipInvitation?> LoadInvitationAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidInvitationToken(token))
        {
            return null;
        }
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT invitation_id, company_id, legacy_crafter_id, issued_by_profile_id,
                   issued_at_utc, expires_at_utc, revoked_at_utc, revoked_by_profile_id,
                   consumed_at_utc, consumed_by_profile_id
            FROM company_membership_invitations
            WHERE token_hash = $tokenHash;
            """;
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadInvitation(reader, timeProvider.GetUtcNow())
            : null;
    }

    public async Task<bool> RevokeInvitationAsync(
        CompanyId companyId,
        Guid invitationId,
        Guid revokedByProfileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE company_membership_invitations
            SET revoked_at_utc = $revokedAt, revoked_by_profile_id = $revokedBy
            WHERE invitation_id = $invitationId AND company_id = $companyId
              AND revoked_at_utc IS NULL AND consumed_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("$revokedAt", now.ToString("O"));
        command.Parameters.AddWithValue("$revokedBy", revokedByProfileId.ToString("D"));
        command.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (changed)
        {
            await InsertInvitationEventAsync(
                connection,
                transaction,
                invitationId,
                companyId,
                "revoked",
                revokedByProfileId,
                now,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<MembershipInvitationConsumptionResult> ConsumeInvitationAsync(
        string token,
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidInvitationToken(token) || accountProfileId == Guid.Empty)
        {
            return new MembershipInvitationConsumptionResult(MembershipInvitationConsumptionStatus.Unavailable);
        }
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var invitation = await LoadInvitationAsync(
            connection,
            transaction,
            HashToken(token),
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (invitation == null || invitation.RevokedAtUtc != null || invitation.ExpiresAtUtc <= now)
        {
            await transaction.CommitAsync(cancellationToken);
            return new MembershipInvitationConsumptionResult(MembershipInvitationConsumptionStatus.Unavailable);
        }
        if (invitation.ConsumedAtUtc != null)
        {
            var replayed = invitation.ConsumedByProfileId == accountProfileId
                ? await LoadAsync(connection, transaction, invitation.CompanyId, accountProfileId, cancellationToken)
                : null;
            await transaction.CommitAsync(cancellationToken);
            return replayed is not { State: MembershipState.Active }
                ? new MembershipInvitationConsumptionResult(MembershipInvitationConsumptionStatus.Unavailable)
                : new MembershipInvitationConsumptionResult(MembershipInvitationConsumptionStatus.Replayed, replayed);
        }

        if (invitation.LegacyCrafterId.HasValue &&
            await HasConflictingCrafterBindingAsync(
                connection,
                transaction,
                invitation.CompanyId,
                invitation.LegacyCrafterId.Value,
                accountProfileId,
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new MembershipInvitationConsumptionResult(MembershipInvitationConsumptionStatus.BindingConflict);
        }

        var existing = await LoadAsync(
            connection,
            transaction,
            invitation.CompanyId,
            accountProfileId,
            cancellationToken);
        var membership = existing is { State: MembershipState.Active }
            ? existing
            : new CompanyMembership(
                invitation.CompanyId,
                accountProfileId,
                MembershipRole.Crafter,
                MembershipState.Active,
                now,
                now,
                invitation.IssuedByProfileId,
                "Joined by company invitation");
        if (existing is not { State: MembershipState.Active })
        {
            await UpsertMembershipAsync(connection, transaction, membership, cancellationToken);
            await InsertEventAsync(
                connection,
                transaction,
                existing?.State,
                membership,
                accountProfileId,
                now,
                "Accepted company invitation",
                cancellationToken);
        }
        if (invitation.LegacyCrafterId.HasValue)
        {
            await UpsertInvitationCrafterBindingAsync(
                connection,
                transaction,
                invitation,
                accountProfileId,
                now,
                cancellationToken);
        }
        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = """
                UPDATE company_membership_invitations
                SET consumed_at_utc = $consumedAt, consumed_by_profile_id = $consumedBy
                WHERE invitation_id = $invitationId AND consumed_at_utc IS NULL;
                """;
            consume.Parameters.AddWithValue("$consumedAt", now.ToString("O"));
            consume.Parameters.AddWithValue("$consumedBy", accountProfileId.ToString("D"));
            consume.Parameters.AddWithValue("$invitationId", invitation.InvitationId.ToString("D"));
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new MembershipInvitationConsumptionResult(MembershipInvitationConsumptionStatus.Unavailable);
            }
        }
        await InsertInvitationEventAsync(
            connection,
            transaction,
            invitation.InvitationId,
            invitation.CompanyId,
            "consumed",
            accountProfileId,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MembershipInvitationConsumptionResult(MembershipInvitationConsumptionStatus.Applied, membership);
    }

    public async Task<IReadOnlyList<CrafterAccountBinding>> LoadCrafterBindingsAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        if (companyId.Value == Guid.Empty)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT company_id, legacy_crafter_id, account_profile_id, evidence_kind,
                   actor_profile_id, created_at_utc, updated_at_utc
            FROM company_crafter_account_bindings
            WHERE company_id = $companyId
            ORDER BY created_at_utc, legacy_crafter_id;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        var bindings = new List<CrafterAccountBinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bindings.Add(ReadCrafterBinding(reader));
        }
        return bindings;
    }

    public async Task<bool> IsCrafterAutoBindingSuppressedAsync(
        CompanyId companyId,
        Guid legacyCrafterId,
        CancellationToken cancellationToken = default)
    {
        if (companyId.Value == Guid.Empty || legacyCrafterId == Guid.Empty)
        {
            return true;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM company_crafter_account_binding_suppressions
            WHERE company_id = $companyId AND legacy_crafter_id = $legacyCrafterId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$legacyCrafterId", legacyCrafterId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken) != null;
    }

    public async Task<CrafterAccountBindingMutationResult> BindCrafterAsync(
        CompanyId companyId,
        Guid legacyCrafterId,
        Guid accountProfileId,
        CrafterAccountBindingEvidence evidence,
        Guid? actorProfileId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, accountProfileId);
        if (legacyCrafterId == Guid.Empty || actorProfileId == Guid.Empty)
        {
            throw new ArgumentException("A valid legacy crafter binding is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var existing = await LoadCrafterBindingAsync(
            connection,
            transaction,
            companyId,
            legacyCrafterId,
            cancellationToken);
        if (existing != null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new CrafterAccountBindingMutationResult(
                existing.AccountProfileId == accountProfileId
                    ? CrafterAccountBindingMutationStatus.Replayed
                    : CrafterAccountBindingMutationStatus.Conflict,
                existing);
        }

        if (evidence == CrafterAccountBindingEvidence.CommittedDiscordClaim &&
            await IsCrafterAutoBindingSuppressedAsync(
                connection,
                transaction,
                companyId,
                legacyCrafterId,
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new CrafterAccountBindingMutationResult(
                CrafterAccountBindingMutationStatus.Suppressed);
        }

        if (evidence == CrafterAccountBindingEvidence.OperatorConfirmed)
        {
            await DeleteCrafterBindingSuppressionAsync(
                connection,
                transaction,
                companyId,
                legacyCrafterId,
                cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var binding = new CrafterAccountBinding(
            companyId,
            legacyCrafterId,
            accountProfileId,
            evidence,
            actorProfileId,
            now,
            now);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO company_crafter_account_bindings (
                    company_id, legacy_crafter_id, account_profile_id, evidence_kind,
                    actor_profile_id, created_at_utc, updated_at_utc)
                VALUES (
                    $companyId, $legacyCrafterId, $accountProfileId, $evidenceKind,
                    $actorProfileId, $createdAtUtc, $updatedAtUtc);
                """;
            AddCrafterBindingParameters(command, binding);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertCrafterBindingEventAsync(
            connection,
            transaction,
            binding,
            "bound",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CrafterAccountBindingMutationResult(
            CrafterAccountBindingMutationStatus.Applied,
            binding);
    }

    public async Task<CrafterAccountBindingMutationResult> UnbindCrafterAsync(
        CompanyId companyId,
        Guid legacyCrafterId,
        Guid actorProfileId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, actorProfileId);
        if (legacyCrafterId == Guid.Empty)
        {
            throw new ArgumentException("A valid legacy crafter binding is required.");
        }

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var existing = await LoadCrafterBindingAsync(
            connection,
            transaction,
            companyId,
            legacyCrafterId,
            cancellationToken);
        if (existing == null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new CrafterAccountBindingMutationResult(
                CrafterAccountBindingMutationStatus.NotFound);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM company_crafter_account_bindings
                WHERE company_id = $companyId AND legacy_crafter_id = $legacyCrafterId;
                """;
            command.Parameters.AddWithValue("$companyId", companyId.ToString());
            command.Parameters.AddWithValue("$legacyCrafterId", legacyCrafterId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertCrafterBindingSuppressionAsync(
            connection,
            transaction,
            companyId,
            legacyCrafterId,
            actorProfileId,
            timeProvider.GetUtcNow(),
            cancellationToken);
        await InsertCrafterBindingEventAsync(
            connection,
            transaction,
            existing with { ActorProfileId = actorProfileId, UpdatedAtUtc = timeProvider.GetUtcNow() },
            "unbound",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CrafterAccountBindingMutationResult(
            CrafterAccountBindingMutationStatus.Applied,
            existing);
    }

    public async Task<FounderBindingResult> BindFounderAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default) =>
        await EnsureFounderAsync(companyId, accountProfileId, cancellationToken);

    public async Task<FounderBindingResult> EnsureFounderAsync(
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
        if (existing != null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new FounderBindingResult(
                FounderBindingStatus.ExistingMembership,
                existing);
        }

        var now = timeProvider.GetUtcNow();
        var founder = new CompanyMembership(
            companyId,
            accountProfileId,
            MembershipRole.Owner,
            MembershipState.Active,
            now,
            now,
            accountProfileId,
            null);
        int inserted;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO company_memberships (
                    company_id, account_profile_id, role, state, requested_at_utc,
                    decided_at_utc, decided_by_profile_id, request_note)
                SELECT
                    $companyId, $accountProfileId, 'owner', 'active', $requestedAtUtc,
                    $decidedAtUtc, $decidedByProfileId, NULL
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM company_memberships
                    WHERE company_id = $companyId
                      AND role = 'owner'
                      AND state = 'active'
                      AND account_profile_id <> $accountProfileId)
                ON CONFLICT(company_id, account_profile_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$companyId", companyId.ToString());
            command.Parameters.AddWithValue("$accountProfileId", accountProfileId.ToString("D"));
            command.Parameters.AddWithValue("$requestedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$decidedAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$decidedByProfileId", accountProfileId.ToString("D"));
            inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (inserted == 0)
        {
            var concurrentlyExisting = await LoadAsync(
                connection,
                transaction,
                companyId,
                accountProfileId,
                cancellationToken);
            if (concurrentlyExisting != null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new FounderBindingResult(
                    FounderBindingStatus.ExistingMembership,
                    concurrentlyExisting);
            }

            var conflictingOwner = await LoadActiveOwnerProfileIdAsync(
                connection,
                transaction,
                companyId,
                accountProfileId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogError(
                "Founder membership refused for company {CompanyId}, profile {ProfileId}: active owner profile {ConflictingProfileId} already exists.",
                companyId,
                accountProfileId,
                conflictingOwner);
            return new FounderBindingResult(
                FounderBindingStatus.ConflictingOwner,
                ConflictingOwnerProfileId: conflictingOwner);
        }
        await InsertEventAsync(
            connection,
            transaction,
            existing?.State,
            founder,
            accountProfileId,
            now,
            null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new FounderBindingResult(FounderBindingStatus.Bound, founder);
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
        var pending = new CompanyMembership(
            companyId,
            accountProfileId,
            MembershipRole.Crafter,
            MembershipState.Pending,
            now,
            null,
            null,
            requestNote);
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
            existing?.State,
            pending,
            accountProfileId,
            now,
            null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MembershipMutationResult(
            MembershipMutationStatus.Applied,
            pending);
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
            null,
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
            null,
            cancellationToken);

    public Task<MembershipMutationResult> RevokeAsync(
        CompanyId companyId,
        Guid accountProfileId,
        Guid actorProfileId,
        CancellationToken cancellationToken = default) =>
        RevokeAsync(companyId, accountProfileId, actorProfileId, null, cancellationToken);

    public Task<MembershipMutationResult> RevokeAsync(
        CompanyId companyId,
        Guid accountProfileId,
        Guid actorProfileId,
        string? reason,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            companyId,
            accountProfileId,
            actorProfileId,
            MembershipState.Active,
            MembershipState.Revoked,
            NormalizeReason(reason),
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

    public async Task<IReadOnlyList<CompanyMembership>> LoadActiveAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        await LoadManyAsync(
            "company_id = $identity AND state = 'active'",
            companyId.ToString(),
            cancellationToken);

    public async Task<IReadOnlyList<CompanyMembership>> LoadForCompanyAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        await LoadManyAsync(
            "company_id = $identity",
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

    public async Task<CompanyMembership?> LoadForAccountAsync(
        CompanyId companyId,
        Guid accountProfileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        return await LoadAsync(connection, null, companyId, accountProfileId, cancellationToken);
    }

    public async Task<CompanyMembership?> SetNotificationsOptedOutAsync(
        CompanyId companyId,
        Guid accountProfileId,
        bool optedOut,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE company_memberships
            SET notifications_opted_out = $optedOut,
                notify_action_required = CASE WHEN $optedOut = 1 THEN 0 ELSE 1 END,
                notify_commissioner_messages = CASE WHEN $optedOut = 1 THEN 0 ELSE 1 END,
                notify_progress_status = CASE WHEN $optedOut = 1 THEN 0 ELSE 1 END
            WHERE company_id = $companyId AND account_profile_id = $accountProfileId;
            """;
        AddMembershipIdentity(command, companyId, accountProfileId);
        command.Parameters.AddWithValue("$optedOut", optedOut ? 1 : 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            return null;
        }
        return await LoadAsync(connection, null, companyId, accountProfileId, cancellationToken);
    }

    public async Task<CompanyMembership?> SetNotificationPreferencesAsync(
        CompanyId companyId,
        Guid accountProfileId,
        MemberNotificationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE company_memberships
            SET notifications_opted_out = $optedOut,
                notify_action_required = $actionRequired,
                notify_commissioner_messages = $commissionerMessages,
                notify_progress_status = $progressAndStatus
            WHERE company_id = $companyId
              AND account_profile_id = $accountProfileId
              AND state = 'active';
            """;
        AddMembershipIdentity(command, companyId, accountProfileId);
        command.Parameters.AddWithValue("$optedOut", preferences.AllowsAny ? 0 : 1);
        command.Parameters.AddWithValue("$actionRequired", preferences.ActionRequired ? 1 : 0);
        command.Parameters.AddWithValue("$commissionerMessages", preferences.CommissionerMessages ? 1 : 0);
        command.Parameters.AddWithValue("$progressAndStatus", preferences.ProgressAndStatus ? 1 : 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            return null;
        }
        return await LoadAsync(connection, null, companyId, accountProfileId, cancellationToken);
    }

    public async Task<long?> LoadCommissionReadRevisionAsync(
        CompanyId companyId,
        Guid accountProfileId,
        Guid commissionId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, accountProfileId);
        if (commissionId == Guid.Empty)
        {
            throw new ArgumentException("A valid commission identity is required.");
        }
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_read_revision
            FROM membership_commission_attention
            WHERE company_id = $companyId
              AND account_profile_id = $accountProfileId
              AND commission_id = $commissionId;
            """;
        AddMembershipIdentity(command, companyId, accountProfileId);
        command.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value == null || value == DBNull.Value
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<long> AdvanceCommissionReadRevisionAsync(
        CompanyId companyId,
        Guid accountProfileId,
        Guid commissionId,
        long openedRevision,
        CancellationToken cancellationToken = default)
    {
        RequireIdentity(companyId, accountProfileId);
        if (commissionId == Guid.Empty || openedRevision < 0)
        {
            throw new ArgumentException("A valid commission identity and revision are required.");
        }
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO membership_commission_attention (
                company_id, account_profile_id, commission_id, last_read_revision, updated_at_utc)
            VALUES ($companyId, $accountProfileId, $commissionId, $openedRevision, $updatedAtUtc)
            ON CONFLICT(company_id, account_profile_id, commission_id) DO UPDATE SET
                last_read_revision = MAX(last_read_revision, excluded.last_read_revision),
                updated_at_utc = CASE
                    WHEN excluded.last_read_revision > last_read_revision
                    THEN excluded.updated_at_utc
                    ELSE updated_at_utc
                END;
            """;
        AddMembershipIdentity(command, companyId, accountProfileId);
        command.Parameters.AddWithValue("$commissionId", commissionId.ToString("D"));
        command.Parameters.AddWithValue("$openedRevision", openedRevision);
        command.Parameters.AddWithValue("$updatedAtUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await LoadCommissionReadRevisionAsync(
            companyId,
            accountProfileId,
            commissionId,
            cancellationToken))!.Value;
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
                   actor_profile_id, created_at_utc, role, requested_at_utc,
                   decided_at_utc, decided_by_profile_id, request_note, reason
            FROM membership_events
            WHERE company_id = $companyId AND account_profile_id = $accountProfileId
            ORDER BY rowid;
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
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                reader.IsDBNull(7) ? null : ParseRole(reader.GetString(7)),
                reader.IsDBNull(8)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                reader.IsDBNull(9)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return events;
    }

    private async Task<MembershipMutationResult> TransitionAsync(
        CompanyId companyId,
        Guid accountProfileId,
        Guid actorProfileId,
        MembershipState expectedState,
        MembershipState targetState,
        string? reason,
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
        if (current.State == targetState)
        {
            await transaction.CommitAsync(cancellationToken);
            return new MembershipMutationResult(MembershipMutationStatus.Replayed, current);
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
        var transitioned = current with
        {
            State = targetState,
            DecidedAtUtc = now,
            DecidedByProfileId = actorProfileId
        };
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
            current.State,
            transitioned,
            actorProfileId,
            now,
            reason,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MembershipMutationResult(
            MembershipMutationStatus.Applied,
            transitioned);
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
                    decided_at_utc, decided_by_profile_id, request_note,
                    notifications_opted_out, notify_action_required,
                    notify_commissioner_messages, notify_progress_status
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
                   decided_at_utc, decided_by_profile_id, request_note,
                   notifications_opted_out, notify_action_required,
                   notify_commissioner_messages, notify_progress_status
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

    private static async Task<Guid?> LoadActiveOwnerProfileIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        Guid excludedProfileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT account_profile_id
            FROM company_memberships
            WHERE company_id = $companyId
              AND role = 'owner'
              AND state = 'active'
              AND account_profile_id <> $excludedProfileId
            ORDER BY account_profile_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$excludedProfileId", excludedProfileId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string profileId ? Guid.Parse(profileId) : null;
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MembershipState? fromState,
        CompanyMembership snapshot,
        Guid actorProfileId,
        DateTimeOffset createdAtUtc,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO membership_events (
                event_id, company_id, account_profile_id, from_state, to_state,
                actor_profile_id, created_at_utc, role, requested_at_utc,
                decided_at_utc, decided_by_profile_id, request_note, reason)
            VALUES (
                $eventId, $companyId, $accountProfileId, $fromState, $toState,
                $actorProfileId, $createdAtUtc, $role, $requestedAtUtc,
                $decidedAtUtc, $decidedByProfileId, $requestNote, $reason);
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        AddMembershipIdentity(command, snapshot.CompanyId, snapshot.AccountProfileId);
        command.Parameters.AddWithValue(
            "$fromState",
            fromState.HasValue ? ToStorage(fromState.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$toState", ToStorage(snapshot.State));
        command.Parameters.AddWithValue("$actorProfileId", actorProfileId.ToString("D"));
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$role", ToStorage(snapshot.Role));
        command.Parameters.AddWithValue("$requestedAtUtc", snapshot.RequestedAtUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$decidedAtUtc",
            snapshot.DecidedAtUtc.HasValue
                ? snapshot.DecidedAtUtc.Value.ToString("O")
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$decidedByProfileId",
            snapshot.DecidedByProfileId.HasValue
                ? snapshot.DecidedByProfileId.Value.ToString("D")
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$requestNote",
            (object?)snapshot.RequestNote ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyMembership membership,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO company_memberships (
                company_id, account_profile_id, role, state, requested_at_utc,
                decided_at_utc, decided_by_profile_id, request_note)
            VALUES (
                $companyId, $accountProfileId, $role, $state, $requestedAtUtc,
                $decidedAtUtc, $decidedByProfileId, $requestNote)
            ON CONFLICT(company_id, account_profile_id) DO UPDATE SET
                role = excluded.role,
                state = excluded.state,
                requested_at_utc = excluded.requested_at_utc,
                decided_at_utc = excluded.decided_at_utc,
                decided_by_profile_id = excluded.decided_by_profile_id,
                request_note = excluded.request_note;
            """;
        AddMembershipIdentity(command, membership.CompanyId, membership.AccountProfileId);
        command.Parameters.AddWithValue("$role", ToStorage(membership.Role));
        command.Parameters.AddWithValue("$state", ToStorage(membership.State));
        command.Parameters.AddWithValue("$requestedAtUtc", membership.RequestedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$decidedAtUtc", membership.DecidedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$decidedByProfileId", membership.DecidedByProfileId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$requestNote", membership.RequestNote ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<CompanyMembershipInvitation?> LoadInvitationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tokenHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT invitation_id, company_id, legacy_crafter_id, issued_by_profile_id,
                   issued_at_utc, expires_at_utc, revoked_at_utc, revoked_by_profile_id,
                   consumed_at_utc, consumed_by_profile_id
            FROM company_membership_invitations
            WHERE token_hash = $tokenHash;
            """;
        command.Parameters.AddWithValue("$tokenHash", tokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadInvitation(reader, DateTimeOffset.MinValue)
            : null;
    }

    private static async Task<bool> HasConflictingCrafterBindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        Guid legacyCrafterId,
        Guid accountProfileId,
        CancellationToken cancellationToken)
    {
        var binding = await LoadCrafterBindingAsync(
            connection,
            transaction,
            companyId,
            legacyCrafterId,
            cancellationToken);
        return binding != null && binding.AccountProfileId != accountProfileId;
    }

    private static async Task UpsertInvitationCrafterBindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyMembershipInvitation invitation,
        Guid accountProfileId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO company_crafter_account_bindings (
                    company_id, legacy_crafter_id, account_profile_id, evidence_kind,
                    actor_profile_id, created_at_utc, updated_at_utc)
                VALUES (
                    $companyId, $legacyCrafterId, $accountProfileId, 'operator_confirmed',
                    $actorProfileId, $createdAtUtc, $updatedAtUtc)
                ON CONFLICT(company_id, legacy_crafter_id) DO UPDATE SET
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$companyId", invitation.CompanyId.ToString());
            command.Parameters.AddWithValue("$legacyCrafterId", invitation.LegacyCrafterId!.Value.ToString("D"));
            command.Parameters.AddWithValue("$accountProfileId", accountProfileId.ToString("D"));
            command.Parameters.AddWithValue("$actorProfileId", invitation.IssuedByProfileId.ToString("D"));
            command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var eventCommand = connection.CreateCommand();
        eventCommand.Transaction = transaction;
        eventCommand.CommandText = """
            INSERT INTO company_crafter_account_binding_events (
                event_id, company_id, legacy_crafter_id, account_profile_id,
                action, evidence_kind, actor_profile_id, created_at_utc)
            VALUES (
                $eventId, $companyId, $legacyCrafterId, $accountProfileId,
                'bound', 'operator_confirmed', $actorProfileId, $createdAtUtc);
            """;
        eventCommand.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        eventCommand.Parameters.AddWithValue("$companyId", invitation.CompanyId.ToString());
        eventCommand.Parameters.AddWithValue("$legacyCrafterId", invitation.LegacyCrafterId!.Value.ToString("D"));
        eventCommand.Parameters.AddWithValue("$accountProfileId", accountProfileId.ToString("D"));
        eventCommand.Parameters.AddWithValue("$actorProfileId", invitation.IssuedByProfileId.ToString("D"));
        eventCommand.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        await eventCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInvitationEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid invitationId,
        CompanyId companyId,
        string action,
        Guid actorProfileId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO company_membership_invitation_events (
                event_id, invitation_id, company_id, action, actor_profile_id, created_at_utc)
            VALUES ($eventId, $invitationId, $companyId, $action, $actorProfileId, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$invitationId", invitationId.ToString("D"));
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$action", action);
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
                    notifications_opted_out INTEGER NOT NULL DEFAULT 0,
                    notify_action_required INTEGER NULL,
                    notify_commissioner_messages INTEGER NULL,
                    notify_progress_status INTEGER NULL,
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
                    created_at_utc TEXT NOT NULL,
                    role TEXT NULL,
                    requested_at_utc TEXT NULL,
                    decided_at_utc TEXT NULL,
                    decided_by_profile_id TEXT NULL,
                    request_note TEXT NULL,
                    reason TEXT NULL CHECK(reason IS NULL OR length(reason) <= 500)
                );

                CREATE INDEX IF NOT EXISTS ix_company_memberships_account_state
                    ON company_memberships(account_profile_id, state);
                CREATE INDEX IF NOT EXISTS ix_membership_events_company_account
                    ON membership_events(company_id, account_profile_id, created_at_utc);

                CREATE TABLE IF NOT EXISTS membership_ownership_transfers (
                    transfer_id TEXT PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    source_profile_id TEXT NOT NULL,
                    target_profile_id TEXT NOT NULL,
                    source_disposition TEXT NOT NULL CHECK(source_disposition IN ('operator', 'revoked')),
                    projected_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS company_membership_invitations (
                    invitation_id TEXT PRIMARY KEY,
                    token_hash TEXT NOT NULL UNIQUE,
                    company_id TEXT NOT NULL,
                    legacy_crafter_id TEXT NULL,
                    issued_by_profile_id TEXT NOT NULL,
                    issued_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    revoked_at_utc TEXT NULL,
                    revoked_by_profile_id TEXT NULL,
                    consumed_at_utc TEXT NULL,
                    consumed_by_profile_id TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_company_membership_invitations_company
                    ON company_membership_invitations(company_id, issued_at_utc);

                CREATE TABLE IF NOT EXISTS company_membership_invitation_events (
                    event_id TEXT PRIMARY KEY,
                    invitation_id TEXT NOT NULL,
                    company_id TEXT NOT NULL,
                    action TEXT NOT NULL CHECK(action IN ('issued', 'revoked', 'consumed')),
                    actor_profile_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS membership_commission_attention (
                    company_id TEXT NOT NULL,
                    account_profile_id TEXT NOT NULL,
                    commission_id TEXT NOT NULL,
                    last_read_revision INTEGER NOT NULL CHECK(last_read_revision >= 0),
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY(company_id, account_profile_id, commission_id)
                );

                CREATE TABLE IF NOT EXISTS company_crafter_account_bindings (
                    company_id TEXT NOT NULL,
                    legacy_crafter_id TEXT NOT NULL,
                    account_profile_id TEXT NOT NULL,
                    evidence_kind TEXT NOT NULL CHECK(evidence_kind IN ('committed_discord_claim', 'operator_confirmed')),
                    actor_profile_id TEXT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY(company_id, legacy_crafter_id)
                );

                CREATE INDEX IF NOT EXISTS ix_company_crafter_bindings_account
                    ON company_crafter_account_bindings(company_id, account_profile_id);

                CREATE TABLE IF NOT EXISTS company_crafter_account_binding_events (
                    event_id TEXT PRIMARY KEY,
                    company_id TEXT NOT NULL,
                    legacy_crafter_id TEXT NOT NULL,
                    account_profile_id TEXT NOT NULL,
                    action TEXT NOT NULL CHECK(action IN ('bound', 'unbound')),
                    evidence_kind TEXT NOT NULL,
                    actor_profile_id TEXT NULL,
                    created_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_company_crafter_binding_events
                    ON company_crafter_account_binding_events(company_id, legacy_crafter_id, created_at_utc);

                CREATE TABLE IF NOT EXISTS company_crafter_account_binding_suppressions (
                    company_id TEXT NOT NULL,
                    legacy_crafter_id TEXT NOT NULL,
                    actor_profile_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    PRIMARY KEY(company_id, legacy_crafter_id)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await AddMembershipColumnIfMissingAsync(
                connection,
                "notifications_opted_out",
                "INTEGER NOT NULL DEFAULT 0",
                cancellationToken);
            await AddMembershipColumnIfMissingAsync(
                connection,
                "notify_action_required",
                "INTEGER NULL",
                cancellationToken);
            await AddMembershipColumnIfMissingAsync(
                connection,
                "notify_commissioner_messages",
                "INTEGER NULL",
                cancellationToken);
            await AddMembershipColumnIfMissingAsync(
                connection,
                "notify_progress_status",
                "INTEGER NULL",
                cancellationToken);
            await AddEventColumnIfMissingAsync(connection, "role", "TEXT NULL", cancellationToken);
            await AddEventColumnIfMissingAsync(
                connection,
                "requested_at_utc",
                "TEXT NULL",
                cancellationToken);
            await AddEventColumnIfMissingAsync(
                connection,
                "decided_at_utc",
                "TEXT NULL",
                cancellationToken);
            await AddEventColumnIfMissingAsync(
                connection,
                "decided_by_profile_id",
                "TEXT NULL",
                cancellationToken);
            await AddEventColumnIfMissingAsync(
                connection,
                "request_note",
                "TEXT NULL",
                cancellationToken);
            await AddEventColumnIfMissingAsync(
                connection,
                "reason",
                "TEXT NULL",
                cancellationToken);
            schemaReady = true;
        }
        finally
        {
            schemaGate.Release();
        }
    }

    private static async Task AddMembershipColumnIfMissingAsync(
        SqliteConnection connection,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(company_memberships);";
        var found = false;
        await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
        }
        if (!found)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE company_memberships ADD COLUMN {column} {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task AddEventColumnIfMissingAsync(
        SqliteConnection connection,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(membership_events);";
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
        alter.CommandText = $"ALTER TABLE membership_events ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<CrafterAccountBinding?> LoadCrafterBindingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        Guid legacyCrafterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT company_id, legacy_crafter_id, account_profile_id, evidence_kind,
                   actor_profile_id, created_at_utc, updated_at_utc
            FROM company_crafter_account_bindings
            WHERE company_id = $companyId AND legacy_crafter_id = $legacyCrafterId;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$legacyCrafterId", legacyCrafterId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCrafterBinding(reader)
            : null;
    }

    private static async Task<bool> IsCrafterAutoBindingSuppressedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        Guid legacyCrafterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM company_crafter_account_binding_suppressions
            WHERE company_id = $companyId AND legacy_crafter_id = $legacyCrafterId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$legacyCrafterId", legacyCrafterId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken) != null;
    }

    private static async Task DeleteCrafterBindingSuppressionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        Guid legacyCrafterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM company_crafter_account_binding_suppressions
            WHERE company_id = $companyId AND legacy_crafter_id = $legacyCrafterId;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$legacyCrafterId", legacyCrafterId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCrafterBindingSuppressionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompanyId companyId,
        Guid legacyCrafterId,
        Guid actorProfileId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO company_crafter_account_binding_suppressions (
                company_id, legacy_crafter_id, actor_profile_id, created_at_utc)
            VALUES ($companyId, $legacyCrafterId, $actorProfileId, $createdAtUtc)
            ON CONFLICT(company_id, legacy_crafter_id) DO UPDATE SET
                actor_profile_id = excluded.actor_profile_id,
                created_at_utc = excluded.created_at_utc;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$legacyCrafterId", legacyCrafterId.ToString("D"));
        command.Parameters.AddWithValue("$actorProfileId", actorProfileId.ToString("D"));
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CrafterAccountBinding ReadCrafterBinding(SqliteDataReader reader) =>
        new(
            ParseCompanyId(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            ParseCrafterBindingEvidence(reader.GetString(3)),
            reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));

    private static void AddCrafterBindingParameters(
        SqliteCommand command,
        CrafterAccountBinding binding)
    {
        command.Parameters.AddWithValue("$companyId", binding.CompanyId.ToString());
        command.Parameters.AddWithValue("$legacyCrafterId", binding.LegacyCrafterId.ToString("D"));
        command.Parameters.AddWithValue("$accountProfileId", binding.AccountProfileId.ToString("D"));
        command.Parameters.AddWithValue("$evidenceKind", ToStorage(binding.Evidence));
        command.Parameters.AddWithValue(
            "$actorProfileId",
            binding.ActorProfileId.HasValue
                ? binding.ActorProfileId.Value.ToString("D")
                : DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", binding.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", binding.UpdatedAtUtc.ToString("O"));
    }

    private static async Task InsertCrafterBindingEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CrafterAccountBinding binding,
        string action,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO company_crafter_account_binding_events (
                event_id, company_id, legacy_crafter_id, account_profile_id, action,
                evidence_kind, actor_profile_id, created_at_utc)
            VALUES (
                $eventId, $companyId, $legacyCrafterId, $accountProfileId, $action,
                $evidenceKind, $actorProfileId, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$companyId", binding.CompanyId.ToString());
        command.Parameters.AddWithValue("$legacyCrafterId", binding.LegacyCrafterId.ToString("D"));
        command.Parameters.AddWithValue("$accountProfileId", binding.AccountProfileId.ToString("D"));
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$evidenceKind", ToStorage(binding.Evidence));
        command.Parameters.AddWithValue(
            "$actorProfileId",
            binding.ActorProfileId.HasValue
                ? binding.ActorProfileId.Value.ToString("D")
                : DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", binding.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ToStorage(CrafterAccountBindingEvidence evidence) => evidence switch
    {
        CrafterAccountBindingEvidence.CommittedDiscordClaim => "committed_discord_claim",
        CrafterAccountBindingEvidence.OperatorConfirmed => "operator_confirmed",
        _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, null)
    };

    private static CrafterAccountBindingEvidence ParseCrafterBindingEvidence(string value) => value switch
    {
        "committed_discord_claim" => CrafterAccountBindingEvidence.CommittedDiscordClaim,
        "operator_confirmed" => CrafterAccountBindingEvidence.OperatorConfirmed,
        _ => throw new InvalidOperationException($"Unknown crafter account binding evidence '{value}'.")
    };

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
             reader.IsDBNull(7) ? null : reader.GetString(7),
             !reader.IsDBNull(8) && reader.GetInt32(8) != 0,
             ReadNotificationPreference(reader, 9, 8),
             ReadNotificationPreference(reader, 10, 8),
             ReadNotificationPreference(reader, 11, 8));

    private static CompanyMembershipInvitation ReadInvitation(
        SqliteDataReader reader,
        DateTimeOffset now)
    {
        var expiresAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture);
        DateTimeOffset? revokedAt = reader.IsDBNull(6)
            ? null
            : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);
        DateTimeOffset? consumedAt = reader.IsDBNull(8)
            ? null
            : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture);
        var state = consumedAt != null
            ? MembershipInvitationState.Consumed
            : revokedAt != null
                ? MembershipInvitationState.Revoked
                : expiresAt <= now
                    ? MembershipInvitationState.Expired
                    : MembershipInvitationState.Active;
        return new CompanyMembershipInvitation(
            Guid.Parse(reader.GetString(0)),
            ParseCompanyId(reader.GetString(1)),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            expiresAt,
            revokedAt,
            reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
            consumedAt,
            reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
            state);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static bool IsValidInvitationToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && token.Length is >= 40 and <= 128 &&
        token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool ReadNotificationPreference(
        SqliteDataReader reader,
        int preferenceOrdinal,
        int legacyOptOutOrdinal) =>
        reader.IsDBNull(preferenceOrdinal)
            ? reader.IsDBNull(legacyOptOutOrdinal) || reader.GetInt32(legacyOptOutOrdinal) == 0
            : reader.GetInt32(preferenceOrdinal) != 0;

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

    private static string? NormalizeReason(string? reason)
    {
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return null;
        }
        if (reason.Length > MaximumRequestNoteLength)
        {
            throw new ArgumentException($"Reason cannot exceed {MaximumRequestNoteLength} characters.");
        }
        return reason;
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

    private static string ToStorage(MembershipRole role) => role switch
    {
        MembershipRole.Owner => "owner",
        MembershipRole.Operator => "operator",
        MembershipRole.Crafter => "crafter",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
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
