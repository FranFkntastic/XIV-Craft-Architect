using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class TradeCompanyCommissionMigrationService
{
    public static CompanyCommissionTermsVersion CreateTermsRevision(
        TradeOrder source,
        CommissionBriefDocument brief,
        int version,
        string reason,
        DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (version <= 1)
        {
            throw new InvalidOperationException(
                "A published terms revision must be version 2 or later.");
        }

        var actor = new CompanyCommissionActor(
            "trade-architect",
            CompanyCommissionActorKind.Commissioner,
            "Trade Architect");
        return CreateTerms(source, brief, actor, createdAtUtc) with
        {
            Version = version,
            ChangeSummary = reason.Trim()
        };
    }

    public static TradeOrder BindPublishedBrief(
        TradeOrder source,
        PublishedCommissionBrief publishedBrief,
        DateTime boundAtUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(publishedBrief);
        if (source.CompanyCommission == null)
        {
            return ConvertLegacyOrder(
                source,
                publishedBrief,
                publishedBrief.Ownership?.CompanyId ??
                    throw new InvalidOperationException(
                        "A company-owned immutable brief is required."),
                initialCommissionRevision: 0,
                boundAtUtc);
        }

        var canonicalCompanyId = publishedBrief.Ownership?.CompanyId ??
            throw new InvalidOperationException(
                "A company-owned immutable brief is required.");
        ValidatePublicationOwnership(source, publishedBrief, canonicalCompanyId);
        var copy = TradeOrderWorkflow.CopyOrder(source);
        var commission = copy.CompanyCommission!;
        if (commission.ActiveClaim != null ||
            commission.PublicMetadata.ViewState != CompanyCommissionPublicViewState.Draft &&
            !string.Equals(
                commission.PublicMetadata.PublicBriefId,
                publishedBrief.PublicId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A claimed or differently published commission cannot be rebound.");
        }

        var actor = new CompanyCommissionActor(
            "hosted-publication",
            CompanyCommissionActorKind.System,
            "Hosted publication");
        var terms = CreateTerms(copy, publishedBrief.Brief, actor, boundAtUtc);
        var companyMaterials = terms.Materials
            .Where(item =>
                item.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(item => new CompanyCommissionMaterialQuantity(
                item.LineId,
                item.ItemId,
                item.Quantity))
            .ToArray();
        copy.CompanyCommission = commission with
        {
            UpdatedAtUtc = boundAtUtc,
            CurrentTermsVersion = publishedBrief.Version,
            TermsVersions = [terms with { Version = publishedBrief.Version }],
            PublicMetadata = CreatePublicMetadata(copy, publishedBrief),
            ActiveClaimCapabilityRevision = publishedBrief.Brief.IsTestFixture
                ? 0
                : Math.Max(commission.ActiveClaimCapabilityRevision, 1),
            Gates = commission.Gates with
            {
                Payment = terms.Payment.Schedule == CompanyCommissionPaymentSchedule.Advance &&
                          terms.Payment.Total > 0
                    ? new CompanyCommissionPaymentClearance(
                        CompanyCommissionClearanceState.Pending,
                        TermsVersion: terms.Version)
                    : new CompanyCommissionPaymentClearance(
                        CompanyCommissionClearanceState.NotRequired,
                        TermsVersion: terms.Version),
                CompanyMaterials = new CompanyCommissionMaterialClearance(
                    companyMaterials.Length == 0
                        ? CompanyCommissionClearanceState.NotRequired
                        : CompanyCommissionClearanceState.Pending,
                    companyMaterials)
            },
            OutputProgress = terms.Outputs.Select(output =>
                new CompanyCommissionOutputProgress(
                    output.LineId,
                    output.ItemId,
                    output.RequiredQuantity,
                    0,
                    0,
                    0,
                    boundAtUtc,
                    actor)).ToArray()
        };
        copy.UpdatedAtUtc = boundAtUtc;
        return copy;
    }

    public static TradeOrder ConvertLegacyOrder(
        TradeOrder source,
        PublishedCommissionBrief? publishedBrief,
        CompanyId canonicalCompanyId,
        long initialCommissionRevision,
        DateTime migratedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Id == Guid.Empty || source.CompanyProfileId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A canonical company and order identity are required for commission migration.");
        }

        if (source.CompanyCommission is { } existing)
        {
            if (existing.SchemaVersion != TradeCompanyCommission.CurrentSchemaVersion ||
                existing.CommissionId != source.Id ||
                existing.CompanyId.Value != source.CompanyProfileId)
            {
                throw new InvalidOperationException(
                    "The hosted Trade order contains an incompatible company commission aggregate.");
            }

            var existingCopy = TradeOrderWorkflow.CopyOrder(source);
            if (!RequiresAssignedClaimRepair(existingCopy))
            {
                return existingCopy;
            }

            var claimId = CreateDeterministicGuid(source.Id, "legacy-assigned-claim");
            existingCopy.CompanyCommission = existing with
            {
                UpdatedAtUtc = migratedAtUtc,
                ActiveClaim = new CompanyCommissionClaim(
                    claimId,
                    existing.CurrentTermsVersion,
                    migratedAtUtc,
                    source.AssignedCrafterId,
                    null),
                ParticipantGrant = new CompanyCommissionParticipantGrant(
                    CreateDeterministicGuid(source.Id, "legacy-participant-grant"),
                    claimId,
                    existing.CurrentTermsVersion,
                    1,
                    migratedAtUtc),
                ParticipantAcknowledgedTermsVersion = existing.CurrentTermsVersion
            };
            existingCopy.UpdatedAtUtc = migratedAtUtc;
            return existingCopy;
        }

        ValidatePublicationOwnership(source, publishedBrief, canonicalCompanyId);

        var copy = TradeOrderWorkflow.CopyOrder(source);
        var migrationActor = new CompanyCommissionActor(
            "hosted-migration",
            CompanyCommissionActorKind.Migration,
            "Hosted migration");
        var brief = publishedBrief?.Brief;
        var terms = CreateTerms(copy, brief, migrationActor, migratedAtUtc);
        var outputs = terms.Outputs;
        var lifecycleProvesWorkBegan = copy.Status is
            TradeOrderStatus.InProgress or
            TradeOrderStatus.AwaitingDelivery or
            TradeOrderStatus.Completed;
        var hasCompanyMaterials = terms.Materials.Any(
            material => material.Responsibility == CommissionMaterialResponsibility.Provided);
        var paymentRequiredBeforeWork =
            terms.Payment.Schedule == CompanyCommissionPaymentSchedule.Advance &&
            terms.Payment.Total > 0;
        var identitySatisfied = copy.AssignedCrafterId.HasValue;
        var paymentSatisfied = paymentRequiredBeforeWork && lifecycleProvesWorkBegan;
        var companyMaterialsSatisfied = hasCompanyMaterials && lifecycleProvesWorkBegan;
        var hasAssignedClaim = copy.AssignedCrafterId.HasValue &&
            copy.Status is
                TradeOrderStatus.Assigned or
                TradeOrderStatus.InProgress or
                TradeOrderStatus.AwaitingDelivery or
                TradeOrderStatus.Completed;
        var assignedClaimId = hasAssignedClaim
            ? CreateDeterministicGuid(copy.Id, "legacy-assigned-claim")
            : Guid.Empty;

        var activity = ConvertHistory(
            copy,
            terms.Version,
            migrationActor,
            initialCommissionRevision,
            migratedAtUtc);
        var lastRevision = activity.Last().CommissionRevision;
        copy.CompanyCommission = new TradeCompanyCommission
        {
            CommissionId = copy.Id,
            CompanyId = canonicalCompanyId,
            CommissionerActorId = "company-owner",
            Reference = string.IsNullOrWhiteSpace(brief?.Reference)
                ? copy.Title
                : brief.Reference,
            CreatedAtUtc = copy.CreatedAtUtc,
            UpdatedAtUtc = migratedAtUtc,
            CurrentTermsVersion = terms.Version,
            TermsVersions = [terms],
            PublicMetadata = CreatePublicMetadata(copy, publishedBrief),
            ActiveClaimCapabilityRevision = publishedBrief == null ||
                                            publishedBrief.Brief.IsTestFixture
                ? 0
                : 1,
            ActiveClaim = hasAssignedClaim
                ? new CompanyCommissionClaim(
                    assignedClaimId,
                    terms.Version,
                    migratedAtUtc,
                    copy.AssignedCrafterId,
                    null)
                : null,
            ParticipantGrant = hasAssignedClaim
                ? new CompanyCommissionParticipantGrant(
                    CreateDeterministicGuid(copy.Id, "legacy-participant-grant"),
                    assignedClaimId,
                    terms.Version,
                    1,
                    migratedAtUtc)
                : null,
            ParticipantAcknowledgedTermsVersion = hasAssignedClaim
                ? terms.Version
                : null,
            Gates = new CompanyCommissionGateState(
                new CompanyCommissionIdentityClearance(
                    identitySatisfied
                        ? CompanyCommissionClearanceState.Satisfied
                        : CompanyCommissionClearanceState.Pending,
                    OwnershipConfirmedAtUtc: identitySatisfied && lifecycleProvesWorkBegan
                        ? migratedAtUtc
                        : null,
                    ConfirmedByActorId: identitySatisfied && lifecycleProvesWorkBegan
                        ? migrationActor.ActorId
                        : null),
                new CompanyCommissionPaymentClearance(
                    !paymentRequiredBeforeWork
                        ? CompanyCommissionClearanceState.NotRequired
                        : paymentSatisfied
                            ? CompanyCommissionClearanceState.Satisfied
                            : CompanyCommissionClearanceState.Pending,
                    RecordedAtUtc: paymentSatisfied ? migratedAtUtc : null,
                    RecordedByActorId: paymentSatisfied ? migrationActor.ActorId : null,
                    Note: paymentSatisfied
                        ? "Converted from a legacy lifecycle that had already entered work."
                        : null,
                    TermsVersion: terms.Version),
                new CompanyCommissionMaterialClearance(
                    !hasCompanyMaterials
                        ? CompanyCommissionClearanceState.NotRequired
                        : companyMaterialsSatisfied
                            ? CompanyCommissionClearanceState.Satisfied
                            : CompanyCommissionClearanceState.Pending,
                    terms.Materials
                        .Where(material =>
                            material.Responsibility == CommissionMaterialResponsibility.Provided)
                        .Select(material => new CompanyCommissionMaterialQuantity(
                            material.LineId,
                            material.ItemId,
                            material.Quantity))
                        .ToArray(),
                    ReadyAtUtc: companyMaterialsSatisfied ? migratedAtUtc : null,
                    ReceivedAtUtc: companyMaterialsSatisfied ? migratedAtUtc : null,
                    ReceivedByActorId: companyMaterialsSatisfied ? migrationActor.ActorId : null)),
            OutputProgress = CreateProgress(copy, outputs, migrationActor, migratedAtUtc),
            DeliveryReadiness = new CompanyCommissionDeliveryReadiness(
                copy.Status is TradeOrderStatus.AwaitingDelivery or TradeOrderStatus.Completed,
                DeclaredAtUtc: copy.Status is TradeOrderStatus.AwaitingDelivery or TradeOrderStatus.Completed
                    ? migratedAtUtc
                    : null),
            SettlementState = ResolveSettlement(copy, terms, paymentSatisfied),
            Activity = activity
        };
        copy.UpdatedAtUtc = migratedAtUtc;

        return copy;
    }

    public static bool RequiresAssignedClaimRepair(TradeOrder order) =>
        order.CompanyCommission is { ActiveClaim: null } &&
        order.AssignedCrafterId.HasValue &&
        order.Status is
            TradeOrderStatus.Assigned or
            TradeOrderStatus.InProgress or
            TradeOrderStatus.AwaitingDelivery or
            TradeOrderStatus.Completed;

    private static CompanyCommissionTermsVersion CreateTerms(
        TradeOrder order,
        CommissionBriefDocument? brief,
        CompanyCommissionActor actor,
        DateTime createdAtUtc)
    {
        var outputs = brief?.Outputs.Count > 0
            ? brief.Outputs.Select((output, index) => new CompanyCommissionOutputTerm(
                CreateDeterministicGuid(
                    order.Id,
                    $"output:{index}:{output.ItemId}:{output.MustBeHq}"),
                output.ItemId,
                output.Name,
                output.Quantity,
                output.MustBeHq)).ToArray()
            : order.SourceSnapshot.RootItems.Select((output, index) => new CompanyCommissionOutputTerm(
                CreateDeterministicGuid(
                    order.Id,
                    $"output:{index}:{output.ItemId}:{output.MustBeHq}"),
                output.ItemId,
                output.Name,
                output.Quantity,
                output.MustBeHq)).ToArray();
        if (outputs.Length == 0)
        {
            throw new InvalidOperationException(
                "The legacy Trade order has no requested outputs to migrate.");
        }

        var materials = brief == null
            ? order.SourceSnapshot.Materials.Select((material, index) => new CompanyCommissionMaterialTerm(
                CreateDeterministicGuid(
                    order.Id,
                    $"material:{index}:{material.ItemId}:{material.RequiresHq}:crafter"),
                material.ItemId,
                material.Name,
                material.Quantity,
                material.RequiresHq,
                CommissionMaterialResponsibility.Crafter,
                material.UnitCost,
                material.TotalCost)).ToArray()
            : brief.CrafterMaterials.Select((material, index) => new CompanyCommissionMaterialTerm(
                    CreateDeterministicGuid(
                        order.Id,
                        $"material:{index}:{material.ItemId}:{material.RequiresHq}:crafter"),
                    material.ItemId,
                    material.Name,
                    material.Quantity,
                    material.RequiresHq,
                    CommissionMaterialResponsibility.Crafter,
                    material.UnitCost,
                    material.TotalCost))
                .Concat(brief.CompanyMaterials.Select((material, index) => new CompanyCommissionMaterialTerm(
                    CreateDeterministicGuid(
                        order.Id,
                        $"material:{index}:{material.ItemId}:{material.RequiresHq}:company"),
                    material.ItemId,
                    material.Name,
                    material.Quantity,
                    material.RequiresHq,
                    CommissionMaterialResponsibility.Provided,
                    material.UnitCost,
                    material.TotalCost)))
                .ToArray();
        var payment = brief?.Payment;
        var evidence = brief?.Evidence;

        return new CompanyCommissionTermsVersion
        {
            Version = 1,
            CreatedAtUtc = createdAtUtc,
            CreatedBy = actor,
            Outputs = outputs,
            Materials = materials,
            Payment = new CompanyCommissionPaymentTerms(
                CompanyCommissionPaymentSchedule.Advance,
                payment?.ContractLabel ?? "Commission",
                payment?.MaterialReimbursement ?? 0,
                payment?.MaterialBonus ?? 0,
                payment?.CraftLabor ?? 0,
                payment?.Total ?? 0,
                CraftSynthCount: payment?.CraftSynthCount ?? 0,
                GilPerSynth: payment?.GilPerSynth ?? 0),
            DeliveryInstructions = brief?.DeliveryInstructions ?? string.Empty,
            PricingEvidence = new CompanyCommissionPricingEvidence(
                evidence?.CostBasis ?? order.SourceSnapshot.CostBasis?.ToString() ?? "Unspecified",
                evidence?.MarketScope ?? order.SourceSnapshot.MarketFetchScope?.ToString() ?? "Unspecified",
                evidence?.Location ?? ResolveLocation(order.SourceSnapshot),
                evidence?.CapturedAtUtc ?? order.SourceSnapshot.ImportedAtUtc),
            ContactInstructions = brief?.Contact ?? string.Empty,
            ChangeSummary = "Converted from the canonical hosted Trade order."
        };

    }

    private static CompanyCommissionPublicMetadata CreatePublicMetadata(
        TradeOrder order,
        PublishedCommissionBrief? publishedBrief)
    {
        var publication = order.CommissionPublication;
        if (publication == null)
        {
            return new CompanyCommissionPublicMetadata
            {
                PublicBriefId = CreateDraftPublicId(order.Id),
                ViewState = CompanyCommissionPublicViewState.Draft
            };
        }

        if (publishedBrief == null ||
            !string.Equals(publication.PublicId, publishedBrief.PublicId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The hosted Trade order publication has no matching immutable public brief.");
        }

        return new CompanyCommissionPublicMetadata
        {
            PublicBriefId = publication.PublicId,
            PublicUrl = publication.PublicUrl,
            IsTestFixture = publishedBrief.Brief.IsTestFixture,
            ViewState = publication.RevokedAtUtc == null
                ? CompanyCommissionPublicViewState.Published
                : CompanyCommissionPublicViewState.Revoked,
            PublishedAtUtc = publication.PublishedAtUtc,
            RevokedAtUtc = publication.RevokedAtUtc,
            LegacyOwnership = publication.Ownership
        };
    }

    private static IReadOnlyList<CompanyCommissionActivityEvent> ConvertHistory(
        TradeOrder order,
        int termsVersion,
        CompanyCommissionActor migrationActor,
        long initialRevision,
        DateTime migratedAtUtc)
    {
        var revision = initialRevision;
        var events = (order.History ?? [])
            .OrderBy(item => item.CreatedAtUtc)
            .ThenBy(item => item.Id)
            .Select(item => new CompanyCommissionActivityEvent
            {
                EventId = item.Id,
                CommissionId = order.Id,
                CommissionRevision = checked(++revision),
                Actor = migrationActor,
                SourceSurface = CompanyCommissionSourceSurface.HostedMigration,
                CreatedAtUtc = item.CreatedAtUtc,
                Kind = CompanyCommissionActivityKind.MigratedTradeOrderHistory,
                TermsVersion = termsVersion,
                Comment = item.Note,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    legacyKind = item.Kind.ToString(),
                    item.FromStatus,
                    item.ToStatus,
                    item.CrafterId
                }),
                MigrationProvenance = "trade-order-history"
            })
            .ToList();
        events.Add(new CompanyCommissionActivityEvent
        {
            EventId = CreateDeterministicGuid(order.Id, "commission-migration"),
            CommissionId = order.Id,
            CommissionRevision = checked(++revision),
            Actor = migrationActor,
            SourceSurface = CompanyCommissionSourceSurface.HostedMigration,
            CreatedAtUtc = migratedAtUtc,
            Kind = CompanyCommissionActivityKind.MigratedFromTradeOrder,
            TermsVersion = termsVersion,
            Comment = "Converted the hosted Trade order to the canonical company commission schema.",
            MigrationProvenance = "trade-order-v1"
        });
        return events;
    }

    private static IReadOnlyList<CompanyCommissionOutputProgress> CreateProgress(
        TradeOrder order,
        IReadOnlyList<CompanyCommissionOutputTerm> outputs,
        CompanyCommissionActor actor,
        DateTime migratedAtUtc)
    {
        var ready = order.Status is TradeOrderStatus.AwaitingDelivery or TradeOrderStatus.Completed;
        var accepted = order.Status == TradeOrderStatus.Completed;
        return outputs.Select(output => new CompanyCommissionOutputProgress(
            output.LineId,
            output.ItemId,
            output.RequiredQuantity,
            ready ? output.RequiredQuantity : 0,
            ready ? output.RequiredQuantity : 0,
            accepted ? output.RequiredQuantity : 0,
            migratedAtUtc,
            actor)).ToArray();
    }

    private static CompanyCommissionSettlementState ResolveSettlement(
        TradeOrder order,
        CompanyCommissionTermsVersion terms,
        bool advancePaymentSatisfied)
    {
        if (terms.Payment.Total <= 0)
        {
            return CompanyCommissionSettlementState.Satisfied;
        }

        return terms.Payment.Schedule == CompanyCommissionPaymentSchedule.Advance &&
               advancePaymentSatisfied
            ? CompanyCommissionSettlementState.Satisfied
            : order.Status == TradeOrderStatus.Completed
                ? CompanyCommissionSettlementState.Pending
                : CompanyCommissionSettlementState.NotDue;
    }

    private static void ValidatePublicationOwnership(
        TradeOrder order,
        PublishedCommissionBrief? publishedBrief,
        CompanyId canonicalCompanyId)
    {
        var publication = order.CommissionPublication;
        if (publication == null)
        {
            if (publishedBrief != null)
            {
                throw new InvalidOperationException(
                    "An immutable brief was supplied for a Trade order without publication metadata.");
            }

            return;
        }

        var ownership = publication.Ownership;
        if (ownership == null ||
            ownership.CompanyId != canonicalCompanyId ||
            ownership.OrderId != order.Id ||
            publishedBrief?.Ownership != ownership)
        {
            throw new InvalidOperationException(
                "The legacy publication ownership is incomplete or conflicts with the hosted Trade order.");
        }
    }

    private static string ResolveLocation(TradeOrderSourceSnapshot source) =>
        source.World ??
        source.DataCenter ??
        source.Region ??
        "Unspecified";

    private static string CreateDraftPublicId(Guid orderId) =>
        Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes($"draft:{orderId:D}"))[..15])
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static Guid CreateDeterministicGuid(Guid source, string purpose)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{source:D}:{purpose}"));
        return new Guid(hash[..16]);
    }
}
