using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed record CompanyCommissionDomainTransition(
    TradeOrder UpdatedOrder,
    CompanyCommissionActivityKind ActivityKind,
    string? Comment = null,
    string? PayloadJson = null,
    CompanyCommissionActivityVisibility Visibility =
        CompanyCommissionActivityVisibility.Shared);

public static class CompanyCommissionCommandWorkflow
{
    public static CompanyCommissionDomainTransition Apply(
        TradeOrder source,
        ICompanyCommissionCommand command,
        CompanyCommissionActor actor,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        var commission = RequireCommission(source, command.Context);

        return command switch
        {
            UpdateCompanyCommissionDraftCommand update =>
                UpdateDraft(source, commission, update, nowUtc, actor),
            AmendCompanyCommissionTermsCommand amend =>
                AmendTerms(source, commission, amend, nowUtc, actor),
            OpenCompanyCommissionCommand =>
                Open(source, commission),
            ClaimCompanyCommissionCommand claim =>
                Claim(source, commission, claim, nowUtc),
            ReleaseCompanyCommissionClaimCommand release =>
                Release(source, commission, release.Reason, nowUtc, rejected: false),
            RejectCompanyCommissionClaimCommand reject =>
                Release(source, commission, reject.Reason, nowUtc, rejected: true),
            SubmitCompanyCommissionIdentityCommand submit =>
                SubmitIdentity(source, commission, submit, nowUtc),
            ConfirmCompanyCommissionIdentityCommand confirm =>
                ConfirmIdentity(source, commission, confirm, nowUtc, actor),
            RequestCompanyCommissionPaymentPolicyChangeCommand request =>
                RequestPaymentChange(source, commission, request, nowUtc),
            DecideCompanyCommissionPaymentPolicyChangeCommand decide =>
                DecidePaymentChange(source, commission, decide, nowUtc, actor),
            AcknowledgeCompanyCommissionTermsCommand acknowledge =>
                AcknowledgeTerms(source, commission, acknowledge),
            RecordCompanyCommissionPaymentCommand payment =>
                RecordPayment(source, commission, payment, nowUtc, actor),
            ConfirmCompanyCommissionPaymentReceivedCommand payment =>
                ConfirmPaymentReceived(source, commission, payment, nowUtc, actor),
            RetractCompanyCommissionPaymentAttestationCommand payment =>
                RetractPaymentAttestation(source, commission, payment, actor),
            MarkCompanyCommissionMaterialsReadyCommand ready =>
                MarkMaterialsReady(source, commission, ready, nowUtc),
            AcknowledgeCompanyCommissionMaterialsCommand received =>
                AcknowledgeMaterials(source, commission, received, nowUtc, actor),
            ReportCompanyCommissionProgressCommand progress =>
                ReportProgress(source, commission, progress, nowUtc, actor),
            AddCompanyCommissionCommentCommand comment =>
                AddComment(source, commission, comment),
            AddCompanyCommissionPrivateNoteCommand note =>
                AddPrivateNote(source, commission, note),
            DeclareCompanyCommissionReadinessCommand ready =>
                DeclareReadiness(source, commission, ready, nowUtc),
            WithdrawCompanyCommissionReadinessCommand withdraw =>
                ReturnToWork(source, commission, withdraw.Reason, nowUtc, commissioner: false),
            ReturnCompanyCommissionToWorkCommand returned =>
                ReturnToWork(source, commission, returned.Reason, nowUtc, commissioner: true),
            AcceptCompanyCommissionDeliveryCommand =>
                AcceptDelivery(source, commission, nowUtc, actor),
            RecordCompanyCommissionSettlementCommand settlement =>
                RecordSettlement(source, commission, settlement, nowUtc, actor),
            ConfirmCompanyCommissionSettlementReceivedCommand settlement =>
                ConfirmSettlementReceived(source, commission, settlement, nowUtc, actor),
            RetractCompanyCommissionSettlementAttestationCommand settlement =>
                RetractSettlementAttestation(source, commission, settlement, actor),
            ResetCompanyCommissionParticipantRecoveryCommand reset =>
                ResetRecovery(source, commission, reset, nowUtc),
            RedeemCompanyCommissionParticipantRecoveryCommand redeem =>
                RedeemRecovery(source, commission, redeem, nowUtc),
            CancelCompanyCommissionCommand cancel =>
                Cancel(source, commission, cancel.Reason),
            RevokeCompanyCommissionPublicationCommand =>
                RevokePublication(source, commission, nowUtc),
            CreateCompanyCommissionCommand =>
                throw new InvalidOperationException(
                    "Commission creation is performed by canonical publication conversion."),
            _ => throw new InvalidOperationException(
                $"Unsupported company commission command '{command.GetType().Name}'.")
        };
    }

    private static CompanyCommissionDomainTransition UpdateDraft(
        TradeOrder source,
        TradeCompanyCommission commission,
        UpdateCompanyCommissionDraftCommand command,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        Require(
            commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Draft,
            "Only an unpublished commission draft can be edited directly.");
        Require(commission.ActiveClaim == null, "Claimed terms cannot be edited as a draft.");
        if (command.WorkPackage is not
            {
                RequestedOutputs.Count: > 0,
                SourceSnapshot: { }
            } workPackage)
        {
            throw new InvalidOperationException(
                "Draft edits require a canonical work package with at least one output.");
        }
        ValidateTerms(command.Terms);
        Require(
            command.Terms.Version == commission.CurrentTermsVersion,
            "Draft edits must replace the current unclaimed terms version.");
        RequireDraftWorkPackageMatchesTerms(workPackage, command.Terms);
        var terms = command.Terms with
        {
            CreatedAtUtc = nowUtc,
            CreatedBy = actor
        };
        var updated = Copy(source);
        updated.SourceSnapshot = TradeOrderWorkflow.CopySourceSnapshot(
            workPackage.SourceSnapshot);
        updated.CraftPlanId = workPackage.CraftPlanId;
        updated.CraftPlanName = workPackage.CraftPlanName;
        updated.CraftPlanSavedAtUtc = workPackage.CraftPlanSavedAtUtc;
        updated.CraftPlanLinkKind = workPackage.CraftPlanLinkKind;
        return Transition(
            updated,
            commission with
            {
                TermsVersions = commission.TermsVersions
                    .Where(item => item.Version != terms.Version)
                    .Append(terms)
                    .OrderBy(item => item.Version)
                    .ToArray(),
                Gates = commission.Gates with
                {
                    Payment = CreatePaymentGate(terms),
                    CompanyMaterials = CreateMaterialGate(terms)
                },
                OutputProgress = terms.Outputs.Select(output =>
                    new CompanyCommissionOutputProgress(
                        output.LineId,
                        output.ItemId,
                        output.RequiredQuantity,
                        0,
                        0,
                        0,
                        nowUtc,
                        actor)).ToArray(),
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(false),
                SettlementState = CompanyCommissionSettlementState.NotDue
            },
            CompanyCommissionActivityKind.DraftUpdated,
            "Updated draft commission terms.",
            visibility: CompanyCommissionActivityVisibility.CompanyOnly);
    }

