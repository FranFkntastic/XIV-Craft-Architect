using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public static class DiscordRuntimePermission
{
    public const long ViewChannel = 1L << 10;
    public const long SendMessages = 1L << 11;
    public const long EmbedLinks = 1L << 14;
    public const long Required =
        ViewChannel |
        SendMessages |
        EmbedLinks;

    public static bool CanPublish(long permissions) =>
        (permissions & Required) == Required;
}

public sealed record DiscordCompanyInstallationBinding(
    Guid InstallationId,
    CompanyId CompanyId,
    string ApplicationId,
    string GuildId,
    string ChannelId,
    long GrantedPermissions,
    bool Active,
    DateTimeOffset VerifiedAt);

public enum DiscordPublicationState
{
    Open,
    Assigned,
    Closed,
    Revoked,
    ReconciliationRequired,
    Failed
}

public sealed record DiscordPublicationRecord(
    Guid PublicationId,
    CompanyId CompanyId,
    Guid OrderId,
    CompanyRecordRevision SourceOrderRevision,
    string PublicId,
    int BriefVersion,
    Guid InstallationId,
    string ApplicationId,
    string GuildId,
    string ChannelId,
    string? MessageId,
    string ActionToken,
    DiscordPublicationState State,
    long DesiredProjectionRevision,
    long AppliedProjectionRevision,
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

public enum DiscordInterestClaimState
{
    Pending,
    AssignmentPending,
    Accepted,
    Declined,
    Withdrawn,
    Superseded
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

public sealed record DiscordRosterIdentityBinding(
    CompanyId CompanyId,
    string DiscordUserId,
    Guid CrafterId,
    string DiscordDisplayName,
    DateTimeOffset BoundAt);

public enum DiscordOutboxState
{
    Pending,
    InFlight,
    Retry,
    Succeeded,
    Superseded,
    ReconciliationRequired,
    Failed
}
