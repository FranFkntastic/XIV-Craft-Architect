using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCommissionOperationsService(
    TradeCommissionOperationsClient client,
    TradeCompanyCollaborationClient collaborationClient,
    TradeOperationsPersistenceService tradeOperations,
    ProfileSyncLocalStateService localState,
    ProfileSyncService profileSync,
    HostedOrderProjectionStore hostedOrders,
    WebPlanPersistenceService planPersistence,
    AppState appState)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed record OrderCommandAuthority(
        HostedOrderAuthorityScope Projection,
        HostedProfileConnectionSettings Connection);
    private sealed record LinkedOwnerProjection(
        HostedOrderAuthorityScope Authority,
        CompanyCommissionOwnerProjection Projection);
    private readonly Dictionary<Guid, string> _errors = [];
    private readonly Dictionary<Guid, LinkedOwnerProjection> _linkedOwnerProjections = [];
    private readonly HashSet<Guid> _missingCanonicalOwners = [];
    private readonly Dictionary<Guid, IReadOnlyList<TradeDiscordNotificationDiagnostic>>
        _notificationDiagnostics = [];
    private readonly Dictionary<Guid, string> _notificationErrors = [];

    public CompanyCommissionOwnerProjection? GetForOrder(Guid orderId) =>
        GetCurrentLinkedProjection(orderId) ?? hostedOrders.GetOwnerProjection(orderId);

    public void DismissLinkedProjectionForLocalOrder(Guid orderId) =>
        _linkedOwnerProjections.Remove(orderId);

    public string? GetErrorForOrder(Guid orderId) =>
        _errors.GetValueOrDefault(orderId);

    public bool IsCanonicalOwnerMissing(Guid orderId) =>
        _missingCanonicalOwners.Contains(orderId);

    public IReadOnlyList<TradeDiscordNotificationDiagnostic>
        GetNotificationDiagnostics(Guid orderId) =>
        _notificationDiagnostics.GetValueOrDefault(orderId) ?? [];

    public string? GetNotificationError(Guid orderId) =>
        _notificationErrors.GetValueOrDefault(orderId);

    public async Task RefreshCanonicalAsync(
        IEnumerable<TradeOrder> orders,
        CancellationToken cancellationToken = default)
    {
        var canonical = orders
            .Where(order => order.CompanyCommission != null)
            .ToArray();
        foreach (var order in canonical)
        {
            await RefreshAsync(order, cancellationToken);
        }

        foreach (var company in canonical
                     .Select(order => order.CompanyCommission!.CompanyId)
                     .Distinct())
        {
            await RefreshNotificationDiagnosticsAsync(
                company,
                canonical,
                cancellationToken);
        }
    }

    public async Task RefreshAsync(
        TradeOrder order,
        CancellationToken cancellationToken = default)
    {
        if (order.CompanyCommission == null)
        {
            hostedOrders.ClearOwner(order.Id);
            _errors.Remove(order.Id);
            _missingCanonicalOwners.Remove(order.Id);
            return;
        }

        if (!CanPerformExternalAction(order, out var reason))
        {
            _missingCanonicalOwners.Remove(order.Id);
            _errors[order.Id] = reason;
            return;
        }

        OrderCommandAuthority? authority = null;
        HostedOrderProjectionSnapshot? expectedProjection = null;
        try
        {
            authority = await CaptureOrderAuthorityAsync();
            var companyId = order.CompanyCommission?.CompanyId ??
                throw new InvalidOperationException(
                    "The cached order does not contain canonical company ownership.");
            var commissionId = order.CompanyCommission.CommissionId;
            expectedProjection = hostedOrders.Get(order.Id);
            var projection = await client.LoadOwnerProjectionAsync(
                authority.Connection,
                companyId.Value,
                commissionId,
                cancellationToken);
            ValidateProjection(order, projection);
            await ApplyProjectionAsync(authority, projection);
        }
        catch (MissingCompanyCommissionOwnerException exception)
        {
            if (authority == null ||
                !await IsCurrentAuthorityAsync(authority) ||
                !hostedOrders.TryClearOwner(
                    authority.Projection,
                    order.Id,
                    expectedProjection))
            {
                return;
            }
            ClearLinkedProjection(authority.Projection, order.Id);
            _missingCanonicalOwners.Add(order.Id);
            _errors[order.Id] = exception.Message;
        }
        catch (Exception exception)
        {
            _missingCanonicalOwners.Remove(order.Id);
            _errors[order.Id] = exception.Message;
        }
    }

    public async Task<CompanyCommissionOwnerProjection?> ResolveNotificationNavigationAsync(
        TradeOrder order,
        CancellationToken cancellationToken = default)
    {
        if (order.CompanyCommission == null ||
            !CanLoadExternalProjection(out _))
        {
            return null;
        }

        OrderCommandAuthority? authority = null;
        try
        {
            authority = await CaptureOrderAuthorityAsync();
            var projection = await client.LoadOwnerProjectionAsync(
                authority.Connection,
                order.CompanyCommission.CompanyId.Value,
                order.CompanyCommission.CommissionId,
                cancellationToken);
            if (!await IsCurrentAuthorityAsync(authority))
            {
                return null;
            }

            ValidateProjection(order, projection);
            await ApplyProjectionAsync(authority, projection);
            _errors.Remove(order.Id);
            return projection;
        }
        catch (Exception exception)
        {
            _errors[order.Id] = exception.Message;
            return null;
        }
    }

    public async Task<CompanyCommissionOwnerProjection?> ResolveNotificationNavigationAsync(
        Guid companyId,
        Guid commissionId,
        CancellationToken cancellationToken = default)
    {
        if (companyId == Guid.Empty || commissionId == Guid.Empty)
        {
            return null;
        }
        if (!CanLoadExternalProjection(out _))
        {
            return null;
        }

        OrderCommandAuthority? authority = null;
        try
        {
            authority = await CaptureOrderAuthorityAsync();
            var projection = await client.LoadOwnerProjectionAsync(
                authority.Connection,
                companyId,
                commissionId,
                cancellationToken);
            if (!await IsCurrentAuthorityAsync(authority))
            {
                return null;
            }

            ValidateProjection(companyId, commissionId, projection);
            var adoption = await ApplyProjectionAsync(
                authority,
                projection,
                allowStale: true);
            if (adoption == HostedOrderCommittedProjectionResult.Stale)
            {
                var newer = await ResolveNewerNotificationWinnerAsync(
                    authority,
                    companyId,
                    commissionId,
                    projection);
                if (newer != null)
                {
                    _errors.Remove(commissionId);
                    _missingCanonicalOwners.Remove(commissionId);
                }
                return newer;
            }

            var current = GetForOrder(commissionId);
            if (current == null)
            {
                return null;
            }
            ValidateProjection(companyId, commissionId, current);
            projection = current;
            _errors.Remove(commissionId);
            return projection;
        }
        catch (MissingCompanyCommissionOwnerException exception)
        {
            if (authority != null && await IsCurrentAuthorityAsync(authority))
            {
                ClearLinkedProjection(authority.Projection, commissionId);
                _missingCanonicalOwners.Add(commissionId);
            }
            _errors[commissionId] = exception.Message;
            return null;
        }
        catch (Exception exception)
        {
            _errors[commissionId] = exception.Message;
            return null;
        }
    }

    private async Task<CompanyCommissionOwnerProjection?> ResolveNewerNotificationWinnerAsync(
        OrderCommandAuthority authority,
        Guid companyId,
        Guid commissionId,
        CompanyCommissionOwnerProjection fetched)
    {
        if (!await IsCurrentAuthorityAsync(authority))
        {
            return null;
        }

        var winner = GetForOrder(commissionId);
        if (winner == null ||
            winner.ObjectRevision.Value <= fetched.ObjectRevision.Value)
        {
            return null;
        }

        ValidateProjection(companyId, commissionId, winner);
        return winner;
    }

    public async Task<TradeCommissionOperatorResult> ConfirmIdentityAsync(
        CompanyCommissionOwnerProjection current,
        TradeCrafterProfile crafter,
        string lodestoneCharacterId,
        CancellationToken cancellationToken = default)
    {
        if (crafter.Id == Guid.Empty ||
            crafter.CompanyProfileId != current.Order.CompanyProfileId ||
            !string.Equals(
                crafter.LodestoneCharacterId,
                lodestoneCharacterId,
                StringComparison.Ordinal))
        {
            return Rejected(
                current,
                "The confirmed company crafter must match the submitted Lodestone identity.");
        }

        try
        {
            var authority = await CaptureOrderAuthorityAsync();
            if (!await tradeOperations.SaveCrafterAsync(crafter))
            {
                return Rejected(
                    current,
                    "Browser storage could not create or update the company crafter.");
            }
            await RequireCurrentAuthorityAsync(authority, "identity confirmation");

            await profileSync.QueueLocalSaveAsync(
                ProfileSyncCollections.TradeCrafters,
                crafter.Id.ToString("D"));
            await RequireCurrentAuthorityAsync(authority, "identity confirmation");
            await profileSync.SyncNowAsync(cancellationToken);
            await RequireCurrentAuthorityAsync(authority, "identity confirmation");
            var crafterConflict = profileSync.Conflicts.FirstOrDefault(item =>
                string.Equals(
                    item.Collection,
                    ProfileSyncCollections.TradeCrafters,
                    StringComparison.Ordinal) &&
                string.Equals(
                    item.ObjectId,
                    crafter.Id.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));
            if (crafterConflict != null)
            {
                return Rejected(
                    current,
                    "The company crafter changed on the hosted profile. Resolve that roster conflict before confirming identity.");
            }

            var commission = RequireCommission(current);
            var fresh = await client.LoadOwnerProjectionAsync(
                authority.Connection,
                commission.CompanyId.Value,
                commission.CommissionId,
                cancellationToken);
            ValidateProjection(current.Order, fresh);
            await ApplyProjectionAsync(authority, fresh);
            return await ExecuteAsync(
                fresh,
                "confirm-identity",
                new { crafterId = crafter.Id, lodestoneCharacterId },
                context => new ConfirmCompanyCommissionIdentityCommand(
                    context,
                    crafter.Id,
                    lodestoneCharacterId),
                cancellationToken,
                authority);
        }
        catch (Exception exception)
        {
            _errors[current.Order.Id] = exception.Message;
            return Rejected(current, exception.Message);
        }
    }

    public async Task<TradeCommissionOperatorResult> AmendTermsAsync(
        CompanyCommissionOwnerProjection current,
        CompanyCommissionTermsVersion terms,
        TradeOrder workPackage,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Rejected(current, "Describe why the terms changed.");
        }

        try
        {
            var authority = await CaptureOrderAuthorityAsync();
            var commandProjection = await PublishChangedLinkedPlanAsync(
                current,
                workPackage,
                authority,
                cancellationToken);
            var draft = CreateDraftWorkPackage(workPackage);
            var result = await ExecuteAsync(
                commandProjection,
                "amend-terms",
                new { Terms = terms, Reason = reason.Trim(), WorkPackage = draft },
                context => new AmendCompanyCommissionTermsCommand(
                    context,
                    terms,
                    reason.Trim(),
                    draft),
                cancellationToken,
                authority,
                CompanyCommissionProtocol.Version2);
            return ValidateCommittedWorkPackage(result, workPackage);
        }
        catch (Exception exception)
        {
            _errors[current.Order.Id] = exception.Message;
            return Rejected(current, exception.Message);
        }
    }

    public async Task<TradeCommissionOperatorResult> UpdateDraftAsync(
        CompanyCommissionOwnerProjection current,
        CompanyCommissionTermsVersion terms,
        TradeOrder workPackage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workPackage);
        try
        {
            var authority = await CaptureOrderAuthorityAsync();
            var commandProjection = await PublishChangedLinkedPlanAsync(
                current,
                workPackage,
                authority,
                cancellationToken);
            var draft = CreateDraftWorkPackage(workPackage);
            var result = await ExecuteAsync(
                commandProjection,
                "update-draft",
                new { Terms = terms, WorkPackage = draft },
                context => new UpdateCompanyCommissionDraftCommand(
                    context,
                    terms,
                    draft),
                cancellationToken,
                authority,
                CompanyCommissionProtocol.Version2);
            return ValidateCommittedWorkPackage(result, workPackage);
        }
        catch (Exception exception)
        {
            _errors[current.Order.Id] = exception.Message;
            return Rejected(current, exception.Message);
        }
    }

    private static CompanyCommissionDraftWorkPackage CreateDraftWorkPackage(
        TradeOrder workPackage) =>
        new(
            GetRequestedOutputs(workPackage),
            TradeOrderWorkflow.CopySourceSnapshot(workPackage.SourceSnapshot),
            workPackage.CraftPlanId,
            workPackage.CraftPlanName,
            workPackage.CraftPlanSavedAtUtc,
            workPackage.CraftPlanLinkKind);

    private static TradeCommissionOperatorResult ValidateCommittedWorkPackage(
        TradeCommissionOperatorResult result,
        TradeOrder workPackage)
    {
        if (!result.Success || result.Projection is not { } committed ||
            string.Equals(
                committed.Order.CraftPlanId,
                workPackage.CraftPlanId,
                StringComparison.Ordinal) &&
            committed.Order.CraftPlanSavedAtUtc == workPackage.CraftPlanSavedAtUtc)
        {
            return result;
        }

        return Rejected(
            committed,
            "The host committed the order but did not adopt the exact linked plan revision.",
            hostCommitted: true);
    }

    private async Task<CompanyCommissionOwnerProjection> PublishChangedLinkedPlanAsync(
        CompanyCommissionOwnerProjection current,
        TradeOrder workPackage,
        OrderCommandAuthority authority,
        CancellationToken cancellationToken)
    {
        if (workPackage.CraftPlanLinkKind != TradeOrderCraftPlanLinkKind.OrderGenerated ||
            string.IsNullOrWhiteSpace(workPackage.CraftPlanId) ||
            !workPackage.CraftPlanSavedAtUtc.HasValue ||
            string.Equals(
                current.Order.CraftPlanId,
                workPackage.CraftPlanId,
                StringComparison.Ordinal) &&
            current.Order.CraftPlanSavedAtUtc == workPackage.CraftPlanSavedAtUtc)
        {
            return current;
        }

        var stored = await planPersistence.LoadPlanPayloadAsync(workPackage.CraftPlanId);
        if (stored == null ||
            !string.Equals(stored.Id, workPackage.CraftPlanId, StringComparison.Ordinal) ||
            stored.SavedAt != workPackage.CraftPlanSavedAtUtc.Value ||
            stored.LinkedOrderId != workPackage.Id)
        {
            throw new InvalidOperationException(
                "The exact linked plan revision is unavailable, so the order was not changed.");
        }

        var publication = await profileSync.PublishLocalObjectAsync(
            ProfileSyncCollections.Plans,
            stored.Id,
            authority.Connection,
            cancellationToken);
        if (!publication.Published)
        {
            throw new InvalidOperationException(
                $"The exact linked plan is saved on this device, but the order was not changed. {publication.Message}");
        }
        await RequireCurrentAuthorityAsync(authority, "linked-plan publication");
        var commission = RequireCommission(current);
        var refreshed = await client.LoadOwnerProjectionAsync(
            authority.Connection,
            commission.CompanyId.Value,
            commission.CommissionId,
            cancellationToken);
        ValidateProjection(current.Order, refreshed);
        if (refreshed.ObjectRevision != current.ObjectRevision)
        {
            throw new InvalidOperationException(
                "The commission changed while its linked plan was being published. Rebase the terms revision before retrying.");
        }
        await ApplyProjectionAsync(authority, refreshed);
        return refreshed;
    }

    public Task<TradeCommissionOperatorResult> RejectClaimAsync(
        CompanyCommissionOwnerProjection current,
        string reason,
        bool blockProvisionalContact,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(Rejected(current, "A rejection reason is required."));
        }

        return ExecuteAsync(
            current,
            "reject-claim",
            new { Reason = reason.Trim(), blockProvisionalContact },
            context => new RejectCompanyCommissionClaimCommand(
                context,
                reason.Trim(),
                blockProvisionalContact),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> DecidePaymentPolicyAsync(
        CompanyCommissionOwnerProjection current,
        PendingPaymentPolicyRequest request,
        bool accepted,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(Rejected(current, "A payment-policy decision reason is required."));
        }

        if (request.RequestedSchedule == null)
        {
            return Task.FromResult(Rejected(
                current,
                request.Error ?? "The payment-policy request is incomplete."));
        }

        return ExecuteAsync(
            current,
            "decide-payment-policy",
            new
            {
                request.EventId,
                Accepted = accepted,
                Reason = reason.Trim(),
                RequestedSchedule = request.RequestedSchedule,
                request.RequestedCustomTerms
            },
            context => new DecideCompanyCommissionPaymentPolicyChangeCommand(
                context,
                accepted,
                reason.Trim()),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> RecordPaymentAsync(
        CompanyCommissionOwnerProjection current,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return Task.FromResult(Rejected(
                current,
                "Describe the observed payment before recording it."));
        }

        return ExecuteAsync(
            current,
            "record-payment",
            new { Note = note.Trim() },
            context => new RecordCompanyCommissionPaymentCommand(context, note.Trim()),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> RetractPaymentAsync(
        CompanyCommissionOwnerProjection current,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(Rejected(
                current,
                "Explain why the payment confirmation is being retracted."));
        }

        return ExecuteAsync(
            current,
            "retract-payment",
            new { Reason = reason.Trim() },
            context => new RetractCompanyCommissionPaymentAttestationCommand(
                context,
                reason.Trim()),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> MarkCompanyMaterialsReadyAsync(
        CompanyCommissionOwnerProjection current,
        CancellationToken cancellationToken = default)
    {
        var quantities = RequireCommission(current).CurrentTerms.Materials
            .Where(material =>
                material.Responsibility == CommissionMaterialResponsibility.Provided)
            .Select(material => new CompanyCommissionMaterialQuantity(
                material.LineId,
                material.ItemId,
                material.Quantity))
            .ToArray();
        if (quantities.Length == 0)
        {
            return Task.FromResult(Rejected(
                current,
                "This commission has no company-provided material bundle."));
        }

        return ExecuteAsync(
            current,
            "mark-company-materials-ready",
            quantities,
            context => new MarkCompanyCommissionMaterialsReadyCommand(context, quantities),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> ReturnToWorkAsync(
        CompanyCommissionOwnerProjection current,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(Rejected(current, "A return-to-work reason is required."));
        }

        return ExecuteAsync(
            current,
            "return-to-work",
            new { Reason = reason.Trim() },
            context => new ReturnCompanyCommissionToWorkCommand(context, reason.Trim()),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> AcceptDeliveryAsync(
        CompanyCommissionOwnerProjection current,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            current,
            "accept-delivery",
            payload: null,
            context => new AcceptCompanyCommissionDeliveryCommand(context),
            cancellationToken);

    public Task<TradeCommissionOperatorResult> RecordSettlementAsync(
        CompanyCommissionOwnerProjection current,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return Task.FromResult(Rejected(
                current,
                "Describe the observed settlement before recording it."));
        }

        return ExecuteAsync(
            current,
            "record-settlement",
            new { Note = note.Trim() },
            context => new RecordCompanyCommissionSettlementCommand(context, note.Trim()),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> RetractSettlementAsync(
        CompanyCommissionOwnerProjection current,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(Rejected(
                current,
                "A final-payment retraction reason is required."));
        }

        return ExecuteAsync(
            current,
            "retract-settlement",
            new { Reason = reason.Trim() },
            context => new RetractCompanyCommissionSettlementAttestationCommand(
                context,
                reason.Trim()),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> CancelAsync(
        CompanyCommissionOwnerProjection current,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(Rejected(current, "A cancellation reason is required."));
        }

        return ExecuteAsync(
            current,
            "cancel",
            new { Reason = reason.Trim() },
            context => new CancelCompanyCommissionCommand(context, reason.Trim()),
            cancellationToken);
    }

    public async Task<TradeCommissionOperatorResult> CancelDraftAsync(
        TradeOrder selectedOrder,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedOrder);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new TradeCommissionOperatorResult(
                false,
                GetForOrder(selectedOrder.Id),
                "A draft-discard reason is required.");
        }

        await RefreshAsync(selectedOrder, cancellationToken);
        var current = GetForOrder(selectedOrder.Id);
        if (current == null)
        {
            return new TradeCommissionOperatorResult(
                false,
                null,
                GetErrorForOrder(selectedOrder.Id) ??
                "The current hosted commission could not be loaded before discarding it.");
        }
        if (TradeOrderWorkflow.GetLifecycleAction(current.Order) !=
            TradeOrderLifecycleAction.DiscardDraft)
        {
            return Rejected(
                current,
                "This commission is no longer an unpublished draft. Its current state was preserved.");
        }

        return await ExecuteAsync(
            current,
            "cancel",
            new { Reason = reason.Trim() },
            context => new CancelCompanyCommissionCommand(context, reason.Trim()),
            cancellationToken,
            replayRevisionConflict: false);
    }

    public Task<TradeCommissionOperatorResult> ReopenAsync(
        CompanyCommissionOwnerProjection current,
        string resolution,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return Task.FromResult(Rejected(
                current,
                "Describe how the canceled or interrupted commission was resolved before reopening it."));
        }

        return ExecuteAsync(
            current,
            "reopen",
            new { Resolution = resolution.Trim() },
            context => new ReopenCompanyCommissionCommand(context, resolution.Trim()),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> RevokePublicationAsync(
        CompanyCommissionOwnerProjection current,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            current,
            "revoke-publication",
            payload: null,
            context => new RevokeCompanyCommissionPublicationCommand(context),
            cancellationToken);

    public Task<TradeCommissionOperatorResult> AddCommentAsync(
        CompanyCommissionOwnerProjection current,
        string comment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return Task.FromResult(Rejected(current, "A shared comment is required."));
        }

        return ExecuteAsync(
            current,
            "add-comment",
            new { Comment = comment.Trim() },
            context => new AddCompanyCommissionCommentCommand(context, comment.Trim()),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> AddPrivateNoteAsync(
        CompanyCommissionOwnerProjection current,
        string comment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return Task.FromResult(Rejected(current, "A company-only note is required."));
        }

        return ExecuteAsync(
            current,
            "add-private-note",
            new { Comment = comment.Trim() },
            context => new AddCompanyCommissionPrivateNoteCommand(
                context,
                comment.Trim()),
            cancellationToken);
    }

    public async Task<TradeCommissionOperatorResult> RecoverParticipantAsync(
        CompanyCommissionOwnerProjection current,
        CancellationToken cancellationToken = default)
    {
        if (!CanPerformExternalAction(current.Order, out var reason))
        {
            return Rejected(current, reason);
        }

        CompanyCommissionOwnerProjection? committedProjection = null;
        var hostCommitted = false;
        try
        {
            ValidateProjection(current.Order, current);
            var authority = await CaptureOrderAuthorityAsync();
            var context = CreateContext(
                current,
                "reset-participant-recovery",
                payload: null);
            var response = await client.ResetParticipantRecoveryAsync(
                new ResetCompanyCommissionParticipantRecoveryCommand(context),
                cancellationToken,
                authority.Connection);
            if (!response.Mutation.Success)
            {
                return Rejected(
                    current,
                    response.Mutation.ErrorMessage ??
                    $"Participant recovery reset was {response.Mutation.Status.ToString().ToLowerInvariant()}.");
            }

            hostCommitted = true;
            committedProjection = response.Projection;
            var updated = response.Projection;
            ValidateProjection(current.Order, updated);
            if (updated.ObjectRevision.Value <= current.ObjectRevision.Value)
            {
                throw new InvalidOperationException(
                    "Participant recovery reset did not advance the authoritative order revision.");
            }

            var recoveryGrant = RequireCommission(updated).RecoveryGrant;
            if (recoveryGrant == null ||
                recoveryGrant.RedeemedAtUtc.HasValue ||
                recoveryGrant.RevokedAtUtc.HasValue)
            {
                throw new InvalidOperationException(
                    "Participant recovery reset did not return an active one-time recovery grant.");
            }

            ValidateCapabilityUrl(updated, response.RecoveryUrl, "recover");
            await ApplyProjectionAsync(authority, updated);
            return new TradeCommissionOperatorResult(
                true,
                updated,
                RecoveryUrl: response.RecoveryUrl,
                HostCommitted: true);
        }
        catch (Exception exception)
        {
            _errors[current.Order.Id] = exception.Message;
            return Rejected(
                committedProjection ?? current,
                exception.Message,
                hostCommitted);
        }
    }

    public async Task<TradeCommissionOperatorResult> IssueClaimLinkAsync(
        CompanyCommissionOwnerProjection current,
        CancellationToken cancellationToken = default)
    {
        if (!CanPerformExternalAction(current.Order, out var reason))
        {
            return Rejected(current, reason);
        }

        if (RequireCommission(current).PublicMetadata.IsTestFixture)
        {
            return Rejected(
                current,
                "This test commission is intentionally unclaimable.");
        }

        try
        {
            ValidateProjection(current.Order, current);
            var authority = await CaptureOrderAuthorityAsync();
            var response = await client.IssueClaimLinkAsync(
                CreateContext(current, "issue-claim-link", payload: null),
                cancellationToken,
                authority.Connection);
            ValidateCapabilityUrl(current, response.ClaimUrl, "claim");
            await RequireCurrentAuthorityAsync(authority, "claim-link issuance");
            _errors.Remove(current.Order.Id);
            return new TradeCommissionOperatorResult(
                true,
                current,
                ClaimUrl: response.ClaimUrl);
        }
        catch (Exception exception)
        {
            _errors[current.Order.Id] = exception.Message;
            return Rejected(current, exception.Message);
        }
    }

    public async Task<string?> RetryNotificationDiagnosticAsync(
        CompanyCommissionOwnerProjection current,
        Guid diagnosticId,
        CancellationToken cancellationToken = default)
    {
        if (!CanPerformExternalAction(current.Order, out var reason))
        {
            return reason;
        }

        try
        {
            var companyId = RequireCommission(current).CompanyId;
            await collaborationClient.RetryNotificationDiagnosticAsync(
                companyId.Value,
                diagnosticId,
                cancellationToken);
            await RefreshNotificationDiagnosticsAsync(
                companyId,
                [current.Order],
                cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    private async Task<TradeCommissionOperatorResult> ExecuteAsync<TCommand>(
        CompanyCommissionOwnerProjection current,
        string route,
        object? payload,
        Func<CompanyCommissionCommandContext, TCommand> createCommand,
        CancellationToken cancellationToken,
        OrderCommandAuthority? capturedAuthority = null,
        int protocolVersion = CompanyCommissionProtocol.Version1,
        bool replayRevisionConflict = true)
        where TCommand : ICompanyCommissionCommand
    {
        if (!CanPerformExternalAction(current.Order, out var reason))
        {
            return Rejected(current, reason);
        }

        var commandProjection = current;
        CompanyCommissionOwnerProjection? committedProjection = null;
        var hostCommitted = false;
        try
        {
            var authority = capturedAuthority ?? await CaptureOrderAuthorityAsync();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ValidateProjection(current.Order, commandProjection);
                TradeCommissionOwnerMutationResponse response;
                try
                {
                    var context = CreateContext(
                        commandProjection,
                        route,
                        payload,
                        protocolVersion);
                    response = await client.ExecuteAsync(
                        route,
                        createCommand(context),
                        cancellationToken,
                        authority.Connection);
                }
                catch (CompanyCommissionRevisionConflictException)
                    when (attempt == 0 &&
                          replayRevisionConflict &&
                          CanReplayAfterRevisionConflict(route))
                {
                    var commission = RequireCommission(commandProjection);
                    commandProjection = await client.LoadOwnerProjectionAsync(
                        authority.Connection,
                        commission.CompanyId.Value,
                        commission.CommissionId,
                        cancellationToken);
                    ValidateProjection(current.Order, commandProjection);
                    await ApplyProjectionAsync(authority, commandProjection);
                    continue;
                }

                var mutation = response.Mutation;
                if (!mutation.Success)
                {
                    return Rejected(
                        commandProjection,
                        mutation.ErrorMessage ??
                        $"The commissioner command was {mutation.Status.ToString().ToLowerInvariant()}.");
                }

                hostCommitted = true;
                var updated = response.Projection ?? throw new InvalidOperationException(
                    "The successful commissioner command returned no committed owner projection.");
                ValidateProjection(current.Order, updated);
                if (mutation.Status == CompanyCommissionMutationStatus.Applied &&
                    updated.ObjectRevision.Value <= commandProjection.ObjectRevision.Value)
                {
                    throw new InvalidOperationException(
                        "The commissioner command did not advance the authoritative order revision.");
                }

                if (!string.IsNullOrWhiteSpace(response.ClaimUrl))
                {
                    ValidateCapabilityUrl(updated, response.ClaimUrl, "claim");
                }
                committedProjection = updated;
                await ApplyProjectionAsync(authority, updated);
                return new TradeCommissionOperatorResult(
                    true,
                    updated,
                    ClaimUrl: response.ClaimUrl,
                    HostCommitted: true);
            }

            throw new InvalidOperationException(
                "The hosted commission changed repeatedly while the command was being applied.");
        }
        catch (Exception exception)
        {
            _errors[current.Order.Id] = exception.Message;
            return Rejected(
                hostCommitted ? committedProjection : commandProjection,
                exception.Message,
                hostCommitted);
        }
    }

    private static bool CanReplayAfterRevisionConflict(string route) =>
        route switch
        {
            "confirm-identity" or
            "reject-claim" or
            "decide-payment-policy" or
            "record-payment" or
            "retract-payment" or
            "mark-company-materials-ready" or
            "return-to-work" or
            "accept-delivery" or
            "record-settlement" or
            "retract-settlement" or
            "cancel" or
            "revoke-publication" or
            "add-comment" or
            "add-private-note" => true,
            "amend-terms" or "update-draft" => false,
            _ => false,
        };

    private async Task<OrderCommandAuthority> CaptureOrderAuthorityAsync()
    {
        var projection = hostedOrders.CaptureAuthorityScope();
        var connection = await localState.LoadConnectionSettingsAsync();
        if (string.IsNullOrWhiteSpace(projection.ProfileId) ||
            string.IsNullOrWhiteSpace(connection.ConnectionScopeId) ||
            !string.Equals(
                projection.ProfileId,
                profileSync.CurrentStatus.ProfileId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                projection.ProfileId,
                connection.ProfileScopeId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                projection.ConnectionScopeId,
                connection.ConnectionScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The hosted order authority is not ready for this commission operation.");
        }
        return new OrderCommandAuthority(projection, connection.Snapshot());
    }

    private async Task<HostedOrderCommittedProjectionResult> ApplyProjectionAsync(
        OrderCommandAuthority authority,
        CompanyCommissionOwnerProjection projection,
        bool allowStale = false)
    {
        if (await ShouldApplyLinkedProjectionAsync(projection))
        {
            var linkedAdoption = await ApplyLinkedProjectionAsync(authority, projection);
            if (allowStale && linkedAdoption == HostedOrderCommittedProjectionResult.Stale)
            {
                return linkedAdoption;
            }
            if (linkedAdoption is not (
                HostedOrderCommittedProjectionResult.Adopted or
                HostedOrderCommittedProjectionResult.AlreadyCurrent))
            {
                throw new InvalidOperationException(
                    $"The linked commission projection could not be applied because its authority is {linkedAdoption}.");
            }
            return linkedAdoption;
        }

        var adoption = await hostedOrders.AdoptAndPersistCommittedOwnerAsync(
            authority.Projection,
            projection,
            async winner =>
            {
                var persisted = winner.Deleted
                    ? await tradeOperations.DeleteOrderAsync(winner.OrderId)
                    : await tradeOperations.ApplyCanonicalOrderAsync(winner.Order!);
                if (!await IsCurrentAuthorityAsync(authority))
                {
                    await RepairDurableOrderFromCurrentProjectionAsync(winner.OrderId);
                    throw new InvalidOperationException(
                        "The hosted order authority changed while commission persistence was in progress.");
                }
                if (!persisted)
                {
                    throw new InvalidOperationException(
                        "The owner projection was authoritative, but browser storage could not apply its Trade order.");
                }
                await localState.SaveObjectRevisionAsync(
                    authority.Connection,
                    ProfileSyncCollections.TradeOrders,
                    winner.OrderId.ToString("D"),
                    winner.ObjectRevision);
            },
            () => IsCurrentAuthorityAsync(authority));
        if (adoption == HostedOrderCommittedProjectionResult.ScopeChanged)
        {
            await RepairDurableOrderFromCurrentProjectionAsync(projection.Order.Id);
        }
        if (allowStale && adoption == HostedOrderCommittedProjectionResult.Stale)
        {
            return adoption;
        }
        if (adoption is not (
            HostedOrderCommittedProjectionResult.Adopted or
            HostedOrderCommittedProjectionResult.AlreadyCurrent))
        {
            throw new InvalidOperationException(
                $"The committed commission projection could not be applied because its authority is {adoption}.");
        }
        _errors.Remove(projection.Order.Id);
        _missingCanonicalOwners.Remove(projection.Order.Id);
        _linkedOwnerProjections.Remove(projection.Order.Id);
        appState.NotifyTradeOperationsDataChanged();
        return adoption;
    }

    private async Task<HostedOrderCommittedProjectionResult> ApplyLinkedProjectionAsync(
        OrderCommandAuthority authority,
        CompanyCommissionOwnerProjection projection)
    {
        if (!await IsCurrentAuthorityAsync(authority))
        {
            return HostedOrderCommittedProjectionResult.ScopeChanged;
        }

        var current = GetCurrentLinkedProjection(projection.Order.Id);
        if (current != null)
        {
            var currentCommission = RequireCommission(current);
            var candidateCommission = RequireCommission(projection);
            if (current.Order.CompanyProfileId != projection.Order.CompanyProfileId ||
                currentCommission.CompanyId != candidateCommission.CompanyId ||
                currentCommission.CommissionId != candidateCommission.CommissionId)
            {
                throw new InvalidOperationException(
                    "The linked commission projection changed identity while local work was protected.");
            }

            if (projection.ObjectRevision.Value < current.ObjectRevision.Value)
            {
                return HostedOrderCommittedProjectionResult.Stale;
            }
            if (projection.ObjectRevision == current.ObjectRevision &&
                projection.CompanyRevision.Value <= current.CompanyRevision.Value)
            {
                return HostedOrderCommittedProjectionResult.AlreadyCurrent;
            }
        }

        _linkedOwnerProjections[projection.Order.Id] = new(
            authority.Projection,
            projection);
        _errors.Remove(projection.Order.Id);
        _missingCanonicalOwners.Remove(projection.Order.Id);
        appState.NotifyTradeOperationsDataChanged();
        return HostedOrderCommittedProjectionResult.Adopted;
    }

    private CompanyCommissionOwnerProjection? GetCurrentLinkedProjection(Guid orderId)
    {
        if (!_linkedOwnerProjections.TryGetValue(orderId, out var linked))
        {
            return null;
        }
        if (hostedOrders.IsCurrentAuthority(linked.Authority))
        {
            var hosted = hostedOrders.GetOwnerProjection(orderId);
            if (hosted != null && IsAtLeastAsNew(hosted, linked.Projection))
            {
                _linkedOwnerProjections.Remove(orderId);
                return null;
            }
            return linked.Projection;
        }

        _linkedOwnerProjections.Remove(orderId);
        return null;
    }

    private async Task<bool> ShouldApplyLinkedProjectionAsync(
        CompanyCommissionOwnerProjection projection)
    {
        if (HasProtectedHostedOrderState(projection.Order.Id))
        {
            return true;
        }
        if (GetCurrentLinkedProjection(projection.Order.Id) == null)
        {
            return false;
        }

        var durable = await tradeOperations.LoadOrderAsync(projection.Order.Id);
        if (durable == null)
        {
            return false;
        }

        var candidateCommission = RequireCommission(projection);
        var durableCommission = durable.CompanyCommission;
        return durableCommission == null ||
               durableCommission.CompanyId != candidateCommission.CompanyId ||
               durableCommission.CommissionId != candidateCommission.CommissionId;
    }

    private void ClearLinkedProjection(
        HostedOrderAuthorityScope authority,
        Guid orderId)
    {
        if (_linkedOwnerProjections.TryGetValue(orderId, out var linked) &&
            linked.Authority == authority)
        {
            _linkedOwnerProjections.Remove(orderId);
        }
    }

    private static bool IsAtLeastAsNew(
        CompanyCommissionOwnerProjection candidate,
        CompanyCommissionOwnerProjection current) =>
        candidate.ObjectRevision.Value > current.ObjectRevision.Value ||
        candidate.ObjectRevision == current.ObjectRevision &&
        candidate.CompanyRevision.Value >= current.CompanyRevision.Value;

    private async Task<bool> IsCurrentAuthorityAsync(OrderCommandAuthority authority)
    {
        if (!hostedOrders.IsCurrentAuthority(authority.Projection))
        {
            return false;
        }
        var connection = await localState.LoadConnectionSettingsAsync();
        return string.Equals(
                   authority.Connection.ConnectionScopeId,
                   connection.ConnectionScopeId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   authority.Connection.ProfileScopeId,
                   connection.ProfileScopeId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task RequireCurrentAuthorityAsync(
        OrderCommandAuthority authority,
        string operation)
    {
        if (!await IsCurrentAuthorityAsync(authority))
        {
            throw new InvalidOperationException(
                $"The hosted order authority changed during {operation}.");
        }
    }

    private async Task RepairDurableOrderFromCurrentProjectionAsync(Guid orderId)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var authority = hostedOrders.CaptureAuthorityScope();
            var connection = (await localState.LoadConnectionSettingsAsync()).Snapshot();
            var scopeIsCurrent = MatchesConnectionScope(authority, connection);
            var current = scopeIsCurrent ? hostedOrders.Get(orderId) : null;
            var repaired = current is { Deleted: false, Order: not null }
                ? await tradeOperations.ApplyCanonicalOrderAsync(current.Order)
                : await tradeOperations.DeleteOrderAsync(orderId);
            if (!repaired)
            {
                throw new InvalidOperationException(
                    "Browser storage could not repair the Trade order after its hosted authority changed.");
            }

            var latestConnection = await localState.LoadConnectionSettingsAsync();
            if (hostedOrders.IsCurrentAuthority(authority) &&
                HasSameConnection(connection, latestConnection) &&
                (!scopeIsCurrent ||
                 HasSameProjectionVersion(current, hostedOrders.Get(orderId))))
            {
                return;
            }
        }

        if (!await tradeOperations.DeleteOrderAsync(orderId))
        {
            throw new InvalidOperationException(
                "Browser storage could not remove a Trade order after repeated hosted authority changes.");
        }
    }

    private static bool HasSameProjectionVersion(
        HostedOrderProjectionSnapshot? left,
        HostedOrderProjectionSnapshot? right) =>
        left?.ObjectRevision == right?.ObjectRevision &&
        left?.CompanyRevision == right?.CompanyRevision &&
        left?.Deleted == right?.Deleted;

    private static bool MatchesConnectionScope(
        HostedOrderAuthorityScope authority,
        HostedProfileConnectionSettings connection) =>
        string.Equals(authority.ProfileId, connection.ProfileScopeId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(authority.ConnectionScopeId, connection.ConnectionScopeId, StringComparison.Ordinal);

    private static bool HasSameConnection(
        HostedProfileConnectionSettings left,
        HostedProfileConnectionSettings right) =>
        string.Equals(left.ProfileScopeId, right.ProfileScopeId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ConnectionScopeId, right.ConnectionScopeId, StringComparison.Ordinal);

    private static IReadOnlyList<TradeRequestedOrderOutput> GetRequestedOutputs(
        TradeOrder order) =>
        (order.SourceSnapshot?.RootItems ?? [])
            .Select(item => new TradeRequestedOrderOutput(
                item.ItemId,
                item.Name,
                item.Quantity,
                item.MustBeHq,
                item.EstimatedSaleValue))
            .ToArray();

    private async Task RefreshNotificationDiagnosticsAsync(
        CompanyId companyId,
        IReadOnlyList<TradeOrder> companyOrders,
        CancellationToken cancellationToken)
    {
        try
        {
            var diagnostics = await collaborationClient.LoadNotificationDiagnosticsAsync(
                companyId.Value,
                cancellationToken);
            foreach (var order in companyOrders.Where(
                         order => order.CompanyCommission?.CompanyId == companyId))
            {
                var commissionId = order.CompanyCommission!.CommissionId;
                _notificationDiagnostics[order.Id] = diagnostics
                    .Where(item => item.CommissionId == commissionId)
                    .OrderByDescending(item => item.UpdatedAt)
                    .ToArray();
                _notificationErrors.Remove(order.Id);
            }
        }
        catch (Exception exception)
        {
            foreach (var order in companyOrders.Where(
                         order => order.CompanyCommission?.CompanyId == companyId))
            {
                _notificationErrors[order.Id] =
                    $"Discord diagnostics unavailable: {exception.Message}";
            }
        }
    }

    private bool CanPerformExternalAction(TradeOrder order, out string reason)
    {
        if (!CanLoadExternalProjection(out reason))
        {
            return false;
        }

        if (HasProtectedHostedOrderState(order.Id) &&
            GetCurrentLinkedProjection(order.Id) == null)
        {
            reason = "Resolve the pending hosted order update before operating its commission.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool CanLoadExternalProjection(out string reason)
    {
        if (!profileSync.CurrentStatus.IsConnected)
        {
            reason = "Connect this browser in Settings before operating this company commission.";
            return false;
        }

        if (!profileSync.CurrentStatus.HostReachable)
        {
            reason = profileSync.CurrentStatus.Message ?? "Profile Hosting is unavailable.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool HasProtectedHostedOrderState(Guid orderId)
    {
        var objectId = orderId.ToString("D");
        return profileSync.PendingSaves.Any(item =>
                   string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal) &&
                   string.Equals(item.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)) ||
               profileSync.Conflicts.Any(item =>
                   string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal) &&
                   string.Equals(item.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
    }

    private static CompanyCommissionCommandContext CreateContext(
        CompanyCommissionOwnerProjection current,
        string route,
        object? payload,
        int protocolVersion = CompanyCommissionProtocol.Version1)
    {
        var fingerprintSource = JsonSerializer.Serialize(
            new
            {
                CommissionId = RequireCommission(current).CommissionId,
                ObjectRevision = current.ObjectRevision.Value,
                Route = route,
                Payload = payload
            },
            JsonOptions);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));
        var commandIdBytes = Convert.FromHexString(fingerprint[..32]);
        return new CompanyCommissionCommandContext(
            RequireCommission(current).CompanyId,
            RequireCommission(current).CommissionId,
            current.ObjectRevision,
            current.CompanyRevision,
            new Guid(commandIdBytes),
            protocolVersion);
    }

    private static void ValidateProjection(
        TradeOrder expectedOrder,
        CompanyCommissionOwnerProjection projection)
    {
        var commission = projection.Order.CompanyCommission;
        var expectedCompanyId = expectedOrder.CompanyCommission?.CompanyId;
        var expectedCommissionId = expectedOrder.CompanyCommission?.CommissionId;
        if (projection.Order.Id != expectedOrder.Id ||
            projection.Order.CompanyProfileId != expectedOrder.CompanyProfileId ||
            commission == null ||
            expectedCommissionId == null ||
            commission.CommissionId != expectedCommissionId ||
            expectedCompanyId == null ||
            commission.CompanyId != expectedCompanyId ||
            projection.ObjectRevision.Value <= 0 ||
            projection.CompanyRevision.Value <= 0)
        {
            throw new InvalidOperationException(
                "The owner endpoint returned the wrong commission or omitted authoritative revisions.");
        }
    }

    private static void ValidateProjection(
        Guid expectedCompanyId,
        Guid expectedCommissionId,
        CompanyCommissionOwnerProjection projection)
    {
        var commission = projection.Order.CompanyCommission;
        if (projection.Order.Id != expectedCommissionId ||
            projection.Order.CompanyProfileId != expectedCompanyId ||
            commission == null ||
            commission.CommissionId != expectedCommissionId ||
            commission.CompanyId.Value != expectedCompanyId ||
            projection.ObjectRevision.Value <= 0 ||
            projection.CompanyRevision.Value <= 0)
        {
            throw new InvalidOperationException(
                "The owner endpoint returned the wrong commission or omitted authoritative revisions.");
        }
    }

    private static TradeCompanyCommission RequireCommission(
        CompanyCommissionOwnerProjection projection) =>
        projection.Order.CompanyCommission ??
        throw new InvalidOperationException(
            "The authenticated owner projection does not contain a company commission.");

    private static TradeCommissionOperatorResult Rejected(
        CompanyCommissionOwnerProjection? current,
        string message,
        bool hostCommitted = false) =>
        new(false, current, message, HostCommitted: hostCommitted);

    private static void ValidateCapabilityUrl(
        CompanyCommissionOwnerProjection projection,
        string capabilityUrl,
        string fragmentName)
    {
        var publicUrl = RequireCommission(projection).PublicMetadata.PublicUrl;
        if (string.IsNullOrWhiteSpace(publicUrl) ||
            string.IsNullOrWhiteSpace(capabilityUrl) ||
            !capabilityUrl.StartsWith(
                publicUrl.Split('#')[0] + $"#{fragmentName}=",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The hosted service returned an invalid #{fragmentName} capability link.");
        }
    }
}

public sealed record TradeCommissionOperatorResult(
    bool Success,
    CompanyCommissionOwnerProjection? Projection,
    string? Message = null,
    string? RecoveryUrl = null,
    string? ClaimUrl = null,
    bool HostCommitted = false);
