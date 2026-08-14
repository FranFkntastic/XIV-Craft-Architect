using System.Text.Json.Serialization;

namespace FFXIV_Craft_Architect.Core.Models;

public sealed record CompanyCommissionCommandContext(
    CompanyId CompanyId,
    Guid CommissionId,
    CompanyRecordRevision ExpectedObjectRevision,
    CompanyRecordRevision ExpectedCompanyRevision,
    Guid CommandId,
    int ProtocolVersion);

public interface ICompanyCommissionCommand
{
    CompanyCommissionCommandContext Context { get; }
}

public interface ICompanyCommissionCompanyCommand : ICompanyCommissionCommand;

public interface ICompanyCommissionParticipantCommand : ICompanyCommissionCommand;

public sealed record CreateCompanyCommissionCommand(
    CompanyCommissionCommandContext Context,
    string Reference,
    CompanyCommissionTermsVersion Terms,
    CompanyCommissionPublicMetadata PublicMetadata) : ICompanyCommissionCompanyCommand;

public sealed record UpdateCompanyCommissionDraftCommand(
    CompanyCommissionCommandContext Context,
    CompanyCommissionTermsVersion Terms,
    CompanyCommissionDraftWorkPackage WorkPackage) : ICompanyCommissionCompanyCommand;

public sealed record CompanyCommissionDraftWorkPackage(
    IReadOnlyList<TradeRequestedOrderOutput> RequestedOutputs,
    TradeOrderSourceSnapshot SourceSnapshot,
    string? CraftPlanId,
    string? CraftPlanName,
    DateTime? CraftPlanSavedAtUtc,
    TradeOrderCraftPlanLinkKind CraftPlanLinkKind);

public sealed record AmendCompanyCommissionTermsCommand(
    CompanyCommissionCommandContext Context,
    CompanyCommissionTermsVersion Terms,
    string Reason,
    CompanyCommissionDraftWorkPackage? WorkPackage = null) : ICompanyCommissionCompanyCommand;

public sealed record OpenCompanyCommissionCommand(
    CompanyCommissionCommandContext Context) : ICompanyCommissionCompanyCommand;

public sealed record ClaimCompanyCommissionCommand(
    CompanyCommissionCommandContext Context,
    int TermsVersion,
    CompanyCommissionProvisionalCrafter? ProvisionalCrafter,
    Guid? ExistingCrafterId,
    CompanyCommissionClaimAccountEvidence? AccountEvidence = null) :
    ICompanyCommissionParticipantCommand;

public sealed record ReleaseCompanyCommissionClaimCommand(
    CompanyCommissionCommandContext Context,
    string Reason) : ICompanyCommissionParticipantCommand;

public sealed record RejectCompanyCommissionClaimCommand(
    CompanyCommissionCommandContext Context,
    string Reason,
    bool BlockProvisionalContact) : ICompanyCommissionCompanyCommand;

public sealed record SubmitCompanyCommissionIdentityCommand(
    CompanyCommissionCommandContext Context,
    CompanyCommissionProvisionalCrafter ProvisionalCrafter) : ICompanyCommissionParticipantCommand;

public sealed record ConfirmCompanyCommissionIdentityCommand(
    CompanyCommissionCommandContext Context,
    Guid CrafterId,
    string LodestoneCharacterId) : ICompanyCommissionCompanyCommand;

public sealed record RequestCompanyCommissionPaymentPolicyChangeCommand(
    CompanyCommissionCommandContext Context,
    CompanyCommissionPaymentSchedule RequestedSchedule,
    string? RequestedCustomTerms,
    string Reason) : ICompanyCommissionParticipantCommand;

public sealed record DecideCompanyCommissionPaymentPolicyChangeCommand(
    CompanyCommissionCommandContext Context,
    bool Accepted,
    string Reason) : ICompanyCommissionCompanyCommand;

public sealed record AcknowledgeCompanyCommissionTermsCommand(
    CompanyCommissionCommandContext Context,
    int TermsVersion) : ICompanyCommissionParticipantCommand;

public sealed record RecordCompanyCommissionPaymentCommand(
    CompanyCommissionCommandContext Context,
    string Note) : ICompanyCommissionCompanyCommand;

public sealed record ConfirmCompanyCommissionPaymentReceivedCommand(
    CompanyCommissionCommandContext Context,
    int TermsVersion,
    string Note) : ICompanyCommissionParticipantCommand;

