using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public enum TradeCompanyConnectionState
{
    LocalOnly,
    Refreshing,
    Current,
    Pending,
    Conflict,
    Unavailable
}

public sealed record TradeCompanyConnectionSnapshot(
    TradeCompanyConnectionState State,
    CompanyId? CompanyId,
    CompanyRevision Revision,
    int PendingCount,
    int ConflictCount,
    string? Message)
{
    public bool IsCurrent => State == TradeCompanyConnectionState.Current;

    public static TradeCompanyConnectionSnapshot LocalOnly() =>
        new(
            TradeCompanyConnectionState.LocalOnly,
            null,
            CompanyRevision.None,
            0,
            0,
            "This company is stored only in this browser.");
}

public enum TradeCompanyMutationDisposition
{
    Synced,
    LocalOnly,
    Pending,
    Conflict,
    Rejected
}

public sealed record TradeCompanyWebMutationResult(
    TradeCompanyMutationDisposition Disposition,
    TradeCompanyRecordEnvelope? Record = null,
    TradeCompanyRecordEnvelope? CurrentRecord = null,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool IsRemoteCurrent => Disposition == TradeCompanyMutationDisposition.Synced;
}

public sealed record TradeCompanyRefreshResult(
    TradeCompanyConnectionSnapshot Connection,
    IReadOnlyList<TradeCompanyRecordEnvelope> ChangedRecords);

public sealed record TradeCompanyPendingMutation(
    TradeCompanyMutationRequest Request,
    DateTime QueuedAtUtc,
    string? LastError);

public sealed record TradeCompanyRecordConflict(
    string RecordKind,
    string RecordId,
    TradeCompanyRecordEnvelope? CurrentRecord,
    DateTime DetectedAtUtc,
    string Message);

public sealed record TradeOrderMutationOutcome(
    bool LocalSaved,
    TradeCompanyMutationDisposition Disposition,
    TradeOrder SavedOrder,
    TradeOrder? CurrentRemoteOrder = null,
    string? Message = null)
{
    public bool HasConflict => Disposition == TradeCompanyMutationDisposition.Conflict;
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
    Revoked
}

public enum TradeCommissionInterestState
{
    Pending,
    Accepted,
    Declined,
    Withdrawn,
    Superseded
}

public static class TradeCompanyWebDocumentKinds
{
    public const string InterestProjection = "tradeCommissionInterest";
    public const string PublicationProjection = "tradeCommissionPublication";
    public const string PublicationCommand = "tradeCommissionPublicationCommand";
    public const string InterestResolutionCommand = "tradeCommissionInterestResolutionCommand";
    public const string InterestResolutionReceipt = "tradeCommissionInterestResolutionReceipt";
}

public sealed record TradeCommissionInterest(
    string ClaimId,
    Guid OrderId,
    string DiscordUserId,
    string DisplayName,
    TradeCommissionInterestState State,
    Guid? MatchedCrafterId,
    DateTime CreatedAtUtc,
    string? Message = null,
    string DocumentKind = TradeCompanyWebDocumentKinds.InterestProjection);

public sealed record TradeCommissionPublicationProjection(
    Guid OrderId,
    TradeCommissionDestination Destination,
    TradeCommissionDeliveryState State,
    string? PublicId,
    string? DestinationLabel,
    DateTime UpdatedAtUtc,
    string? Message = null,
    string DocumentKind = TradeCompanyWebDocumentKinds.PublicationProjection);

public sealed record TradeCommissionPublicationCommand(
    string Action,
    Guid OrderId,
    TradeCommissionDestination Destination,
    CommissionBriefDocument Brief,
    CompanyRecordRevision OrderRevision,
    DateTime RequestedAtUtc,
    string DocumentKind = TradeCompanyWebDocumentKinds.PublicationCommand);

public sealed record TradeCommissionInterestResolutionCommand(
    string Action,
    string ClaimId,
    Guid OrderId,
    Guid? CrafterId,
    CompanyRecordRevision OrderRevision,
    DateTime RequestedAtUtc,
    string DocumentKind = TradeCompanyWebDocumentKinds.InterestResolutionCommand);

public sealed record TradeCommissionInterestResolutionReceipt(
    TradeCommissionInterest Claim,
    TradeOrder? UpdatedOrder,
    string? Message = null,
    string DocumentKind = TradeCompanyWebDocumentKinds.InterestResolutionReceipt);

public sealed record TradeCommissionWorkflowResult(
    bool Success,
    TradeCompanyMutationDisposition Disposition,
    TradeCommissionPublicationProjection? Publication = null,
    TradeCommissionInterestResolutionReceipt? Resolution = null,
    string? Message = null);
