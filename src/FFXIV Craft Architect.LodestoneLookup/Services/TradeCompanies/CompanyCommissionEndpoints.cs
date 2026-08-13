using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record PublicCompanyCommissionCommandEnvelope
{
    public required int ProtocolVersion { get; init; }
    public required string PublicBriefId { get; init; }
    public required long ExpectedProjectionRevision { get; init; }
    public required Guid CommandId { get; init; }
    public string? ParticipantCapability { get; init; }
    public string? ClaimCapability { get; init; }
    public string? RecoveryCapability { get; init; }
    public required JsonElement Command { get; init; }
}

public sealed record TradeCommissionRecoveryResetResponse(
    CompanyCommissionMutationResult Mutation,
    CompanyCommissionOwnerProjection Projection,
    string RecoveryUrl);

public sealed record TradeCommissionOwnerCommandResponse(
    CompanyCommissionMutationStatus Status,
    TradeOrder? Order,
    CompanyCommissionActivityEvent? Activity,
    string? ErrorCode,
    string? ErrorMessage,
    CompanyCommissionOwnerProjection? Projection,
    string? ClaimUrl = null);

public sealed record IssueCompanyCommissionClaimLinkRequest(
    CompanyCommissionCommandContext Context);

public sealed record IssueCompanyCommissionClaimLinkResponse(string ClaimUrl);

