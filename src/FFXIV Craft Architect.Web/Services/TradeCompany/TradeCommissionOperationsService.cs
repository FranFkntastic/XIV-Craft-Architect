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
    AppState appState)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<Guid, CompanyCommissionOwnerProjection> _projections = [];
    private readonly Dictionary<Guid, string> _errors = [];
    private readonly Dictionary<Guid, IReadOnlyList<TradeDiscordNotificationDiagnostic>>
        _notificationDiagnostics = [];
    private readonly Dictionary<Guid, string> _notificationErrors = [];

    public CompanyCommissionOwnerProjection? GetForOrder(Guid orderId) =>
        _projections.GetValueOrDefault(orderId);

    public string? GetErrorForOrder(Guid orderId) =>
        _errors.GetValueOrDefault(orderId);

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
            _projections.Remove(order.Id);
            _errors.Remove(order.Id);
            return;
        }

        if (!CanPerformExternalAction(order, out var reason))
        {
            _projections.Remove(order.Id);
            _errors[order.Id] = reason;
            return;
        }

        try
        {
            var companyId = order.CompanyCommission?.CompanyId ??
                throw new InvalidOperationException(
                    "The cached order does not contain canonical company ownership.");
            var commissionId = order.CompanyCommission.CommissionId;
            var projection = await client.LoadOwnerProjectionAsync(
                companyId.Value,
                commissionId,
                cancellationToken);
            ValidateProjection(order, projection);
            await ApplyProjectionAsync(projection);
        }
        catch (Exception exception)
        {
            _projections.Remove(order.Id);
            _errors[order.Id] = exception.Message;
        }
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
            if (!await tradeOperations.SaveCrafterAsync(crafter))
            {
                return Rejected(
                    current,
                    "Browser storage could not create or update the company crafter.");
            }

            await profileSync.QueueLocalSaveAsync(
                ProfileSyncCollections.TradeCrafters,
                crafter.Id.ToString("D"));
            await profileSync.SyncNowAsync(cancellationToken);
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
                commission.CompanyId.Value,
                commission.CommissionId,
                cancellationToken);
            ValidateProjection(current.Order, fresh);
            await ApplyProjectionAsync(fresh);
            return await ExecuteAsync(
                fresh,
                "confirm-identity",
                new { crafterId = crafter.Id, lodestoneCharacterId },
                context => new ConfirmCompanyCommissionIdentityCommand(
                    context,
                    crafter.Id,
                    lodestoneCharacterId),
                cancellationToken);
        }
        catch (Exception exception)
        {
            _errors[current.Order.Id] = exception.Message;
            return Rejected(current, exception.Message);
        }
    }

    public Task<TradeCommissionOperatorResult> AmendTermsAsync(
        CompanyCommissionOwnerProjection current,
        CompanyCommissionTermsVersion terms,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Task.FromResult(Rejected(current, "Describe why the terms changed."));
        }

        return ExecuteAsync(
            current,
            "amend-terms",
            new { Terms = terms, Reason = reason.Trim() },
            context => new AmendCompanyCommissionTermsCommand(
                context,
                terms,
                reason.Trim()),
            cancellationToken);
    }

    public Task<TradeCommissionOperatorResult> UpdateDraftAsync(
        CompanyCommissionOwnerProjection current,
        CompanyCommissionTermsVersion terms,
        TradeOrder workPackage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workPackage);
        return ExecuteAsync(
            current,
            "update-draft",
            new
            {
                Terms = terms,
                WorkPackage = new CompanyCommissionDraftWorkPackage(
                    GetRequestedOutputs(workPackage),
                    TradeOrderWorkflow.CopySourceSnapshot(workPackage.SourceSnapshot),
                    workPackage.CraftPlanId,
                    workPackage.CraftPlanName,
                    workPackage.CraftPlanSavedAtUtc,
                    workPackage.CraftPlanLinkKind)
            },
            context => new UpdateCompanyCommissionDraftCommand(
                context,
                terms,
                new CompanyCommissionDraftWorkPackage(
                    GetRequestedOutputs(workPackage),
                    TradeOrderWorkflow.CopySourceSnapshot(workPackage.SourceSnapshot),
                    workPackage.CraftPlanId,
                    workPackage.CraftPlanName,
                    workPackage.CraftPlanSavedAtUtc,
                    workPackage.CraftPlanLinkKind)),
            cancellationToken);
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

        try
        {
            ValidateProjection(current.Order, current);
            var context = CreateContext(
                current,
                "reset-participant-recovery",
                payload: null);
            var response = await client.ResetParticipantRecoveryAsync(
                new ResetCompanyCommissionParticipantRecoveryCommand(context),
                cancellationToken);
            if (!response.Mutation.Success)
            {
                return Rejected(
                    current,
                    response.Mutation.ErrorMessage ??
                    $"Participant recovery reset was {response.Mutation.Status.ToString().ToLowerInvariant()}.");
            }

            var updated = await client.LoadOwnerProjectionAsync(
                RequireCommission(current).CompanyId.Value,
                RequireCommission(current).CommissionId,
                cancellationToken);
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
            await ApplyProjectionAsync(updated);
            return new TradeCommissionOperatorResult(
                true,
                updated,
                RecoveryUrl: response.RecoveryUrl);
        }
        catch (Exception exception)
        {
            _errors[current.Order.Id] = exception.Message;
            return Rejected(current, exception.Message);
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
            var response = await client.IssueClaimLinkAsync(
                CreateContext(current, "issue-claim-link", payload: null),
                cancellationToken);
            ValidateCapabilityUrl(current, response.ClaimUrl, "claim");
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
        CancellationToken cancellationToken)
        where TCommand : ICompanyCommissionCommand
    {
        if (!CanPerformExternalAction(current.Order, out var reason))
        {
            return Rejected(current, reason);
        }

        try
        {
            ValidateProjection(current.Order, current);
            var context = CreateContext(current, route, payload);
            var response = await client.ExecuteAsync(
                route,
                createCommand(context),
                cancellationToken);
            var mutation = response.Mutation;
            if (!mutation.Success)
            {
                return Rejected(
                    current,
                    mutation.ErrorMessage ??
                    $"The commissioner command was {mutation.Status.ToString().ToLowerInvariant()}.");
            }

            var updated = await client.LoadOwnerProjectionAsync(
                RequireCommission(current).CompanyId.Value,
                RequireCommission(current).CommissionId,
                cancellationToken);
            ValidateProjection(current.Order, updated);
            if (updated.ObjectRevision.Value <= current.ObjectRevision.Value)
            {
                throw new InvalidOperationException(
                    "The commissioner command did not advance the authoritative order revision.");
            }

            if (!string.IsNullOrWhiteSpace(response.ClaimUrl))
            {
                ValidateCapabilityUrl(updated, response.ClaimUrl, "claim");
            }
            await ApplyProjectionAsync(updated);
            return new TradeCommissionOperatorResult(
                true,
                updated,
                ClaimUrl: response.ClaimUrl);
        }
        catch (Exception exception)
        {
            _errors[current.Order.Id] = exception.Message;
            return Rejected(current, exception.Message);
        }
    }

    private async Task ApplyProjectionAsync(CompanyCommissionOwnerProjection projection)
    {
        if (!await tradeOperations.ApplyCanonicalOrderAsync(projection.Order))
        {
            throw new InvalidOperationException(
                "The owner projection was authoritative, but browser storage could not apply its Trade order.");
        }

        await localState.SaveObjectRevisionAsync(
            ProfileSyncCollections.TradeOrders,
            projection.Order.Id.ToString("D"),
            projection.ObjectRevision.Value);
        _projections[projection.Order.Id] = projection;
        _errors.Remove(projection.Order.Id);
        appState.NotifyTradeOperationsDataChanged();
    }

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
        if (!profileSync.CurrentStatus.IsConnected)
        {
            reason = "Connect Profile Hosting in Options before operating this company commission.";
            return false;
        }

        if (!profileSync.CurrentStatus.HostReachable)
        {
            reason = profileSync.CurrentStatus.Message ?? "Profile Hosting is unavailable.";
            return false;
        }

        var objectId = order.Id.ToString("D");
        if (profileSync.PendingSaves.Any(item =>
                string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal) &&
                string.Equals(item.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)) ||
            profileSync.Conflicts.Any(item =>
                string.Equals(item.Collection, ProfileSyncCollections.TradeOrders, StringComparison.Ordinal) &&
                string.Equals(item.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Resolve the pending hosted order update before operating its commission.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static CompanyCommissionCommandContext CreateContext(
        CompanyCommissionOwnerProjection current,
        string route,
        object? payload)
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
            CompanyCommissionProtocol.Version1);
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

    private static TradeCompanyCommission RequireCommission(
        CompanyCommissionOwnerProjection projection) =>
        projection.Order.CompanyCommission ??
        throw new InvalidOperationException(
            "The authenticated owner projection does not contain a company commission.");

    private static TradeCommissionOperatorResult Rejected(
        CompanyCommissionOwnerProjection current,
        string message) =>
        new(false, current, message);

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
    string? ClaimUrl = null);
