namespace FFXIV_Craft_Architect.Core.Models;

public sealed record CompanyCommissionCommandContext(
    CompanyId CompanyId,
    Guid CommissionId,
    long ExpectedObjectRevision,
    long ExpectedCompanyRevision,
    Guid CommandId,
    string Fingerprint,
    int ProtocolVersion,
    CompanyCommissionActor Actor,
    CompanyCommissionSourceSurface SourceSurface);

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
    CompanyCommissionTermsVersion Terms) : ICompanyCommissionCompanyCommand;

public sealed record OpenCompanyCommissionCommand(
    CompanyCommissionCommandContext Context) : ICompanyCommissionCompanyCommand;

public sealed record ClaimCompanyCommissionCommand(
    CompanyCommissionCommandContext Context,
    int TermsVersion,
    CompanyCommissionProvisionalCrafter? ProvisionalCrafter,
    Guid? ExistingCrafterId) : ICompanyCommissionParticipantCommand;

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
    CompanyCommissionPaymentTerms RequestedPayment,
    string Reason) : ICompanyCommissionParticipantCommand;

public sealed record DecideCompanyCommissionPaymentPolicyChangeCommand(
    CompanyCommissionCommandContext Context,
    bool Accepted,
    string Reason,
    CompanyCommissionTermsVersion? AcceptedTerms) : ICompanyCommissionCompanyCommand;

public sealed record AcknowledgeCompanyCommissionTermsCommand(
    CompanyCommissionCommandContext Context,
    int TermsVersion) : ICompanyCommissionParticipantCommand;

public sealed record RecordCompanyCommissionPaymentCommand(
    CompanyCommissionCommandContext Context,
    string Note) : ICompanyCommissionCompanyCommand;

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
    int ItemId,
    int CompletedQuantity,
    int ReadyQuantity);

public sealed record AddCompanyCommissionCommentCommand(
    CompanyCommissionCommandContext Context,
    string Comment) : ICompanyCommissionCommand;

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

public sealed record RecoverCompanyCommissionParticipantCapabilityCommand(
    CompanyCommissionCommandContext Context,
    string NewCapabilityHash) : ICompanyCommissionCompanyCommand;

public sealed record CancelCompanyCommissionCommand(
    CompanyCommissionCommandContext Context,
    string Reason) : ICompanyCommissionCompanyCommand;

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
    string? ErrorMessage = null)
{
    public bool Success =>
        Status is CompanyCommissionMutationStatus.Applied or CompanyCommissionMutationStatus.Replayed;
}
