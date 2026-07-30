using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

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
    Failed = 5
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

public enum DiscordInterestClaimState
{
    Pending = 0,
    AssignmentPending = 1,
    Accepted = 2,
    Declined = 3
}

public sealed record DiscordInterestClaim(
    Guid ClaimId,
    Guid PublicationId,
    CompanyId CompanyId,
    Guid OrderId,
    string DiscordUserId,
    string DiscordDisplayName,
    DiscordInterestClaimState State,
    Guid? ResolvedCrafterId,
    CompanyRecordRevision? AcceptedOrderRevision,
    string? ResolutionIdempotencyKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public enum DiscordClaimTransitionStatus
{
    Applied,
    Replayed,
    Conflict,
    Missing
}

public sealed record DiscordClaimTransitionResult(
    DiscordClaimTransitionStatus Status,
    DiscordInterestClaim? Claim,
    string? Error = null)
{
    public bool Success =>
        Status is DiscordClaimTransitionStatus.Applied or DiscordClaimTransitionStatus.Replayed;
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
