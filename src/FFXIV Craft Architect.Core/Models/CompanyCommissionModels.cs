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

public enum CompanyCommissionPaymentPolicyRequestState
{
    Pending,
    Accepted,
    Refused
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
    CommissionPublicationRevoked,
    ParticipantRecoveryIssued,
    ParticipantRecoveryRedeemed,
    MigratedFromTradeOrder,
    MigratedTradeOrderHistory
}

public sealed record CompanyCommissionActor(
    string ActorId,
    CompanyCommissionActorKind Kind,
    string? DisplayName = null);

public sealed record CompanyCommissionOutputTerm(
    Guid LineId,
    int ItemId,
    string Name,
    int RequiredQuantity,
    bool MustBeHq);

public sealed record CompanyCommissionMaterialTerm(
    Guid LineId,
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
    string? CustomTerms = null,
    int CraftSynthCount = 0,
    decimal GilPerSynth = 0);

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
    DateTime IssuedAtUtc,
    DateTime? RevokedAtUtc = null);

public sealed record CompanyCommissionRecoveryGrant(
    Guid RecoveryGrantId,
    Guid ParticipantGrantId,
    long RecoveryRevision,
    DateTime IssuedAtUtc,
    DateTime? RedeemedAtUtc = null,
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

public sealed record CompanyCommissionPaymentPolicyChangeRequest(
    Guid RequestId,
    int RequestedAgainstTermsVersion,
    CompanyCommissionPaymentSchedule RequestedSchedule,
    string? RequestedCustomTerms,
    string Reason,
    CompanyCommissionPaymentPolicyRequestState State,
    DateTime RequestedAtUtc,
    DateTime? DecidedAtUtc = null,
    string? DecisionReason = null);

public sealed record CompanyCommissionMaterialClearance(
    CompanyCommissionClearanceState State,
    IReadOnlyList<CompanyCommissionMaterialQuantity> PromisedQuantities,
    DateTime? ReadyAtUtc = null,
    DateTime? ReceivedAtUtc = null,
    string? ReceivedByActorId = null);

public sealed record CompanyCommissionMaterialQuantity(
    Guid LineId,
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
    Guid LineId,
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
    public Guid? CommandId { get; init; }
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
    public TradeCompanyPublicationOwnership? LegacyOwnership { get; init; }
    public IReadOnlyList<CompanyCommissionDiscordBinding> DiscordBindings { get; init; } = [];
}

public sealed record CompanyCommissionProcessedCommand(
    Guid CommandId,
    string Fingerprint,
    Guid ActivityEventId,
    DateTime AppliedAtUtc);

public sealed record TradeCompanyCommission
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
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
    public CompanyCommissionRecoveryGrant? RecoveryGrant { get; init; }
    public CompanyCommissionPaymentPolicyChangeRequest? PaymentPolicyChangeRequest { get; init; }
    public int? ParticipantAcknowledgedTermsVersion { get; init; }
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
    public required string Title { get; init; }
    public required string CompanyDisplayName { get; init; }
    public required string Reference { get; init; }
    public required CompanyCommissionPublicViewState ViewState { get; init; }
    public required CompanyCommissionPublicTerms Terms { get; init; }
    public required TradeOrderStatus Status { get; init; }
    public required CompanyCommissionPublicGateState Gates { get; init; }
    public required bool ClearedToWork { get; init; }
    public required bool IsClaimed { get; init; }
    public IReadOnlyList<CompanyCommissionPublicOutputProgress> OutputProgress { get; init; } = [];
    public required CompanyCommissionPublicDeliveryReadiness DeliveryReadiness { get; init; }
    public required CompanyCommissionSettlementState SettlementState { get; init; }
    public required bool Closed { get; init; }
    public required long ProjectionRevision { get; init; }
}

public sealed record CompanyCommissionPublicTerms
{
    public required int Version { get; init; }
    public IReadOnlyList<CompanyCommissionOutputTerm> Outputs { get; init; } = [];
    public IReadOnlyList<CompanyCommissionMaterialTerm> Materials { get; init; } = [];
    public required CompanyCommissionPaymentTerms Payment { get; init; }
    public string DeliveryInstructions { get; init; } = string.Empty;
    public required CompanyCommissionPricingEvidence PricingEvidence { get; init; }
    public string ContactInstructions { get; init; } = string.Empty;
}

public sealed record CompanyCommissionPublicGateState(
    CompanyCommissionClearanceState Identity,
    CompanyCommissionClearanceState Payment,
    CompanyCommissionClearanceState CompanyMaterials);

public sealed record CompanyCommissionPublicOutputProgress(
    Guid LineId,
    int ItemId,
    int RequiredQuantity,
    int CompletedQuantity,
    int ReadyQuantity,
    int AcceptedQuantity,
    DateTime UpdatedAtUtc);

public sealed record CompanyCommissionPublicDeliveryReadiness(
    bool IsReady,
    DateTime? DeclaredAtUtc,
    DateTime? WithdrawnAtUtc);

public sealed record CompanyCommissionParticipantActivity(
    Guid EventId,
    long CommissionRevision,
    CompanyCommissionActorKind ActorKind,
    string? ActorDisplayName,
    CompanyCommissionSourceSurface SourceSurface,
    DateTime CreatedAtUtc,
    CompanyCommissionActivityKind Kind,
    int TermsVersion,
    string? Comment);

public sealed record CompanyCommissionParticipantBrief
{
    public required CompanyCommissionPublicBrief Public { get; init; }
    public CompanyCommissionProvisionalCrafter? ProvisionalCrafter { get; init; }
    public required long ParticipantCapabilityRevision { get; init; }
    public IReadOnlyList<CompanyCommissionParticipantActivity> Activity { get; init; } = [];
}

public sealed record CompanyCommissionOwnerProjection
{
    public required TradeOrder Order { get; init; }
    public required CompanyRecordRevision ObjectRevision { get; init; }
    public required CompanyRecordRevision CompanyRevision { get; init; }
}
