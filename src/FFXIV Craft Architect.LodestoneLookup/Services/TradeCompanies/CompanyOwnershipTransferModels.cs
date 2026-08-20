using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public enum PreviousOwnerDisposition
{
    Operator,
    Revoked
}

public enum CompanyOwnershipTransferStatus
{
    Applied,
    Replayed,
    NotFound,
    Forbidden,
    InvalidTarget,
    Conflict
}

public sealed record CompanyOwnershipTransferCounts(
    int CompanyProfiles,
    int Orders,
    int Crafters,
    int Publications,
    int PayrollDrafts,
    int LinkedPlans,
    int DeepArchivedOrders,
    int Collisions,
    int TargetOnlyObjects);

public sealed record CompanyOwnershipTransferPreview(
    CompanyId CompanyId,
    Guid SourceProfileId,
    Guid TargetProfileId,
    string TargetDisplayName,
    string ScopeFingerprint,
    CompanyOwnershipTransferCounts Counts);

public sealed record CompanyOwnershipTransferReceipt(
    Guid TransferId,
    Guid IdempotencyKey,
    CompanyId CompanyId,
    Guid SourceProfileId,
    Guid TargetProfileId,
    PreviousOwnerDisposition PreviousOwnerDisposition,
    string ScopeFingerprint,
    CompanyOwnershipTransferCounts Counts,
    DateTimeOffset CommittedAtUtc,
    DateTimeOffset? MembershipProjectedAtUtc);

public sealed record CompanyOwnershipTransferResult(
    CompanyOwnershipTransferStatus Status,
    CompanyOwnershipTransferReceipt? Receipt = null,
    CompanyOwnershipTransferPreview? Preview = null,
    string? Error = null);