public sealed record RetractCompanyCommissionPaymentAttestationCommand(
    CompanyCommissionCommandContext Context,
    string Reason) :
    ICompanyCommissionCompanyCommand,
    ICompanyCommissionParticipantCommand;

public sealed record MarkCompanyCommissionMaterialsReadyCommand(
    CompanyCommissionCommandContext Context,
    IReadOnlyList<CompanyCommissionMaterialQuantity> Quantities) : ICompanyCommissionCompanyCommand;

public sealed record AcknowledgeCompanyCommissionMaterialsCommand(
    CompanyCommissionCommandContext Context,
    IReadOnlyList<CompanyCommissionMaterialQuantity> Quantities) : ICompanyCommissionParticipantCommand;

public sealed record ReportCompanyCommissionProgressCommand(
    CompanyCommissionCommandContext Context,
    IReadOnlyList<CompanyCommissionProgressQuantity> Outputs,
    string? Comment = null) : ICompanyCommissionParticipantCommand;

public sealed record CompanyCommissionProgressQuantity(
    Guid LineId,
    int ItemId,
    int CompletedQuantity,
    int ReadyQuantity);

public sealed record AddCompanyCommissionCommentCommand(
    CompanyCommissionCommandContext Context,
    string Comment) :
    ICompanyCommissionCompanyCommand,
    ICompanyCommissionParticipantCommand;

public sealed record AddCompanyCommissionPrivateNoteCommand(
    CompanyCommissionCommandContext Context,
    string Comment) : ICompanyCommissionCompanyCommand;

public sealed record DeclareCompanyCommissionReadinessCommand(
    CompanyCommissionCommandContext Context,
    string? Comment = null) : ICompanyCommissionParticipantCommand;

public sealed record WithdrawCompanyCommissionReadinessCommand(
    CompanyCommissionCommandContext Context,
    string Reason) : ICompanyCommissionParticipantCommand;

public sealed record ReturnCompanyCommissionToWorkCommand(
    CompanyCommissionCommandContext Context,
    string Reason) : ICompanyCommissionCompanyCommand;

public sealed record AcceptCompanyCommissionDeliveryCommand(
    CompanyCommissionCommandContext Context) : ICompanyCommissionCompanyCommand;

public sealed record RecordCompanyCommissionSettlementCommand(
    CompanyCommissionCommandContext Context,
    string Note) : ICompanyCommissionCompanyCommand;

public sealed record ConfirmCompanyCommissionSettlementReceivedCommand(
    CompanyCommissionCommandContext Context,
    int TermsVersion,
    string Note) : ICompanyCommissionParticipantCommand;

public sealed record RetractCompanyCommissionSettlementAttestationCommand(
    CompanyCommissionCommandContext Context,
    string Reason) :
    ICompanyCommissionCompanyCommand,
    ICompanyCommissionParticipantCommand;

public sealed record ResetCompanyCommissionParticipantRecoveryCommand(
    CompanyCommissionCommandContext Context) : ICompanyCommissionCompanyCommand;

public sealed record RedeemCompanyCommissionParticipantRecoveryCommand(
    CompanyCommissionCommandContext Context,
    Guid RecoveryGrantId) : ICompanyCommissionParticipantCommand;

public sealed record CancelCompanyCommissionCommand(
    CompanyCommissionCommandContext Context,
    string Reason) : ICompanyCommissionCompanyCommand;

public sealed record ReopenCompanyCommissionCommand(
    CompanyCommissionCommandContext Context,
    string Resolution) : ICompanyCommissionCompanyCommand;

public sealed record RevokeCompanyCommissionPublicationCommand(
    CompanyCommissionCommandContext Context) : ICompanyCommissionCompanyCommand;

public enum CompanyCommissionMutationStatus
{
    Applied,
    Replayed,
    Conflict,
    Rejected,
    NotFound,
    Unauthorized
}

public sealed record CompanyCommissionMutationResult(
    CompanyCommissionMutationStatus Status,
    TradeOrder? Order = null,
    CompanyCommissionActivityEvent? Activity = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    [property: JsonIgnore] CompanyRecordRevision? ObjectRevision = null,
    [property: JsonIgnore] CompanyRecordRevision? CompanyRevision = null)
{
    public bool Success =>
        Status is CompanyCommissionMutationStatus.Applied or CompanyCommissionMutationStatus.Replayed;
}
