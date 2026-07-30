namespace FFXIV_Craft_Architect.Core.Models;

public static class CompanyCommissionProtocol
{
    public const int Version1 = 1;
}

public enum CompanyCommissionPublicViewState
{
    Draft,
    Published,
    Revoked
}

public enum CompanyCommissionPaymentSchedule
{
    Advance,
    OnDelivery,
    Custom
}

public enum CompanyCommissionClearanceState
{
    NotRequired,
    Pending,
    Satisfied
}

public enum CompanyCommissionSettlementState
{
    NotDue,
    Pending,
    Satisfied
}

public enum CompanyCommissionActorKind
{
    Commissioner,
    Crafter,
    System,
    Migration
}

public enum CompanyCommissionSourceSurface
{
    TradeArchitect,
    PublicBrief,
    Discord,
    HostedMigration,
    System
}

public enum CompanyCommissionActivityKind
{
    CommissionOpened,
    ClaimAccepted,
    ClaimRejected,
    ClaimReleased,
    ClaimRecovered,
    ProvisionalIdentitySubmitted,
    ProvisionalIdentityConfirmed,
    ProvisionalIdentityRejected,
    PaymentPolicyChangeRequested,
    PaymentPolicyChangeAccepted,
    PaymentPolicyChangeRefused,
    TermsAcknowledged,
    PaymentClearanceRecorded,
    CompanyMaterialsReady,
    CompanyMaterialsReceived,
    WorkClearanceAchieved,
    ProgressReported,
    CommentAdded,
    DeliveryReadinessDeclared,
    DeliveryReadinessWithdrawn,
    DeliveryReturnedToWork,
    DeliveryAccepted,
    SettlementRecorded,
    CommissionCanceled,
    CommissionClosed,
    MigratedFromTradeOrder,
    MigratedTradeOrderHistory
}

public sealed record CompanyCommissionActor(
    string ActorId,
    CompanyCommissionActorKind Kind,
    string? DisplayName = null);

public sealed record CompanyCommissionOutputTerm(
    int ItemId,
    string Name,
    int RequiredQuantity,
    bool MustBeHq);

public sealed record CompanyCommissionMaterialTerm(
    int ItemId,
    string Name,
    int Quantity,
    bool RequiresHq,
    CommissionMaterialResponsibility Responsibility,
    decimal UnitCost = 0,
    decimal TotalCost = 0);

public sealed record CompanyCommissionPaymentTerms(
    CompanyCommissionPaymentSchedule Schedule,
    string ContractLabel,
    decimal MaterialReimbursement,
    decimal MaterialAdjustment,
    decimal CraftLabor,
    decimal Total,
    string? CustomTerms = null);

public sealed record CompanyCommissionPricingEvidence(
    string CostBasis,
    string MarketScope,
    string Location,
    DateTime CapturedAtUtc);

