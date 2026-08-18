using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public static class TradeCompanyCommissionMigrationService
{
    public static CompanyCommissionTermsVersion CreateDraftTerms(
        TradeOrder source,
        CommissionBriefDocument brief,
        CompanyCommissionTermsVersion currentTerms,
        DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(currentTerms);
        var actor = new CompanyCommissionActor(
            "trade-architect",
            CompanyCommissionActorKind.Commissioner,
            "Trade Architect");
        return PreserveCurrentLineIdentity(
            CreateTerms(source, brief, actor, createdAtUtc, companyPaymentPolicy: null),
            currentTerms) with
        {
            Version = currentTerms.Version,
            ChangeSummary = currentTerms.ChangeSummary
        };
    }

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
        var currentTerms = source.CompanyCommission?.CurrentTerms ??
            throw new InvalidOperationException(
                "A canonical commission is required to create a terms revision.");
        return PreserveCurrentLineIdentity(
            CreateTerms(source, brief, actor, createdAtUtc, companyPaymentPolicy: null),
            currentTerms) with
        {
            Version = version,
            ChangeSummary = reason.Trim()
        };
    }

    public static void RequireCanonicalBriefMatchesCurrentTerms(
        TradeOrder order,
        CommissionBriefDocument brief)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(brief);
        if (order.CompanyCommission is { } commission)
        {
            ValidateCanonicalBrief(order, commission, brief);
        }
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
            var converted = ConvertLegacyOrder(
                source,
                publishedBrief,
                publishedBrief.Ownership?.CompanyId ??
                    throw new InvalidOperationException(
                        "A company-owned immutable brief is required."),
                initialCommissionRevision: 0,
                boundAtUtc,
                companyPaymentPolicy: null);
            CompanyCommissionCommandWorkflow.ValidateTerms(
                converted.CompanyCommission!.CurrentTerms,
                converted.CompanyCommission.CompanyId,
                workPackage: null);
            return converted;
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
        var terms = commission.CurrentTerms;
        CompanyCommissionCommandWorkflow.ValidateTerms(
            terms,
            canonicalCompanyId,
            workPackage: null);
        RequireCanonicalBriefMatchesCurrentTerms(copy, publishedBrief.Brief);
        var companyMaterials = terms.Materials
            .Where(item =>
                item.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(item => new CompanyCommissionMaterialQuantity(
                item.LineId,
                item.ItemId,
                item.Quantity))
            .ToArray();
        var hasOpening = commission.Activity.Any(
            item => item.Kind == CompanyCommissionActivityKind.CommissionOpened);
        var activity = hasOpening
            ? commission.Activity
            : commission.Activity.Append(new CompanyCommissionActivityEvent
            {
                EventId = CreateDeterministicGuid(
                    commission.CommissionId,
                    $"commission-opened:{publishedBrief.PublicId}"),
                CommissionId = commission.CommissionId,
                CommissionRevision = checked(
                    (commission.Activity.LastOrDefault()?.CommissionRevision ?? 0) + 1),
                Actor = actor,
                SourceSurface = CompanyCommissionSourceSurface.TradeArchitect,
                CreatedAtUtc = boundAtUtc,
                Kind = CompanyCommissionActivityKind.CommissionOpened,
                TermsVersion = commission.CurrentTermsVersion,
                Comment = "Opened the commission for one exclusive claim."
            }).ToArray();
        copy.CompanyCommission = commission with
        {
            UpdatedAtUtc = boundAtUtc,
            PublicMetadata = CreatePublicMetadata(copy, publishedBrief) with
            {
                DiscordBindings = commission.PublicMetadata.DiscordBindings
            },
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
                    actor)).ToArray(),
            Activity = activity
        };
        copy.Status = TradeOrderStatus.ReadyToAssign;
        copy.UpdatedAtUtc = boundAtUtc;
        return copy;
    }

    public static TradeOrder ConvertLegacyOrder(
        TradeOrder source,
        PublishedCommissionBrief? publishedBrief,
        CompanyId canonicalCompanyId,
        long initialCommissionRevision,
        DateTime migratedAtUtc,
        TradePaymentPolicy? companyPaymentPolicy)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Id == Guid.Empty || source.CompanyProfileId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A canonical company and order identity are required for commission migration.");
        }

        if (source.CompanyCommission is { } existing)
        {
            if (existing.SchemaVersion is < 1 or > TradeCompanyCommission.CurrentSchemaVersion ||
                existing.CommissionId != source.Id ||
                existing.CompanyId.Value != source.CompanyProfileId)
            {
                throw new InvalidOperationException(
                    "The hosted Trade order contains an incompatible company commission aggregate.");
            }

            var existingCopy = TradeOrderWorkflow.CopyOrder(source);
            var migratedExisting = existing;
            if (existing.SchemaVersion < TradeCompanyCommission.CurrentSchemaVersion)
            {
                var normalizedProgress = existing.OutputProgress
                    .Select(progress => progress with { ReadyQuantity = progress.CompletedQuantity })
                    .ToArray();
                var allOutputsComplete = existing.CurrentTerms.Outputs.Count > 0 &&
                    existing.CurrentTerms.Outputs.All(output => normalizedProgress.Any(progress =>
                        progress.LineId == output.LineId &&
                        progress.CompletedQuantity >= output.RequiredQuantity));
                migratedExisting = existing with
                {
                    SchemaVersion = TradeCompanyCommission.CurrentSchemaVersion,
                    OutputProgress = normalizedProgress,
                    DeliveryReadiness = allOutputsComplete
                        ? new CompanyCommissionDeliveryReadiness(
                            true,
                            existing.DeliveryReadiness.DeclaredAtUtc ?? migratedAtUtc)
                        : existing.DeliveryReadiness
                };
                existingCopy.CompanyCommission = migratedExisting;
                if (allOutputsComplete &&
                    existing.ActiveClaim != null &&
                    existingCopy.Status is TradeOrderStatus.Assigned or TradeOrderStatus.InProgress)
                {
                    existingCopy.Status = TradeOrderStatus.AwaitingDelivery;
                }
                existingCopy.UpdatedAtUtc = migratedAtUtc;
            }
            if (!RequiresAssignedClaimRepair(existingCopy))
            {
                return existingCopy;
            }

            var claimId = CreateDeterministicGuid(source.Id, "legacy-assigned-claim");
            existingCopy.CompanyCommission = migratedExisting with
            {
                UpdatedAtUtc = migratedAtUtc,
                ActiveClaim = new CompanyCommissionClaim(
                    claimId,
                    migratedExisting.CurrentTermsVersion,
                    migratedAtUtc,
                    source.AssignedCrafterId,
                    null),
                ParticipantGrant = new CompanyCommissionParticipantGrant(
                    CreateDeterministicGuid(source.Id, "legacy-participant-grant"),
                    claimId,
                    migratedExisting.CurrentTermsVersion,
                    1,
                    migratedAtUtc),
                ParticipantAcknowledgedTermsVersion = migratedExisting.CurrentTermsVersion
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
        var terms = CreateTerms(copy, brief, migrationActor, migratedAtUtc, companyPaymentPolicy);
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
        DateTime createdAtUtc,
        TradePaymentPolicy? companyPaymentPolicy)
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
        var authoringPolicy = brief == null
            ? order.PaymentPolicyOverride ?? companyPaymentPolicy ??
                throw new InvalidOperationException(
                    "The canonical Trade company payment policy is required to migrate an unpublished legacy order.")
            : null;
        var authoringPayment = authoringPolicy == null
            ? null
            : TradeCommissionPaymentSummary.FromOrder(
                order,
                draft: null,
                authoringPolicy).Active;
        var evidence = brief?.Evidence;

        return new CompanyCommissionTermsVersion
        {
            Version = 1,
            CreatedAtUtc = createdAtUtc,
            CreatedBy = actor,
            Outputs = outputs,
            Materials = materials,
            Payment = new CompanyCommissionPaymentTerms(
                payment?.Schedule ?? order.PaymentSchedule,
                payment?.ContractLabel ?? "Labor + material-value bonus",
                payment?.MaterialReimbursement ?? authoringPayment?.MaterialReimbursementTotal ?? 0,
                payment?.MaterialBonus ?? authoringPayment?.CommissionAmount ?? 0,
                payment?.CraftLabor ?? authoringPayment?.CraftLaborTotal ?? 0,
                payment?.Total ?? authoringPayment?.Total ?? 0,
                CustomTerms: payment?.CustomTerms ?? order.CustomPaymentTerms,
                CraftSynthCount: payment?.CraftSynthCount ?? authoringPayment?.CraftSynthCount ?? 0,
                GilPerSynth: payment?.GilPerSynth ?? authoringPayment?.GilPerSynth ?? 0),
            DeliveryInstructions = brief?.DeliveryInstructions ?? string.Empty,
            PricingEvidence = new CompanyCommissionPricingEvidence(
                evidence?.CostBasis ?? order.SourceSnapshot.CostBasis?.ToString() ?? "Unspecified",
                evidence?.MarketScope ?? order.SourceSnapshot.MarketFetchScope?.ToString() ?? "Unspecified",
                evidence?.Location ?? ResolveLocation(order.SourceSnapshot),
                evidence?.CapturedAtUtc ?? order.SourceSnapshot.ImportedAtUtc,
                order.SourceSnapshot.MaterialQuote),
            ContactInstructions = brief?.Contact ?? string.Empty,
            ChangeSummary = "Converted from the canonical hosted Trade order."
        };

    }

    private static CompanyCommissionTermsVersion PreserveCurrentLineIdentity(
        CompanyCommissionTermsVersion candidate,
        CompanyCommissionTermsVersion current)
    {
        var outputs = candidate.Outputs.Select(output =>
        {
            var existing = current.Outputs.FirstOrDefault(item =>
                item.ItemId == output.ItemId &&
                item.MustBeHq == output.MustBeHq);
            return existing == null ? output : output with { LineId = existing.LineId };
        }).ToArray();
        var materials = candidate.Materials.Select(material =>
        {
            var existing = current.Materials.FirstOrDefault(item =>
                item.ItemId == material.ItemId &&
                item.RequiresHq == material.RequiresHq &&
                item.Responsibility == material.Responsibility);
            return existing == null ? material : material with { LineId = existing.LineId };
        }).ToArray();
        return candidate with
        {
            Outputs = outputs,
            Materials = materials
        };
    }

    private static void ValidateCanonicalBrief(
        TradeOrder order,
        TradeCompanyCommission commission,
        CommissionBriefDocument brief)
    {
        var terms = commission.CurrentTerms;
        var outputs = brief.Outputs
            .Select(item => (item.ItemId, item.Name, item.Quantity, item.MustBeHq))
            .ToArray();
        var canonicalOutputs = terms.Outputs
            .Select(item => (item.ItemId, item.Name, Quantity: item.RequiredQuantity, item.MustBeHq))
            .ToArray();
        var crafterMaterials = brief.CrafterMaterials
            .Select(item => (item.ItemId, item.Name, item.Quantity, item.RequiresHq, item.UnitCost, item.TotalCost))
            .ToArray();
        var canonicalCrafterMaterials = terms.Materials
            .Where(item => item.Responsibility == CommissionMaterialResponsibility.Crafter)
            .Select(item => (item.ItemId, item.Name, item.Quantity, item.RequiresHq, item.UnitCost, item.TotalCost))
            .ToArray();
        var companyMaterials = brief.CompanyMaterials
            .Select(item => (item.ItemId, item.Name, item.Quantity, item.RequiresHq, item.UnitCost, item.TotalCost))
            .ToArray();
        var canonicalCompanyMaterials = terms.Materials
            .Where(item => item.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(item => (item.ItemId, item.Name, item.Quantity, item.RequiresHq, item.UnitCost, item.TotalCost))
            .ToArray();
        var payment = brief.Payment;
        var exact =
            string.Equals(brief.Title, order.Title, StringComparison.Ordinal) &&
            string.Equals(brief.Reference, commission.Reference, StringComparison.Ordinal) &&
            brief.IsTestFixture == commission.PublicMetadata.IsTestFixture &&
            outputs.SequenceEqual(canonicalOutputs) &&
            crafterMaterials.SequenceEqual(canonicalCrafterMaterials) &&
            companyMaterials.SequenceEqual(canonicalCompanyMaterials) &&
            string.Equals(brief.Contact, terms.ContactInstructions, StringComparison.Ordinal) &&
            string.Equals(brief.DeliveryInstructions, terms.DeliveryInstructions, StringComparison.Ordinal) &&
            string.Equals(payment.ContractLabel, terms.Payment.ContractLabel, StringComparison.Ordinal) &&
            payment.MaterialReimbursement == terms.Payment.MaterialReimbursement &&
            payment.MaterialBonus == terms.Payment.MaterialAdjustment &&
            payment.CraftLabor == terms.Payment.CraftLabor &&
            payment.Total == terms.Payment.Total &&
            payment.CraftSynthCount == terms.Payment.CraftSynthCount &&
            payment.GilPerSynth == terms.Payment.GilPerSynth &&
            payment.Schedule == terms.Payment.Schedule &&
            string.Equals(payment.CustomTerms, terms.Payment.CustomTerms, StringComparison.Ordinal) &&
            string.Equals(brief.Evidence.CostBasis, terms.PricingEvidence.CostBasis, StringComparison.Ordinal) &&
            string.Equals(brief.Evidence.MarketScope, terms.PricingEvidence.MarketScope, StringComparison.Ordinal) &&
            string.Equals(brief.Evidence.Location, terms.PricingEvidence.Location, StringComparison.Ordinal) &&
            brief.Evidence.CapturedAtUtc == terms.PricingEvidence.CapturedAtUtc;
        if (!exact)
        {
            throw new InvalidOperationException(
                "The immutable brief does not exactly match the canonical commission terms.");
        }
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