    private static CompanyCommissionDomainTransition AmendTerms(
        TradeOrder source,
        TradeCompanyCommission commission,
        AmendCompanyCommissionTermsCommand command,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        Require(
            commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Published,
            "Only a published commission can create a terms revision.");
        Require(
            !TradeOrderStatusWorkflow.IsArchived(source.Status),
            "Closed commission terms cannot be revised.");
        RequireReason(command.Reason);
        ValidateTerms(command.Terms);
        Require(
            command.Terms.Version == checked(commission.CurrentTermsVersion + 1),
            "A terms revision must advance the canonical version exactly once.");
        Require(
            commission.OutputProgress.All(item =>
                item.CompletedQuantity == 0 &&
                item.ReadyQuantity == 0 &&
                item.AcceptedQuantity == 0) &&
            !commission.DeliveryReadiness.IsReady,
            "Terms cannot be revised after production progress begins.");

        var terms = command.Terms with
        {
            CreatedAtUtc = nowUtc,
            CreatedBy = actor,
            ChangeSummary = command.Reason.Trim()
        };
        return Transition(
            source,
            commission with
            {
                CurrentTermsVersion = terms.Version,
                TermsVersions = commission.TermsVersions
                    .Append(terms)
                    .OrderBy(item => item.Version)
                    .ToArray(),
                ParticipantAcknowledgedTermsVersion = null,
                PaymentPolicyChangeRequest = null,
                Gates = commission.Gates with
                {
                    Payment = RevisePaymentGate(
                        commission.Gates.Payment,
                        commission.CurrentTerms,
                        terms),
                    CompanyMaterials = ReviseMaterialGate(
                        commission.Gates.CompanyMaterials,
                        commission.CurrentTerms,
                        terms)
                },
                OutputProgress = terms.Outputs.Select(output =>
                    new CompanyCommissionOutputProgress(
                        output.LineId,
                        output.ItemId,
                        output.RequiredQuantity,
                        0,
                        0,
                        0,
                        nowUtc,
                        actor)).ToArray(),
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(false),
                SettlementState = CompanyCommissionSettlementState.NotDue
            },
            CompanyCommissionActivityKind.TermsAmended,
            command.Reason);
    }

    private static CompanyCommissionDomainTransition Open(
        TradeOrder source,
        TradeCompanyCommission commission)
    {
        Require(
            source.Status is TradeOrderStatus.Draft or TradeOrderStatus.ReadyToAssign,
            "Only a draft commission can be opened.");
        Require(
            commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Published,
            "The canonical public brief must be published before opening.");
        var updated = Copy(source);
        updated.Status = TradeOrderStatus.ReadyToAssign;
        return Transition(
            updated,
            commission,
            CompanyCommissionActivityKind.CommissionOpened,
            "Opened the commission for one exclusive claim.");
    }

    private static CompanyCommissionDomainTransition Claim(
        TradeOrder source,
        TradeCompanyCommission commission,
        ClaimCompanyCommissionCommand command,
        DateTime nowUtc)
    {
        Require(source.Status == TradeOrderStatus.ReadyToAssign, "The commission is not open.");
        Require(
            !commission.PublicMetadata.IsTestFixture,
            "This test commission is intentionally unclaimable.");
        Require(commission.ActiveClaim == null, "The commission claim slot is unavailable.");
        Require(
            command.TermsVersion == commission.CurrentTermsVersion,
            "The claim terms version is stale.");
        Require(
            command.ExistingCrafterId.HasValue ^ command.ProvisionalCrafter != null,
            "A claim requires exactly one existing or provisional crafter identity.");
        if (command.ExistingCrafterId == Guid.Empty)
        {
            throw new InvalidOperationException("The existing crafter identity is invalid.");
        }
        if (command.ProvisionalCrafter != null)
        {
            ValidateProvisionalCrafter(command.ProvisionalCrafter);
        }

        var claimId = command.Context.CommandId;
        var assignedCrafterId = command.ExistingCrafterId;
        var identityState = assignedCrafterId.HasValue
            ? CompanyCommissionClearanceState.Satisfied
            : CompanyCommissionClearanceState.Pending;
        var gates = InitializeGates(
            commission.CurrentTerms,
            identityState,
            nowUtc,
            assignedCrafterId.HasValue ? "existing-company-crafter" : null);
        var updated = Copy(source);
        updated.Status = TradeOrderStatus.Assigned;
        updated.AssignedCrafterId = assignedCrafterId;
        return Transition(
            updated,
            commission with
            {
                ActiveClaim = new CompanyCommissionClaim(
                    claimId,
                    commission.CurrentTermsVersion,
                    nowUtc,
                    assignedCrafterId,
                    command.ProvisionalCrafter?.ProvisionalCrafterId),
                ProvisionalCrafter = command.ProvisionalCrafter,
                ParticipantGrant = new CompanyCommissionParticipantGrant(
                    claimId,
                    claimId,
                    commission.CurrentTermsVersion,
                    1,
                    nowUtc),
                ParticipantAcknowledgedTermsVersion = commission.CurrentTermsVersion,
                Gates = gates
            },
            CompanyCommissionActivityKind.ClaimAccepted,
            "Accepted the first valid claim.",
            JsonSerializer.Serialize(new
            {
                claimId,
                termsVersion = commission.CurrentTermsVersion,
                provisional = command.ProvisionalCrafter != null
            }));
    }

