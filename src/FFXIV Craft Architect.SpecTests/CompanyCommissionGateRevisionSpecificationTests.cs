using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.SpecTests;

public sealed class CompanyCommissionGateRevisionSpecificationTests
{
    private static readonly DateTime StartedAt =
        new(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc);

    private static readonly CompanyCommissionActor Commissioner =
        new("commissioner", CompanyCommissionActorKind.Commissioner, "Commissioner");

    private static readonly CompanyCommissionActor Crafter =
        new("crafter", CompanyCommissionActorKind.Crafter, "Crafter");

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
            case GateRevisionScenario.UnchangedBoundFacts:
                TermsRevisionPreservesSatisfiedGatesWhoseBoundFactsDidNotChange();
                break;
            case GateRevisionScenario.PaymentChanged:
                PaymentChangeResetsOnlyPaymentEvidence();
                break;
            case GateRevisionScenario.PartialPaymentAfterRevision:
                UnchangedPartialPaymentEvidenceCanCompleteAfterTermsRevision();
                break;
            case GateRevisionScenario.MaterialQuantityChanged:
                CompanyMaterialPromiseChangeResetsOnlyHandoffEvidence();
                break;
            case GateRevisionScenario.MaterialQualityChanged:
                CompanyMaterialQualityChangeResetsOnlyHandoffEvidence();
                break;
            case GateRevisionScenario.PaymentClearsFirst:
                PaymentAndCompanyMaterialGatesCanClearInEitherOrder(paymentFirst: true);
                break;
            case GateRevisionScenario.MaterialsClearFirst:
                PaymentAndCompanyMaterialGatesCanClearInEitherOrder(paymentFirst: false);
                break;
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
        Assert.All(
            order.CompanyCommission.OutputProgress,
            progress => Assert.Equal(0, progress.CompletedQuantity));
    }

    [Fact]
    public void CrafterProgressProjectsToPublicAndParticipantBriefs()
    {
        var order = CompleteBothGates(CreateClaimedOrder());
        var progress = Assert.Single(order.CompanyCommission!.OutputProgress);
        var transition = CompanyCommissionCommandWorkflow.Apply(
            order,
            new ReportCompanyCommissionProgressCommand(
                Context(order),
                [new CompanyCommissionProgressQuantity(
                    progress.LineId,
                    progress.ItemId,
                    CompletedQuantity: 1,
                    ReadyQuantity: 1)],
                "Ready for handoff."),
            Crafter,
            StartedAt.AddMinutes(5));
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
        Assert.Equal(
            original.Gates.Payment.CommissionerSent,
            revised.Gates.Payment.CommissionerSent);
        Assert.Equal(
            original.Gates.Payment.CrafterReceived,
            revised.Gates.Payment.CrafterReceived);
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
            Payment = original.CurrentTerms.Payment with
            {
                MaterialAdjustment = 25,
                Total = original.CurrentTerms.Payment.Total + 25
            }
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
        order = Apply(
            order,
            new RecordCompanyCommissionPaymentCommand(
                Context(order),
                "Advance payment sent."),
            Commissioner,
            1);
        var originalAttestation = order.CompanyCommission!.Gates.Payment.CommissionerSent;
        var nextTerms = order.CompanyCommission.CurrentTerms with
        {
            Version = 2,
            DeliveryInstructions = "Deliver in Gridania."
        };
        order = Amend(order, nextTerms);

        order = Apply(
            order,
            new ConfirmCompanyCommissionPaymentReceivedCommand(
                Context(order),
                2,
                "Advance payment received."),
            Crafter,
            11);

        Assert.Equal(
            CompanyCommissionClearanceState.Satisfied,
            order.CompanyCommission!.Gates.Payment.State);
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
        var nextTerms = original.CurrentTerms with
        {
            Version = 2,
            Materials = [changedMaterial]
        };

        var revised = Amend(order, nextTerms).CompanyCommission!;

        Assert.Equal(2, revised.Gates.Payment.TermsVersion);
        Assert.Equal(
            original.Gates.Payment.CommissionerSent,
            revised.Gates.Payment.CommissionerSent);
        Assert.Equal(
            original.Gates.Payment.CrafterReceived,
            revised.Gates.Payment.CrafterReceived);
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
        var nextTerms = original.CurrentTerms with
        {
            Version = 2,
            Materials = [changedMaterial]
        };

        var revised = Amend(order, nextTerms).CompanyCommission!;

        Assert.Equal(2, revised.Gates.Payment.TermsVersion);
        Assert.Equal(original.Gates.Payment.State, revised.Gates.Payment.State);
        Assert.Equal(
            CompanyCommissionClearanceState.Pending,
            revised.Gates.CompanyMaterials.State);
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

    private static TradeOrder CompletePayment(TradeOrder order)
    {
        order = Apply(
            order,
            new RecordCompanyCommissionPaymentCommand(
                Context(order),
                "Advance payment sent."),
            Commissioner,
            1);
        return Apply(
            order,
            new ConfirmCompanyCommissionPaymentReceivedCommand(
                Context(order),
                order.CompanyCommission!.CurrentTermsVersion,
                "Advance payment received."),
            Crafter,
            2);
    }

    private static TradeOrder CompleteMaterials(TradeOrder order)
    {
        var quantities = order.CompanyCommission!.Gates.CompanyMaterials.PromisedQuantities;
        order = Apply(
            order,
            new MarkCompanyCommissionMaterialsReadyCommand(Context(order), quantities),
            Commissioner,
            3);
        return Apply(
            order,
            new AcknowledgeCompanyCommissionMaterialsCommand(Context(order), quantities),
            Crafter,
            4);
    }

    private static TradeOrder Apply(
        TradeOrder order,
        ICompanyCommissionCommand command,
        CompanyCommissionActor actor,
        int minute) =>
        CompanyCommissionCommandWorkflow.Apply(
            order,
            command,
            actor,
            StartedAt.AddMinutes(minute)).UpdatedOrder;

    private static TradeOrder Amend(
        TradeOrder order,
        CompanyCommissionTermsVersion terms) =>
        Apply(
            order,
            new AmendCompanyCommissionTermsCommand(
                Context(order),
                terms,
                "Update commission terms."),
            Commissioner,
            10);

    private static TradeOrder CreateClaimedOrder()
    {
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var companyId = new CompanyId(
            Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var output = new CompanyCommissionOutputTerm(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            10,
            "Test output",
            1,
            false);
        var material = new CompanyCommissionMaterialTerm(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            20,
            "Test material",
            3,
            false,
            CommissionMaterialResponsibility.Provided,
            100,
            300);
        var terms = new CompanyCommissionTermsVersion
        {
            Version = 1,
            CreatedAtUtc = StartedAt,
            CreatedBy = Commissioner,
            Outputs = [output],
            Materials = [material],
            Payment = new CompanyCommissionPaymentTerms(
                CompanyCommissionPaymentSchedule.Advance,
                "Advance commission",
                300,
                0,
                100,
                400),
            DeliveryInstructions = "Deliver in Limsa.",
            PricingEvidence = new CompanyCommissionPricingEvidence(
                "Selected routes",
                "Aether",
                "Siren",
                StartedAt)
        };
        var materialQuantities = new[]
        {
            new CompanyCommissionMaterialQuantity(
                material.LineId,
                material.ItemId,
                material.Quantity)
        };

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
                ActiveClaim = new CompanyCommissionClaim(
                    Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    1,
                    StartedAt,
                    Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    null),
                ParticipantGrant = new CompanyCommissionParticipantGrant(
                    Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    1,
                    1,
                    StartedAt),
                ParticipantAcknowledgedTermsVersion = 1,
                Gates = new CompanyCommissionGateState(
                    new CompanyCommissionIdentityClearance(
                        CompanyCommissionClearanceState.Satisfied,
                        OwnershipConfirmedAtUtc: StartedAt,
                        ConfirmedByActorId: Commissioner.ActorId),
                    new CompanyCommissionPaymentClearance(
                        CompanyCommissionClearanceState.Pending,
                        TermsVersion: 1),
                    new CompanyCommissionMaterialClearance(
                        CompanyCommissionClearanceState.Pending,
                        materialQuantities)),
                OutputProgress =
                [
                    new CompanyCommissionOutputProgress(
                        output.LineId,
                        output.ItemId,
                        output.RequiredQuantity,
                        0,
                        0,
                        0,
                        StartedAt,
                        Commissioner)
                ],
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(false),
                SettlementState = CompanyCommissionSettlementState.NotDue
            }
        };
    }

    private static CompanyCommissionCommandContext Context(TradeOrder order) =>
        new(
            order.CompanyCommission!.CompanyId,
            order.Id,
            new CompanyRecordRevision(1),
            new CompanyRecordRevision(1),
            Guid.NewGuid(),
            CompanyCommissionProtocol.Version1);

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
