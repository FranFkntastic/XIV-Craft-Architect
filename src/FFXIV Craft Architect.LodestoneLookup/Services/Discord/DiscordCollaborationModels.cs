using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public static class DiscordPublicationProjectionFormat
{
    public const int CurrentVersion = 3;
}

public sealed record DiscordCompanyInstallationBinding(
    CompanyId CompanyId,
    string ApplicationId,
    string GuildId,
    string ChannelId,
    DateTimeOffset UpdatedAt);

public enum DiscordPublicationState
{
    Open = 0,
    Assigned = 1,
    Closed = 2,
    Revoked = 3,
    ReconciliationRequired = 4,
    Failed = 5,
    TestFixture = 6,
    Suppressed = 7
}

public sealed record DiscordPublicationRecord(
    Guid PublicationId,
    CompanyId CompanyId,
    Guid OrderId,
    CompanyRecordRevision SourceOrderRevision,
    string PublicId,
    int BriefVersion,
    string ChannelId,
    string? MessageId,
    string ActionToken,
    DiscordPublicationState State,
    long DesiredProjectionRevision,
    int ProjectionFormatVersion,
    string IdempotencyKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum DiscordPublicationCreateStatus
{
    Created,
    Replayed,
    Conflict
}

public sealed record DiscordPublicationCreateResult(
    DiscordPublicationCreateStatus Status,
    DiscordPublicationRecord? Publication,
    string? Error = null)
{
    public bool Success =>
        Status is DiscordPublicationCreateStatus.Created or DiscordPublicationCreateStatus.Replayed;
}

public enum DiscordPublicationRetryStatus
{
    Queued,
    Conflict,
    Missing
}

public sealed record DiscordPublicationRetryResult(
    DiscordPublicationRetryStatus Status,
    DiscordPublicationRecord? Publication,
    string? Error = null)
{
    public bool Success => Status == DiscordPublicationRetryStatus.Queued;
}

public enum DiscordPublicationReconcileStatus
{
    Queued,
    Conflict,
    Missing
}

public sealed record DiscordPublicationReconcileResult(
    DiscordPublicationReconcileStatus Status,
    DiscordPublicationRecord? Publication,
    string? Error = null)
{
    public bool Success => Status == DiscordPublicationReconcileStatus.Queued;
}

public enum DiscordOutboxState
{
    Pending = 0,
    InFlight = 1,
    Retry = 2,
    Succeeded = 3,
    ReconciliationRequired = 5,
    Failed = 6
}