    private static CompanyCommissionDomainTransition Release(
        TradeOrder source,
        TradeCompanyCommission commission,
        string reason,
        DateTime nowUtc,
        bool rejected)
    {
        RequireClaim(commission);
        RequireReason(reason);
        Require(
            source.Status == TradeOrderStatus.Assigned &&
            commission.Gates.Payment.State != CompanyCommissionClearanceState.Satisfied &&
            commission.Gates.CompanyMaterials.ReadyAtUtc == null &&
            commission.Gates.CompanyMaterials.ReceivedAtUtc == null &&
            commission.OutputProgress.All(item =>
                item.CompletedQuantity == 0 &&
                item.ReadyQuantity == 0 &&
                item.AcceptedQuantity == 0),
            "A claim can be released or rejected only before payment, material handoff, or work begins.");
        var participant = commission.ParticipantGrant ??
            throw new InvalidOperationException(
                "The active claim has no participant grant.");
        var updated = Copy(source);
        updated.Status = TradeOrderStatus.ReadyToAssign;
        updated.AssignedCrafterId = null;
        return Transition(
            updated,
            commission with
            {
                ActiveClaim = null,
                ProvisionalCrafter = null,
                ParticipantGrant = participant with
                {
                    RevokedAtUtc = nowUtc
                },
                RecoveryGrant = null,
                ParticipantAcknowledgedTermsVersion = null,
                PaymentPolicyChangeRequest = null,
                ActiveClaimCapabilityRevision = checked(
                    commission.ActiveClaimCapabilityRevision + 1),
                Gates = InitializeGates(
                    commission.CurrentTerms,
                    CompanyCommissionClearanceState.Pending,
                    nowUtc),
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(false)
            },
            rejected
                ? CompanyCommissionActivityKind.ClaimRejected
                : CompanyCommissionActivityKind.ClaimReleased,
            reason);
    }

    private static CompanyCommissionDomainTransition SubmitIdentity(
        TradeOrder source,
        TradeCompanyCommission commission,
        SubmitCompanyCommissionIdentityCommand command,
        DateTime nowUtc)
    {
        RequireClaim(commission);
        Require(source.Status == TradeOrderStatus.Assigned, "Identity cannot change after work begins.");
        ValidateProvisionalCrafter(command.ProvisionalCrafter);
        return Transition(
            source,
            commission with
            {
                ProvisionalCrafter = command.ProvisionalCrafter with
                {
                    SubmittedAtUtc = nowUtc
                },
                Gates = commission.Gates with
                {
                    Identity = new CompanyCommissionIdentityClearance(
                        CompanyCommissionClearanceState.Pending,
                        command.ProvisionalCrafter.LodestoneCharacterId,
                        CharacterVerifiedAtUtc:
                        command.ProvisionalCrafter.LodestoneCharacterId == null ? null : nowUtc)
                }
            },
            CompanyCommissionActivityKind.ProvisionalIdentitySubmitted,
            "Submitted a provisional crafter identity for commissioner review.");
    }

    private static CompanyCommissionDomainTransition ConfirmIdentity(
        TradeOrder source,
        TradeCompanyCommission commission,
        ConfirmCompanyCommissionIdentityCommand command,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        RequireClaim(commission);
        Require(command.CrafterId != Guid.Empty, "The confirmed crafter identity is invalid.");
        Require(
            !string.IsNullOrWhiteSpace(command.LodestoneCharacterId),
            "A verified Lodestone character is required.");
        Require(
            commission.ProvisionalCrafter is { } provisional &&
            string.Equals(
                provisional.LodestoneCharacterId,
                command.LodestoneCharacterId,
                StringComparison.Ordinal),
            "Commissioner confirmation must match the submitted Lodestone candidate.");
        var updated = Copy(source);
        updated.AssignedCrafterId = command.CrafterId;
        return Transition(
            updated,
            commission with
            {
                ActiveClaim = commission.ActiveClaim! with
                {
                    CrafterId = command.CrafterId
                },
                Gates = commission.Gates with
                {
                    Identity = new CompanyCommissionIdentityClearance(
                        CompanyCommissionClearanceState.Satisfied,
                        command.LodestoneCharacterId,
                        CharacterVerifiedAtUtc: nowUtc,
                        OwnershipConfirmedAtUtc: nowUtc,
                        ConfirmedByActorId: actor.ActorId)
                }
            },
            CompanyCommissionActivityKind.ProvisionalIdentityConfirmed,
            "Confirmed the claimant's contact and in-game character.");
    }

    private static CompanyCommissionDomainTransition RequestPaymentChange(
        TradeOrder source,
        TradeCompanyCommission commission,
        RequestCompanyCommissionPaymentPolicyChangeCommand command,
        DateTime nowUtc)
    {
        RequireClaim(commission);
        Require(source.Status == TradeOrderStatus.Assigned, "Payment timing cannot change after work begins.");
        RequireReason(command.Reason);
        Require(
            command.RequestedSchedule != CompanyCommissionPaymentSchedule.Custom ||
            !string.IsNullOrWhiteSpace(command.RequestedCustomTerms),
            "Custom payment timing requires explicit terms.");
        return Transition(
            source,
            commission with
            {
                PaymentPolicyChangeRequest = new CompanyCommissionPaymentPolicyChangeRequest(
                    command.Context.CommandId,
                    commission.CurrentTermsVersion,
                    command.RequestedSchedule,
                    command.RequestedCustomTerms?.Trim(),
                    command.Reason.Trim(),
                    CompanyCommissionPaymentPolicyRequestState.Pending,
                    nowUtc)
            },
            CompanyCommissionActivityKind.PaymentPolicyChangeRequested,
            command.Reason);
    }

    private static CompanyCommissionDomainTransition DecidePaymentChange(
        TradeOrder source,
        TradeCompanyCommission commission,
        DecideCompanyCommissionPaymentPolicyChangeCommand command,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        var request = commission.PaymentPolicyChangeRequest;
        if (request is not { State: CompanyCommissionPaymentPolicyRequestState.Pending })
        {
            throw new InvalidOperationException(
                "There is no pending payment-policy request.");
        }
        RequireReason(command.Reason);
        if (!command.Accepted)
        {
            return Transition(
                source,
                commission with
                {
                    PaymentPolicyChangeRequest = request with
                    {
                        State = CompanyCommissionPaymentPolicyRequestState.Refused,
                        DecidedAtUtc = nowUtc,
                        DecisionReason = command.Reason.Trim()
                    }
                },
                CompanyCommissionActivityKind.PaymentPolicyChangeRefused,
                command.Reason);
        }

        var current = commission.CurrentTerms;
        var nextVersion = checked(commission.CurrentTermsVersion + 1);
        var nextTerms = current with
        {
            Version = nextVersion,
            CreatedAtUtc = nowUtc,
            CreatedBy = actor,
            Payment = current.Payment with
            {
                Schedule = request!.RequestedSchedule,
                CustomTerms = request.RequestedCustomTerms
            },
            ChangeSummary = "Accepted participant payment-timing request."
        };
        var paymentGate = CreatePaymentGate(nextTerms);
        return Transition(
            source,
            commission with
            {
                CurrentTermsVersion = nextVersion,
                TermsVersions = commission.TermsVersions.Append(nextTerms).ToArray(),
                ParticipantAcknowledgedTermsVersion = null,
                PaymentPolicyChangeRequest = request with
                {
                    State = CompanyCommissionPaymentPolicyRequestState.Accepted,
                    DecidedAtUtc = nowUtc,
                    DecisionReason = command.Reason.Trim()
                },
                Gates = commission.Gates with { Payment = paymentGate }
            },
            CompanyCommissionActivityKind.PaymentPolicyChangeAccepted,
            command.Reason);
    }