public sealed record CompanyCommissionTermsVersion
{
    public required int Version { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required CompanyCommissionActor CreatedBy { get; init; }
    public IReadOnlyList<CompanyCommissionOutputTerm> Outputs { get; init; } = [];
    public IReadOnlyList<CompanyCommissionMaterialTerm> Materials { get; init; } = [];
    public required CompanyCommissionPaymentTerms Payment { get; init; }
    public string DeliveryInstructions { get; init; } = string.Empty;
    public required CompanyCommissionPricingEvidence PricingEvidence { get; init; }
    public string ContactInstructions { get; init; } = string.Empty;
    public string? ChangeSummary { get; init; }
}

public sealed record CompanyCommissionClaim(
    Guid ClaimId,
    int AcceptedTermsVersion,
    DateTime ClaimedAtUtc,
    Guid? CrafterId,
    Guid? ProvisionalCrafterId);

public sealed record CompanyCommissionProvisionalCrafter(
    Guid ProvisionalCrafterId,
    string CharacterName,
    string HomeWorld,
    string ContactMethod,
    string ContactValue,
    string? DiscordUserId,
    string? DiscordDisplayNameSnapshot,
    string? LodestoneCharacterId,
    string? LodestoneProfileUrl,
    DateTime SubmittedAtUtc);

public sealed record CompanyCommissionParticipantGrant(
    Guid GrantId,
    Guid ClaimId,
    int TermsVersionFloor,
    long CapabilityRevision,
    string CapabilityHash,
    DateTime IssuedAtUtc,
    DateTime? RevokedAtUtc = null);

public sealed record CompanyCommissionIdentityClearance(
    CompanyCommissionClearanceState State,
    string? LodestoneCharacterId = null,
    DateTime? CharacterVerifiedAtUtc = null,
    DateTime? OwnershipConfirmedAtUtc = null,
    string? ConfirmedByActorId = null);

public sealed record CompanyCommissionPaymentClearance(
    CompanyCommissionClearanceState State,
    DateTime? RecordedAtUtc = null,
    string? RecordedByActorId = null,
    string? Note = null);

public sealed record CompanyCommissionMaterialClearance(
    CompanyCommissionClearanceState State,
    IReadOnlyList<CompanyCommissionMaterialQuantity> PromisedQuantities,
    DateTime? ReadyAtUtc = null,
    DateTime? ReceivedAtUtc = null,
    string? ReceivedByActorId = null);

public sealed record CompanyCommissionMaterialQuantity(
    int ItemId,
    int Quantity);

public sealed record CompanyCommissionGateState(
    CompanyCommissionIdentityClearance Identity,
    CompanyCommissionPaymentClearance Payment,
    CompanyCommissionMaterialClearance CompanyMaterials)
{
    public bool ClearedToWork =>
        Identity.State is CompanyCommissionClearanceState.NotRequired or CompanyCommissionClearanceState.Satisfied &&
        Payment.State is CompanyCommissionClearanceState.NotRequired or CompanyCommissionClearanceState.Satisfied &&
        CompanyMaterials.State is CompanyCommissionClearanceState.NotRequired or CompanyCommissionClearanceState.Satisfied;
}

public sealed record CompanyCommissionOutputProgress(
    int ItemId,
    int RequiredQuantity,
    int CompletedQuantity,
    int ReadyQuantity,
    int AcceptedQuantity,
    DateTime UpdatedAtUtc,
    CompanyCommissionActor UpdatedBy);

public sealed record CompanyCommissionDeliveryReadiness(
    bool IsReady,
    DateTime? DeclaredAtUtc = null,
    DateTime? WithdrawnAtUtc = null,
    string? LastReason = null);

public sealed record CompanyCommissionActivityEvent
{
    public required Guid EventId { get; init; }
    public required Guid CommissionId { get; init; }
    public required long CommissionRevision { get; init; }
    public required CompanyCommissionActor Actor { get; init; }
    public required CompanyCommissionSourceSurface SourceSurface { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required CompanyCommissionActivityKind Kind { get; init; }
    public required int TermsVersion { get; init; }
    public string? Comment { get; init; }
    public string? PayloadJson { get; init; }
    public string? MigrationProvenance { get; init; }
}

public sealed record CompanyCommissionDiscordBinding(
    string ChannelId,
    string MessageId,
    long DesiredProjectionRevision);

public sealed record CompanyCommissionPublicMetadata
{
    public required string PublicBriefId { get; init; }
    public string? PublicUrl { get; init; }
    public required CompanyCommissionPublicViewState ViewState { get; init; }
    public DateTime? PublishedAtUtc { get; init; }
    public DateTime? RevokedAtUtc { get; init; }
    public string? ClaimCapabilityHash { get; init; }
    public TradeCompanyPublicationOwnership? LegacyOwnership { get; init; }
    public IReadOnlyList<CompanyCommissionDiscordBinding> DiscordBindings { get; init; } = [];
}

public sealed record CompanyCommissionProcessedCommand(
    Guid CommandId,
    string Fingerprint,
    long AppliedOrderRevision,
    Guid ActivityEventId,
    DateTime AppliedAtUtc);

public sealed record TradeCompanyCommission
{
    public required Guid CommissionId { get; init; }
    public required CompanyId CompanyId { get; init; }
    public required string CommissionerActorId { get; init; }
    public required string Reference { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime UpdatedAtUtc { get; init; }
    public required int CurrentTermsVersion { get; init; }
    public IReadOnlyList<CompanyCommissionTermsVersion> TermsVersions { get; init; } = [];
    public required CompanyCommissionPublicMetadata PublicMetadata { get; init; }
    public required long ActiveClaimCapabilityRevision { get; init; }
    public CompanyCommissionClaim? ActiveClaim { get; init; }
    public CompanyCommissionProvisionalCrafter? ProvisionalCrafter { get; init; }
    public CompanyCommissionParticipantGrant? ParticipantGrant { get; init; }
    public required CompanyCommissionGateState Gates { get; init; }
    public IReadOnlyList<CompanyCommissionOutputProgress> OutputProgress { get; init; } = [];
    public required CompanyCommissionDeliveryReadiness DeliveryReadiness { get; init; }
    public required CompanyCommissionSettlementState SettlementState { get; init; }
    public IReadOnlyList<CompanyCommissionActivityEvent> Activity { get; init; } = [];
    public IReadOnlyList<CompanyCommissionProcessedCommand> ProcessedCommands { get; init; } = [];

    public CompanyCommissionTermsVersion CurrentTerms =>
        TermsVersions.Single(terms => terms.Version == CurrentTermsVersion);

    public bool ClearedToWork => Gates.ClearedToWork;

    public bool IsClosed(TradeOrderStatus status) =>
        status == TradeOrderStatus.Completed &&
        SettlementState == CompanyCommissionSettlementState.Satisfied;
}

public sealed record CompanyCommissionPublicBrief
{
    public required string PublicBriefId { get; init; }
    public required Guid CommissionId { get; init; }
    public required string Reference { get; init; }
    public required CompanyCommissionPublicViewState ViewState { get; init; }
    public required CompanyCommissionTermsVersion Terms { get; init; }
    public required TradeOrderStatus Status { get; init; }
    public required CompanyCommissionGateState Gates { get; init; }
    public required bool ClearedToWork { get; init; }
    public Guid? AssignedCrafterId { get; init; }
    public CompanyCommissionProvisionalCrafter? ProvisionalCrafter { get; init; }
    public IReadOnlyList<CompanyCommissionOutputProgress> OutputProgress { get; init; } = [];
    public required CompanyCommissionDeliveryReadiness DeliveryReadiness { get; init; }
    public required CompanyCommissionSettlementState SettlementState { get; init; }
    public required bool Closed { get; init; }
    public IReadOnlyList<CompanyCommissionActivityEvent> Activity { get; init; } = [];
    public required long ProjectionRevision { get; init; }
}
