using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

public static class CommissionBriefEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapCommissionBriefEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/xivdata/commission-briefs");

        group.MapPost(
            "/",
            async (
                HttpContext context,
                CommissionBriefCreateRequest request,
                CommissionBriefOptions options,
                SqliteCommissionBriefStore store,
                CancellationToken ct) =>
            {
                if (!IsAvailable(context, options))
                {
                    return Results.NotFound();
                }

                if (request.Ownership != null)
                {
                    return Results.BadRequest(new
                    {
                        error = "canonical_company_ownership_required",
                        message = "Company-owned publications require the authenticated Trade Company API."
                    });
                }

                var validationError = CommissionBriefValidator.Validate(request.Brief);
                if (validationError != null)
                {
                    return Results.BadRequest(new { error = validationError });
                }

                var created = await store.CreateAsync(request.Brief, request.Ownership, ct);
                if (!options.TryBuildPublicUrl(created.Published.PublicId, out var publicUrl))
                {
                    await store.RevokeAsync(
                        created.Published.PublicId,
                        created.EditorToken,
                        ct);
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Commission links are unavailable.",
                        detail: "The canonical commission page URL is not configured safely.");
                }

                return Results.Ok(new CommissionBriefCreateResponse
                {
                    PublicId = created.Published.PublicId,
                    PublicUrl = publicUrl,
                    EditorToken = created.EditorToken,
                    Version = created.Published.Version,
                    PublishedAtUtc = created.Published.PublishedAtUtc
                });
            });

        group.MapGet(
            "/{publicId}",
            async (
                HttpContext context,
                string publicId,
                CommissionBriefOptions options,
                SqliteCommissionBriefStore store,
                SqliteCompanyCommissionCapabilityStore capabilities,
                HostedCompanyCommissionService commissions,
                CancellationToken ct) =>
            {
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.Vary = "X-Commission-Participant";

                if (!IsAvailable(context, options) || !IsValidPublicId(publicId))
                {
                    return Results.NotFound();
                }

                var brief = await store.LoadAsync(publicId, ct);
                if (brief == null)
                {
                    return Results.NotFound();
                }
                if (brief.Ownership == null)
                {
                    SetProjectionTag(context.Response, brief);
                    return Results.Ok(brief);
                }

                try
                {
                    var participantToken = context.Request.Headers[
                        "X-Commission-Participant"].ToString();
                    if (!string.IsNullOrWhiteSpace(participantToken))
                    {
                        var capability = await capabilities.ResolveAsync(
                            publicId,
                            CompanyCommissionCapabilityKind.Participant,
                            participantToken,
                            ct);
                        if (capability == null)
                        {
                            return Results.Unauthorized();
                        }

                        var participant = await commissions.LoadParticipantAsync(
                            capability,
                            ct);
                        if (participant != null)
                        {
                            SetProjectionTag(context.Response, participant);
                        }
                        return participant == null
                            ? Results.Unauthorized()
                            : Results.Ok(participant);
                    }

                    var projection = await commissions.LoadPublicAsync(publicId, ct);
                    if (projection != null)
                    {
                        SetProjectionTag(context.Response, projection);
                    }
                    return projection == null
                        ? Results.Conflict(new
                        {
                            error = "canonical_commission_unavailable",
                            message = "The company publication has not completed canonical migration."
                        })
                        : Results.Ok(projection);
                }
                catch (InvalidOperationException)
                {
                    return Results.Conflict(new
                    {
                        error = "canonical_commission_invalid",
                        message = "The company publication has conflicting canonical ownership."
                    });
                }
            });

        group.MapGet("/{publicId}/stream", StreamProjectionAsync);

        group.MapGet(
            "/{publicId}/link",
            async (
                HttpContext context,
                string publicId,
                CommissionBriefOptions options,
                SqliteCommissionBriefStore store,
                CancellationToken ct) =>
            {
                if (!IsAvailable(context, options) || !IsValidPublicId(publicId))
                {
                    return Results.NotFound();
                }

                var brief = await store.LoadAsync(publicId, ct);
                if (brief == null)
                {
                    return Results.NotFound();
                }

                if (!options.TryBuildPublicUrl(publicId, out var publicUrl))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Commission links are unavailable.",
                        detail: "The canonical commission page URL is not configured safely.");
                }

                return Results.Ok(new CommissionBriefLinkResponse
                {
                    PublicId = publicId,
                    PublicUrl = publicUrl,
                    Version = brief.Version,
                    PublishedAtUtc = brief.PublishedAtUtc
                });
            });

        group.MapDelete(
            "/{publicId}",
            async (
                HttpContext context,
                string publicId,
                CommissionBriefOptions options,
                SqliteCommissionBriefStore store,
                CommissionProjectionChangeSignal changeSignal,
                CancellationToken ct) =>
            {
                if (!IsAvailable(context, options) || !IsValidPublicId(publicId))
                {
                    return Results.NotFound();
                }

                var token = context.Request.Headers["X-Commission-Editor"].ToString();
                if (string.IsNullOrWhiteSpace(token))
                {
                    return Results.Unauthorized();
                }

                if (!await store.RevokeAsync(publicId, token, ct))
                {
                    return Results.Unauthorized();
                }

                var publications = context.RequestServices
                    .GetRequiredService<DiscordPublicationService>();
                try
                {
                    await publications.RevokeAsync(publicId, ct);
                }
                finally
                {
                    changeSignal.Publish(publicId);
                }

                return Results.NoContent();
            });
    }

    public static void MapCompanyCommissionBriefEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(
            "/trade/v1/companies/{companyId}/commission-briefs");

        group.MapPost(
            "/",
            async (
                string companyId,
                CompanyCommissionBriefCreateRequest request,
                HttpRequest httpRequest,
                CommissionBriefOptions options,
                TradeCompanyAuthorization authorization,
                ProfileHostedTradeCompanyService companies,
                SqliteCommissionBriefStore store,
                SqliteCompanyCommissionCapabilityStore capabilities,
                TimeProvider timeProvider,
                CancellationToken ct) =>
            {
                if (!options.Enabled)
                {
                    return Results.NotFound();
                }

                var access = await authorization.ResolveAsync(
                    httpRequest,
                    companyId,
                    ct);
                if (access == null)
                {
                    return Results.Unauthorized();
                }
                if (access.Role is not (TradeCompanyRole.Operator or TradeCompanyRole.Owner))
                {
                    return Results.Forbid();
                }

                if (request.OrderId == Guid.Empty ||
                    request.OrderRevision.Value <= 0 ||
                    string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
                    request.IdempotencyKey.Length > 120)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_publication",
                        message = "A revisioned order and bounded idempotency key are required."
                    });
                }

                var validationError = CommissionBriefValidator.Validate(request.Brief);
                if (validationError != null)
                {
                    return Results.BadRequest(new { error = validationError });
                }

                var ownership = new TradeCompanyPublicationOwnership(
                    access.CompanyId,
                    request.OrderId,
                    request.OrderRevision);
                var publicId = SqliteCommissionBriefStore.CreateCompanyPublicId(
                    ownership,
                    request.IdempotencyKey);
                if (!options.TryBuildPublicUrl(publicId, out var publicUrl))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Commission links are unavailable.",
                        detail: "The canonical commission page URL is not configured safely.");
                }

                var orderRecord = await companies.LoadRecordAsync(
                    access,
                    TradeCompanyRecordKinds.Order,
                    request.OrderId.ToString("D"),
                    ct);
                if (orderRecord == null)
                {
                    return Results.NotFound(new
                    {
                        error = "canonical_order_missing",
                        message = "The canonical Trade order is unavailable."
                    });
                }

                TradeOrder order;
                try
                {
                    order = JsonSerializer.Deserialize<TradeOrder>(
                            orderRecord.PayloadJson,
                            JsonOptions)
                        ?? throw new JsonException("The canonical Trade order is empty.");
                }
                catch (JsonException)
                {
                    return Results.Conflict(new
                    {
                        error = "canonical_order_invalid",
                        message = "The canonical Trade order could not be read."
                    });
                }

                var alreadyBound = PublicationIdentityMatches(
                    order.CommissionPublication,
                    publicId,
                    ownership);
                if (alreadyBound && order.CommissionPublication?.RevokedAtUtc != null)
                {
                    return Results.Conflict(new
                    {
                        error = "publication_revoked",
                        message = "The canonical Trade order publication was already revoked."
                    });
                }
                if (orderRecord.RecordRevision != request.OrderRevision && !alreadyBound)
                {
                    return Results.Conflict(new
                    {
                        error = "order_revision_conflict",
                        message = "The canonical Trade order changed before publication."
                    });
                }

                if (!alreadyBound)
                {
                    try
                    {
                        TradeCompanyCommissionMigrationService
                            .RequireCanonicalBriefMatchesCurrentTerms(order, request.Brief);
                    }
                    catch (InvalidOperationException exception)
                    {
                        return Results.Conflict(new
                        {
                            error = "canonical_terms_conflict",
                            message = exception.Message
                        });
                    }
                }

                PublishedCommissionBrief published;
                try
                {
                    published = await store.CreateCompanyOwnedAsync(
                        request.Brief,
                        ownership,
                        request.IdempotencyKey,
                        ct);
                }
                catch (InvalidOperationException exception)
                {
                    return Results.Conflict(new
                    {
                        error = "publication_conflict",
                        message = exception.Message
                    });
                }

                TradeCompanyMutationResult? ownershipMutation = null;
                var ownershipVerified = false;
                try
                {
                    ownershipMutation = await companies.PutRecordAsync(
                        access,
                        TradeCompanyRecordKinds.Publication,
                        published.PublicId,
                        JsonSerializer.Serialize(ownership, JsonOptions),
                        CompanyRecordRevision.None,
                        $"portable-publication:{request.IdempotencyKey}",
                        ct);
                    ownershipVerified = ownershipMutation.Success ||
                        await companies.ResolvePublicationOwnershipAsync(
                            published.PublicId,
                            ct) == ownership;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    ownershipVerified = false;
                }

                if (!ownershipVerified)
                {
                    await store.DiscardCompanyOwnedAsync(
                        published.PublicId,
                        ownership,
                        ct);
                    return Results.Conflict(new
                    {
                        error = ownershipMutation?.ErrorCode ??
                            "publication_ownership_conflict",
                        message = ownershipMutation?.ErrorMessage ??
                            "Canonical publication ownership could not be recorded."
                    });
                }

                TradeCompanyRecordEnvelope committedOrder = orderRecord;
                string? claimUrl = null;
                if (!PublicationMatches(
                        order.CommissionPublication,
                        published,
                        publicUrl,
                        ownership) ||
                    order.CompanyCommission == null ||
                    order.CompanyCommission.PublicMetadata.ViewState !=
                    CompanyCommissionPublicViewState.Published)
                {
                    var publishedOrder = TradeOrderWorkflow.CopyOrder(order);
                    publishedOrder.CommissionPublication = new TradeCommissionPublication
                    {
                        PublicId = published.PublicId,
                        PublicUrl = publicUrl,
                        Version = published.Version,
                        PublishedAtUtc = published.PublishedAtUtc,
                        IsTestFixture = published.Brief.IsTestFixture,
                        Ownership = ownership
                    };
                    if (!alreadyBound)
                    {
                        publishedOrder.UpdatedAtUtc = published.PublishedAtUtc;
                        publishedOrder.History = publishedOrder.History
                            .Append(new TradeOrderHistoryEvent
                            {
                                Id = Guid.NewGuid(),
                                CompanyProfileId = publishedOrder.CompanyProfileId,
                                OrderId = publishedOrder.Id,
                                Kind = TradeOrderHistoryEventKind.CommissionPublished,
                                Note = $"Published portable commission brief v{published.Version}.",
                                CreatedAtUtc = published.PublishedAtUtc
                            })
                            .ToArray();
                    }

                    publishedOrder = TradeCompanyCommissionMigrationService.BindPublishedBrief(
                        publishedOrder,
                        published,
                        published.PublishedAtUtc);
                    var canonicalCommission = publishedOrder.CompanyCommission!;
                    if (!canonicalCommission.PublicMetadata.IsTestFixture &&
                        canonicalCommission.ActiveClaimCapabilityRevision <= 0)
                    {
                        publishedOrder.CompanyCommission = canonicalCommission with
                        {
                            ActiveClaimCapabilityRevision = 1
                        };
                    }

                    var expectedCompanyRevision = await companies.LoadCompanyRevisionAsync(
                        access,
                        ct);
                    var orderMutation = await companies.PutRecordAsync(
                        access,
                        TradeCompanyRecordKinds.Order,
                        request.OrderId.ToString("D"),
                        JsonSerializer.Serialize(publishedOrder, JsonOptions),
                        orderRecord.RecordRevision,
                        $"portable-publication-order:{request.IdempotencyKey}",
                        ct,
                        expectedCompanyRevision);
                    if (!orderMutation.Success || orderMutation.Record == null)
                    {
                        var current = await companies.LoadRecordAsync(
                            access,
                            TradeCompanyRecordKinds.Order,
                            request.OrderId.ToString("D"),
                            ct);
                        if (!TryReadMatchingOrder(
                                current,
                                published,
                                publicUrl,
                                ownership,
                                out committedOrder))
                        {
                            await store.DiscardCompanyOwnedAsync(
                                published.PublicId,
                                ownership,
                                ct);
                            return Results.Conflict(new
                            {
                                error = orderMutation.ErrorCode ?? "order_revision_conflict",
                                message = orderMutation.ErrorMessage ??
                                    "The canonical Trade order changed before publication completed."
                            });
                        }
                    }
                    else
                    {
                        committedOrder = orderMutation.Record;
                    }
                }

                var committedTradeOrder = JsonSerializer.Deserialize<TradeOrder>(
                        committedOrder.PayloadJson,
                        JsonOptions)
                    ?? throw new InvalidOperationException(
                        "The committed publication has no authoritative Trade order.");
                var committedCanonical = committedTradeOrder.CompanyCommission
                    ?? throw new InvalidOperationException(
                        "The committed publication has no canonical commission.");
                if (committedCanonical.ActiveClaim == null &&
                    !committedCanonical.PublicMetadata.IsTestFixture &&
                    committedCanonical.PublicMetadata.ViewState ==
                    CompanyCommissionPublicViewState.Published &&
                    committedCanonical.ActiveClaimCapabilityRevision > 0)
                {
                    var issuedClaimCapability = await capabilities.IssueAsync(
                        access.CompanyId,
                        request.OrderId,
                        published.PublicId,
                        CompanyCommissionCapabilityKind.Claim,
                        grantId: null,
                        committedCanonical.ActiveClaimCapabilityRevision,
                        timeProvider.GetUtcNow().UtcDateTime,
                        ct);
                    claimUrl = SqliteCompanyCommissionCapabilityStore.BuildFragmentUrl(
                        publicUrl,
                        "claim",
                        issuedClaimCapability.PlaintextToken);
                }

                var profileOrderRevision = await companies.MirrorOrderToGrantAsync(
                    access,
                    committedTradeOrder,
                    ct);
                return Results.Ok(new CommissionBriefCreateResponse
                {
                    PublicId = published.PublicId,
                    PublicUrl = publicUrl,
                    ClaimUrl = claimUrl,
                    EditorToken = string.Empty,
                    Version = published.Version,
                    PublishedAtUtc = published.PublishedAtUtc,
                    OrderRecord = committedOrder,
                    ProfileOrderRevision = profileOrderRevision
                });
            });

        group.MapDelete(
            "/{publicId}",
            async (
                string companyId,
                string publicId,
                HttpRequest request,
                TradeCompanyAuthorization authorization,
                ProfileHostedTradeCompanyService companies,
                SqliteCommissionBriefStore store,
                HostedCompanyCommissionService commissions,
                SqliteCompanyCommissionCapabilityStore capabilities,
                DiscordPublicationService discordPublications,
                TimeProvider timeProvider,
                CancellationToken ct) =>
            {
                var access = await authorization.ResolveAsync(
                    request,
                    companyId,
                    ct);
                if (access == null)
                {
                    return Results.Unauthorized();
                }
                if (access.Role is not (TradeCompanyRole.Operator or TradeCompanyRole.Owner))
                {
                    return Results.Forbid();
                }
                if (!IsValidPublicId(publicId))
                {
                    return Results.NotFound();
                }

                var ownership = await companies.ResolvePublicationOwnershipAsync(
                    publicId,
                    ct);
                if (ownership == null || ownership.CompanyId != access.CompanyId)
                {
                    return Results.NotFound();
                }

                var snapshot = await commissions.LoadOwnerAsync(
                    access,
                    ownership.OrderId,
                    ct);
                if (snapshot == null ||
                    !string.Equals(
                        snapshot.Order.CompanyCommission?.PublicMetadata.PublicBriefId,
                        publicId,
                        StringComparison.Ordinal))
                {
                    return Results.Conflict(new
                    {
                        error = "canonical_commission_unavailable",
                        message = "The publication is not bound to one canonical commission."
                    });
                }

                var commandId = Guid.NewGuid();
                var context = new CompanyCommissionCommandContext(
                    access.CompanyId,
                    ownership.OrderId,
                    snapshot.Envelope.RecordRevision,
                    snapshot.CompanyRevision,
                    commandId,
                    CompanyCommissionProtocol.Version1);
                var mutation = await commissions.ExecuteCompanyAsync(
                    access,
                    new RevokeCompanyCommissionPublicationCommand(context),
                    ct);
                if (!mutation.Success)
                {
                    return mutation.Status == CompanyCommissionMutationStatus.Conflict
                        ? Results.Conflict(new
                        {
                            error = mutation.ErrorCode,
                            message = mutation.ErrorMessage
                        })
                        : Results.BadRequest(new
                        {
                            error = mutation.ErrorCode,
                            message = mutation.ErrorMessage
                        });
                }

                await capabilities.RevokeAllAsync(
                    access.CompanyId,
                    ownership.OrderId,
                    CompanyCommissionCapabilityKind.Claim,
                    timeProvider.GetUtcNow().UtcDateTime,
                    ct);
                await store.RevokeCompanyOwnedAsync(publicId, access.CompanyId, ct);
                await discordPublications.RevokeAsync(publicId, ct);
                return Results.NoContent();
            });
    }

    private static async Task StreamProjectionAsync(
        HttpContext context,
        string publicId,
        string? projectionTag,
        CommissionBriefOptions options,
        SqliteCommissionBriefStore store,
        SqliteCompanyCommissionCapabilityStore capabilities,
        HostedCompanyCommissionService commissions,
        CommissionProjectionChangeSignal changeSignal)
    {
        if (!IsAvailable(context, options) || !IsValidPublicId(publicId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (projectionTag != null && !CommissionProjectionTag.IsValid(projectionTag))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var cancellationToken = context.RequestAborted;
        var participantToken = context.Request.Headers[
            "X-Commission-Participant"].ToString();
        var authorized = await LoadAuthorizedProjectionAsync(
            publicId,
            participantToken,
            store,
            capabilities,
            commissions,
            cancellationToken);
        if (authorized.StatusCode != StatusCodes.Status200OK ||
            authorized.Projection == null)
        {
            context.Response.StatusCode = authorized.StatusCode;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "private, no-cache, no-store";
        context.Response.Headers.Vary = "X-Commission-Participant";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        await context.Response.StartAsync(cancellationToken);

        var currentTag = CommissionProjectionTag.Create(authorized.Projection);
        if (!string.Equals(currentTag, projectionTag, StringComparison.Ordinal))
        {
            await WriteProjectionEventAsync(
                context.Response,
                currentTag,
                cancellationToken);
        }

        var leaseEndsAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var nextHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var remainingLease = leaseEndsAt - now;
            if (remainingLease <= TimeSpan.Zero)
            {
                break;
            }
            if (now >= nextHeartbeatAt)
            {
                await context.Response.WriteAsync(": keepalive\n\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                do
                {
                    nextHeartbeatAt = nextHeartbeatAt.AddSeconds(15);
                }
                while (nextHeartbeatAt <= now);
                continue;
            }

            var observation = changeSignal.Observe(publicId);
            authorized = await LoadAuthorizedProjectionAsync(
                publicId,
                participantToken,
                store,
                capabilities,
                commissions,
                cancellationToken);
            if (authorized.StatusCode != StatusCodes.Status200OK ||
                authorized.Projection == null)
            {
                break;
            }

            var nextTag = CommissionProjectionTag.Create(authorized.Projection);
            if (!string.Equals(nextTag, currentTag, StringComparison.Ordinal))
            {
                currentTag = nextTag;
                await WriteProjectionEventAsync(
                    context.Response,
                    currentTag,
                    cancellationToken);
                continue;
            }

            now = DateTimeOffset.UtcNow;
            var nextWakeAt = nextHeartbeatAt < leaseEndsAt
                ? nextHeartbeatAt
                : leaseEndsAt;
            var delay = nextWakeAt - now;
            if (delay <= TimeSpan.Zero)
            {
                continue;
            }
            var scheduledWake = Task.Delay(delay, cancellationToken);
            await Task.WhenAny(observation.Changed, scheduledWake);
        }
    }

    private static async Task<AuthorizedProjectionResult> LoadAuthorizedProjectionAsync(
        string publicId,
        string participantToken,
        SqliteCommissionBriefStore store,
        SqliteCompanyCommissionCapabilityStore capabilities,
        HostedCompanyCommissionService commissions,
        CancellationToken cancellationToken)
    {
        var brief = await store.LoadAsync(publicId, cancellationToken);
        if (brief == null)
        {
            return new AuthorizedProjectionResult(StatusCodes.Status404NotFound, null);
        }
        if (brief.Ownership == null)
        {
            return new AuthorizedProjectionResult(StatusCodes.Status200OK, brief);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(participantToken))
            {
                var capability = await capabilities.ResolveAsync(
                    publicId,
                    CompanyCommissionCapabilityKind.Participant,
                    participantToken,
                    cancellationToken);
                if (capability == null)
                {
                    return new AuthorizedProjectionResult(
                        StatusCodes.Status401Unauthorized,
                        null);
                }

                var participant = await commissions.LoadParticipantAsync(
                    capability,
                    cancellationToken);
                return participant == null
                    ? new AuthorizedProjectionResult(StatusCodes.Status401Unauthorized, null)
                    : new AuthorizedProjectionResult(StatusCodes.Status200OK, participant);
            }

            var projection = await commissions.LoadPublicAsync(
                publicId,
                cancellationToken);
            return projection == null
                ? new AuthorizedProjectionResult(StatusCodes.Status409Conflict, null)
                : new AuthorizedProjectionResult(StatusCodes.Status200OK, projection);
        }
        catch (InvalidOperationException)
        {
            return new AuthorizedProjectionResult(StatusCodes.Status409Conflict, null);
        }
    }

    private static void SetProjectionTag(HttpResponse response, object projection) =>
        response.Headers["X-Commission-Projection-Tag"] =
            CommissionProjectionTag.Create(projection);

    private static async Task WriteProjectionEventAsync(
        HttpResponse response,
        string projectionTag,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new { projectionTag },
            JsonOptions);
        await response.WriteAsync(
            $"id: {projectionTag}\nevent: commission-projection\ndata: {payload}\n\n",
            cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private sealed record AuthorizedProjectionResult(
        int StatusCode,
        object? Projection);

    private static bool IsAvailable(HttpContext context, CommissionBriefOptions options) =>
        options.Enabled &&
        options.IsAllowedRequestHost(context.Request.Host.Host);

    private static bool IsValidPublicId(string value) =>
        value.Length is >= 8 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool PublicationIdentityMatches(
        TradeCommissionPublication? publication,
        string publicId,
        TradeCompanyPublicationOwnership ownership) =>
        publication != null &&
        string.Equals(publication.PublicId, publicId, StringComparison.Ordinal) &&
        publication.Ownership == ownership;

    private static bool PublicationMatches(
        TradeCommissionPublication? publication,
        PublishedCommissionBrief published,
        string publicUrl,
        TradeCompanyPublicationOwnership ownership) =>
        PublicationIdentityMatches(publication, published.PublicId, ownership) &&
        string.Equals(publication!.PublicUrl, publicUrl, StringComparison.Ordinal) &&
        publication.Version == published.Version &&
        publication.PublishedAtUtc == published.PublishedAtUtc &&
        publication.RevokedAtUtc == null;

    private static bool TryReadMatchingOrder(
        TradeCompanyRecordEnvelope? record,
        PublishedCommissionBrief published,
        string publicUrl,
        TradeCompanyPublicationOwnership ownership,
        out TradeCompanyRecordEnvelope matchingRecord)
    {
        matchingRecord = null!;
        if (record == null)
        {
            return false;
        }

        try
        {
            var order = JsonSerializer.Deserialize<TradeOrder>(
                record.PayloadJson,
                JsonOptions);
            if (!PublicationMatches(
                    order?.CommissionPublication,
                    published,
                    publicUrl,
                    ownership) ||
                order?.CompanyCommission is not { } commission ||
                !string.Equals(
                    commission.PublicMetadata.PublicBriefId,
                    published.PublicId,
                    StringComparison.Ordinal) ||
                commission.PublicMetadata.ViewState !=
                CompanyCommissionPublicViewState.Published)
            {
                return false;
            }

            matchingRecord = record;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