    private static CompanyCommissionDomainTransition AcknowledgeTerms(
        TradeOrder source,
        TradeCompanyCommission commission,
        AcknowledgeCompanyCommissionTermsCommand command)
    {
        RequireClaim(commission);
        Require(
            command.TermsVersion == commission.CurrentTermsVersion,
            "Only the current terms version can be acknowledged.");
        return Transition(
            source,
            commission with
            {
                ParticipantAcknowledgedTermsVersion = command.TermsVersion
            },
            CompanyCommissionActivityKind.TermsAcknowledged,
            $"Acknowledged terms version {command.TermsVersion}.");
    }

    private static CompanyCommissionDomainTransition RecordPayment(
        TradeOrder source,
        TradeCompanyCommission commission,
        RecordCompanyCommissionPaymentCommand command,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        RequireClaim(commission);
        RequireMutableStartGates(source);
        Require(
            !string.IsNullOrWhiteSpace(command.Note),
            "A truthful payment observation note is required.");
        Require(
            commission.Gates.Payment.State == CompanyCommissionClearanceState.Pending,
            "This commission has no pending advance-payment gate.");
        Require(
            commission.Gates.Payment.TermsVersion is 0 ||
            commission.Gates.Payment.TermsVersion == commission.CurrentTermsVersion,
            "The payment gate belongs to an earlier terms version.");
        Require(
            commission.Gates.Payment.CommissionerSent == null,
            "The commissioner has already recorded payment sent.");
        var sent = new CompanyCommissionPaymentAttestation(
            commission.CurrentTermsVersion,
            nowUtc,
            actor.ActorId,
            command.Note.Trim());
        var received = commission.Gates.Payment.CrafterReceived;
        var state = received == null
            ? CompanyCommissionClearanceState.Pending
            : CompanyCommissionClearanceState.Satisfied;
        return Transition(
            source,
            commission with
            {
                Gates = commission.Gates with
                {
                    Payment = new CompanyCommissionPaymentClearance(
                        state,
                        RecordedAtUtc:
                        state == CompanyCommissionClearanceState.Satisfied ? nowUtc : null,
                        RecordedByActorId:
                        state == CompanyCommissionClearanceState.Satisfied ? actor.ActorId : null,
                        Note:
                        state == CompanyCommissionClearanceState.Satisfied
                            ? "Both parties confirmed the advance payment."
                            : null,
                        TermsVersion: commission.CurrentTermsVersion,
                        CommissionerSent: sent,
                        CrafterReceived: received)
                },
                SettlementState =
                    state == CompanyCommissionClearanceState.Satisfied
                        ? CompanyCommissionSettlementState.Satisfied
                        : commission.SettlementState
            },
            CompanyCommissionActivityKind.PaymentSentRecorded,
            command.Note);
    }

    private static CompanyCommissionDomainTransition ConfirmPaymentReceived(
        TradeOrder source,
        TradeCompanyCommission commission,
        ConfirmCompanyCommissionPaymentReceivedCommand command,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        RequireClaim(commission);
        RequireReason(command.Note);
        Require(
            command.TermsVersion == commission.CurrentTermsVersion,
            "Payment receipt must confirm the current terms version.");
        Require(
            commission.Gates.Payment.State == CompanyCommissionClearanceState.Pending,
            "This commission has no pending advance-payment gate.");
        Require(
            commission.Gates.Payment.CrafterReceived == null,
            "The crafter has already confirmed payment received.");

        var received = new CompanyCommissionPaymentAttestation(
            commission.CurrentTermsVersion,
            nowUtc,
            actor.ActorId,
            command.Note.Trim());
        var sent = commission.Gates.Payment.CommissionerSent;
        var state = sent == null
            ? CompanyCommissionClearanceState.Pending
            : CompanyCommissionClearanceState.Satisfied;
        return Transition(
            source,
            commission with
            {
                Gates = commission.Gates with
                {
                    Payment = new CompanyCommissionPaymentClearance(
                        state,
                        RecordedAtUtc:
                        state == CompanyCommissionClearanceState.Satisfied ? nowUtc : null,
                        RecordedByActorId:
                        state == CompanyCommissionClearanceState.Satisfied ? actor.ActorId : null,
                        Note:
                        state == CompanyCommissionClearanceState.Satisfied
                            ? "Both parties confirmed the advance payment."
                            : null,
                        TermsVersion: commission.CurrentTermsVersion,
                        CommissionerSent: sent,
                        CrafterReceived: received)
                },
                SettlementState =
                    state == CompanyCommissionClearanceState.Satisfied
                        ? CompanyCommissionSettlementState.Satisfied
                        : commission.SettlementState
            },
            CompanyCommissionActivityKind.PaymentReceivedConfirmed,
            command.Note);
    }

    private static CompanyCommissionDomainTransition RetractPaymentAttestation(
        TradeOrder source,
        TradeCompanyCommission commission,
        RetractCompanyCommissionPaymentAttestationCommand command,
        CompanyCommissionActor actor)
    {
        RequireClaim(commission);
        RequireReason(command.Reason);
        Require(
            source.Status == TradeOrderStatus.Assigned,
            "Payment confirmation can be retracted only before work begins.");
        var payment = commission.Gates.Payment;
        var commissioner = actor.Kind == CompanyCommissionActorKind.Commissioner;
        Require(
            commissioner
                ? payment.CommissionerSent != null
                : payment.CrafterReceived != null,
            commissioner
                ? "The commissioner has no payment-sent confirmation to retract."
                : "The crafter has no payment-received confirmation to retract.");
        return Transition(
            source,
            commission with
            {
                Gates = commission.Gates with
                {
                    Payment = payment with
                    {
                        State = CompanyCommissionClearanceState.Pending,
                        RecordedAtUtc = null,
                        RecordedByActorId = null,
                        Note = null,
                        CommissionerSent =
                            commissioner ? null : payment.CommissionerSent,
                        CrafterReceived =
                            commissioner ? payment.CrafterReceived : null
                    }
                },
                SettlementState = CompanyCommissionSettlementState.NotDue
            },
            CompanyCommissionActivityKind.PaymentAttestationRetracted,
            command.Reason);
    }

