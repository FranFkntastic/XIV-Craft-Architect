using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class CompanyCommissionGateRevisionSpecificationTests
{
    private static readonly DateTime StartedAt = new(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc);
    private static readonly CompanyCommissionActor Commissioner = new("commissioner", CompanyCommissionActorKind.Commissioner, "Commissioner");
    private static readonly CompanyCommissionActor Crafter = new("crafter", CompanyCommissionActorKind.Crafter, "Crafter");

    [Theory]
    [InlineData(GateRevisionScenario.UnchangedBoundFacts)]
    [InlineData(GateRevisionScenario.PaymentChanged)]
    [InlineData(GateRevisionScenario.PartialPaymentAfterRevision)]
    [InlineData(GateRevisionScenario.MaterialQuantityChanged)]
    [InlineData(GateRevisionScenario.MaterialQualityChanged)]
    [InlineData(GateRevisionScenario.PaymentClearsFirst)]
    [InlineData(GateRevisionScenario.MaterialsClearFirst)]
    public void TermsRevisionAndParallelGatesPreserveOnlyMatchingEvidence(
        GateRevisionScenario scenario)
    {
        switch (scenario)
        {
            case GateRevisionScenario.UnchangedBoundFacts: TermsRevisionPreservesSatisfiedGatesWhoseBoundFactsDidNotChange(); break;
            case GateRevisionScenario.PaymentChanged: PaymentChangeResetsOnlyPaymentEvidence(); break;
            case GateRevisionScenario.PartialPaymentAfterRevision: UnchangedPartialPaymentEvidenceCanCompleteAfterTermsRevision(); break;
            case GateRevisionScenario.MaterialQuantityChanged: CompanyMaterialPromiseChangeResetsOnlyHandoffEvidence(); break;
            case GateRevisionScenario.MaterialQualityChanged: CompanyMaterialQualityChangeResetsOnlyHandoffEvidence(); break;
            case GateRevisionScenario.PaymentClearsFirst: PaymentAndCompanyMaterialGatesCanClearInEitherOrder(paymentFirst: true); break;
            case GateRevisionScenario.MaterialsClearFirst: PaymentAndCompanyMaterialGatesCanClearInEitherOrder(paymentFirst: false); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    [Fact]
    public void ClearedCommissionCanBeCraftedWithoutAProgressReport()
    {
        var order = CompleteBothGates(CreateClaimedOrder());

        Assert.True(order.CompanyCommission!.ClearedToWork);
        Assert.Equal(TradeOrderStatus.Assigned, order.Status);
        Assert.All(order.CompanyCommission.OutputProgress, progress => Assert.Equal(0, progress.CompletedQuantity));
    }

    [Fact]
    public void CrafterProgressProjectsToPublicAndParticipantBriefs()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var progress = Assert.Single(order.CompanyCommission!.OutputProgress);
        var report = new CompanyCommissionProgressQuantity(progress.LineId, progress.ItemId, CompletedQuantity: 1, ReadyQuantity: 1);
        var command = new ReportCompanyCommissionProgressCommand(Context(order), [report], "Ready for handoff.");
        var transition = CompanyCommissionCommandWorkflow.Apply(order, command, Crafter, StartedAt.AddMinutes(5));
        order = transition.UpdatedOrder;

        var publicBrief = CompanyCommissionProjectionService.CreatePublicBrief(order, "Test Company");
        var participantBrief = CompanyCommissionProjectionService.CreateParticipantBrief(order, "Test Company");
        var publicProgress = Assert.Single(publicBrief.OutputProgress);
        var participantProgress = Assert.Single(participantBrief.Public.OutputProgress);

        Assert.Equal(1, publicProgress.CompletedQuantity);
        Assert.Equal(1, publicProgress.ReadyQuantity);
        Assert.Equal(publicProgress, participantProgress);
        Assert.Equal(CompanyCommissionActivityKind.ProgressReported, transition.ActivityKind);
        Assert.Equal("Ready for handoff.", transition.Comment);
    }

    [Fact]
    public void UnrelatedRevisionPreservesPartialPaymentAndReadyMaterialBindings()
    {
        var order = PreparePartialGateEvidence(CreateClaimedOrder());
        var original = order.CompanyCommission!;
        var nextTerms = original.CurrentTerms with { Version = 2, DeliveryInstructions = "Deliver in Gridania." };

        order = Amend(order, nextTerms);
        var revised = order.CompanyCommission!;
        var participant = CompanyCommissionProjectionService.CreateParticipantBrief(order, "Test Company");

        Assert.Equal(original.Gates.Payment.CommissionerSent, revised.Gates.Payment.CommissionerSent);
        Assert.Equal(original.Gates.CompanyMaterials.ReadyAtUtc, revised.Gates.CompanyMaterials.ReadyAtUtc);
        Assert.NotNull(participant.Payment.CommissionerSent);
        Assert.Null(participant.Payment.CrafterReceived);
        Assert.True(participant.CompanyMaterialsReadyForHandoff);
    }

    [Fact]
    public void TermsRevisionCanAtomicallyAdvanceTheLinkedWorkPackage()
    {
        var order = CreateClaimedOrder();
        var terms = order.CompanyCommission!.CurrentTerms with
        {
            Version = 2,
            DeliveryInstructions = "Deliver in Gridania."
        };
        var savedAt = StartedAt.AddMinutes(9);
        var snapshot = TradeOrderWorkflow.CopySourceSnapshot(order.SourceSnapshot);
        snapshot.World = "Siren";
        snapshot.ImportedAtUtc = savedAt;
        var workPackage = new CompanyCommissionDraftWorkPackage(
            terms.Outputs.Select(output => new TradeRequestedOrderOutput(
                output.ItemId,
                output.Name,
                output.RequiredQuantity,
                output.MustBeHq,
                0)).ToArray(),
            snapshot,
            "plan-v2",
            "Order - Gate test commission",
            savedAt,
            TradeOrderCraftPlanLinkKind.OrderGenerated);

        var revised = Apply(
            order,
            new AmendCompanyCommissionTermsCommand(
                Context(order),
                terms,
                "Update commission terms.",
                workPackage),
            Commissioner,
            10);

        Assert.Equal("plan-v2", revised.CraftPlanId);
        Assert.Equal(savedAt, revised.CraftPlanSavedAtUtc);
        Assert.Equal(TradeOrderCraftPlanLinkKind.OrderGenerated, revised.CraftPlanLinkKind);
        Assert.Equal("Siren", revised.SourceSnapshot.World);
        Assert.Equal(2, revised.CompanyCommission!.CurrentTermsVersion);
    }

    [Fact]
    public void ChangedPaymentAndMaterialFactsInvalidatePartialGateEvidence()
    {
        var order = PreparePartialGateEvidence(CreateClaimedOrder());
        var original = order.CompanyCommission!;
        var nextTerms = original.CurrentTerms with
        {
            Version = 2,
            Payment = original.CurrentTerms.Payment with
            {
                MaterialAdjustment = original.CurrentTerms.Payment.MaterialAdjustment + 25,
                Total = original.CurrentTerms.Payment.Total + 25
            },
            Materials = [original.CurrentTerms.Materials[0] with { Quantity = original.CurrentTerms.Materials[0].Quantity + 1 }]
        };

        order = Amend(order, nextTerms);
        var participant = CompanyCommissionProjectionService.CreateParticipantBrief(order, "Test Company");

        Assert.Null(participant.Payment.CommissionerSent);
        Assert.Null(participant.Payment.CrafterReceived);
        Assert.False(participant.CompanyMaterialsReadyForHandoff);
    }

    [Fact]
    public void TerminalCommissionsRejectStartGateMutations()
    {
        foreach (var status in TradeOrderStatusWorkflow.ArchiveStatuses)
        {
            var order = CreateClaimedOrder();
            order.Status = status;
            var quantities = order.CompanyCommission!.Gates.CompanyMaterials.PromisedQuantities;
            ICompanyCommissionCommand[] commands =
            [
                new RecordCompanyCommissionPaymentCommand(Context(order), "Payment sent."),
                new MarkCompanyCommissionMaterialsReadyCommand(Context(order), quantities)
            ];

            foreach (var command in commands)
            {
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    CompanyCommissionCommandWorkflow.Apply(order, command, Commissioner, StartedAt));
                Assert.Contains("completed or canceled", exception.Message, StringComparison.Ordinal);
            }
        }
    }

    private static void TermsRevisionPreservesSatisfiedGatesWhoseBoundFactsDidNotChange()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var original = order.CompanyCommission!;
        var nextTerms = original.CurrentTerms with
        {
            Version = 2,
            DeliveryInstructions = "Deliver in Gridania."
        };

        var revised = Amend(order, nextTerms).CompanyCommission!;

        Assert.Equal(2, revised.CurrentTermsVersion);
        Assert.Equal(2, revised.Gates.Payment.TermsVersion);
        Assert.Equal(original.Gates.Payment.CommissionerSent, revised.Gates.Payment.CommissionerSent);
        Assert.Equal(original.Gates.Payment.CrafterReceived, revised.Gates.Payment.CrafterReceived);
        Assert.Equal(original.Gates.Payment.State, revised.Gates.Payment.State);
        Assert.Equal(original.Gates.CompanyMaterials, revised.Gates.CompanyMaterials);
        Assert.True(revised.ClearedToWork);
        Assert.Null(revised.ParticipantAcknowledgedTermsVersion);
    }

    private static void PaymentChangeResetsOnlyPaymentEvidence()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var original = order.CompanyCommission!;
        var nextTerms = original.CurrentTerms with
        {
            Version = 2,
            Payment = original.CurrentTerms.Payment with { MaterialAdjustment = 25, Total = original.CurrentTerms.Payment.Total + 25 }
        };

        var revised = Amend(order, nextTerms).CompanyCommission!;

        Assert.Equal(CompanyCommissionClearanceState.Pending, revised.Gates.Payment.State);
        Assert.Equal(2, revised.Gates.Payment.TermsVersion);
        Assert.Null(revised.Gates.Payment.CommissionerSent);
        Assert.Null(revised.Gates.Payment.CrafterReceived);
        Assert.Equal(original.Gates.CompanyMaterials, revised.Gates.CompanyMaterials);
    }

    private static void UnchangedPartialPaymentEvidenceCanCompleteAfterTermsRevision()
    {
        var order = CreateClaimedOrder();
        order = RecordPayment(order);
        var originalAttestation = order.CompanyCommission!.Gates.Payment.CommissionerSent;
        var nextTerms = order.CompanyCommission.CurrentTerms with { Version = 2, DeliveryInstructions = "Deliver in Gridania." };
        order = Amend(order, nextTerms);

        order = ConfirmPayment(order, minute: 11);

        Assert.Equal(CompanyCommissionClearanceState.Satisfied, order.CompanyCommission!.Gates.Payment.State);
        Assert.Equal(2, order.CompanyCommission.Gates.Payment.TermsVersion);
        Assert.Equal(originalAttestation, order.CompanyCommission.Gates.Payment.CommissionerSent);
        Assert.Equal(1, order.CompanyCommission.Gates.Payment.CommissionerSent!.TermsVersion);
        Assert.Equal(2, order.CompanyCommission.Gates.Payment.CrafterReceived!.TermsVersion);
    }

    private static void CompanyMaterialPromiseChangeResetsOnlyHandoffEvidence()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var original = order.CompanyCommission!;
        var changedMaterial = original.CurrentTerms.Materials[0] with { Quantity = 4 };
        var nextTerms = original.CurrentTerms with { Version = 2, Materials = [changedMaterial] };

        var revised = Amend(order, nextTerms).CompanyCommission!;

        Assert.Equal(2, revised.Gates.Payment.TermsVersion);
        Assert.Equal(original.Gates.Payment.CommissionerSent, revised.Gates.Payment.CommissionerSent);
        Assert.Equal(original.Gates.Payment.CrafterReceived, revised.Gates.Payment.CrafterReceived);
        Assert.Equal(original.Gates.Payment.State, revised.Gates.Payment.State);
        Assert.Equal(CompanyCommissionClearanceState.Pending, revised.Gates.CompanyMaterials.State);
        Assert.Null(revised.Gates.CompanyMaterials.ReadyAtUtc);
        Assert.Null(revised.Gates.CompanyMaterials.ReceivedAtUtc);
        Assert.Equal(4, Assert.Single(revised.Gates.CompanyMaterials.PromisedQuantities).Quantity);
    }

    private static void CompanyMaterialQualityChangeResetsOnlyHandoffEvidence()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var original = order.CompanyCommission!;
        var changedMaterial = original.CurrentTerms.Materials[0] with { RequiresHq = true };
        var nextTerms = original.CurrentTerms with { Version = 2, Materials = [changedMaterial] };

        var revised = Amend(order, nextTerms).CompanyCommission!;

        Assert.Equal(2, revised.Gates.Payment.TermsVersion);
        Assert.Equal(original.Gates.Payment.State, revised.Gates.Payment.State);
        Assert.Equal(CompanyCommissionClearanceState.Pending, revised.Gates.CompanyMaterials.State);
        Assert.Null(revised.Gates.CompanyMaterials.ReadyAtUtc);
        Assert.Null(revised.Gates.CompanyMaterials.ReceivedAtUtc);
    }

    private static void PaymentAndCompanyMaterialGatesCanClearInEitherOrder(bool paymentFirst)
    {
        var order = CreateClaimedOrder();

        order = paymentFirst ? CompletePayment(order) : CompleteMaterials(order);
        Assert.False(order.CompanyCommission!.ClearedToWork);

        order = paymentFirst ? CompleteMaterials(order) : CompletePayment(order);
        Assert.True(order.CompanyCommission!.ClearedToWork);
    }

    private static TradeOrder CompleteBothGates(TradeOrder order) =>
        CompleteMaterials(CompletePayment(order));

    [Fact]
    public void FinalProgressAtomicallyBecomesAwaitingDeliveryAndNormalizesLegacyReadyQuantity()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var output = Assert.Single(order.CompanyCommission!.OutputProgress);

        order = Apply(
            order,
            new ReportCompanyCommissionProgressCommand(
                Context(order),
                [new CompanyCommissionProgressQuantity(
                    output.LineId,
                    output.ItemId,
                    output.RequiredQuantity,
                    ReadyQuantity: 0)],
                "Craft complete."),
            Crafter,
            6);

        Assert.Equal(TradeOrderStatus.AwaitingDelivery, order.Status);
        var progress = Assert.Single(order.CompanyCommission!.OutputProgress);
        Assert.Equal(progress.RequiredQuantity, progress.CompletedQuantity);
        Assert.Equal(progress.CompletedQuantity, progress.ReadyQuantity);
        Assert.True(order.CompanyCommission.DeliveryReadiness.IsReady);
    }

    [Fact]
    public void OptionalHandoffAddsReviewContextWithoutGatingCommissionerAcceptance()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var output = Assert.Single(order.CompanyCommission!.OutputProgress);
        order = Apply(
            order,
            new ReportCompanyCommissionProgressCommand(
                Context(order),
                [new CompanyCommissionProgressQuantity(
                    output.LineId,
                    output.ItemId,
                    output.RequiredQuantity,
                    output.RequiredQuantity)]),
            Crafter,
            6);
        order = Apply(
            order,
            new RecordCompanyCommissionDeliveryHandoffCommand(
                Context(order),
                CompanyCommissionDeliveryHandoffMethod.Mail,
                Recipient: "Company chest reviewer",
                Note: "Mailed for review."),
            Crafter,
            7);

        var handoff = Assert.Single(order.CompanyCommission!.DeliveryHandoffs);
        Assert.Equal(order.CompanyCommission.CurrentTermsVersion, handoff.TermsVersion);
        order = Apply(
            order,
            new AcceptCompanyCommissionDeliveryCommand(Context(order)),
            Commissioner,
            8);

        Assert.Equal(TradeOrderStatus.Completed, order.Status);
        Assert.Equal(output.RequiredQuantity, Assert.Single(order.CompanyCommission!.OutputProgress).AcceptedQuantity);
    }

    [Fact]
    public void CommissionerCanAcceptCompletedDeliveryWithoutCrafterHandoff()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var output = Assert.Single(order.CompanyCommission!.OutputProgress);
        order = Apply(
            order,
            new ReportCompanyCommissionProgressCommand(
                Context(order),
                [new CompanyCommissionProgressQuantity(
                    output.LineId,
                    output.ItemId,
                    output.RequiredQuantity,
                    ReadyQuantity: 0)]),
            Crafter,
            6);

        order = Apply(
            order,
            new AcceptCompanyCommissionDeliveryCommand(Context(order)),
            Commissioner,
            7);

        Assert.Equal(TradeOrderStatus.Completed, order.Status);
        Assert.Empty(order.CompanyCommission!.DeliveryHandoffs);
    }

    [Fact]
    public void ReturnedCompletedDeliveryResubmitsWithoutRecraftingOrReadyCeremony()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var output = Assert.Single(order.CompanyCommission!.OutputProgress);
        var completed = new CompanyCommissionProgressQuantity(
            output.LineId,
            output.ItemId,
            output.RequiredQuantity,
            ReadyQuantity: 0);
        order = Apply(
            order,
            new ReportCompanyCommissionProgressCommand(Context(order), [completed]),
            Crafter,
            6);
        order = Apply(
            order,
            new ReturnCompanyCommissionToWorkCommand(
                Context(order),
                "Please place the finished craft in the company chest."),
            Commissioner,
            7);

        Assert.Equal(TradeOrderStatus.InProgress, order.Status);
        Assert.Equal(output.RequiredQuantity, Assert.Single(order.CompanyCommission!.OutputProgress).CompletedQuantity);

        var resubmission = CompanyCommissionCommandWorkflow.Apply(
            order,
            new ReportCompanyCommissionProgressCommand(Context(order), [completed], "Placed in the chest."),
            Crafter,
            StartedAt.AddMinutes(8));
        order = resubmission.UpdatedOrder;

        Assert.Equal(TradeOrderStatus.AwaitingDelivery, order.Status);
        Assert.Equal("Placed in the chest.", resubmission.Comment);
    }

    [Fact]
    public void FinalPreworkGateAtomicallyLocksTheCurrentMaterialQuote()
    {
        var order = WithMaterialQuote(CreateClaimedOrder(), StartedAt.AddMinutes(30));

        order = CompleteBothGates(order);

        Assert.Equal(
            StartedAt.AddMinutes(4),
            order.CompanyCommission!.CurrentTerms.PricingEvidence.MaterialQuote!.LockedAtUtc);
    }

    [Fact]
    public void ExpiredQuoteRefusesTheFinalPreworkGateWithoutLosingTheClaim()
    {
        var order = WithMaterialQuote(CreateClaimedOrder(), StartedAt.AddMinutes(3));
        order = CompletePayment(order);
        order = MarkMaterials(order);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AcknowledgeMaterials(order, minute: 4));

        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(order.CompanyCommission!.ActiveClaim);
        Assert.False(order.CompanyCommission.ClearedToWork);
    }

    [Fact]
    public void LegacyScalarCrafterMaterialsRequireRouteRepricingBeforeFinalClearance()
    {
        var order = CreateClaimedOrder();
        var commission = order.CompanyCommission!;
        var crafterMaterial = Assert.Single(commission.CurrentTerms.Materials) with
        {
            Responsibility = CommissionMaterialResponsibility.Crafter
        };
        var terms = commission.CurrentTerms with { Materials = [crafterMaterial] };
        order.CompanyCommission = commission with
        {
            TermsVersions = [terms],
            Gates = commission.Gates with
            {
                CompanyMaterials = new CompanyCommissionMaterialClearance(
                    CompanyCommissionClearanceState.NotRequired,
                    [])
            }
        };
        order = RecordPayment(order);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConfirmPayment(order));

        Assert.Contains("refreshed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(order.CompanyCommission!.ActiveClaim);
        Assert.False(order.CompanyCommission.ClearedToWork);
    }

    [Fact]
    public void NoGateClaimAtomicallyLocksTheCurrentRouteQuote()
    {
        var order = WithMaterialQuote(CreateClaimedOrder(), StartedAt.AddMinutes(30));
        var commission = order.CompanyCommission!;
        var crafterMaterial = Assert.Single(commission.CurrentTerms.Materials) with
        {
            Responsibility = CommissionMaterialResponsibility.Crafter
        };
        var terms = commission.CurrentTerms with
        {
            Materials = [crafterMaterial],
            Payment = commission.CurrentTerms.Payment with
            {
                Schedule = CompanyCommissionPaymentSchedule.OnDelivery
            }
        };
        order.Status = TradeOrderStatus.ReadyToAssign;
        order.AssignedCrafterId = null;
        order.CompanyCommission = commission with
        {
            TermsVersions = [terms],
            ActiveClaim = null,
            ParticipantGrant = null,
            ParticipantAcknowledgedTermsVersion = null
        };
        var crafterId = Guid.Parse("88888888-8888-8888-8888-888888888888");

        order = Apply(
            order,
            new ClaimCompanyCommissionCommand(
                Context(order),
                1,
                ProvisionalCrafter: null,
                ExistingCrafterId: crafterId),
            Crafter,
            2);

        Assert.True(order.CompanyCommission!.ClearedToWork);
        Assert.Equal(
            StartedAt.AddMinutes(2),
            order.CompanyCommission.CurrentTerms.PricingEvidence.MaterialQuote!.LockedAtUtc);
    }

    [Fact]
    public void SchemaOneCompletedProgressMigratesToOneDerivedDeliveryState()
    {
        var order = CreateClaimedOrder();
        var commission = order.CompanyCommission!;
        var output = Assert.Single(commission.OutputProgress);
        order.Status = TradeOrderStatus.InProgress;
        order.CompanyCommission = commission with
        {
            SchemaVersion = 1,
            OutputProgress =
            [
                output with
                {
                    CompletedQuantity = output.RequiredQuantity,
                    ReadyQuantity = 0
                }
            ]
        };

        var migrated = TradeCompanyCommissionMigrationService.ConvertLegacyOrder(
            order,
            publishedBrief: null,
            canonicalCompanyId: new CompanyId(order.CompanyProfileId),
            initialCommissionRevision: 0,
            migratedAtUtc: StartedAt.AddMinutes(10),
            companyPaymentPolicy: null);

        Assert.Equal(TradeCompanyCommission.CurrentSchemaVersion, migrated.CompanyCommission!.SchemaVersion);
        Assert.Equal(TradeOrderStatus.AwaitingDelivery, migrated.Status);
        var progress = Assert.Single(migrated.CompanyCommission.OutputProgress);
        Assert.Equal(progress.CompletedQuantity, progress.ReadyQuantity);
        Assert.True(migrated.CompanyCommission.DeliveryReadiness.IsReady);
    }

    [Fact]
    public void CompletedCommissionRejectsReplayedProgress()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var output = Assert.Single(order.CompanyCommission!.OutputProgress);
        var completed = new CompanyCommissionProgressQuantity(
            output.LineId,
            output.ItemId,
            output.RequiredQuantity,
            output.RequiredQuantity);
        order = Apply(
            order,
            new ReportCompanyCommissionProgressCommand(Context(order), [completed]),
            Crafter,
            6);
        order = Apply(
            order,
            new AcceptCompanyCommissionDeliveryCommand(Context(order)),
            Commissioner,
            7);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompanyCommissionCommandWorkflow.Apply(
                order,
                new ReportCompanyCommissionProgressCommand(Context(order), [completed]),
                Crafter,
                StartedAt.AddMinutes(8)));

        Assert.Contains("Completed or canceled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryHandoffRejectsUnknownMethod()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var output = Assert.Single(order.CompanyCommission!.OutputProgress);
        order = Apply(
            order,
            new ReportCompanyCommissionProgressCommand(
                Context(order),
                [new CompanyCommissionProgressQuantity(
                    output.LineId,
                    output.ItemId,
                    output.RequiredQuantity,
                    output.RequiredQuantity)]),
            Crafter,
            6);

        Assert.Throws<InvalidOperationException>(() =>
            CompanyCommissionCommandWorkflow.Apply(
                order,
                new RecordCompanyCommissionDeliveryHandoffCommand(
                    Context(order),
                    (CompanyCommissionDeliveryHandoffMethod)999),
                Crafter,
                StartedAt.AddMinutes(7)));
    }

    [Fact]
    public void TermsRevisionRejectsQuoteThatDoesNotCoverCrafterMaterials()
    {
        var order = WithMaterialQuote(CreateClaimedOrder(), StartedAt.AddMinutes(30));
        var current = order.CompanyCommission!.CurrentTerms;
        var payment = current.Payment with
        {
            MaterialReimbursement = 330,
            Total = current.Payment.Total - current.Payment.MaterialReimbursement + 330
        };
        var malformed = current with
        {
            Version = 2,
            Materials = current.Materials
                .Select(item => item with
                {
                    Responsibility = CommissionMaterialResponsibility.Crafter
                })
                .ToArray(),
            Payment = payment,
            PricingEvidence = current.PricingEvidence with
            {
                MaterialQuote = current.PricingEvidence.MaterialQuote! with { Lines = [] }
            }
        };

        Assert.Throws<InvalidOperationException>(() =>
            CompanyCommissionCommandWorkflow.Apply(
                order,
                new AmendCompanyCommissionTermsCommand(
                    Context(order),
                    malformed,
                    "Refresh material quote."),
                Commissioner,
                StartedAt.AddMinutes(10)));
    }

    private static TradeOrder PreparePartialGateEvidence(TradeOrder order) => MarkMaterials(RecordPayment(order), minute: 2);

    private static TradeOrder CompletePayment(TradeOrder order) => ConfirmPayment(RecordPayment(order));

    private static TradeOrder CompleteMaterials(TradeOrder order) => AcknowledgeMaterials(MarkMaterials(order));

    private static TradeOrder RecordPayment(TradeOrder order, int minute = 1) =>
        Apply(order, new RecordCompanyCommissionPaymentCommand(Context(order), "Advance payment sent."), Commissioner, minute);

    private static TradeOrder ConfirmPayment(TradeOrder order, int minute = 2) =>
        Apply(order, new ConfirmCompanyCommissionPaymentReceivedCommand(Context(order), order.CompanyCommission!.CurrentTermsVersion, "Advance payment received."), Crafter, minute);

    private static TradeOrder MarkMaterials(TradeOrder order, int minute = 3) =>
        Apply(order, new MarkCompanyCommissionMaterialsReadyCommand(Context(order), order.CompanyCommission!.Gates.CompanyMaterials.PromisedQuantities), Commissioner, minute);

    private static TradeOrder AcknowledgeMaterials(TradeOrder order, int minute = 4) =>
        Apply(order, new AcknowledgeCompanyCommissionMaterialsCommand(Context(order), order.CompanyCommission!.Gates.CompanyMaterials.PromisedQuantities), Crafter, minute);

    private static TradeOrder Apply(TradeOrder order, ICompanyCommissionCommand command, CompanyCommissionActor actor, int minute) =>
        CompanyCommissionCommandWorkflow.Apply(order, command, actor, StartedAt.AddMinutes(minute)).UpdatedOrder;

    private static TradeOrder Amend(TradeOrder order, CompanyCommissionTermsVersion terms) =>
        Apply(order, new AmendCompanyCommissionTermsCommand(Context(order), terms, "Update commission terms."), Commissioner, 10);

    private static TradeOrder CreateClaimedOrder()
    {
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var companyId = new CompanyId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var output = new CompanyCommissionOutputTerm(Guid.Parse("33333333-3333-3333-3333-333333333333"), 10, "Test output", 1, false);
        var material = new CompanyCommissionMaterialTerm(Guid.Parse("44444444-4444-4444-4444-444444444444"), 20, "Test material", 3, false, CommissionMaterialResponsibility.Provided, 100, 300);
        var terms = new CompanyCommissionTermsVersion
        {
            Version = 1,
            CreatedAtUtc = StartedAt,
            CreatedBy = Commissioner,
            Outputs = [output],
            Materials = [material],
            Payment = new CompanyCommissionPaymentTerms(CompanyCommissionPaymentSchedule.Advance, "Advance commission", 300, 0, 100, 400),
            DeliveryInstructions = "Deliver in Limsa.",
            PricingEvidence = new CompanyCommissionPricingEvidence("Selected routes", "Aether", "Siren", StartedAt)
        };
        var materialQuantities = new[] { new CompanyCommissionMaterialQuantity(material.LineId, material.ItemId, material.Quantity) };

        return new TradeOrder
        {
            Id = orderId,
            CompanyProfileId = companyId.Value,
            Title = "Gate test commission",
            Status = TradeOrderStatus.Assigned,
            CreatedAtUtc = StartedAt,
            UpdatedAtUtc = StartedAt,
            CommissionedAtUtc = StartedAt,
            AssignedCrafterId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                RootItems = [new TradeOrderRootItemSnapshot(10, "Test output", 1, false, 0)],
                Materials = [new TradeOrderMaterialSnapshot(20, "Test material", 3, false, 100, 300)],
                ImportedAtUtc = StartedAt
            },
            CompanyCommission = new TradeCompanyCommission
            {
                CommissionId = orderId,
                CompanyId = companyId,
                CommissionerActorId = Commissioner.ActorId,
                Reference = "CA-GATES",
                CreatedAtUtc = StartedAt,
                UpdatedAtUtc = StartedAt,
                CurrentTermsVersion = 1,
                TermsVersions = [terms],
                PublicMetadata = new CompanyCommissionPublicMetadata
                {
                    PublicBriefId = "gate-test",
                    ViewState = CompanyCommissionPublicViewState.Published
                },
                ActiveClaimCapabilityRevision = 1,
                ActiveClaim = new CompanyCommissionClaim(Guid.Parse("66666666-6666-6666-6666-666666666666"), 1, StartedAt, Guid.Parse("55555555-5555-5555-5555-555555555555"), null),
                ParticipantGrant = new CompanyCommissionParticipantGrant(Guid.Parse("77777777-7777-7777-7777-777777777777"), Guid.Parse("66666666-6666-6666-6666-666666666666"), 1, 1, StartedAt),
                ParticipantAcknowledgedTermsVersion = 1,
                Gates = new CompanyCommissionGateState(
                    new CompanyCommissionIdentityClearance(CompanyCommissionClearanceState.Satisfied, OwnershipConfirmedAtUtc: StartedAt, ConfirmedByActorId: Commissioner.ActorId),
                    new CompanyCommissionPaymentClearance(CompanyCommissionClearanceState.Pending, TermsVersion: 1),
                    new CompanyCommissionMaterialClearance(CompanyCommissionClearanceState.Pending, materialQuantities)),
                OutputProgress = [new CompanyCommissionOutputProgress(output.LineId, output.ItemId, output.RequiredQuantity, 0, 0, 0, StartedAt, Commissioner)],
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(false),
                SettlementState = CompanyCommissionSettlementState.NotDue
            }
        };
    }

    private static TradeOrder WithMaterialQuote(TradeOrder order, DateTime expiresAtUtc)
    {
        var commission = order.CompanyCommission!;
        var terms = commission.CurrentTerms with
        {
            Payment = commission.CurrentTerms.Payment with
            {
                MaterialReimbursement = 330,
                Total = commission.CurrentTerms.Payment.Total -
                    commission.CurrentTerms.Payment.MaterialReimbursement + 330
            },
            PricingEvidence = commission.CurrentTerms.PricingEvidence with
            {
                CostBasis = CommissionCostBasis.ProcurementRoute.ToString(),
                MaterialQuote = new TradeMaterialQuote
                {
                    CompanyProfileId = order.CompanyProfileId,
                    SourcePlanId = order.CraftPlanId ?? "gate-plan",
                    PlanSessionVersion = Math.Max(1, order.SourceSnapshot.PlanSessionVersion),
                    MarketAnalysisVersion = Math.Max(1, order.SourceSnapshot.MarketAnalysisVersion),
                    RouteSelectionKey = "gate-route",
                    PolicyFingerprint = TradeMaterialPricingPolicyNormalizer.Fingerprint(
                        TradeMaterialPricingPolicy.Default),
                    AppliedPolicy = TradeMaterialPricingPolicy.Default,
                    QuotedAtUtc = StartedAt,
                    ExpiresAtUtc = expiresAtUtc,
                    RouteCashRequired = 300,
                    SafetyAllowance = 30,
                    MaterialReimbursement = 330,
                    WorldStops = 1,
                    DataCenterTransfers = 0,
                    Lines =
                    [
                        new TradeMaterialQuoteLine(
                            20,
                            "Test material",
                            3,
                            RequiresHq: false,
                            CashRequired: 300,
                            Worlds: ["Siren (Aether)"],
                            OldestEvidenceAtUtc: StartedAt)
                    ]
                }
            }
        };
        order.CompanyCommission = commission with { TermsVersions = [terms] };
        return order;
    }

    private static CompanyCommissionCommandContext Context(TradeOrder order) =>
        new(order.CompanyCommission!.CompanyId, order.Id, new CompanyRecordRevision(1), new CompanyRecordRevision(1), Guid.NewGuid(), CompanyCommissionProtocol.Version1);

    public enum GateRevisionScenario
    {
        UnchangedBoundFacts,
        PaymentChanged,
        PartialPaymentAfterRevision,
        MaterialQuantityChanged,
        MaterialQualityChanged,
        PaymentClearsFirst,
        MaterialsClearFirst
    }
}