public static class CompanyCommissionEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static void MapCompanyCommissionEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/trade/v1/commissions/{commissionId:guid}/owner",
            async (
                Guid commissionId,
                HttpRequest request,
                MembershipAccessResolver accessResolver,
                TradeCompanyAuthorization authorization,
                ProfileHostedTradeCompanyService companyService,
                HostedCompanyCommissionService commissions,
                CancellationToken cancellationToken) =>
            {
                var account = await accessResolver.ResolveAccountAsync(
                    request,
                    cancellationToken);
                if (account == null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var companyId = await companyService.ResolveCommissionCompanyAsync(
                        commissionId,
                        cancellationToken);
                    if (companyId == null)
                    {
                        return MissingCanonicalCommission();
                    }

                    var access = await authorization.ResolveAsync(
                        account,
                        companyId.Value,
                        cancellationToken);
                    if (access == null)
                    {
                        return MissingCanonicalCommission();
                    }

                    var snapshot = await commissions.LoadOwnerAsync(
                        access,
                        commissionId,
                        cancellationToken);
                    return snapshot == null
                        ? MissingCanonicalCommission()
                        : Results.Ok(new CompanyCommissionOwnerProjection
                        {
                            Order = snapshot.Order,
                            ObjectRevision = snapshot.Envelope.RecordRevision,
                            CompanyRevision = snapshot.CompanyRevision
                        });
                }
                catch (DuplicateHostedObjectIdentityException)
                {
                    return MissingCanonicalCommission();
                }
                catch (InvalidOperationException)
                {
                    return MissingCanonicalCommission();
                }
            });

        var company = app.MapGroup(
            "/trade/v1/companies/{companyId}/commissions");

        company.MapPost(
            "/{commissionId:guid}/claim",
            async (
                string companyId,
                Guid commissionId,
                HttpRequest request,
                MembershipAccessResolver accessResolver,
                ProfileHostedTradeCompanyService companyService,
                SqliteMembershipStore memberships,
                HostedCompanyCommissionService commissions,
                DiscordClaimContactCommitter claimContacts,
                SqliteDiscordIdentityStore identities,
                CancellationToken cancellationToken) =>
            {
                var account = await accessResolver.ResolveAccountAsync(request, cancellationToken);
                if (account == null)
                {
                    return Results.Unauthorized();
                }
                if (!CompanyId.TryParse(companyId, out var parsedCompanyId) ||
                    await companyService.LoadPublicCompanyProfileAsync(
                        parsedCompanyId,
                        cancellationToken) == null)
                {
                    return Results.NotFound();
                }

                var membership = await memberships.LoadForAccountAsync(
                    parsedCompanyId,
                    account.ProfileId,
                    cancellationToken);
                if (membership is not { State: MembershipState.Active })
                {
                    return MembershipForbidden(membership);
                }

                var access = await accessResolver.ResolveCompanyAccessAsync(
                    account,
                    parsedCompanyId,
                    cancellationToken);
                if (access == null)
                {
                    return Results.NotFound();
                }
                var snapshot = await commissions.LoadMemberAsync(
                    access,
                    commissionId,
                    cancellationToken);
                if (snapshot?.Order.CompanyCommission is not { } commission)
                {
                    return MissingCanonicalCommission();
                }
                if (commission.ActiveClaim != null)
                {
                    return ClaimSlotTaken();
                }
                if (snapshot.Order.Status != TradeOrderStatus.ReadyToAssign ||
                    commission.PublicMetadata.ViewState !=
                        CompanyCommissionPublicViewState.Published ||
                    commission.PublicMetadata.IsTestFixture)
                {
                    return Results.Conflict(new MembershipErrorResponse(
                        "commission_not_open",
                        "Only an open, published commission can be claimed."));
                }

                var command = new ClaimCompanyCommissionCommand(
                    new CompanyCommissionCommandContext(
                        parsedCompanyId,
                        commissionId,
                        snapshot.Envelope.RecordRevision,
                        snapshot.CompanyRevision,
                        Guid.NewGuid(),
                        CompanyCommissionProtocol.Version1),
                    commission.CurrentTermsVersion,
                    null,
                    account.ProfileId);
                var mutation = await commissions.ExecuteMemberAsync(
                    access,
                    account.ProfileId,
                    command,
                    cancellationToken);
                if (!mutation.Success)
                {
                    if (mutation.Status == CompanyCommissionMutationStatus.Conflict &&
                        (await commissions.LoadMemberAsync(
                            access,
                            commissionId,
                            cancellationToken))?.Order.CompanyCommission?.ActiveClaim != null)
                    {
                        return ClaimSlotTaken();
                    }
                    return ToCommandError(mutation);
                }

                using var committedWork = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await claimContacts.CaptureMemberAsync(
                    account.ProfileId,
                    identities,
                    mutation,
                    committedWork.Token);
                var projection = await commissions.LoadMemberParticipantAsync(
                    access,
                    commissionId,
                    committedWork.Token);
                return projection == null
                    ? CanonicalCommissionConflict()
                    : Results.Ok(projection);
            });

        company.MapGet(
            "/{commissionId:guid}/owner",
            async (
                string companyId,
                Guid commissionId,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                HostedCompanyCommissionService commissions,
                CancellationToken cancellationToken) =>
            {
                var access = await authorization.ResolveAsync(
                    request,
                    companyId,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var snapshot = await commissions.LoadOwnerAsync(
                        access,
                        commissionId,
                        cancellationToken);
                    return snapshot == null
                        ? MissingCanonicalCommission()
                        : Results.Ok(new CompanyCommissionOwnerProjection
                        {
                            Order = snapshot.Order,
                            ObjectRevision = snapshot.Envelope.RecordRevision,
                            CompanyRevision = snapshot.CompanyRevision
                        });
                }
                catch (InvalidOperationException)
                {
                    return CanonicalCommissionConflict();
                }
            });

        company.MapPost(
            "/{commissionId:guid}/commands/{route}",
            async (
                string companyId,
                Guid commissionId,
                string route,
                JsonElement body,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                HostedCompanyCommissionService commissions,
                SqliteCompanyCommissionCapabilityStore capabilities,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                var access = await authorization.ResolveAsync(
                    request,
                    companyId,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                if (string.Equals(
                        route,
                        "issue-claim-link",
                        StringComparison.Ordinal))
                {
                    IssueCompanyCommissionClaimLinkRequest issue;
                    try
                    {
                        issue = Deserialize<IssueCompanyCommissionClaimLinkRequest>(body);
                    }
                    catch (JsonException exception)
                    {
                        return InvalidCommand(exception.Message);
                    }

                    var supplied = CanonicalizeOwnerContext(
                        issue.Context,
                        access.CompanyId,
                        commissionId);
                    var snapshot = await commissions.LoadOwnerAsync(
                        access,
                        commissionId,
                        cancellationToken);
                    if (snapshot == null)
                    {
                        return Results.NotFound();
                    }
                    if (supplied.CommandId == Guid.Empty ||
                        supplied.ProtocolVersion != CompanyCommissionProtocol.Version1 ||
                        supplied.ExpectedObjectRevision != snapshot.Envelope.RecordRevision ||
                        supplied.ExpectedCompanyRevision != snapshot.CompanyRevision)
                    {
                        return Results.Conflict(new
                        {
                            error = "revision_conflict",
                            message =
                                "The hosted commission changed before the claim link was issued."
                        });
                    }

                    var claimUrl = await IssueClaimUrlAsync(
                        snapshot.Order.CompanyCommission
                        ?? throw new InvalidOperationException(
                            "The canonical commission is unavailable."),
                        capabilities,
                        timeProvider,
                        cancellationToken);
                    return claimUrl == null
                        ? Results.Conflict(new
                        {
                            error = "claim_link_unavailable",
                            message =
                                "Only an open, published, unclaimed commission can issue a claim link."
                        })
                        : Results.Ok(
                            new IssueCompanyCommissionClaimLinkResponse(claimUrl));
                }

                ICompanyCommissionCompanyCommand command;
                try
                {
                    command = DeserializeOwnerCommand(
                        route,
                        body,
                        access.CompanyId,
                        commissionId);
                }
                catch (JsonException exception)
                {
                    return InvalidCommand(exception.Message);
                }

                var mutation = await commissions.ExecuteCompanyAsync(
                    access,
                    command,
                    cancellationToken);
                if (!mutation.Success)
                {
                    return ToCommandError(mutation);
                }

                var canonical = mutation.Order?.CompanyCommission
                    ?? throw new InvalidOperationException(
                        "The applied command did not return its canonical commission.");
                using var committedWork = new CancellationTokenSource(
                    TimeSpan.FromSeconds(15));
                var committedCancellationToken = committedWork.Token;
                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (command is ResetCompanyCommissionParticipantRecoveryCommand)
                {
                    var recovery = canonical.RecoveryGrant
                        ?? throw new InvalidOperationException(
                            "Participant recovery reset did not create a recovery grant.");
                    if (string.IsNullOrWhiteSpace(canonical.PublicMetadata.PublicUrl))
                    {
                        return CanonicalCommissionConflict();
                    }

                    var issued = await capabilities.IssueAsync(
                        canonical.CompanyId,
                        canonical.CommissionId,
                        canonical.PublicMetadata.PublicBriefId,
                        CompanyCommissionCapabilityKind.Recovery,
                        recovery.RecoveryGrantId,
                        recovery.RecoveryRevision,
                        now,
                        committedCancellationToken);
                    var recoveryUrl =
                        SqliteCompanyCommissionCapabilityStore.BuildFragmentUrl(
                            canonical.PublicMetadata.PublicUrl,
                            "recover",
                            $"{recovery.RecoveryGrantId:D}.{issued.PlaintextToken}");
                    return Results.Ok(new TradeCommissionRecoveryResetResponse(
                        mutation,
                        ToCommittedProjection(mutation),
                        recoveryUrl));
                }

                if (command is RejectCompanyCommissionClaimCommand)
                {
                    await RevokeParticipantAuthoritiesAsync(
                        capabilities,
                        canonical,
                        now,
                        committedCancellationToken);
                    var claimUrl = await IssueClaimUrlAsync(
                        canonical,
                        capabilities,
                        timeProvider,
                        committedCancellationToken);
                    return Results.Ok(ToOwnerResponse(mutation, claimUrl));
                }
                else if (command is CancelCompanyCommissionCommand or
                          RevokeCompanyCommissionPublicationCommand)
                {
                    foreach (var kind in Enum.GetValues<CompanyCommissionCapabilityKind>())
                    {
                        await capabilities.RevokeAllAsync(
                            canonical.CompanyId,
                            canonical.CommissionId,
                            kind,
                            now,
                            committedCancellationToken);
                    }
                }
                else if (command is ReopenCompanyCommissionCommand)
                {
                    await RevokeParticipantAuthoritiesAsync(
                        capabilities,
                        canonical,
                        now,
                        committedCancellationToken);
                    var claimUrl = await IssueClaimUrlAsync(
                        canonical,
                        capabilities,
                        timeProvider,
                        committedCancellationToken);
                    return Results.Ok(ToOwnerResponse(mutation, claimUrl));
                }

                return Results.Ok(ToOwnerResponse(mutation));
            });

        company.MapGet(
            "/migration-diagnostics",
            async (
                string companyId,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                CompanyCommissionMigrationDiagnostics diagnostics,
                CancellationToken cancellationToken) =>
            {
                var access = await authorization.ResolveAsync(
                    request,
                    companyId,
                    cancellationToken);
                if (access == null)
                {
                    return Results.Unauthorized();
                }

                return Results.Ok(diagnostics.Failures
                    .Where(item => item.CompanyId == access.CompanyId)
                    .ToArray());
            });

        app.MapPost(
            "/xivdata/commission-briefs/{publicId}/commands/{route}",
            async (
                string publicId,
                string route,
                PublicCompanyCommissionCommandEnvelope envelope,
                 HttpRequest request,
                 SqliteCompanyCommissionCapabilityStore capabilities,
                 DiscordClaimContactCommitter claimContacts,
                 HostedCompanyCommissionService commissions,
                 MembershipAccessResolver accessResolver,
                 ProfileHostedTradeCompanyService companyService,
                 SqliteMembershipStore memberships,
                 TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (!string.Equals(
                        publicId,
                        envelope.PublicBriefId,
                        StringComparison.Ordinal) ||
                    publicId.Length > SqliteCompanyCommissionCapabilityStore
                        .MaximumPublicBriefIdLength)
                {
                    return InvalidCommand(
                        "The route and command public brief identities do not match.");
                }

                var capabilityKind = RouteCapabilityKind(route);
                if (capabilityKind == null)
                {
                    return InvalidCommand("The public command route is not supported.");
                }

                var plaintextCapability = capabilityKind switch
                {
                    CompanyCommissionCapabilityKind.Claim =>
                        envelope.ClaimCapability,
                    CompanyCommissionCapabilityKind.Recovery =>
                        envelope.RecoveryCapability,
                    _ => envelope.ParticipantCapability
                };
                if (!SqliteCompanyCommissionCapabilityStore.IsValidCapability(
                        plaintextCapability))
                {
                    return await ExecuteMembershipParticipantAsync(
                        publicId,
                        route,
                        envelope,
                        request,
                        capabilityKind.Value,
                        accessResolver,
                        companyService,
                        memberships,
                        commissions,
                        cancellationToken);
                }

                var capability = await capabilities.ResolveForCommandAsync(
                    publicId,
                    capabilityKind.Value,
                    plaintextCapability!,
                    envelope.CommandId,
                    cancellationToken);
                if (capability == null)
                {
                    return Results.Unauthorized();
                }

                var context = await commissions.CreateCapabilityCommandContextAsync(
                    capability,
                    envelope.ExpectedProjectionRevision,
                    envelope.CommandId,
                    envelope.ProtocolVersion,
                    cancellationToken);
                if (context == null)
                {
                    return Results.Conflict(new
                    {
                        error = "projection_conflict",
                        message =
                            "The canonical commission changed before the public command was applied."
                    });
                }

                ICompanyCommissionParticipantCommand command;
                string? newParticipantCredential;
                try
                {
                    (command, newParticipantCredential) =
                        DeserializeParticipantCommand(
                            route,
                            envelope.Command,
                            context);
                }
                catch (JsonException exception)
                {
                    return InvalidCommand(exception.Message);
                }
                if ((command is ClaimCompanyCommissionCommand or
                         RedeemCompanyCommissionParticipantRecoveryCommand) &&
                    !IsValidParticipantCredential(newParticipantCredential))
                {
                    return InvalidCommand(
                        "A bounded browser-generated participant credential is required.");
                }

                var mutation = await commissions.ExecuteCapabilityAsync(
                    capability,
                    command,
                    cancellationToken);
                if (!mutation.Success)
                {
                    return ToCommandError(mutation);
                }

                var canonical = mutation.Order?.CompanyCommission
                    ?? throw new InvalidOperationException(
                        "The applied public command did not return its canonical commission.");
                using var committedWork = new CancellationTokenSource(
                    TimeSpan.FromSeconds(15));
                var committedCancellationToken = committedWork.Token;
                var now = timeProvider.GetUtcNow().UtcDateTime;
                CompanyCommissionCapabilityResolution participantResolution = capability;
                if (command is ClaimCompanyCommissionCommand)
                {
                    await claimContacts.CaptureAsync(
                        capability,
                        mutation,
                        committedCancellationToken);
                }

                if (command is ClaimCompanyCommissionCommand or
                    RedeemCompanyCommissionParticipantRecoveryCommand)
                {
                    var participant = canonical.ParticipantGrant
                        ?? throw new InvalidOperationException(
                            "The authority exchange did not create an active participant grant.");
                    participantResolution =
                        await capabilities.FinalizeAuthorityExchangeAsync(
                            capability,
                            plaintextCapability!,
                            envelope.CommandId,
                            participant.GrantId,
                            participant.CapabilityRevision,
                            newParticipantCredential!,
                            now,
                        committedCancellationToken);
                }

                if (command is ReleaseCompanyCommissionClaimCommand)
                {
                    await RevokeParticipantAuthoritiesAsync(
                        capabilities,
                        canonical,
                        now,
                        committedCancellationToken);
                    var publicProjection = await commissions.LoadPublicAsync(
                        publicId,
                        committedCancellationToken);
                    return publicProjection == null
                        ? CanonicalCommissionConflict()
                        : Results.Ok(publicProjection);
                }

                var participantProjection = await commissions.LoadParticipantAsync(
                    participantResolution,
                    committedCancellationToken);
                return participantProjection == null
                    ? CanonicalCommissionConflict()
                    : Results.Ok(participantProjection);
            });
    }

    private static async Task<IResult> ExecuteMembershipParticipantAsync(
        string publicId,
        string route,
        PublicCompanyCommissionCommandEnvelope envelope,
        HttpRequest request,
        CompanyCommissionCapabilityKind capabilityKind,
        MembershipAccessResolver accessResolver,
        ProfileHostedTradeCompanyService companyService,
        SqliteMembershipStore memberships,
        HostedCompanyCommissionService commissions,
        CancellationToken cancellationToken)
    {
        if (capabilityKind != CompanyCommissionCapabilityKind.Participant)
        {
            return Results.Unauthorized();
        }

        var account = await accessResolver.ResolveAccountAsync(request, cancellationToken);
        if (account == null)
        {
            return Results.Unauthorized();
        }
        var ownership = await companyService.ResolvePublicationOwnershipAsync(
            publicId,
            cancellationToken);
        if (ownership == null)
        {
            return Results.NotFound();
        }
        var membership = await memberships.LoadForAccountAsync(
            ownership.CompanyId,
            account.ProfileId,
            cancellationToken);
        if (membership is not { State: MembershipState.Active })
        {
            return MembershipForbidden(membership);
        }
        var access = await accessResolver.ResolveCompanyAccessAsync(
            account,
            ownership.CompanyId,
            cancellationToken);
        if (access == null)
        {
            return Results.NotFound();
        }
        var snapshot = await commissions.LoadMemberAsync(
            access,
            ownership.OrderId,
            cancellationToken);
        var commission = snapshot?.Order.CompanyCommission;
        if (snapshot == null ||
            commission == null ||
            !string.Equals(
                commission.PublicMetadata.PublicBriefId,
                publicId,
                StringComparison.Ordinal))
        {
            return MissingCanonicalCommission();
        }

        var activeClaim = commission.ActiveClaim;
        var participantIsLive =
            activeClaim?.CrafterId == account.ProfileId &&
            commission.ParticipantGrant is { RevokedAtUtc: null } participant &&
            participant.ClaimId == activeClaim.ClaimId &&
            snapshot.Order.Status != TradeOrderStatus.Canceled &&
            commission.PublicMetadata.ViewState == CompanyCommissionPublicViewState.Published;
        if (!participantIsLive)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var recordedReplay = commission.ProcessedCommands.Any(
            item => item.CommandId == envelope.CommandId);
        if (!recordedReplay &&
            (commission.Activity.LastOrDefault()?.CommissionRevision ?? 0) !=
                envelope.ExpectedProjectionRevision)
        {
            return Results.Conflict(new
            {
                error = "projection_conflict",
                message =
                    "The canonical commission changed before the public command was applied."
            });
        }

        ICompanyCommissionParticipantCommand command;
        try
        {
            (command, _) = DeserializeParticipantCommand(
                route,
                envelope.Command,
                new CompanyCommissionCommandContext(
                    ownership.CompanyId,
                    ownership.OrderId,
                    snapshot.Envelope.RecordRevision,
                    snapshot.CompanyRevision,
                    envelope.CommandId,
                    envelope.ProtocolVersion));
        }
        catch (JsonException exception)
        {
            return InvalidCommand(exception.Message);
        }

        var mutation = await commissions.ExecuteMemberAsync(
            access,
            account.ProfileId,
            command,
            cancellationToken);
        if (!mutation.Success)
        {
            return ToCommandError(mutation);
        }
        if (command is ReleaseCompanyCommissionClaimCommand)
        {
            var publicProjection = await commissions.LoadPublicAsync(
                publicId,
                cancellationToken);
            return publicProjection == null
                ? CanonicalCommissionConflict()
                : Results.Ok(publicProjection);
        }

        var participantProjection = await commissions.LoadMemberParticipantAsync(
            access,
            ownership.OrderId,
            cancellationToken);
        return participantProjection == null
            ? CanonicalCommissionConflict()
            : Results.Ok(participantProjection);
    }

    private static ICompanyCommissionCompanyCommand DeserializeOwnerCommand(
        string route,
        JsonElement body,
        CompanyId companyId,
        Guid commissionId)
    {
        ICompanyCommissionCompanyCommand command = route switch
        {
            "update-draft" => Deserialize<UpdateCompanyCommissionDraftCommand>(body),
            "amend-terms" => Deserialize<AmendCompanyCommissionTermsCommand>(body),
            "open" => Deserialize<OpenCompanyCommissionCommand>(body),
            "reject-claim" => Deserialize<RejectCompanyCommissionClaimCommand>(body),
            "confirm-identity" =>
                Deserialize<ConfirmCompanyCommissionIdentityCommand>(body),
            "decide-payment-policy" =>
                Deserialize<DecideCompanyCommissionPaymentPolicyChangeCommand>(body),
            "record-payment" =>
                Deserialize<RecordCompanyCommissionPaymentCommand>(body),
            "retract-payment" =>
                Deserialize<RetractCompanyCommissionPaymentAttestationCommand>(body),
            "mark-company-materials-ready" =>
                Deserialize<MarkCompanyCommissionMaterialsReadyCommand>(body),
            "add-comment" => Deserialize<AddCompanyCommissionCommentCommand>(body),
            "add-private-note" =>
                Deserialize<AddCompanyCommissionPrivateNoteCommand>(body),
            "return-to-work" =>
                Deserialize<ReturnCompanyCommissionToWorkCommand>(body),
            "accept-delivery" =>
                Deserialize<AcceptCompanyCommissionDeliveryCommand>(body),
            "record-settlement" =>
                Deserialize<RecordCompanyCommissionSettlementCommand>(body),
            "retract-settlement" =>
                Deserialize<RetractCompanyCommissionSettlementAttestationCommand>(body),
            "reset-participant-recovery" =>
                Deserialize<ResetCompanyCommissionParticipantRecoveryCommand>(body),
            "cancel" => Deserialize<CancelCompanyCommissionCommand>(body),
            "reopen" => Deserialize<ReopenCompanyCommissionCommand>(body),
            "revoke-publication" =>
                Deserialize<RevokeCompanyCommissionPublicationCommand>(body),
            _ => throw new JsonException("The commissioner command route is not supported.")
        };
        var context = CanonicalizeOwnerContext(
            command.Context,
            companyId,
            commissionId);
        return command switch
        {
            UpdateCompanyCommissionDraftCommand value =>
                value with { Context = context },
            AmendCompanyCommissionTermsCommand value =>
                value with { Context = context },
            OpenCompanyCommissionCommand value => value with { Context = context },
            RejectCompanyCommissionClaimCommand value =>
                value with { Context = context },
            ConfirmCompanyCommissionIdentityCommand value =>
                value with { Context = context },
            DecideCompanyCommissionPaymentPolicyChangeCommand value =>
                value with { Context = context },
            RecordCompanyCommissionPaymentCommand value =>
                value with { Context = context },
            RetractCompanyCommissionPaymentAttestationCommand value =>
                value with { Context = context },
            MarkCompanyCommissionMaterialsReadyCommand value =>
                value with { Context = context },
            AddCompanyCommissionCommentCommand value =>
                value with { Context = context },
            AddCompanyCommissionPrivateNoteCommand value =>
                value with { Context = context },
            ReturnCompanyCommissionToWorkCommand value =>
                value with { Context = context },
            AcceptCompanyCommissionDeliveryCommand value =>
                value with { Context = context },
            RecordCompanyCommissionSettlementCommand value =>
                value with { Context = context },
            RetractCompanyCommissionSettlementAttestationCommand value =>
                value with { Context = context },
            ResetCompanyCommissionParticipantRecoveryCommand value =>
                value with { Context = context },
            CancelCompanyCommissionCommand value => value with { Context = context },
            ReopenCompanyCommissionCommand value => value with { Context = context },
            RevokeCompanyCommissionPublicationCommand value =>
                value with { Context = context },
            _ => throw new JsonException("The commissioner command type is invalid.")
        };
    }

    private static (
        ICompanyCommissionParticipantCommand Command,
        string? NewParticipantCredential)
        DeserializeParticipantCommand(
            string route,
            JsonElement body,
            CompanyCommissionCommandContext context)
    {
        switch (route)
        {
            case "claim":
                {
                    var payload = Deserialize<ClaimPayload>(body);
                    var provisional = payload.ProvisionalCrafter == null
                        ? null
                        : SanitizeProvisionalCrafter(
                            payload.ProvisionalCrafter);
                    return (
                        new ClaimCompanyCommissionCommand(
                            context,
                            payload.TermsVersion,
                            provisional,
                            payload.ExistingCrafterId),
                        payload.NewParticipantCredential);
                }
            case "redeem-participant-recovery":
                {
                    var payload = Deserialize<RecoveryPayload>(body);
                    return (
                        new RedeemCompanyCommissionParticipantRecoveryCommand(
                            context,
                            payload.RecoveryGrantId),
                        payload.NewParticipantCredential);
                }
            case "release-claim":
                return (
                    new ReleaseCompanyCommissionClaimCommand(
                        context,
                        Deserialize<ReasonPayload>(body).Reason),
                    null);
            case "submit-identity":
                {
                    var provisional = SanitizeProvisionalCrafter(
                        Deserialize<IdentityPayload>(body).ProvisionalCrafter);
                    return (
                        new SubmitCompanyCommissionIdentityCommand(context, provisional),
                        null);
                }
            case "request-payment-policy-change":
                {
                    var payload = Deserialize<PaymentPolicyPayload>(body);
                    return (
                        new RequestCompanyCommissionPaymentPolicyChangeCommand(
                            context,
                            payload.RequestedSchedule,
                            payload.RequestedCustomTerms,
                            payload.Reason),
                        null);
                }
            case "acknowledge-terms":
                return (
                    new AcknowledgeCompanyCommissionTermsCommand(
                        context,
                        Deserialize<TermsPayload>(body).TermsVersion),
                    null);
            case "confirm-payment-received":
                {
                    var payload = Deserialize<PaymentReceiptPayload>(body);
                    return (
                        new ConfirmCompanyCommissionPaymentReceivedCommand(
                            context,
                            payload.TermsVersion,
                            payload.Note),
                        null);
                }
            case "retract-payment":
                return (
                    new RetractCompanyCommissionPaymentAttestationCommand(
                        context,
                        Deserialize<ReasonPayload>(body).Reason),
                    null);
            case "confirm-settlement-received":
                {
                    var payload = Deserialize<PaymentReceiptPayload>(body);
                    return (
                        new ConfirmCompanyCommissionSettlementReceivedCommand(
                            context,
                            payload.TermsVersion,
                            payload.Note),
                        null);
                }
            case "retract-settlement":
                return (
                    new RetractCompanyCommissionSettlementAttestationCommand(
                        context,
                        Deserialize<ReasonPayload>(body).Reason),
                    null);
            case "acknowledge-company-materials":
                return (
                    new AcknowledgeCompanyCommissionMaterialsCommand(
                        context,
                        Deserialize<MaterialsPayload>(body).Quantities),
                    null);
            case "report-progress":
                {
                    var payload = Deserialize<ProgressPayload>(body);
                    return (
                        new ReportCompanyCommissionProgressCommand(
                            context,
                            payload.Outputs,
                            payload.Comment),
                        null);
                }
            case "add-comment":
                return (
                    new AddCompanyCommissionCommentCommand(
                        context,
                        Deserialize<CommentPayload>(body).Comment),
                    null);
            case "declare-readiness":
                return (
                    new DeclareCompanyCommissionReadinessCommand(
                        context,
                        Deserialize<OptionalCommentPayload>(body).Comment),
                    null);
            case "withdraw-readiness":
                return (
                    new WithdrawCompanyCommissionReadinessCommand(
                        context,
                        Deserialize<ReasonPayload>(body).Reason),
                    null);
            default:
                throw new JsonException("The participant command route is not supported.");
        }
    }

    private static CompanyCommissionCapabilityKind? RouteCapabilityKind(string route) =>
        route switch
        {
            "claim" => CompanyCommissionCapabilityKind.Claim,
            "redeem-participant-recovery" =>
                CompanyCommissionCapabilityKind.Recovery,
            "release-claim" or
            "submit-identity" or
            "request-payment-policy-change" or
            "acknowledge-terms" or
            "confirm-payment-received" or
            "retract-payment" or
            "confirm-settlement-received" or
            "retract-settlement" or
            "acknowledge-company-materials" or
            "report-progress" or
            "add-comment" or
            "declare-readiness" or
            "withdraw-readiness" =>
                CompanyCommissionCapabilityKind.Participant,
            _ => null
        };

    private static CompanyCommissionCommandContext CanonicalizeOwnerContext(
        CompanyCommissionCommandContext supplied,
        CompanyId companyId,
        Guid commissionId) =>
        new(
            companyId,
            commissionId,
            supplied.ExpectedObjectRevision,
            supplied.ExpectedCompanyRevision,
            supplied.CommandId,
            supplied.ProtocolVersion);

    private static T Deserialize<T>(JsonElement body) =>
        body.Deserialize<T>(JsonOptions)
        ?? throw new JsonException("The command body is empty.");

    private static bool IsValidParticipantCredential(string? credential) =>
        SqliteCompanyCommissionCapabilityStore.IsValidCapability(credential);

    private static CompanyCommissionProvisionalCrafter SanitizeProvisionalCrafter(
        CompanyCommissionProvisionalCrafter supplied)
    {
        if (supplied.ProvisionalCrafterId == Guid.Empty ||
            !IsBounded(supplied.CharacterName, 64) ||
            !IsBounded(supplied.HomeWorld, 64) ||
            !IsBounded(supplied.ContactMethod, 32) ||
            !IsBounded(supplied.ContactValue, 256) ||
            supplied.SubmittedAtUtc == default ||
            !IsLodestoneCandidate(
                supplied.LodestoneCharacterId,
                supplied.LodestoneProfileUrl))
        {
            throw new JsonException(
                "The provisional crafter identity or Lodestone evidence is invalid.");
        }

        return supplied with
        {
            CharacterName = supplied.CharacterName.Trim(),
            HomeWorld = supplied.HomeWorld.Trim(),
            ContactMethod = supplied.ContactMethod.Trim(),
            ContactValue = supplied.ContactValue.Trim(),
            LodestoneCharacterId = supplied.LodestoneCharacterId!.Trim(),
            LodestoneProfileUrl = supplied.LodestoneProfileUrl!.Trim(),
            SubmittedAtUtc = supplied.SubmittedAtUtc.ToUniversalTime()
        };
    }

    private static bool IsBounded(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength;

    private static bool IsLodestoneCandidate(
        string? characterId,
        string? profileUrl)
    {
        if (!IsBounded(characterId, 32) ||
            characterId!.Any(character => !char.IsAsciiDigit(character)) ||
            !IsBounded(profileUrl, 512) ||
            !Uri.TryCreate(profileUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(string.Equals(
                  uri.Host,
                  "finalfantasyxiv.com",
                  StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(
                  ".finalfantasyxiv.com",
                  StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return uri.AbsolutePath.Contains(
            $"/lodestone/character/{characterId!.Trim()}/",
            StringComparison.Ordinal);
    }

    private static async Task RevokeParticipantAuthoritiesAsync(
        SqliteCompanyCommissionCapabilityStore capabilities,
        TradeCompanyCommission commission,
        DateTime revokedAtUtc,
        CancellationToken cancellationToken)
    {
        await capabilities.RevokeAllAsync(
            commission.CompanyId,
            commission.CommissionId,
            CompanyCommissionCapabilityKind.Participant,
            revokedAtUtc,
            cancellationToken);
        await capabilities.RevokeAllAsync(
            commission.CompanyId,
            commission.CommissionId,
            CompanyCommissionCapabilityKind.Recovery,
            revokedAtUtc,
            cancellationToken);
    }

    private static async Task<string?> IssueClaimUrlAsync(
        TradeCompanyCommission commission,
        SqliteCompanyCommissionCapabilityStore capabilities,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (commission.PublicMetadata.ViewState !=
                CompanyCommissionPublicViewState.Published ||
            commission.PublicMetadata.IsTestFixture ||
            string.IsNullOrWhiteSpace(commission.PublicMetadata.PublicUrl) ||
            commission.ActiveClaimCapabilityRevision <= 0)
        {
            return null;
        }

        var issued = await capabilities.IssueAsync(
            commission.CompanyId,
            commission.CommissionId,
            commission.PublicMetadata.PublicBriefId,
            CompanyCommissionCapabilityKind.Claim,
            grantId: null,
            commission.ActiveClaimCapabilityRevision,
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);
        return SqliteCompanyCommissionCapabilityStore.BuildFragmentUrl(
            commission.PublicMetadata.PublicUrl,
            "claim",
            issued.PlaintextToken);
    }

    private static IResult InvalidCommand(string message) =>
        Results.BadRequest(new
        {
            error = "invalid_command",
            message
        });

    private static IResult MembershipForbidden(CompanyMembership? membership) =>
        Results.Json(
            new MembershipErrorResponse(
                membership == null ? "active_membership_required" : "membership_inactive",
                membership == null
                    ? "An active company membership is required."
                    : "The company membership is not active."),
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult ClaimSlotTaken() =>
        Results.Conflict(new MembershipErrorResponse(
            "claim_slot_taken",
            "The commission already has an active crafter."));

    private static IResult CanonicalCommissionConflict() =>
        Results.Conflict(new
        {
            error = "canonical_commission_invalid",
            message =
                "The hosted Trade order does not contain a valid canonical commission."
        });

    private static IResult MissingCanonicalCommission() =>
        Results.NotFound(new
        {
            error = "commission_missing",
            message = "The hosted canonical commission no longer exists."
        });

    private static IResult ToCommandError(CompanyCommissionMutationResult mutation) =>
        mutation.Status switch
        {
            CompanyCommissionMutationStatus.Conflict => Results.Conflict(new
            {
                error = mutation.ErrorCode,
                message = mutation.ErrorMessage
            }),
            CompanyCommissionMutationStatus.Unauthorized => Results.Unauthorized(),
            CompanyCommissionMutationStatus.NotFound => Results.NotFound(new
            {
                error = mutation.ErrorCode,
                message = mutation.ErrorMessage
            }),
            _ => Results.BadRequest(new
            {
                error = mutation.ErrorCode,
                message = mutation.ErrorMessage
            })
        };

    private static TradeCommissionOwnerCommandResponse ToOwnerResponse(
        CompanyCommissionMutationResult mutation,
        string? claimUrl = null) =>
        new(
            mutation.Status,
            mutation.Order,
            mutation.Activity,
            mutation.ErrorCode,
            mutation.ErrorMessage,
            mutation.Success ? ToCommittedProjection(mutation) : null,
            claimUrl);

    private static CompanyCommissionOwnerProjection ToCommittedProjection(
        CompanyCommissionMutationResult mutation) =>
        new()
        {
            Order = mutation.Order ?? throw new InvalidOperationException(
                "A successful commission command returned no committed order."),
            ObjectRevision = mutation.ObjectRevision ?? throw new InvalidOperationException(
                "A successful commission command returned no committed object revision."),
            CompanyRevision = mutation.CompanyRevision ?? throw new InvalidOperationException(
                "A successful commission command returned no committed company revision.")
        };

    private sealed record ClaimPayload(
        int TermsVersion,
        CompanyCommissionProvisionalCrafter? ProvisionalCrafter,
        Guid? ExistingCrafterId,
        string NewParticipantCredential);

    private sealed record RecoveryPayload(
        Guid RecoveryGrantId,
        string NewParticipantCredential);

    private sealed record ReasonPayload(string Reason);

    private sealed record IdentityPayload(
        CompanyCommissionProvisionalCrafter ProvisionalCrafter);

    private sealed record PaymentPolicyPayload(
        CompanyCommissionPaymentSchedule RequestedSchedule,
        string? RequestedCustomTerms,
        string Reason);

    private sealed record TermsPayload(int TermsVersion);

    private sealed record PaymentReceiptPayload(int TermsVersion, string Note);

    private sealed record MaterialsPayload(
        IReadOnlyList<CompanyCommissionMaterialQuantity> Quantities);

    private sealed record ProgressPayload(
        IReadOnlyList<CompanyCommissionProgressQuantity> Outputs,
        string? Comment);

    private sealed record CommentPayload(string Comment);

    private sealed record OptionalCommentPayload(string? Comment);
}
