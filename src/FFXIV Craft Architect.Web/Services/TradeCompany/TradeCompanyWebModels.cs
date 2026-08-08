using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public enum TradeCompanyMutationDisposition
{
    Synced,
    LocalOnly,
    Pending,
    Conflict,
    Rejected
}

public enum TradeCommissionDestination
{
    PublicLink,
    DiscordChannel
}

public enum TradeCommissionDeliveryState
{
    Pending,
    Published,
    Failed,
    Revoked,
    Suppressed
}

public sealed record TradeCommissionPublicationProjection(
    Guid OrderId,
    TradeCommissionDestination Destination,
    TradeCommissionDeliveryState State,
    string? PublicId,
    string? DestinationLabel,
    DateTime UpdatedAtUtc,
    string? Message = null);

public sealed record TradeCommissionWorkflowResult(
    bool Success,
    TradeCompanyMutationDisposition Disposition,
    TradeCommissionPublicationProjection? Publication = null,
    string? Message = null);

public enum TradeDiscordNotificationDestinationMode
{
    CommissionerDirectMessage,
    UpdateChannel,
    Both
}

public enum TradeDiscordDirectMessageFallback
{
    None,
    UpdateChannel
}

public enum TradeDiscordNotificationBehavior
{
    Push,
    SilentPing,
    NoPing,
    Off
}

public sealed record TradeDiscordNotificationRoute(
    string CommissionerDiscordUserId,
    TradeDiscordNotificationDestinationMode DestinationMode,
    string? UpdateChannelId,
    TradeDiscordDirectMessageFallback DirectMessageFallback,
    TradeDiscordNotificationBehavior RoutineBehavior,
    TradeDiscordNotificationBehavior ActionRequiredBehavior,
    TradeDiscordNotificationBehavior CriticalExceptionBehavior,
    long Revision);

public sealed record TradeDiscordNotificationRouteUpdate(
    string CommissionerDiscordUserId,
    TradeDiscordNotificationDestinationMode DestinationMode,
    string? UpdateChannelId,
    TradeDiscordDirectMessageFallback DirectMessageFallback,
    TradeDiscordNotificationBehavior RoutineBehavior,
    TradeDiscordNotificationBehavior ActionRequiredBehavior,
    TradeDiscordNotificationBehavior CriticalExceptionBehavior,
    long ExpectedRevision,
    string IdempotencyKey);

public enum TradeDiscordNotificationRouteSaveStatus
{
    Saved,
    Conflict,
    Invalid
}

public sealed record TradeDiscordNotificationRouteSaveResult(
    TradeDiscordNotificationRouteSaveStatus Status,
    TradeDiscordNotificationRoute? Route,
    string? Error = null)
{
    public bool Success => Status == TradeDiscordNotificationRouteSaveStatus.Saved;
}

public enum TradeDiscordNotificationDestinationKind
{
    CommissionerDirectMessage,
    UpdateChannel
}

public enum TradeDiscordNotificationDiagnosticState
{
    Failed,
    ReconciliationRequired
}

public sealed record TradeDiscordNotificationDiagnostic(
    Guid DiagnosticId,
    Guid CommissionId,
    Guid EventId,
    long DesiredProjectionRevision,
    TradeDiscordNotificationDestinationKind Destination,
    TradeDiscordNotificationDiagnosticState State,
    string Summary,
    string Detail,
    string RecommendedAction,
    bool CanRetry,
    bool FallbackQueued,
    DateTimeOffset UpdatedAt);
