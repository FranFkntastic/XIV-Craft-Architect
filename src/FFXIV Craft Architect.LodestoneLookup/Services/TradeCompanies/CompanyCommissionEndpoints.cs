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

public sealed record CompanyCommissionAccountClaimStanding(bool Eligible);

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
            "/owner-comparison",
            async (
                string companyId,
                CompanyCommissionOwnerComparisonRequest body,
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
                    return Results.Ok(await commissions.CompareOwnersAsync(
                        access,
                        body,
                        cancellationToken));
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new MembershipErrorResponse(
                        "invalid_owner_comparison",
                        exception.Message));
                }
            });

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

                var identity = await identities.LoadByProfileAsync(
                    account.ProfileId,
                    cancellationToken);
                if (identity == null)
                {
                    return ClaimRequiresAccount();
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
                    account.ProfileId,
                    identity == null
                        ? null
                        : new CompanyCommissionClaimAccountEvidence(
                            account.ProfileId,
                            identity.DiscordUserId,
                            identity.DisplayNameSnapshot));
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
                ProfileHostedTradeCompanyService companyService,
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
                    if (snapshot == null)
                    {
                        return MissingCanonicalCommission();
                    }
                    var profileObjectRevision = await companyService.MirrorOrderToGrantAsync(
                        access,
                        snapshot.Order,
                        cancellationToken);
                    return Results.Ok(new CompanyCommissionOwnerProjection
                    {
                        Order = snapshot.Order,
                        ObjectRevision = snapshot.Envelope.RecordRevision,
                        CompanyRevision = snapshot.CompanyRevision,
                        ProfileObjectRevision = profileObjectRevision
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
                ProfileHostedTradeCompanyService companyService,
                HostedCompanyCommissionService commissions,
                SqliteCompanyCommissionCapabilityStore capabilities,
                LegacyCrafterAccountResolver crafterAccounts,
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
                var profileObjectRevision = await companyService.MirrorOrderToGrantAsync(
                    access,
                    mutation.Order!,
                    cancellationToken);
                using var committedWork = new CancellationTokenSource(
                    TimeSpan.FromSeconds(15));
                var committedCancellationToken = committedWork.Token;
                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (command is ConfirmCompanyCommissionIdentityCommand)
                {
                    await crafterAccounts.BindConfirmedClaimCrafterAsync(
                        canonical.CompanyId,
                        canonical.ActiveClaim,
                        committedCancellationToken);
                }
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
                        ToCommittedProjection(mutation, profileObjectRevision),
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
                    return Results.Ok(ToOwnerResponse(
                        mutation,
                        profileObjectRevision,
                        claimUrl));
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
                    return Results.Ok(ToOwnerResponse(
                        mutation,
                        profileObjectRevision,
                        claimUrl));
                }

                return Results.Ok(ToOwnerResponse(mutation, profileObjectRevision));
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
                 SqliteDiscordIdentityStore identities,
                 HostedCompanyCommissionService commissions,
                 MembershipAccessResolver accessResolver,
                 ProfileHostedTradeCompanyService companyService,
                 SqliteMembershipStore memberships,
                 LegacyCrafterAccountResolver crafterAccounts,
                 CompanyHubService hubs,
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
                    if (capabilityKind == CompanyCommissionCapabilityKind.Claim &&
                        !string.IsNullOrWhiteSpace(request.Headers["X-Profile-Key"].ToString()))
                    {
                        return await ExecuteAccountClaimAsync(
                            publicId,
                            envelope,
                            request,
                            accessResolver,
                            companyService,
                            hubs,
                            memberships,
                            crafterAccounts,
                            identities,
                            claimContacts,
                            capabilities,
                            commissions,
                            timeProvider,
                            cancellationToken);
                    }
                    return await ExecuteMembershipParticipantAsync(
                        publicId,
                        route,
                        envelope,
                        request,
                        capabilityKind.Value,
                        accessResolver,
                        companyService,
                        memberships,
                        crafterAccounts,
                        commissions,
                        cancellationToken);
                }

                MembershipAccount? claimAccount = null;
                DiscordIdentityLink? claimIdentity = null;
                var suppliedProfileKey = request.Headers["X-Profile-Key"].ToString();
                if (capabilityKind == CompanyCommissionCapabilityKind.Claim &&
                    !string.IsNullOrWhiteSpace(suppliedProfileKey))
                {
                    claimAccount = await accessResolver.ResolveAccountAsync(
                        request,
                        cancellationToken);
                    if (claimAccount == null)
                    {
                        return Results.Unauthorized();
                    }

                    claimIdentity = await identities.LoadByProfileAsync(
                        claimAccount.ProfileId,
                        cancellationToken);
                    if (claimIdentity == null)
                    {
                        return Results.Json(
                            new
                            {
                                error = "verified_discord_identity_required",
                                message = "Sign in with a Discord-linked account before making a verified claim."
                            },
                            statusCode: StatusCodes.Status403Forbidden);
                    }
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

                // Claims are account-bound. A retry of a claim that already committed
                // replays exactly as recorded, so in-flight claims keep their outcome.
                var recordedClaim = capability.Kind == CompanyCommissionCapabilityKind.Claim
                    ? await commissions.LoadRecordedCommandAsync(
                        capability,
                        envelope.CommandId,
                        cancellationToken)
                    : null;
                if (capability.Kind == CompanyCommissionCapabilityKind.Claim &&
                    claimAccount == null &&
                    recordedClaim == null)
                {
                    return ClaimRequiresAccount();
                }

                var context = await commissions.CreateCapabilityCommandContextAsync(
                    capability,
                    envelope.ExpectedProjectionRevision,
                    envelope.CommandId,
                    envelope.ProtocolVersion,
                    cancellationToken,
                    claimAccount?.ProfileId,
                    ReadClaimTermsVersion(envelope.Command));
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
                if (command is ClaimCompanyCommissionCommand recordedRetry &&
                    recordedClaim != null)
                {
                    // A retry of a committed claim is rebuilt in the form that was
                    // recorded: the display name seen now may differ, and the fingerprint
                    // must match the original.
                    command = await RebuildRecordedClaimAsync(
                        recordedRetry,
                        recordedClaim,
                        capability.CompanyId,
                        crafterAccounts,
                        memberships,
                        cancellationToken);
                }
                else if (command is ClaimCompanyCommissionCommand claim &&
                    claimAccount != null &&
                    claimIdentity != null)
                {
                    // One crafter per person per company: reuse the account's existing
                    // company crafter instead of the browser-minted character ID.
                    var (existingCrafterId, confirmed) = await ResolveExactClaimCrafterAsync(
                            capability.CompanyId,
                            claimAccount.ProfileId,
                            claim.ClaimantCharacter?.LodestoneCharacterId,
                            crafterAccounts,
                            memberships,
                            cancellationToken);
                    command = BindClaimToAccount(
                        claim,
                        existingCrafterId,
                        new CompanyCommissionClaimAccountEvidence(
                            claimAccount.ProfileId,
                            claimIdentity.DiscordUserId,
                            claimIdentity.DisplayNameSnapshot),
                        confirmed);
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
                    if (claimAccount != null)
                    {
                        await claimContacts.CaptureMemberAsync(
                            claimAccount.ProfileId,
                            identities,
                            mutation,
                            committedCancellationToken);
                    }
                    else
                    {
                        await claimContacts.CaptureAsync(
                            capability,
                            mutation,
                            committedCancellationToken);
                    }
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

        // Participant authority belongs to the claiming account on any device: an
        // authenticated owner installs a fresh browser-generated credential for the
        // live grant without a bearer link or company membership.
        app.MapPost(
            "/xivdata/commission-briefs/{publicId}/participant-access",
            async (
                string publicId,
                CompanyCommissionParticipantAccessRequest body,
                HttpRequest request,
                MembershipAccessResolver accessResolver,
                ProfileHostedTradeCompanyService companyService,
                LegacyCrafterAccountResolver crafterAccounts,
                SqliteCompanyCommissionCapabilityStore capabilities,
                HostedCompanyCommissionService commissions,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(publicId) ||
                    publicId.Length > SqliteCompanyCommissionCapabilityStore
                        .MaximumPublicBriefIdLength)
                {
                    return Results.NotFound();
                }
                if (!IsValidParticipantCredential(body?.ParticipantCredential))
                {
                    return InvalidCommand(
                        "A bounded browser-generated participant credential is required.");
                }

                var account = await accessResolver.ResolveAccountAsync(
                    request,
                    cancellationToken);
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
                var canonical = await companyService.LoadPublicOrderAsync(
                    ownership,
                    publicId,
                    cancellationToken);
                var order = canonical?.Order;
                var commission = order?.CompanyCommission;
                if (order == null ||
                    commission == null ||
                    commission.CompanyId != ownership.CompanyId ||
                    commission.CommissionId != ownership.OrderId ||
                    !string.Equals(
                        commission.PublicMetadata.PublicBriefId,
                        publicId,
                        StringComparison.Ordinal))
                {
                    return MissingCanonicalCommission();
                }

                var activeClaim = commission.ActiveClaim;
                if (activeClaim == null ||
                    commission.ParticipantGrant is not { RevokedAtUtc: null } grant ||
                    grant.ClaimId != activeClaim.ClaimId ||
                    order.Status == TradeOrderStatus.Canceled ||
                    commission.PublicMetadata.ViewState !=
                        CompanyCommissionPublicViewState.Published ||
                    !await crafterAccounts.IsClaimOwnedByAccountAsync(
                        ownership.CompanyId,
                        activeClaim,
                        account.ProfileId,
                        cancellationToken))
                {
                    return ParticipantAccessDenied();
                }

                var resolution = await capabilities.InstallLinkedParticipantAsync(
                    ownership.CompanyId,
                    ownership.OrderId,
                    publicId,
                    grant.GrantId,
                    grant.CapabilityRevision,
                    body!.ParticipantCredential!,
                    timeProvider.GetUtcNow().UtcDateTime,
                    cancellationToken);
                if (resolution == null)
                {
                    return Results.Json(
                        new MembershipErrorResponse(
                            "participant_credential_revoked",
                            "This browser credential was revoked; a new one is required."),
                        statusCode: StatusCodes.Status409Conflict);
                }

                var participant = await commissions.LoadParticipantAsync(
                    resolution,
                    cancellationToken);
                return participant == null
                    ? CanonicalCommissionConflict()
                    : Results.Ok(participant);
            });

        // Lets the claim page offer an account claim only to accounts the claim command
        // would accept. Advisory; the command re-checks everything itself.
        app.MapGet(
            "/xivdata/commission-briefs/{publicId}/account-claim",
            async (
                string publicId,
                HttpRequest request,
                MembershipAccessResolver accessResolver,
                ProfileHostedTradeCompanyService companyService,
                CompanyHubService hubs,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(publicId) ||
                    publicId.Length > SqliteCompanyCommissionCapabilityStore
                        .MaximumPublicBriefIdLength)
                {
                    return Results.NotFound();
                }
                var account = await accessResolver.ResolveAccountAsync(
                    request,
                    cancellationToken);
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
                return Results.Ok(new CompanyCommissionAccountClaimStanding(
                    await hubs.CanClaimOpenCommissionsAsync(
                        account,
                        ownership.CompanyId,
                        cancellationToken)));
            });
    }

    // D8/K3: an account the Crafter hub shows this open commission to claims it with the
    // signed-in account as the authority instead of a claim link. Eligibility is checked
    // again on every attempt, so a direct call is refused once the account is no longer
    // a member or the company stops sharing open commissions with freelancers. The claim
    // itself is the same account-bound claim a link produces: Lodestone character,
    // account evidence, crafter reuse, and a device participant credential.
    private static async Task<IResult> ExecuteAccountClaimAsync(
        string publicId,
        PublicCompanyCommissionCommandEnvelope envelope,
        HttpRequest request,
        MembershipAccessResolver accessResolver,
        ProfileHostedTradeCompanyService companyService,
        CompanyHubService hubs,
        SqliteMembershipStore memberships,
        LegacyCrafterAccountResolver crafterAccounts,
        SqliteDiscordIdentityStore identities,
        DiscordClaimContactCommitter claimContacts,
        SqliteCompanyCommissionCapabilityStore capabilities,
        HostedCompanyCommissionService commissions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var account = await accessResolver.ResolveAccountAsync(request, cancellationToken);
        if (account == null)
        {
            return ClaimRequiresAccount();
        }
        var identity = await identities.LoadByProfileAsync(account.ProfileId, cancellationToken);
        if (identity == null)
        {
            return Results.Json(
                new
                {
                    error = "verified_discord_identity_required",
                    message = "Sign in with a Discord-linked account before making a verified claim."
                },
                statusCode: StatusCodes.Status403Forbidden);
        }
        if (envelope.CommandId == Guid.Empty ||
            envelope.ProtocolVersion != CompanyCommissionProtocol.Version1)
        {
            return InvalidCommand("The claim command identity or protocol is invalid.");
        }

        var ownership = await companyService.ResolvePublicationOwnershipAsync(
            publicId,
            cancellationToken);
        if (ownership == null)
        {
            return Results.NotFound();
        }
        var snapshot = await commissions.LoadPublishedAsync(
            ownership,
            publicId,
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

        // A retry of a claim that already committed replays as recorded, but only for the
        // account that made it.
        var recordedClaim = commission.ProcessedCommands
            .FirstOrDefault(item => item.CommandId == envelope.CommandId) is { } processed
            ? new RecordedCompanyCommissionCommand(processed.Fingerprint, commission.ActiveClaim)
            : null;
        if (recordedClaim != null)
        {
            if (recordedClaim.ActiveClaim?.AccountEvidence?.ProfileId != account.ProfileId)
            {
                return ParticipantAccessDenied();
            }
        }
        else
        {
            if (!await hubs.CanClaimOpenCommissionsAsync(
                    account,
                    ownership.CompanyId,
                    cancellationToken))
            {
                return Results.Json(
                    new MembershipErrorResponse(
                        "account_claim_not_allowed",
                        "This account cannot claim this company's commissions. Use the company's claim link."),
                    statusCode: StatusCodes.Status403Forbidden);
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
            if (commission.ClaimPriceReview?.IsRetry(account.ProfileId, envelope.CommandId, ReadClaimTermsVersion(envelope.Command)) != true &&
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
        }

        ICompanyCommissionParticipantCommand parsed;
        string? newParticipantCredential;
        try
        {
            (parsed, newParticipantCredential) = DeserializeParticipantCommand(
                "claim",
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
        if (parsed is not ClaimCompanyCommissionCommand claim)
        {
            return InvalidCommand("The public command route is not supported.");
        }
        if (!IsValidParticipantCredential(newParticipantCredential))
        {
            return InvalidCommand(
                "A bounded browser-generated participant credential is required.");
        }

        if (recordedClaim != null)
        {
            claim = (ClaimCompanyCommissionCommand)await RebuildRecordedClaimAsync(
                claim,
                recordedClaim,
                ownership.CompanyId,
                crafterAccounts,
                memberships,
                cancellationToken);
        }
        else
        {
            // One crafter per person per company: reuse the account's existing company
            // crafter instead of the browser-minted character ID.
            var (existingCrafterId, confirmed) = await ResolveExactClaimCrafterAsync(
                ownership.CompanyId,
                account.ProfileId,
                claim.ClaimantCharacter?.LodestoneCharacterId,
                crafterAccounts,
                memberships,
                cancellationToken);
            claim = BindClaimToAccount(
                claim,
                existingCrafterId,
                new CompanyCommissionClaimAccountEvidence(
                    account.ProfileId,
                    identity.DiscordUserId,
                    identity.DisplayNameSnapshot),
                confirmed);
        }

        var mutation = await commissions.ExecuteAccountClaimAsync(
            ownership,
            publicId,
            account.ProfileId,
            claim,
            cancellationToken);
        if (!mutation.Success)
        {
            if (mutation.Status == CompanyCommissionMutationStatus.Conflict &&
                mutation.ErrorCode != "claim_price_changed" &&
                (await commissions.LoadPublishedAsync(
                    ownership,
                    publicId,
                    cancellationToken))?.Order.CompanyCommission?.ActiveClaim != null)
            {
                return ClaimSlotTaken();
            }
            return ToCommandError(mutation);
        }

        var participant = mutation.Order?.CompanyCommission?.ParticipantGrant
            ?? throw new InvalidOperationException(
                "The account claim did not create an active participant grant.");
        using var committedWork = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var committedCancellationToken = committedWork.Token;
        await claimContacts.CaptureMemberAsync(
            account.ProfileId,
            identities,
            mutation,
            committedCancellationToken);
        var resolution = await capabilities.InstallLinkedParticipantAsync(
            ownership.CompanyId,
            ownership.OrderId,
            publicId,
            participant.GrantId,
            participant.CapabilityRevision,
            newParticipantCredential!,
            timeProvider.GetUtcNow().UtcDateTime,
            committedCancellationToken);
        if (resolution == null)
        {
            return Results.Json(
                new MembershipErrorResponse(
                    "participant_credential_revoked",
                    "This browser credential was revoked; a new one is required."),
                statusCode: StatusCodes.Status409Conflict);
        }
        var projection = await commissions.LoadParticipantAsync(
            resolution,
            committedCancellationToken);
        return projection == null
            ? CanonicalCommissionConflict()
            : Results.Ok(projection);
    }

    private static IResult ParticipantAccessDenied() =>
        Results.Json(
            new MembershipErrorResponse(
                "participant_access_denied",
                "This account does not hold the commission claim."),
            statusCode: StatusCodes.Status403Forbidden);

    private static async Task<IResult> ExecuteMembershipParticipantAsync(
        string publicId,
        string route,
        PublicCompanyCommissionCommandEnvelope envelope,
        HttpRequest request,
        CompanyCommissionCapabilityKind capabilityKind,
        MembershipAccessResolver accessResolver,
        ProfileHostedTradeCompanyService companyService,
        SqliteMembershipStore memberships,
        LegacyCrafterAccountResolver crafterAccounts,
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

        // Settlement attestations are made and retracted after delivery is accepted.
        var settlementAfterDelivery =
            route is "confirm-settlement-received" or "retract-settlement" &&
            snapshot.Order.Status == TradeOrderStatus.Completed;
        var activeClaim = commission.ActiveClaim;
        var participantIsLive =
            activeClaim != null &&
            await crafterAccounts.IsClaimOwnedByAccountAsync(
                ownership.CompanyId,
                activeClaim,
                account.ProfileId,
                cancellationToken) &&
            commission.ParticipantGrant is { RevokedAtUtc: null } participant &&
            participant.ClaimId == activeClaim.ClaimId &&
            (!TradeOrderStatusWorkflow.IsArchived(snapshot.Order.Status) || settlementAfterDelivery) &&
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
                    if (!body.TryGetProperty("claimIdentityVersion", out var version) ||
                        !version.TryGetInt32(out var identityVersion) || identityVersion != 2)
                    {
                        throw new JsonException(
                            "claim_identity_version_mismatch: This claim page uses a retired identity format. Reload the page after the release finishes; no claim was submitted.");
                    }
                    var payload = Deserialize<ClaimPayload>(body);
                    var character = payload.ClaimantCharacter == null
                        ? null
                        : SanitizeClaimantCharacter(
                            payload.ClaimantCharacter);
                    return (
                        new ClaimCompanyCommissionCommand(
                            context,
                            payload.TermsVersion,
                            character,
                            ExistingCrafterId: null),
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
                    var character = SanitizeClaimantCharacter(
                        Deserialize<IdentityPayload>(body).ClaimantCharacter);
                    return (
                        new SubmitCompanyCommissionIdentityCommand(context, character),
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
            case "record-delivery-handoff":
                {
                    var payload = Deserialize<DeliveryHandoffPayload>(body);
                    return (
                        new RecordCompanyCommissionDeliveryHandoffCommand(
                            context,
                            payload.Method,
                            payload.Recipient,
                            payload.Note),
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
            "record-delivery-handoff" or
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

    private static CompanyCommissionClaimantCharacter SanitizeClaimantCharacter(
        CompanyCommissionClaimantCharacter supplied)
    {
        if (supplied.ClaimantCharacterId == Guid.Empty ||
            !IsBounded(supplied.CharacterName, 64) ||
            !IsBounded(supplied.HomeWorld, 64) ||
            supplied.SubmittedAtUtc == default ||
            !IsLodestoneCandidate(
                supplied.LodestoneCharacterId,
                supplied.LodestoneProfileUrl))
        {
            throw new JsonException(
                "The claimant character identity or Lodestone evidence is invalid.");
        }

        return supplied with
        {
            CharacterName = supplied.CharacterName.Trim(),
            HomeWorld = supplied.HomeWorld.Trim(),
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
            error = message.StartsWith("claim_identity_version_mismatch:", StringComparison.Ordinal)
                ? "claim_identity_version_mismatch"
                : message.StartsWith("claimant_character_selection_required:", StringComparison.Ordinal)
                    ? "claimant_character_selection_required"
                    : "invalid_command",
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

    private static ClaimCompanyCommissionCommand BindClaimToAccount(
        ClaimCompanyCommissionCommand claim,
        Guid? existingCrafterId,
        CompanyCommissionClaimAccountEvidence evidence,
        bool confirmed = false) =>
        claim with
        {
            ExistingCrafterId = confirmed ? existingCrafterId : null,
            ClaimantCharacter = claim.ClaimantCharacter == null
                ? null
                : claim.ClaimantCharacter with
                {
                    ClaimantCharacterId = existingCrafterId ??
                        claim.ClaimantCharacter.ClaimantCharacterId
                },
            AccountEvidence = evidence
        };

    // Rebuild the account-bound command with the evidence it committed with. The
    // crafter is never taken from the record, so a retry carrying a different
    // claimant character still fails as a changed command; it is the browser's own ID
    // or the account's reused crafter as resolved for this retry. Anything else
    // replays as sent and the service reports the mismatch.
    private static async Task<ICompanyCommissionParticipantCommand> RebuildRecordedClaimAsync(
        ClaimCompanyCommissionCommand claim,
        RecordedCompanyCommissionCommand recorded,
        CompanyId companyId,
        LegacyCrafterAccountResolver crafterAccounts,
        SqliteMembershipStore memberships,
        CancellationToken cancellationToken)
    {
        if (HostedCompanyCommissionService.MatchesRecordedFingerprint(claim, recorded) ||
            recorded.ActiveClaim?.AccountEvidence is not { } evidence)
        {
            return claim;
        }

        var asSent = BindClaimToAccount(claim, null, evidence);
        if (HostedCompanyCommissionService.MatchesRecordedFingerprint(asSent, recorded) ||
            claim.ClaimantCharacter == null)
        {
            return asSent;
        }

        // Resolve the crafter the way the claim itself does. Claims committed before the
        // exact-character resolver shipped were bound by the account resolver, so that
        // result stays a candidate. Only a recorded-fingerprint match is ever returned.
        var (exact, _) = await ResolveExactClaimCrafterAsync(
            companyId,
            evidence.ProfileId,
            claim.ClaimantCharacter.LodestoneCharacterId,
            crafterAccounts,
            memberships,
            cancellationToken);
        var accountReused = await crafterAccounts.ResolveClaimantCrafterIdAsync(
            companyId,
            evidence.ProfileId,
            claim.ClaimantCharacter.LodestoneCharacterId,
            cancellationToken);
        // The claim may have been confirmed after it committed, so the current claim
        // cannot say which form was recorded; accept whichever form matches.
        foreach (var reused in new[] { exact, accountReused }.Distinct())
        {
            foreach (var confirmed in new[] { false, true })
            {
                var rebuilt = BindClaimToAccount(claim, reused, evidence, confirmed && reused.HasValue);
                if (HostedCompanyCommissionService.MatchesRecordedFingerprint(rebuilt, recorded))
                {
                    return rebuilt;
                }
            }
        }
        return claim;
    }

    private static IResult ClaimRequiresAccount() =>
        Results.Json(
            new MembershipErrorResponse(
                "claim_requires_account",
                "Sign in with Discord to claim this commission."),
            statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<(Guid? CrafterId, bool Confirmed)> ResolveExactClaimCrafterAsync(
        CompanyId companyId,
        Guid accountProfileId,
        string? lodestoneCharacterId,
        LegacyCrafterAccountResolver crafters,
        SqliteMembershipStore memberships,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lodestoneCharacterId))
        {
            return (null, false);
        }
        var bindings = await memberships.LoadCrafterBindingsAsync(companyId, cancellationToken);
        var matches = (await crafters.LoadCompanyCraftersAsync(companyId, cancellationToken))
            .Where(row => string.Equals(row.Crafter.LodestoneCharacterId?.Trim(),
                lodestoneCharacterId.Trim(), StringComparison.Ordinal))
            .Where(row => !bindings.Any(binding => binding.LegacyCrafterId == row.Crafter.Id &&
                binding.AccountProfileId != accountProfileId))
            .OrderByDescending(row => bindings.Any(binding =>
                binding.LegacyCrafterId == row.Crafter.Id && binding.AccountProfileId == accountProfileId))
            .ThenBy(row => row.Crafter.CreatedAtUtc)
            .ThenBy(row => row.Crafter.Id);
        var match = matches.FirstOrDefault();
        return match == null
            ? (null, false)
            : (match.Crafter.Id, bindings.Any(binding =>
                binding.LegacyCrafterId == match.Crafter.Id &&
                binding.AccountProfileId == accountProfileId &&
                binding.Evidence == CrafterAccountBindingEvidence.ConfirmedClaimReuse));
    }

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
            CompanyCommissionMutationStatus.Conflict when mutation.ErrorCode == "claim_price_changed" =>
                ClaimPriceChanged(mutation),
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

    private static int? ReadClaimTermsVersion(JsonElement command) =>
        command.ValueKind == JsonValueKind.Object && command.TryGetProperty("termsVersion", out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var version) ? version : null;

    internal static IResult ClaimPriceChanged(CompanyCommissionMutationResult mutation)
    {
        var commission = mutation.Order!.CompanyCommission!;
        var terms = commission.CurrentTerms;
        var previous = commission.TermsVersions.Single(t => t.Version == terms.Version - 1).Payment.Total;
        return Results.Conflict(new
        {
            error = mutation.ErrorCode,
            code = mutation.ErrorCode,
            message = mutation.ErrorMessage,
            previousPayment = previous,
            currentPayment = terms.Payment.Total,
            changePercent = previous == 0 ? (decimal?)null : (terms.Payment.Total - previous) / previous * 100m,
            termsVersion = terms.Version,
            projectionRevision = mutation.Activity!.CommissionRevision
        });
    }

    private static TradeCommissionOwnerCommandResponse ToOwnerResponse(
        CompanyCommissionMutationResult mutation,
        CompanyRecordRevision profileObjectRevision,
        string? claimUrl = null) =>
        new(
            mutation.Status,
            mutation.Order,
            mutation.Activity,
            mutation.ErrorCode,
            mutation.ErrorMessage,
            mutation.Success
                ? ToCommittedProjection(mutation, profileObjectRevision)
                : null,
            claimUrl);

    private static CompanyCommissionOwnerProjection ToCommittedProjection(
        CompanyCommissionMutationResult mutation,
        CompanyRecordRevision profileObjectRevision) =>
        new()
        {
            Order = mutation.Order ?? throw new InvalidOperationException(
                "A successful commission command returned no committed order."),
            ObjectRevision = mutation.ObjectRevision ?? throw new InvalidOperationException(
                "A successful commission command returned no committed object revision."),
            CompanyRevision = mutation.CompanyRevision ?? throw new InvalidOperationException(
                "A successful commission command returned no committed company revision."),
            ProfileObjectRevision = profileObjectRevision
        };

    private sealed record ClaimPayload(
        int TermsVersion,
        CompanyCommissionClaimantCharacter? ClaimantCharacter,
        string NewParticipantCredential);

    private sealed record RecoveryPayload(
        Guid RecoveryGrantId,
        string NewParticipantCredential);

    private sealed record ReasonPayload(string Reason);

    private sealed record IdentityPayload(
        CompanyCommissionClaimantCharacter ClaimantCharacter);

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

    private sealed record DeliveryHandoffPayload(
        CompanyCommissionDeliveryHandoffMethod Method,
        string? Recipient,
        string? Note);

    private sealed record CommentPayload(string Comment);

    private sealed record OptionalCommentPayload(string? Comment);
}

public sealed record CompanyCommissionParticipantAccessRequest(string? ParticipantCredential);