    private static CompanyCommissionDomainTransition MarkMaterialsReady(
        TradeOrder source,
        TradeCompanyCommission commission,
        MarkCompanyCommissionMaterialsReadyCommand command,
        DateTime nowUtc)
    {
        RequireClaim(commission);
        RequireMutableStartGates(source);
        RequireExactMaterials(commission, command.Quantities);
        return Transition(
            source,
            commission with
            {
                Gates = commission.Gates with
                {
                    CompanyMaterials = commission.Gates.CompanyMaterials with
                    {
                        ReadyAtUtc = nowUtc
                    }
                }
            },
            CompanyCommissionActivityKind.CompanyMaterialsReady,
            "Marked the complete commissioner-provided material bundle ready.");
    }

    private static CompanyCommissionDomainTransition AcknowledgeMaterials(
        TradeOrder source,
        TradeCompanyCommission commission,
        AcknowledgeCompanyCommissionMaterialsCommand command,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        RequireClaim(commission);
        Require(
            commission.Gates.CompanyMaterials.ReadyAtUtc != null,
            "The commissioner has not marked the complete material bundle ready.");
        Require(
            commission.Gates.CompanyMaterials.State ==
            CompanyCommissionClearanceState.Pending,
            "The commissioner-provided material bundle was already acknowledged.");
        RequireExactMaterials(commission, command.Quantities);
        return Transition(
            source,
            commission with
            {
                Gates = commission.Gates with
                {
                    CompanyMaterials = commission.Gates.CompanyMaterials with
                    {
                        State = CompanyCommissionClearanceState.Satisfied,
                        ReceivedAtUtc = nowUtc,
                        ReceivedByActorId = actor.ActorId
                    }
                }
            },
            CompanyCommissionActivityKind.CompanyMaterialsReceived,
            "Acknowledged receipt of the complete commissioner-provided material bundle.");
    }

    private static CompanyCommissionDomainTransition ReportProgress(
        TradeOrder source,
        TradeCompanyCommission commission,
        ReportCompanyCommissionProgressCommand command,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        RequireCanWork(commission);
        var reported = command.Outputs.ToDictionary(item => item.LineId);
        Require(
            reported.Count == command.Outputs.Count &&
            reported.Count == commission.OutputProgress.Count,
            "Progress must report every output line exactly once.");
        var next = commission.OutputProgress.Select(current =>
        {
            if (!reported.TryGetValue(current.LineId, out var value) ||
                value.ItemId != current.ItemId)
            {
                throw new InvalidOperationException(
                    "Progress output identity does not match the accepted terms.");
            }
            Require(
                value.CompletedQuantity >= current.CompletedQuantity &&
                value.CompletedQuantity <= current.RequiredQuantity &&
                value.ReadyQuantity >= current.ReadyQuantity &&
                value.ReadyQuantity <= value.CompletedQuantity,
                "Progress quantities must be monotonic and within the required quantity.");
            return current with
            {
                CompletedQuantity = value.CompletedQuantity,
                ReadyQuantity = value.ReadyQuantity,
                UpdatedAtUtc = nowUtc,
                UpdatedBy = actor
            };
        }).ToArray();
        var updated = Copy(source);
        if (updated.Status == TradeOrderStatus.Assigned)
        {
            updated.Status = TradeOrderStatus.InProgress;
        }

        return Transition(
            updated,
            commission with { OutputProgress = next },
            CompanyCommissionActivityKind.ProgressReported,
            command.Comment,
            JsonSerializer.Serialize(command.Outputs));
    }

    private static CompanyCommissionDomainTransition AddComment(
        TradeOrder source,
        TradeCompanyCommission commission,
        AddCompanyCommissionCommentCommand command)
    {
        RequireReason(command.Comment);
        Require(command.Comment.Length <= 2000, "Comments cannot exceed 2,000 characters.");
        return Transition(
            source,
            commission,
            CompanyCommissionActivityKind.CommentAdded,
            command.Comment);
    }

    private static CompanyCommissionDomainTransition AddPrivateNote(
        TradeOrder source,
        TradeCompanyCommission commission,
        AddCompanyCommissionPrivateNoteCommand command)
    {
        RequireReason(command.Comment);
        Require(command.Comment.Length <= 2000, "Notes cannot exceed 2,000 characters.");
        return Transition(
            source,
            commission,
            CompanyCommissionActivityKind.CommentAdded,
            command.Comment,
            visibility: CompanyCommissionActivityVisibility.CompanyOnly);
    }

    private static CompanyCommissionDomainTransition DeclareReadiness(
        TradeOrder source,
        TradeCompanyCommission commission,
        DeclareCompanyCommissionReadinessCommand command,
        DateTime nowUtc)
    {
        RequireCanWork(commission);
        Require(
            commission.OutputProgress.All(item =>
                item.CompletedQuantity == item.RequiredQuantity &&
                item.ReadyQuantity == item.RequiredQuantity),
            "Every output must be completely ready before delivery can be declared.");
        var updated = Copy(source);
        updated.Status = TradeOrderStatus.AwaitingDelivery;
        return Transition(
            updated,
            commission with
            {
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(
                    true,
                    DeclaredAtUtc: nowUtc)
            },
            CompanyCommissionActivityKind.DeliveryReadinessDeclared,
            command.Comment);
    }

    private static CompanyCommissionDomainTransition ReturnToWork(
        TradeOrder source,
        TradeCompanyCommission commission,
        string reason,
        DateTime nowUtc,
        bool commissioner)
    {
        Require(
            source.Status == TradeOrderStatus.AwaitingDelivery &&
            commission.DeliveryReadiness.IsReady,
            "The commission is not ready for delivery.");
        RequireReason(reason);
        var updated = Copy(source);
        updated.Status = TradeOrderStatus.InProgress;
        return Transition(
            updated,
            commission with
            {
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(
                    false,
                    commission.DeliveryReadiness.DeclaredAtUtc,
                    nowUtc,
                    reason.Trim())
            },
            commissioner
                ? CompanyCommissionActivityKind.DeliveryReturnedToWork
                : CompanyCommissionActivityKind.DeliveryReadinessWithdrawn,
            reason);
    }

