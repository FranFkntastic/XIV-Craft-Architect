using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
namespace FFXIV_Craft_Architect.SpecTests;
public sealed class CompanyCommissionDraftSpecificationTests
{
    private static void LifecycleActionUsesOneSlotAcrossDraftPublicationAndClosure()
    {
        var plainDraft = new TradeOrder { Status = TradeOrderStatus.Draft };
        Assert.Equal(TradeOrderLifecycleAction.DiscardDraft, TradeOrderWorkflow.GetLifecycleAction(plainDraft));
        Assert.Equal(TradeOrderLifecycleAction.DiscardDraft, TradeOrderWorkflow.GetLifecycleAction(new TradeOrder { Status = TradeOrderStatus.ReadyToAssign }));
        var hostedDraft = CreateDraftOrder();
        Assert.Equal(TradeOrderLifecycleAction.DiscardDraft, TradeOrderWorkflow.GetLifecycleAction(hostedDraft));
        var canceledDraft = CompanyCommissionCommandWorkflow.Apply(hostedDraft,
            new CancelCompanyCommissionCommand(Context(hostedDraft), "Draft discarded before publication."),
            new CompanyCommissionActor("commissioner", CompanyCommissionActorKind.Commissioner, "Commissioner"),
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(TradeOrderStatus.Canceled, canceledDraft.UpdatedOrder.Status);
        var published = TradeOrderWorkflow.CopyOrder(hostedDraft);
        published.CompanyCommission = published.CompanyCommission! with { PublicMetadata = published.CompanyCommission.PublicMetadata with { ViewState = CompanyCommissionPublicViewState.Published } };
        Assert.Equal(TradeOrderLifecycleAction.CancelCommission, TradeOrderWorkflow.GetLifecycleAction(published));
        published.Status = TradeOrderStatus.Canceled;
        Assert.Equal(TradeOrderLifecycleAction.None, TradeOrderWorkflow.GetLifecycleAction(published));
    }
    [Fact]
    public void DraftUpdateOwnsTermsAndReloadableWorkPackageThenClosesAfterPublication()
    {
        LifecycleActionUsesOneSlotAcrossDraftPublicationAndClosure();
        var order = CreateDraftOrder();
        var actor = new CompanyCommissionActor(
            "commissioner",
            CompanyCommissionActorKind.Commissioner,
            "Commissioner");
        var terms = order.CompanyCommission!.CurrentTerms with
        {
            Outputs =
            [
                order.CompanyCommission.CurrentTerms.Outputs[0] with
                {
                    RequiredQuantity = 2
                }
            ]
        };
        var snapshot = TradeOrderWorkflow.CopySourceSnapshot(order.SourceSnapshot);
        snapshot.RootItems = [new TradeOrderRootItemSnapshot(10, "Test output", 2, false, 0)];
        var command = new UpdateCompanyCommissionDraftCommand(
            Context(order),
            terms,
            new CompanyCommissionDraftWorkPackage(
                [new TradeRequestedOrderOutput(10, "Test output", 2, false, 0)],
                snapshot,
                CraftPlanId: null,
                CraftPlanName: null,
                CraftPlanSavedAtUtc: null,
                TradeOrderCraftPlanLinkKind.Unknown));
        var transition = CompanyCommissionCommandWorkflow.Apply(
            order,
            command,
            actor,
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(2, transition.UpdatedOrder.CompanyCommission!.CurrentTerms.Outputs[0].RequiredQuantity);
        Assert.Equal(2, transition.UpdatedOrder.SourceSnapshot.RootItems[0].Quantity);
        Assert.Equal(2, transition.UpdatedOrder.CompanyCommission.OutputProgress[0].RequiredQuantity);
        Assert.Null(transition.UpdatedOrder.CraftPlanId);
        var published = TradeOrderWorkflow.CopyOrder(transition.UpdatedOrder);
        published.CompanyCommission = published.CompanyCommission! with
        {
            PublicMetadata = published.CompanyCommission.PublicMetadata with
            {
                ViewState = CompanyCommissionPublicViewState.Published
            }
        };
        Assert.Throws<InvalidOperationException>(() =>
            CompanyCommissionCommandWorkflow.Apply(
                published,
                command,
                actor,
                DateTime.UtcNow));
        var timingOrder = CreateDraftOrder();
        var current = timingOrder.CompanyCommission!.CurrentTerms; var brief = BuildBrief(timingOrder);
        brief.Payment = brief.Payment with { Schedule = CompanyCommissionPaymentSchedule.Custom, CustomTerms = "Half at handoff; half on delivery." };
        var updated = TradeCompanyCommissionMigrationService.CreateDraftTerms(timingOrder, brief, current, new DateTime(2026, 8, 1, 12, 15, 0, DateTimeKind.Utc));
        Assert.Equal((current.Outputs[0].LineId, current.Materials[0].LineId), (updated.Outputs[0].LineId, updated.Materials[0].LineId));
        Assert.Equal((CompanyCommissionPaymentSchedule.Custom, "Half at handoff; half on delivery."), (updated.Payment.Schedule, updated.Payment.CustomTerms));
        var historical = CreateDraftOrder();
        historical.CompanyCommission = null;
        historical.PaymentPolicyOverride = null;
        historical.PaymentSchedule = CompanyCommissionPaymentSchedule.Custom;
        historical.CustomPaymentTerms = "Half now; half later.";
        historical.SourceSnapshot.CraftLabor = [new("test", 10, "Test output", 1, 1)];
        Assert.Throws<InvalidOperationException>(() => TradeCompanyCommissionMigrationService.ConvertLegacyOrder(historical, null, new(historical.CompanyProfileId), 0, DateTime.UtcNow, null));
        var companyPolicy = new TradePaymentPolicy(TradePaymentContractMode.LaborStandard, 20, 250);
        var migratedPayment = TradeCompanyCommissionMigrationService.ConvertLegacyOrder(historical, null, new(historical.CompanyProfileId), 0, DateTime.UtcNow, companyPolicy).CompanyCommission!.CurrentTerms.Payment;
        Assert.Equal((CompanyCommissionPaymentSchedule.Custom, "Half now; half later.", "Labor standard", 300m, 250m, 550m, 1, 250m), (migratedPayment.Schedule, migratedPayment.CustomTerms, migratedPayment.ContractLabel, migratedPayment.MaterialReimbursement, migratedPayment.CraftLabor, migratedPayment.Total, migratedPayment.CraftSynthCount, migratedPayment.GilPerSynth));
        historical.PaymentPolicyOverride = TradePaymentPolicy.LegacyDefault;
        Assert.Equal("Legacy commission", TradeCompanyCommissionMigrationService.ConvertLegacyOrder(historical, null, new(historical.CompanyProfileId), 0, DateTime.UtcNow, companyPolicy).CompanyCommission!.CurrentTerms.Payment.ContractLabel);
    }
    [Fact]
    public void PublicationPreservesExactTermsAndRecordsOneOpeningAcrossReplay()
    {
        var order = CreateDraftOrder();
        var companyId = order.CompanyCommission!.CompanyId;
        var ownership = new TradeCompanyPublicationOwnership(
            companyId,
            order.Id,
            new CompanyRecordRevision(3));
        order.CommissionPublication = new TradeCommissionPublication
        {
            PublicId = "canonical-brief",
            PublicUrl = "https://example.invalid/commission/canonical-brief",
            Version = 7,
            PublishedAtUtc = new DateTime(2026, 8, 1, 12, 30, 0, DateTimeKind.Utc),
            Ownership = ownership
        };
        var brief = BuildBrief(order);
        var published = new PublishedCommissionBrief
        {
            PublicId = order.CommissionPublication.PublicId,
            Version = order.CommissionPublication.Version,
            PublishedAtUtc = order.CommissionPublication.PublishedAtUtc,
            Brief = brief,
            Ownership = ownership
        };
        var opened = TradeCompanyCommissionMigrationService.BindPublishedBrief(
            order,
            published,
            published.PublishedAtUtc);
        var replayed = TradeCompanyCommissionMigrationService.BindPublishedBrief(
            opened,
            published,
            published.PublishedAtUtc.AddSeconds(1));
        Assert.Equal(TradeOrderStatus.ReadyToAssign, replayed.Status);
        Assert.Equal(1, replayed.CompanyCommission!.CurrentTermsVersion);
        Assert.Single(replayed.CompanyCommission.TermsVersions);
        Assert.Equal(
            CompanyCommissionPaymentSchedule.OnDelivery,
            replayed.CompanyCommission.CurrentTerms.Payment.Schedule);
        Assert.Single(
            replayed.CompanyCommission.Activity,
            item => item.Kind == CompanyCommissionActivityKind.CommissionOpened);
        brief.Payment = brief.Payment with
        {
            Schedule = CompanyCommissionPaymentSchedule.Advance
        };
        Assert.Throws<InvalidOperationException>(() =>
            TradeCompanyCommissionMigrationService.BindPublishedBrief(
                CreateDraftOrderWithPublication(ownership),
                published,
                published.PublishedAtUtc));
    }
    private static TradeOrder CreateDraftOrder()
    {
        var orderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var companyGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var companyId = new CompanyId(companyGuid);
        var created = new DateTime(2026, 8, 1, 11, 0, 0, DateTimeKind.Utc);
        var actor = new CompanyCommissionActor("commissioner", CompanyCommissionActorKind.Commissioner, "Commissioner");
        var output = new CompanyCommissionOutputTerm(Guid.Parse("33333333-3333-3333-3333-333333333333"), 10, "Test output", 1, false);
        var material = new CompanyCommissionMaterialTerm(Guid.Parse("44444444-4444-4444-4444-444444444444"), 20, "Test material", 3, false, CommissionMaterialResponsibility.Provided, 100, 300);
        var terms = new CompanyCommissionTermsVersion
        {
            Version = 1, CreatedAtUtc = created, CreatedBy = actor, Outputs = [output], Materials = [material],
            Payment = new CompanyCommissionPaymentTerms(CompanyCommissionPaymentSchedule.OnDelivery, "Labor standard", 0, 60, 200, 260, CraftSynthCount: 1, GilPerSynth: 200),
            DeliveryInstructions = "Deliver in Limsa.", ContactInstructions = "Reply in Discord.",
            PricingEvidence = new CompanyCommissionPricingEvidence("Selected routes", "Aether", "Siren", created)
        };
        return new TradeOrder
        {
            Id = orderId, CompanyProfileId = companyGuid, Title = "Test commission", Status = TradeOrderStatus.ReadyToAssign,
            CreatedAtUtc = created, UpdatedAtUtc = created, CommissionedAtUtc = created,
            SourceSnapshot = new TradeOrderSourceSnapshot
            {
                RootItems = [new TradeOrderRootItemSnapshot(10, "Test output", 1, false, 0)],
                Materials = [new TradeOrderMaterialSnapshot(20, "Test material", 3, false, 100, 300)], ImportedAtUtc = created
            },
            CraftPlanId = "stale-plan", CraftPlanName = "Stale plan", CraftPlanSavedAtUtc = created,
            CraftPlanLinkKind = TradeOrderCraftPlanLinkKind.OrderGenerated,
            CompanyCommission = new TradeCompanyCommission
            {
                CommissionId = orderId, CompanyId = companyId, CommissionerActorId = actor.ActorId, Reference = "CA-TEST",
                CreatedAtUtc = created, UpdatedAtUtc = created, CurrentTermsVersion = 1, TermsVersions = [terms],
                PublicMetadata = new CompanyCommissionPublicMetadata { PublicBriefId = "draft-test", ViewState = CompanyCommissionPublicViewState.Draft },
                ActiveClaimCapabilityRevision = 0,
                Gates = new CompanyCommissionGateState(
                    new CompanyCommissionIdentityClearance(CompanyCommissionClearanceState.NotRequired),
                    new CompanyCommissionPaymentClearance(CompanyCommissionClearanceState.NotRequired, TermsVersion: 1),
                    new CompanyCommissionMaterialClearance(CompanyCommissionClearanceState.Pending, [new CompanyCommissionMaterialQuantity(material.LineId, material.ItemId, material.Quantity)])),
                OutputProgress = [new CompanyCommissionOutputProgress(output.LineId, output.ItemId, output.RequiredQuantity, 0, 0, 0, created, actor)],
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(false),
                SettlementState = CompanyCommissionSettlementState.NotDue
            }
        };
    }
    private static TradeOrder CreateDraftOrderWithPublication(
        TradeCompanyPublicationOwnership ownership)
    {
        var order = CreateDraftOrder();
        order.CommissionPublication = new TradeCommissionPublication { PublicId = "canonical-brief", PublicUrl = "https://example.invalid/commission/canonical-brief", Version = 7, PublishedAtUtc = new DateTime(2026, 8, 1, 12, 30, 0, DateTimeKind.Utc), Ownership = ownership };
        return order;
    }
    private static CommissionBriefDocument BuildBrief(TradeOrder order)
    {
        var commission = order.CompanyCommission!;
        var terms = commission.CurrentTerms;
        return new CommissionBriefDocument {
            Title = order.Title, Reference = commission.Reference, Contact = terms.ContactInstructions, DeliveryInstructions = terms.DeliveryInstructions,
            Outputs = [new CommissionBriefOutput(10, "Test output", 1, false)],
            CompanyMaterials = [new CommissionBriefMaterial(20, "Test material", 3, false, 100, 300)],
            Payment = new CommissionBriefPayment(terms.Payment.ContractLabel, terms.Payment.MaterialReimbursement, terms.Payment.MaterialAdjustment, terms.Payment.CraftLabor, terms.Payment.Total, CraftSynthCount: terms.Payment.CraftSynthCount, GilPerSynth: terms.Payment.GilPerSynth, Schedule: terms.Payment.Schedule, CustomTerms: terms.Payment.CustomTerms),
            Evidence = new CommissionBriefEvidence(terms.PricingEvidence.CostBasis, terms.PricingEvidence.MarketScope, terms.PricingEvidence.Location, terms.PricingEvidence.CapturedAtUtc)
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
}
