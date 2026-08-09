using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record HostedCompanyCommissionSnapshot(
    TradeCompanyRecordEnvelope Envelope,
    TradeOrder Order,
    CompanyRecordRevision CompanyRevision,
    string CompanyDisplayName);

public sealed record CompanyCommissionRecoveryTarget(
    Guid ParticipantGrantId,
    long NextParticipantCapabilityRevision);

public interface ICompanyCommissionPostCommitSink
{
    Task OnCommittedAsync(
        TradeCompanyAccessContext access,
        HostedCompanyCommissionSnapshot committed,
        CompanyCommissionActivityEvent activity,
        CancellationToken cancellationToken);
}

public sealed class HostedCompanyCommissionService(
    ProfileHostedTradeCompanyService companies,
    SqliteProfileHostStore profileHost,
    TimeProvider timeProvider,
    IEnumerable<ICompanyCommissionPostCommitSink> postCommitSinks,
    ILogger<HostedCompanyCommissionService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<HostedCompanyCommissionSnapshot?> LoadOwnerAsync(
        TradeCompanyAccessContext access,
        Guid commissionId,
        CancellationToken cancellationToken = default)
    {
        RequireCompanyOperator(access);
        if (commissionId == Guid.Empty)
        {
            return null;
        }

        var record = await companies.LoadRecordAsync(
            access,
            TradeCompanyRecordKinds.Order,
            commissionId.ToString("D"),
            cancellationToken);
        if (record == null)
        {
            return null;
        }

        var order = DeserializeCanonicalOrder(record, access.CompanyId, commissionId);
        var profile = await companies.LoadCompanyProfileAsync(access, cancellationToken)
            ?? throw new InvalidOperationException(
                "The canonical Trade company profile is unavailable.");
        var companyRevision = await companies.LoadCompanyRevisionAsync(
            access,
            cancellationToken);
        return new HostedCompanyCommissionSnapshot(
            record,
            order,
            companyRevision,
            profile.Name);
    }

    public async Task<CompanyCommissionPublicBrief?> LoadPublicAsync(
        string publicBriefId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicBriefId);
        var ownership = await companies.ResolvePublicationOwnershipAsync(
            publicBriefId,
            cancellationToken);
        if (ownership == null)
        {
            return null;
        }

        var canonical = await companies.LoadPublicOrderAsync(
            ownership,
            cancellationToken);
        if (canonical == null)
        {
            return null;
        }

        var order = canonical.Value.Order;
        ValidateCanonicalOrder(order, ownership.CompanyId, ownership.OrderId);
        var commission = order.CompanyCommission!;
        if (!string.Equals(
                commission.PublicMetadata.PublicBriefId,
                publicBriefId,
                StringComparison.Ordinal) ||
            commission.PublicMetadata.ViewState != CompanyCommissionPublicViewState.Published)
        {
            return null;
        }

        var profile = await companies.LoadPublicCompanyProfileAsync(
            ownership.CompanyId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The canonical commission company profile is unavailable.");
        return CompanyCommissionProjectionService.CreatePublicBrief(order, profile.Name);
    }

    public async Task<CompanyCommissionParticipantBrief?> LoadParticipantAsync(
        CompanyCommissionCapabilityResolution capability,
        CancellationToken cancellationToken = default)
    {
        if (capability.Kind != CompanyCommissionCapabilityKind.Participant ||
            capability.GrantId == null)
        {
            throw new UnauthorizedAccessException(
                "A participant capability is required.");
        }

        var ownership = await companies.ResolvePublicationOwnershipAsync(
            capability.PublicBriefId,
            cancellationToken);
        if (ownership == null ||
            ownership.CompanyId != capability.CompanyId ||
            ownership.OrderId != capability.CommissionId)
        {
            return null;
        }

        var canonical = await companies.LoadPublicOrderAsync(
            ownership,
            cancellationToken);
        if (canonical == null)
        {
            return null;
        }

        var order = canonical.Value.Order;
        ValidateCanonicalOrder(order, ownership.CompanyId, ownership.OrderId);
        var grant = order.CompanyCommission!.ParticipantGrant;
        if (grant == null ||
            grant.GrantId != capability.GrantId ||
            grant.CapabilityRevision != capability.CapabilityRevision ||
            grant.RevokedAtUtc != null)
        {
            return null;
        }

        var profile = await companies.LoadPublicCompanyProfileAsync(
            ownership.CompanyId,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "The canonical commission company profile is unavailable.");
        return CompanyCommissionProjectionService.CreateParticipantBrief(order, profile.Name);
    }

    public async Task<CompanyCommissionRecoveryTarget?> LoadRecoveryTargetAsync(
        CompanyCommissionCapabilityResolution capability,
        CancellationToken cancellationToken = default)
    {
        if (capability.Kind != CompanyCommissionCapabilityKind.Recovery ||
            capability.GrantId == null)
        {
            return null;
        }

        var ownership = await companies.ResolvePublicationOwnershipAsync(
            capability.PublicBriefId,
            cancellationToken);
        if (ownership == null ||
            ownership.CompanyId != capability.CompanyId ||
            ownership.OrderId != capability.CommissionId)
        {
            return null;
        }

        var canonical = await companies.LoadPublicOrderAsync(
            ownership,
            cancellationToken);
        var commission = canonical?.Order.CompanyCommission;
        if (commission?.RecoveryGrant is not
            {
                RedeemedAtUtc: null,
                RevokedAtUtc: null
            } recovery ||
            recovery.RecoveryGrantId != capability.GrantId ||
            recovery.RecoveryRevision != capability.CapabilityRevision ||
            commission.ParticipantGrant is not { RevokedAtUtc: null } participant ||
            participant.GrantId != recovery.ParticipantGrantId)
        {
            return null;
        }

        return new CompanyCommissionRecoveryTarget(
            participant.GrantId,
            checked(participant.CapabilityRevision + 1));
    }

    public async Task<CompanyCommissionCommandContext?> CreateCapabilityCommandContextAsync(
        CompanyCommissionCapabilityResolution capability,
        long expectedProjectionRevision,
        Guid commandId,
        int protocolVersion,
        CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty ||
            expectedProjectionRevision < 0 ||
            protocolVersion != CompanyCommissionProtocol.Version1)
        {
            return null;
        }

        var ownership = await companies.ResolvePublicationOwnershipAsync(
            capability.PublicBriefId,
            cancellationToken);
        if (ownership == null ||
            ownership.CompanyId != capability.CompanyId ||
            ownership.OrderId != capability.CommissionId)
        {
            return null;
        }

        var access = await companies.ResolvePublicAccessAsync(
            ownership,
            cancellationToken);
        if (access == null)
        {
            return null;
        }

        var snapshot = await LoadOwnerAsync(
            access,
            capability.CommissionId,
            cancellationToken);
        var commission = snapshot?.Order.CompanyCommission;
        if (snapshot == null || commission == null)
        {
            return null;
        }

        var recordedReplay = commission.ProcessedCommands.Any(
            item => item.CommandId == commandId);
        if (!IsCapabilityAuthorized(capability, commission, recordedReplay) ||
            !recordedReplay &&
            (commission.Activity.LastOrDefault()?.CommissionRevision ?? 0) !=
            expectedProjectionRevision)
        {
            return null;
        }

        return new CompanyCommissionCommandContext(
            capability.CompanyId,
            capability.CommissionId,
            snapshot.Envelope.RecordRevision,
            snapshot.CompanyRevision,
            commandId,
            protocolVersion);
    }

    public Task<CompanyCommissionMutationResult> ExecuteCompanyAsync(
        TradeCompanyAccessContext access,
        ICompanyCommissionCompanyCommand command,
        CancellationToken cancellationToken = default) =>
        ExecuteCompanyCoreAsync(access, command, cancellationToken);

    private Task<CompanyCommissionMutationResult> ExecuteCompanyCoreAsync(
        TradeCompanyAccessContext access,
        ICompanyCommissionCompanyCommand command,
        CancellationToken cancellationToken)
    {
        var actor = new CompanyCommissionActor(
                $"company-grant:{access.GrantId:D}",
                CompanyCommissionActorKind.Commissioner);
        return ExecuteAuthenticatedAsync(
            access,
            command,
            actor,
            CompanyCommissionSourceSurface.TradeArchitect,
            order => CompanyCommissionCommandWorkflow.Apply(
                order,
                command,
                actor,
                timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);
    }

    public async Task<CompanyCommissionMutationResult> ExecuteCapabilityAsync(
        CompanyCommissionCapabilityResolution capability,
        ICompanyCommissionParticipantCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var ownership = await companies.ResolvePublicationOwnershipAsync(
            capability.PublicBriefId,
            cancellationToken);
        if (ownership == null ||
            ownership.CompanyId != capability.CompanyId ||
            ownership.OrderId != capability.CommissionId)
        {
            return Unauthorized();
        }

        var access = await companies.ResolvePublicAccessAsync(
            ownership,
            cancellationToken);
        if (access == null)
        {
            return Unauthorized();
        }

        var snapshot = await LoadOwnerAsync(
            access,
            capability.CommissionId,
            cancellationToken);
        var commission = snapshot?.Order.CompanyCommission;
        var recordedReplay = commission?.ProcessedCommands.Any(
            item => item.CommandId == command.Context.CommandId) == true;
        var authorized =
            commission != null &&
            IsCapabilityAuthorized(capability, commission, recordedReplay) &&
            (capability.Kind != CompanyCommissionCapabilityKind.Claim ||
             command is ClaimCompanyCommissionCommand) &&
            (capability.Kind != CompanyCommissionCapabilityKind.Recovery ||
             command is RedeemCompanyCommissionParticipantRecoveryCommand);
        if (!authorized)
        {
            return Unauthorized();
        }

        var actor = new CompanyCommissionActor(
            capability.Kind switch
            {
                CompanyCommissionCapabilityKind.Claim =>
                    $"claim-revision:{capability.CapabilityRevision}",
                CompanyCommissionCapabilityKind.Recovery =>
                    $"recovery-grant:{capability.GrantId:D}",
                _ => $"participant-grant:{capability.GrantId:D}"
            },
            CompanyCommissionActorKind.Crafter);
        return await ExecuteAuthenticatedAsync(
            access,
            command,
            actor,
            CompanyCommissionSourceSurface.PublicBrief,
            order => CompanyCommissionCommandWorkflow.Apply(
                order,
                command,
                actor,
                timeProvider.GetUtcNow().UtcDateTime),
            cancellationToken);
    }

    private async Task<CompanyCommissionMutationResult> ExecuteAuthenticatedAsync(
        TradeCompanyAccessContext access,
        ICompanyCommissionCommand command,
        CompanyCommissionActor actor,
        CompanyCommissionSourceSurface sourceSurface,
        Func<TradeOrder, CompanyCommissionDomainTransition> transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(transition);
        RequireCompanyOperator(access);
        var context = command.Context;
        var fingerprint = CreateFingerprint(command);
        var validationError = ValidateCommandContext(access, context);
        if (validationError != null)
        {
            return Rejected("invalid_command", validationError);
        }

        var snapshot = await LoadOwnerAsync(
            access,
            context.CommissionId,
            cancellationToken);
        if (snapshot == null)
        {
            return new CompanyCommissionMutationResult(
                CompanyCommissionMutationStatus.NotFound,
                ErrorCode: "commission_missing",
                ErrorMessage: "The canonical company commission was not found.");
        }

        var replay = ResolveReplay(snapshot, context, fingerprint);
        if (replay != null)
        {
            await NotifyPostCommitAsync(
                access,
                snapshot,
                replay.Activity!,
                cancellationToken);
            return replay;
        }
        if (snapshot.Envelope.RecordRevision != context.ExpectedObjectRevision ||
            snapshot.CompanyRevision != context.ExpectedCompanyRevision)
        {
            return new CompanyCommissionMutationResult(
                CompanyCommissionMutationStatus.Conflict,
                snapshot.Order,
                ErrorCode: "revision_conflict",
                ErrorMessage: "The hosted commission or company changed before the command was applied.");
        }

        var linkedPlanValidation = await ValidateLinkedPlanCommandAsync(
            access,
            snapshot.Order,
            command,
            cancellationToken);
        if (linkedPlanValidation != null)
        {
            return Rejected("linked_plan_invalid", linkedPlanValidation, snapshot.Order);
        }

        CompanyCommissionDomainTransition application;
        try
        {
            application = transition(TradeOrderWorkflow.CopyOrder(snapshot.Order));
        }
        catch (InvalidOperationException exception)
        {
            return Rejected("command_rejected", exception.Message, snapshot.Order);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = application.UpdatedOrder;
        ValidateTransition(snapshot.Order, updated, access.CompanyId);
        var commission = updated.CompanyCommission!;
        var nextCommissionRevision =
            checked((commission.Activity.LastOrDefault()?.CommissionRevision ?? 0) + 1);
        var activity = new CompanyCommissionActivityEvent
        {
            EventId = Guid.NewGuid(),
            CommandId = context.CommandId,
            CommissionId = context.CommissionId,
            CommissionRevision = nextCommissionRevision,
            Actor = actor,
            SourceSurface = sourceSurface,
            CreatedAtUtc = now,
            Kind = application.ActivityKind,
            Visibility = application.Visibility,
            TermsVersion = commission.CurrentTermsVersion,
            Comment = application.Comment,
            PayloadJson = application.PayloadJson
        };
        updated.CompanyCommission = commission with
        {
            UpdatedAtUtc = now,
            Activity = commission.Activity.Append(activity).ToArray(),
            ProcessedCommands = commission.ProcessedCommands
                .Append(new CompanyCommissionProcessedCommand(
                    context.CommandId,
                    fingerprint,
                    activity.EventId,
                    now))
                .ToArray()
        };
        updated.UpdatedAtUtc = now;
        if (activity.Kind != CompanyCommissionActivityKind.DraftUpdated)
        {
            updated.History = updated.History
                .Append(ProjectCompatibilityHistory(updated, activity))
                .ToArray();
        }

        var mutation = await companies.PutRecordAsync(
            access,
            TradeCompanyRecordKinds.Order,
            updated.Id.ToString("D"),
            JsonSerializer.Serialize(updated, JsonOptions),
            context.ExpectedObjectRevision,
            $"commission-command:{context.CommandId:D}",
            cancellationToken,
            context.ExpectedCompanyRevision);
        if (mutation.Success)
        {
            var committedEnvelope = mutation.Record
                ?? throw new InvalidOperationException(
                    "The hosted commission mutation returned no committed record.");
            var committed = new HostedCompanyCommissionSnapshot(
                committedEnvelope,
                updated,
                mutation.CompanyRevision
                ?? throw new InvalidOperationException(
                    "The hosted commission mutation returned no committed company revision."),
                snapshot.CompanyDisplayName);
            var result = new CompanyCommissionMutationResult(
                CompanyCommissionMutationStatus.Applied,
                updated,
                activity,
                ObjectRevision: committed.Envelope.RecordRevision,
                CompanyRevision: committed.CompanyRevision);
            await NotifyPostCommitAsync(access, committed, activity, cancellationToken);
            return result;
        }

        var current = await LoadOwnerAsync(
            access,
            context.CommissionId,
            cancellationToken);
        var replayAfterConflict = current == null
            ? null
            : ResolveReplay(current, context, fingerprint);
        if (replayAfterConflict != null)
        {
            await NotifyPostCommitAsync(
                access,
                current!,
                replayAfterConflict.Activity!,
                cancellationToken);
            return replayAfterConflict;
        }

        return new CompanyCommissionMutationResult(
                CompanyCommissionMutationStatus.Conflict,
                current?.Order,
                ErrorCode: mutation.ErrorCode ?? "revision_conflict",
                ErrorMessage: mutation.ErrorMessage ??
                    "The hosted commission changed before the command completed.");
    }

    private async Task NotifyPostCommitAsync(
        TradeCompanyAccessContext access,
        HostedCompanyCommissionSnapshot committed,
        CompanyCommissionActivityEvent activity,
        CancellationToken requestCancellationToken)
    {
        using var committedWork = new CancellationTokenSource(
            TimeSpan.FromSeconds(15));
        foreach (var sink in postCommitSinks)
        {
            try
            {
                await sink.OnCommittedAsync(
                    access,
                    committed,
                    activity,
                    committedWork.Token);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Commission post-commit projection failed for company {CompanyId}, " +
                    "commission {CommissionId}, event {EventId}. The canonical command remains committed.",
                    access.CompanyId,
                    activity.CommissionId,
                    activity.EventId);
            }
        }
    }

    private static bool IsCapabilityAuthorized(
        CompanyCommissionCapabilityResolution capability,
        TradeCompanyCommission commission,
        bool recordedReplay) =>
        capability.Kind switch
        {
            CompanyCommissionCapabilityKind.Claim =>
                recordedReplay ||
                commission.ActiveClaim == null &&
                commission.ActiveClaimCapabilityRevision == capability.CapabilityRevision,
            CompanyCommissionCapabilityKind.Participant =>
                commission.ParticipantGrant is { RevokedAtUtc: null } participant &&
                participant.GrantId == capability.GrantId &&
                participant.CapabilityRevision == capability.CapabilityRevision,
            CompanyCommissionCapabilityKind.Recovery =>
                recordedReplay ||
                commission.RecoveryGrant is
                { RedeemedAtUtc: null, RevokedAtUtc: null } recovery &&
                recovery.RecoveryGrantId == capability.GrantId &&
                recovery.RecoveryRevision == capability.CapabilityRevision,
            _ => false
        };

    private static TradeOrder DeserializeCanonicalOrder(
        TradeCompanyRecordEnvelope record,
        CompanyId companyId,
        Guid commissionId)
    {
        try
        {
            var order = JsonSerializer.Deserialize<TradeOrder>(
                    record.PayloadJson,
                    JsonOptions)
                ?? throw new JsonException("The hosted Trade order is empty.");
            ValidateCanonicalOrder(order, companyId, commissionId);
            return order;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The canonical hosted Trade order could not be read.",
                exception);
        }
    }

    private static void ValidateCanonicalOrder(
        TradeOrder order,
        CompanyId companyId,
        Guid commissionId)
    {
        var commission = order.CompanyCommission;
        if (order.Id != commissionId ||
            commission == null ||
            commission.SchemaVersion != TradeCompanyCommission.CurrentSchemaVersion ||
            commission.CommissionId != order.Id ||
            commission.CompanyId != companyId ||
            commission.TermsVersions.Count == 0 ||
            commission.TermsVersions.All(item =>
                item.Version != commission.CurrentTermsVersion))
        {
            throw new InvalidOperationException(
                "The hosted Trade order does not contain a compatible canonical company commission.");
        }

        var outputLineIds = commission.CurrentTerms.Outputs
            .Select(item => item.LineId)
            .ToHashSet();
        var materialLineIds = commission.CurrentTerms.Materials
            .Where(item =>
                item.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(item => item.LineId)
            .ToHashSet();
        if (outputLineIds.Contains(Guid.Empty) ||
            outputLineIds.Count != commission.CurrentTerms.Outputs.Count ||
            commission.OutputProgress.Any(item =>
                !outputLineIds.Contains(item.LineId)) ||
            materialLineIds.Contains(Guid.Empty) ||
            commission.Gates.CompanyMaterials.PromisedQuantities.Any(item =>
                !materialLineIds.Contains(item.LineId)))
        {
            throw new InvalidOperationException(
                "The canonical commission line identities are incomplete or inconsistent.");
        }
    }

    private static void ValidateTransition(
        TradeOrder current,
        TradeOrder updated,
        CompanyId companyId)
    {
        if (updated.Id != current.Id ||
            updated.CompanyProfileId != current.CompanyProfileId ||
            updated.CompanyCommission == null ||
            updated.CompanyCommission.CommissionId != current.Id ||
            updated.CompanyCommission.CompanyId != companyId)
        {
            throw new InvalidOperationException(
                "A commission transition cannot change canonical ownership or identity.");
        }

        ValidateCanonicalOrder(updated, companyId, current.Id);
    }

    private static string? ValidateCommandContext(
        TradeCompanyAccessContext access,
        CompanyCommissionCommandContext context)
    {
        if (context.CompanyId != access.CompanyId ||
            context.CommissionId == Guid.Empty ||
            context.CommandId == Guid.Empty ||
            context.ExpectedObjectRevision.Value <= 0 ||
            context.ExpectedCompanyRevision.Value <= 0 ||
            !CompanyCommissionProtocol.IsSupportedOwnerCommandVersion(context.ProtocolVersion))
        {
            return "The command identity, revisions, or protocol are invalid.";
        }

        return null;
    }

    private async Task<string?> ValidateLinkedPlanCommandAsync(
        TradeCompanyAccessContext access,
        TradeOrder current,
        ICompanyCommissionCommand command,
        CancellationToken cancellationToken)
    {
        CompanyCommissionDraftWorkPackage? workPackage = command switch
        {
            AmendCompanyCommissionTermsCommand amend => amend.WorkPackage,
            UpdateCompanyCommissionDraftCommand update => update.WorkPackage,
            _ => null
        };
        if (command is AmendCompanyCommissionTermsCommand &&
            command.Context.ProtocolVersion == CompanyCommissionProtocol.Version2 &&
            workPackage == null)
        {
            return "Protocol v2 terms revisions require the complete work package.";
        }
        if (workPackage == null)
        {
            return null;
        }

        var changesLinkedPlan =
            !string.Equals(current.CraftPlanId, workPackage.CraftPlanId, StringComparison.Ordinal) ||
            current.CraftPlanSavedAtUtc != workPackage.CraftPlanSavedAtUtc ||
            current.CraftPlanLinkKind != workPackage.CraftPlanLinkKind;
        if (!changesLinkedPlan)
        {
            return null;
        }
        if (command.Context.ProtocolVersion != CompanyCommissionProtocol.Version2)
        {
            return "Changing a commission's linked plan requires protocol v2.";
        }
        if (workPackage.CraftPlanLinkKind != TradeOrderCraftPlanLinkKind.OrderGenerated ||
            string.IsNullOrWhiteSpace(workPackage.CraftPlanId) ||
            !workPackage.CraftPlanSavedAtUtc.HasValue)
        {
            return "The revised commission must reference an exact generated plan snapshot.";
        }

        var profileId = access.HostProfileId?.ToString("D");
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return "The commission is not bound to a hosted profile.";
        }
        var hostedPlan = await profileHost.LoadObjectAsync(
            profileId,
            ProfileSyncCollections.Plans,
            workPackage.CraftPlanId,
            cancellationToken);
        if (hostedPlan is not { Deleted: false })
        {
            return "The exact linked plan snapshot is not present in this hosted profile.";
        }

        var snapshot = ProfileSyncPlanPayloadCodec.Deserialize(
            hostedPlan.PayloadJson,
            workPackage.CraftPlanId);
        return snapshot.LinkedOrderId == current.Id &&
               snapshot.SavedAt == workPackage.CraftPlanSavedAtUtc.Value
            ? null
            : "The hosted plan snapshot does not match this order and saved revision.";
    }

    private static CompanyCommissionMutationResult? ResolveReplay(
        HostedCompanyCommissionSnapshot snapshot,
        CompanyCommissionCommandContext context,
        string fingerprint)
    {
        var order = snapshot.Order;
        var commission = order.CompanyCommission;
        var prior = commission?.ProcessedCommands
            .SingleOrDefault(item => item.CommandId == context.CommandId);
        if (prior == null)
        {
            return null;
        }
        if (!string.Equals(prior.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return Rejected(
                "command_id_reused",
                "The command ID was already used for a different payload.",
                order);
        }

        var activity = commission!.Activity.SingleOrDefault(
            item => item.EventId == prior.ActivityEventId &&
                    item.CommandId == prior.CommandId);
        if (activity == null)
        {
            throw new InvalidOperationException(
                "A processed commission command has no matching activity event.");
        }

        return new CompanyCommissionMutationResult(
            CompanyCommissionMutationStatus.Replayed,
            order,
            activity,
            ObjectRevision: snapshot.Envelope.RecordRevision,
            CompanyRevision: snapshot.CompanyRevision);
    }

    private static TradeOrderHistoryEvent ProjectCompatibilityHistory(
        TradeOrder order,
        CompanyCommissionActivityEvent activity) =>
        new()
        {
            Id = activity.EventId,
            CompanyProfileId = order.CompanyProfileId,
            OrderId = order.Id,
            Kind = activity.Kind switch
            {
                CompanyCommissionActivityKind.ClaimAccepted =>
                    TradeOrderHistoryEventKind.Assigned,
                CompanyCommissionActivityKind.DeliveryAccepted =>
                    TradeOrderHistoryEventKind.Closed,
                CompanyCommissionActivityKind.CommissionCanceled =>
                    TradeOrderHistoryEventKind.Closed,
                _ => TradeOrderHistoryEventKind.ManualNote
            },
            Note = activity.Comment ?? activity.Kind.ToString(),
            ToStatus = order.Status,
            CrafterId = order.AssignedCrafterId,
            CreatedAtUtc = activity.CreatedAtUtc
        };

    private static void RequireCompanyOperator(TradeCompanyAccessContext access)
    {
        if (access.GrantId == Guid.Empty ||
            access.Role is not (TradeCompanyRole.Operator or TradeCompanyRole.Owner))
        {
            throw new UnauthorizedAccessException(
                "A hosted company operator capability is required.");
        }
    }

    private static CompanyCommissionMutationResult Rejected(
        string code,
        string message,
        TradeOrder? order = null) =>
        new(
            CompanyCommissionMutationStatus.Rejected,
            order,
            ErrorCode: code,
            ErrorMessage: message);

    private static CompanyCommissionMutationResult Unauthorized() =>
        new(
            CompanyCommissionMutationStatus.Unauthorized,
            ErrorCode: "participant_capability_invalid",
            ErrorMessage: "The participant capability is invalid or no longer active.");

    private static string CreateFingerprint(ICompanyCommissionCommand command)
    {
        var payload = JsonSerializer.SerializeToNode(
                command,
                command.GetType(),
                JsonOptions)?.AsObject()
            ?? throw new InvalidOperationException(
                "The commission command payload could not be canonicalized.");
        payload.Remove("context");
        var material =
            $"{command.GetType().FullName}:{payload.ToJsonString(JsonOptions)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

}