    private static CompanyCommissionDomainTransition AcceptDelivery(
        TradeOrder source,
        TradeCompanyCommission commission,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        Require(
            source.Status == TradeOrderStatus.AwaitingDelivery &&
            commission.DeliveryReadiness.IsReady,
            "The commission is not ready for delivery acceptance.");
        var updated = Copy(source);
        updated.Status = TradeOrderStatus.Completed;
        var progress = commission.OutputProgress.Select(item => item with
        {
            AcceptedQuantity = item.RequiredQuantity,
            UpdatedAtUtc = nowUtc,
            UpdatedBy = actor
        }).ToArray();
        var settlement = commission.CurrentTerms.Payment.Total <= 0 ||
                         commission.CurrentTerms.Payment.Schedule ==
                         CompanyCommissionPaymentSchedule.Advance &&
                         commission.Gates.Payment.State ==
                         CompanyCommissionClearanceState.Satisfied
            ? CompanyCommissionSettlementState.Satisfied
            : CompanyCommissionSettlementState.Pending;
        return Transition(
            updated,
            commission with
            {
                OutputProgress = progress,
                SettlementState = settlement,
                SettlementPayment = settlement == CompanyCommissionSettlementState.Pending
                    ? new CompanyCommissionSettlementConfirmation(
                        commission.CurrentTermsVersion)
                    : commission.SettlementPayment
            },
            CompanyCommissionActivityKind.DeliveryAccepted,
            "Accepted the complete delivery.");
    }

    private static CompanyCommissionDomainTransition RecordSettlement(
        TradeOrder source,
        TradeCompanyCommission commission,
        RecordCompanyCommissionSettlementCommand command,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        Require(
            source.Status == TradeOrderStatus.Completed,
            "Settlement can close only a fulfilled commission.");
        Require(
            actor.Kind == CompanyCommissionActorKind.Commissioner,
            "Only the commissioner can record settlement payment sent.");
        Require(
            commission.SettlementState == CompanyCommissionSettlementState.Pending,
            "This commission has no pending settlement.");
        RequireReason(command.Note);
        Require(
            commission.SettlementPayment.CommissionerSent == null,
            "The commissioner has already recorded settlement payment sent.");
        var sent = new CompanyCommissionPaymentAttestation(
            commission.CurrentTermsVersion,
            nowUtc,
            actor.ActorId,
            command.Note.Trim());
        var confirmation = commission.SettlementPayment with
        {
            TermsVersion = commission.CurrentTermsVersion,
            CommissionerSent = sent
        };
        return Transition(
            source,
            commission with
            {
                SettlementState = confirmation.IsSatisfied
                    ? CompanyCommissionSettlementState.Satisfied
                    : CompanyCommissionSettlementState.Pending,
                SettlementPayment = confirmation
            },
            CompanyCommissionActivityKind.SettlementPaymentSentRecorded,
            command.Note);
    }

    private static CompanyCommissionDomainTransition ConfirmSettlementReceived(
        TradeOrder source,
        TradeCompanyCommission commission,
        ConfirmCompanyCommissionSettlementReceivedCommand command,
        DateTime nowUtc,
        CompanyCommissionActor actor)
    {
        RequireClaim(commission);
        Require(
            actor.Kind == CompanyCommissionActorKind.Crafter,
            "Only the assigned crafter can confirm settlement payment received.");
        Require(
            source.Status == TradeOrderStatus.Completed,
            "Settlement receipt can be confirmed only after delivery is accepted.");
        Require(
            commission.SettlementState == CompanyCommissionSettlementState.Pending,
            "This commission has no pending settlement.");
        Require(
            command.TermsVersion == commission.CurrentTermsVersion,
            "Settlement receipt must confirm the current terms version.");
        RequireReason(command.Note);
        Require(
            commission.SettlementPayment.CrafterReceived == null,
            "The crafter has already confirmed settlement payment received.");
        var received = new CompanyCommissionPaymentAttestation(
            commission.CurrentTermsVersion,
            nowUtc,
            actor.ActorId,
            command.Note.Trim());
        var confirmation = commission.SettlementPayment with
        {
            TermsVersion = commission.CurrentTermsVersion,
            CrafterReceived = received
        };
        return Transition(
            source,
            commission with
            {
                SettlementState = confirmation.IsSatisfied
                    ? CompanyCommissionSettlementState.Satisfied
                    : CompanyCommissionSettlementState.Pending,
                SettlementPayment = confirmation
            },
            CompanyCommissionActivityKind.SettlementPaymentReceivedConfirmed,
            command.Note);
    }

    private static CompanyCommissionDomainTransition RetractSettlementAttestation(
        TradeOrder source,
        TradeCompanyCommission commission,
        RetractCompanyCommissionSettlementAttestationCommand command,
        CompanyCommissionActor actor)
    {
        RequireClaim(commission);
        Require(
            actor.Kind is CompanyCommissionActorKind.Commissioner or
                CompanyCommissionActorKind.Crafter,
            "Only a commission party can retract a settlement confirmation.");
        Require(
            source.Status == TradeOrderStatus.Completed,
            "Settlement confirmation can be retracted only after delivery is accepted.");
        RequireReason(command.Reason);
        var commissioner = actor.Kind == CompanyCommissionActorKind.Commissioner;
        var confirmation = commission.SettlementPayment;
        Require(
            commissioner
                ? confirmation.CommissionerSent != null
                : confirmation.CrafterReceived != null,
            commissioner
                ? "The commissioner has no settlement payment confirmation to retract."
                : "The crafter has no settlement receipt confirmation to retract.");
        return Transition(
            source,
            commission with
            {
                SettlementState = CompanyCommissionSettlementState.Pending,
                SettlementPayment = confirmation with
                {
                    CommissionerSent = commissioner
                        ? null
                        : confirmation.CommissionerSent,
                    CrafterReceived = commissioner
                        ? confirmation.CrafterReceived
                        : null
                }
            },
            CompanyCommissionActivityKind.SettlementPaymentAttestationRetracted,
            command.Reason);
    }

    private static CompanyCommissionDomainTransition ResetRecovery(
        TradeOrder source,
        TradeCompanyCommission commission,
        ResetCompanyCommissionParticipantRecoveryCommand command,
        DateTime nowUtc)
    {
        if (commission.ParticipantGrant is not { RevokedAtUtc: null } participant)
        {
            throw new InvalidOperationException(
                "There is no active participant grant to recover.");
        }
        var revision = checked((commission.RecoveryGrant?.RecoveryRevision ?? 0) + 1);
        return Transition(
            source,
            commission with
            {
                RecoveryGrant = new CompanyCommissionRecoveryGrant(
                    command.Context.CommandId,
                    participant.GrantId,
                    revision,
                    nowUtc)
            },
            CompanyCommissionActivityKind.ParticipantRecoveryIssued,
            "Issued one-time participant recovery authority.");
    }

