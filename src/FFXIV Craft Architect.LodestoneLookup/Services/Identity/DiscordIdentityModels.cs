using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;

public sealed record DiscordIdentityLink(
    Guid LinkId,
    Guid ProfileId,
    string DiscordUserId,
    string DisplayNameSnapshot,
    DateTimeOffset LinkedAt,
    DateTimeOffset UpdatedAt);

public sealed record DiscordIdentityLinkStatus(
    bool Enabled,
    bool Linked,
    string? DisplayName,
    DateTimeOffset? LinkedAt);

public enum DiscordIdentityLinkResultStatus
{
    Linked,
    Refreshed,
    ProfileConflict,
    DiscordConflict
}

public sealed record DiscordIdentityLinkResult(
    DiscordIdentityLinkResultStatus Status,
    DiscordIdentityLink? Link = null);

public sealed record DiscordIdentityAuditEvent(
    Guid EventId,
    Guid ProfileId,
    string EventKind,
    string? DiscordUserId,
    DateTimeOffset CreatedAt);

public enum DiscordOAuthStateStatus
{
    Consumed,
    Unknown,
    Expired,
    Replayed
}

public sealed record DiscordOAuthStateConsumption(
    DiscordOAuthStateStatus Status,
    Guid? ProfileId = null,
    string? PkceVerifier = null);

public sealed record DiscordOAuthIdentity(
    string DiscordUserId,
    string DisplayName);

public sealed record DiscordLinkStartResponse(string AuthorizationUrl);

public sealed record DiscordParticipantExchangeRequest(
    string BootstrapToken,
    string ParticipantCredential);

public sealed record DiscordParticipantExchangeResponse(string PublicBriefId);

public sealed record DiscordInteractionTarget(
    string InteractionId,
    string DiscordUserId,
    CompanyId CompanyId,
    Guid CommissionId,
    string PublicBriefId);

public enum DiscordInteractionAccessStatus
{
    Authorized,
    IdentityUnlinked,
    IdentityInactive,
    TargetUnavailable,
    Forbidden
}

public enum DiscordInteractionActionKind
{
    OpenOwnerOrder,
    OpenParticipantCommission
}

public enum DiscordInteractionActionDelivery
{
    EphemeralOnly
}

public sealed record DiscordInteractionAction(
    DiscordInteractionActionKind Kind,
    string Label,
    Uri Uri,
    DiscordInteractionActionDelivery Delivery);

public sealed record DiscordInteractionAccessResolution(
    DiscordInteractionAccessStatus Status,
    Guid? ProfileId,
    bool IsCompanyOperator,
    bool IsActiveParticipant,
    IReadOnlyList<DiscordInteractionAction> Actions)
{
    public bool Authorized => Status == DiscordInteractionAccessStatus.Authorized;
}

public interface IDiscordInteractionAccessResolver
{
    Task<DiscordInteractionAccessResolution> ResolveAsync(
        DiscordInteractionTarget target,
        CancellationToken cancellationToken = default);

    Task<DiscordInteractionAccessResolution> IssueParticipantEntryAsync(
        DiscordInteractionTarget target,
        CancellationToken cancellationToken = default);
}

public interface IDiscordParticipantExchangeService
{
    Task<DiscordParticipantExchangeResponse?> ExchangeAsync(
        DiscordParticipantExchangeRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed record DiscordParticipantAuthority(
    Guid ProfileId,
    string DiscordUserId,
    CompanyId CompanyId,
    Guid CommissionId,
    string PublicBriefId,
    Guid ParticipantGrantId,
    long ParticipantCapabilityRevision,
    Uri PublicUrl,
    bool IsCompanyOperator,
    bool IsActiveParticipant);

internal sealed record DiscordParticipantBootstrapBinding(
    string ProviderEventId,
    Guid ProfileId,
    string DiscordUserId,
    CompanyId CompanyId,
    Guid CommissionId,
    string PublicBriefId,
    Guid ParticipantGrantId,
    long ParticipantCapabilityRevision,
    DateTimeOffset ExpiresAt);

internal enum DiscordParticipantBootstrapRedemptionStatus
{
    Redeemed,
    Replayed,
    Unknown,
    Expired,
    ReplayRejected
}

internal sealed record DiscordParticipantBootstrapRedemption(
    DiscordParticipantBootstrapRedemptionStatus Status,
    DiscordParticipantBootstrapBinding? Binding = null);
