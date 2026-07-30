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

public sealed record TradeCommissionInterest(
    string ClaimId,
    Guid OrderId,
    string DiscordUserId,
    string DisplayName,
    TradeCommissionInterestState State,
    Guid? MatchedCrafterId,
    DateTime CreatedAtUtc,
    string? Message = null);

public sealed record TradeCommissionPublicationProjection(
    Guid OrderId,
    TradeCommissionDestination Destination,
    TradeCommissionDeliveryState State,
    string? PublicId,
    string? DestinationLabel,
    DateTime UpdatedAtUtc,
    string? Message = null);

public sealed record TradeCommissionInterestResolutionReceipt(
    TradeCommissionInterest Claim,
    TradeOrder? UpdatedOrder,
    long? UpdatedOrderRevision = null,
    string? Message = null);

public sealed record TradeCommissionWorkflowResult(
    bool Success,
    TradeCompanyMutationDisposition Disposition,
    TradeCommissionPublicationProjection? Publication = null,
    TradeCommissionInterestResolutionReceipt? Resolution = null,
    string? Message = null);