    private static CompanyCommissionDomainTransition RedeemRecovery(
        TradeOrder source,
        TradeCompanyCommission commission,
        RedeemCompanyCommissionParticipantRecoveryCommand command,
        DateTime nowUtc)
    {
        if (commission.RecoveryGrant is not
            {
                RedeemedAtUtc: null,
                RevokedAtUtc: null
            } recovery ||
            recovery.RecoveryGrantId != command.RecoveryGrantId)
        {
            throw new InvalidOperationException(
                "The recovery authority is invalid or already used.");
        }
        if (commission.ParticipantGrant is not { RevokedAtUtc: null } participant ||
            participant.GrantId != recovery.ParticipantGrantId)
        {
            throw new InvalidOperationException(
                "The participant grant is unavailable.");
        }
        return Transition(
            source,
            commission with
            {
                ParticipantGrant = participant with
                {
                    CapabilityRevision = checked(participant.CapabilityRevision + 1),
                    IssuedAtUtc = nowUtc
                },
                RecoveryGrant = recovery with { RedeemedAtUtc = nowUtc }
            },
            CompanyCommissionActivityKind.ParticipantRecoveryRedeemed,
            "Redeemed one-time participant recovery authority.");
    }

    private static CompanyCommissionDomainTransition Cancel(
        TradeOrder source,
        TradeCompanyCommission commission,
        string reason)
    {
        Require(
            source.Status is not (TradeOrderStatus.Completed or TradeOrderStatus.Canceled),
            "The commission is already closed.");
        RequireReason(reason);
        var updated = Copy(source);
        updated.Status = TradeOrderStatus.Canceled;
        return Transition(
            updated,
            commission,
            CompanyCommissionActivityKind.CommissionCanceled,
            reason);
    }

    private static CompanyCommissionDomainTransition RevokePublication(
        TradeOrder source,
        TradeCompanyCommission commission,
        DateTime nowUtc)
    {
        Require(
            commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Published,
            "The commission publication is not active.");
        var updated = Copy(source);
        var publication = updated.CommissionPublication ??
            throw new InvalidOperationException(
                "The canonical publication metadata is missing.");
        updated.CommissionPublication = new TradeCommissionPublication
        {
            PublicId = publication.PublicId,
            PublicUrl = publication.PublicUrl,
            Version = publication.Version,
            PublishedAtUtc = publication.PublishedAtUtc,
            RevokedAtUtc = nowUtc,
            IsTestFixture = publication.IsTestFixture,
            Ownership = publication.Ownership
        };
        return Transition(
            updated,
            commission with
            {
                ActiveClaimCapabilityRevision = checked(
                    commission.ActiveClaimCapabilityRevision + 1),
                PublicMetadata = commission.PublicMetadata with
                {
                    ViewState = CompanyCommissionPublicViewState.Revoked,
                    RevokedAtUtc = nowUtc
                }
            },
            CompanyCommissionActivityKind.CommissionPublicationRevoked,
            "Revoked the public commission publication.");
    }

    private static TradeCompanyCommission RequireCommission(
        TradeOrder order,
        CompanyCommissionCommandContext context)
    {
        var commission = order.CompanyCommission;
        if (commission == null ||
            commission.CommissionId != context.CommissionId ||
            commission.CompanyId != context.CompanyId)
        {
            throw new InvalidOperationException(
                "The command does not address the canonical company commission.");
        }

        return commission;
    }

    private static CompanyCommissionGateState InitializeGates(
        CompanyCommissionTermsVersion terms,
        CompanyCommissionClearanceState identityState,
        DateTime nowUtc,
        string? confirmedBy = null)
    {
        var companyMaterials = terms.Materials
            .Where(item =>
                item.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(item => new CompanyCommissionMaterialQuantity(
                item.LineId,
                item.ItemId,
                item.Quantity))
            .ToArray();
        return new CompanyCommissionGateState(
            new CompanyCommissionIdentityClearance(
                identityState,
                OwnershipConfirmedAtUtc:
                identityState == CompanyCommissionClearanceState.Satisfied ? nowUtc : null,
                ConfirmedByActorId: confirmedBy),
            CreatePaymentGate(terms),
            new CompanyCommissionMaterialClearance(
                companyMaterials.Length == 0
                    ? CompanyCommissionClearanceState.NotRequired
                    : CompanyCommissionClearanceState.Pending,
                companyMaterials));
    }

    private static CompanyCommissionPaymentClearance CreatePaymentGate(
        CompanyCommissionTermsVersion terms) =>
        terms.Payment.Schedule == CompanyCommissionPaymentSchedule.Advance &&
        terms.Payment.Total > 0
            ? new CompanyCommissionPaymentClearance(
                CompanyCommissionClearanceState.Pending,
                TermsVersion: terms.Version)
            : new CompanyCommissionPaymentClearance(
                CompanyCommissionClearanceState.NotRequired,
                TermsVersion: terms.Version);

    private static CompanyCommissionMaterialClearance CreateMaterialGate(
        CompanyCommissionTermsVersion terms)
    {
        var quantities = terms.Materials
            .Where(item =>
                item.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(item => new CompanyCommissionMaterialQuantity(
                item.LineId,
                item.ItemId,
                item.Quantity))
            .ToArray();
        return new CompanyCommissionMaterialClearance(
            quantities.Length == 0
                ? CompanyCommissionClearanceState.NotRequired
                : CompanyCommissionClearanceState.Pending,
            quantities);
    }

    private static CompanyCommissionPaymentClearance RevisePaymentGate(
        CompanyCommissionPaymentClearance currentGate,
        CompanyCommissionTermsVersion currentTerms,
        CompanyCommissionTermsVersion nextTerms)
    {
        return currentTerms.Payment == nextTerms.Payment
            ? currentGate with { TermsVersion = nextTerms.Version }
            : CreatePaymentGate(nextTerms);
    }

    private static CompanyCommissionMaterialClearance ReviseMaterialGate(
        CompanyCommissionMaterialClearance currentGate,
        CompanyCommissionTermsVersion currentTerms,
        CompanyCommissionTermsVersion nextTerms)
    {
        return HaveSameCompanyMaterialPromise(currentTerms, nextTerms)
            ? currentGate
            : CreateMaterialGate(nextTerms);
    }

    private static bool HaveSameCompanyMaterialPromise(
        CompanyCommissionTermsVersion currentTerms,
        CompanyCommissionTermsVersion nextTerms)
    {
        static IEnumerable<(Guid LineId, int ItemId, int Quantity, bool RequiresHq)> GetPromise(
            CompanyCommissionTermsVersion terms) =>
            terms.Materials
                .Where(item =>
                    item.Responsibility == CommissionMaterialResponsibility.Provided)
                .Select(item => (item.LineId, item.ItemId, item.Quantity, item.RequiresHq))
                .OrderBy(item => item.LineId);

        return GetPromise(currentTerms).SequenceEqual(GetPromise(nextTerms));
    }

    private static void RequireDraftWorkPackageMatchesTerms(
        CompanyCommissionDraftWorkPackage workPackage,
        CompanyCommissionTermsVersion terms)
    {
        var requested = workPackage.RequestedOutputs
            .OrderBy(item => item.ItemId)
            .ThenBy(item => item.MustBeHq)
            .Select(item => (item.ItemId, item.Name, item.Quantity, item.MustBeHq))
            .ToArray();
        var canonical = terms.Outputs
            .OrderBy(item => item.ItemId)
            .ThenBy(item => item.MustBeHq)
            .Select(item => (item.ItemId, item.Name, Quantity: item.RequiredQuantity, item.MustBeHq))
            .ToArray();
        Require(
            requested.SequenceEqual(canonical),
            "The draft work package outputs do not match the canonical terms.");

        var snapshotOutputs = workPackage.SourceSnapshot.RootItems
            .OrderBy(item => item.ItemId)
            .ThenBy(item => item.MustBeHq)
            .Select(item => (item.ItemId, item.Name, item.Quantity, item.MustBeHq))
            .ToArray();
        Require(
            snapshotOutputs.SequenceEqual(canonical),
            "The draft source snapshot outputs do not match the canonical terms.");
    }

    private static void RequireCanWork(TradeCompanyCommission commission)
    {
        RequireClaim(commission);
        Require(commission.ClearedToWork, "Every applicable pre-work gate must be satisfied.");
        Require(
            commission.ParticipantAcknowledgedTermsVersion == commission.CurrentTermsVersion,
            "The participant must acknowledge the current terms before work can continue.");
    }

    private static void RequireExactMaterials(
        TradeCompanyCommission commission,
        IReadOnlyList<CompanyCommissionMaterialQuantity> quantities)
    {
        var expected = commission.Gates.CompanyMaterials.PromisedQuantities
            .OrderBy(item => item.LineId)
            .ToArray();
        var actual = quantities.OrderBy(item => item.LineId).ToArray();
        Require(expected.Length > 0, "This commission has no commissioner-provided materials.");
        Require(
            expected.SequenceEqual(actual),
            "The complete promised commissioner-material bundle must match exactly.");
    }

    private static void ValidateTerms(CompanyCommissionTermsVersion terms)
    {
        Require(terms.Version > 0, "Terms versions must be positive.");
        Require(terms.Outputs.Count > 0, "At least one requested output is required.");
        Require(
            terms.Outputs.All(item =>
                item.LineId != Guid.Empty &&
                item.ItemId > 0 &&
                !string.IsNullOrWhiteSpace(item.Name) &&
                item.RequiredQuantity > 0) &&
            terms.Outputs.Select(item => item.LineId).Distinct().Count() == terms.Outputs.Count,
            "Output lines require unique stable identities and positive quantities.");
        Require(
            terms.Materials.All(item =>
                item.LineId != Guid.Empty &&
                item.ItemId > 0 &&
                !string.IsNullOrWhiteSpace(item.Name) &&
                item.Quantity > 0) &&
            terms.Materials.Select(item => item.LineId).Distinct().Count() == terms.Materials.Count,
            "Material lines require unique stable identities and positive quantities.");
        Require(
            terms.Payment.Total >= 0 &&
            terms.Payment.MaterialReimbursement >= 0 &&
            terms.Payment.MaterialAdjustment >= 0 &&
            terms.Payment.CraftLabor >= 0 &&
            terms.Payment.CraftSynthCount >= 0 &&
            terms.Payment.GilPerSynth >= 0,
            "Payment amounts and labor basis cannot be negative.");
        Require(
            terms.Payment.MaterialReimbursement +
                terms.Payment.MaterialAdjustment +
                terms.Payment.CraftLabor == terms.Payment.Total,
            "The payment total must equal its material, adjustment, and labor components.");
        Require(
            terms.Payment.Schedule != CompanyCommissionPaymentSchedule.Custom ||
                !string.IsNullOrWhiteSpace(terms.Payment.CustomTerms),
            "Custom payment timing requires explicit terms.");
    }

    private static void ValidateProvisionalCrafter(
        CompanyCommissionProvisionalCrafter provisional)
    {
        Require(provisional.ProvisionalCrafterId != Guid.Empty, "The provisional crafter ID is invalid.");
        Require(
            !string.IsNullOrWhiteSpace(provisional.CharacterName) &&
            !string.IsNullOrWhiteSpace(provisional.HomeWorld) &&
            !string.IsNullOrWhiteSpace(provisional.ContactMethod) &&
            !string.IsNullOrWhiteSpace(provisional.ContactValue),
            "Character, world, and usable contact details are required.");
    }

    private static void RequireClaim(TradeCompanyCommission commission) =>
        Require(commission.ActiveClaim != null, "The commission has no active claim.");

    private static void RequireReason(string value) =>
        Require(!string.IsNullOrWhiteSpace(value), "A reason is required.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireMutableStartGates(TradeOrder source) =>
        Require(
            !TradeOrderStatusWorkflow.IsArchived(source.Status),
            "Payment and material handoff cannot change after the commission is completed or canceled.");

    private static TradeOrder Copy(TradeOrder source) =>
        TradeOrderWorkflow.CopyOrder(source);

    private static CompanyCommissionDomainTransition Transition(
        TradeOrder source,
        TradeCompanyCommission commission,
        CompanyCommissionActivityKind activityKind,
        string? comment = null,
        string? payloadJson = null,
        CompanyCommissionActivityVisibility visibility =
            CompanyCommissionActivityVisibility.Shared)
    {
        var updated = Copy(source);
        updated.CompanyCommission = commission;
        return new CompanyCommissionDomainTransition(
            updated,
            activityKind,
            comment,
            payloadJson,
            visibility);
    }
}
