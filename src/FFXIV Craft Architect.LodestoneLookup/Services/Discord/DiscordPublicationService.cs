using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed record DiscordDirectPublicationResult(
    DiscordPublicationCreateStatus Status,
    DiscordPublicationRecord? Publication,
    string? Error = null)
{
    public bool Success =>
        Status is DiscordPublicationCreateStatus.Created or DiscordPublicationCreateStatus.Replayed;
}

public sealed record DiscordNewPublicationResult(
    DiscordDirectPublicationResult Delivery,
    PublishedCommissionBrief? Brief,
    bool OrderCommitted = false)
{
    public bool Success => Delivery.Success && Brief != null;
}

public sealed class DiscordPublicationService(
    ProfileHostedTradeCompanyService companies,
    DiscordCompanyOrderAdapter orders,
    SqliteCommissionBriefStore briefs,
    SqliteDiscordCollaborationStore collaboration,
    DiscordCommissionOptions options,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<DiscordDirectPublicationResult> PublishExistingBriefAsync(
        TradeCompanyAccessContext access,
        string publicId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        RequireOperator(access);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var published = await briefs.LoadAsync(publicId, cancellationToken);
        if (published?.Ownership is not { } ownership ||
            ownership.CompanyId != access.CompanyId)
        {
            return Conflict("The commission brief is not bound to the authenticated company.");
        }

        var canonicalOwnership = await companies.ResolvePublicationOwnershipAsync(
            publicId,
            cancellationToken);
        if (canonicalOwnership != ownership)
        {
            return Conflict("The canonical publication ownership could not be verified.");
        }

        var order = await orders.LoadOrderAsync(access, ownership.OrderId, cancellationToken);
        if (order == null ||
            !string.Equals(
                order.Order.CommissionPublication?.PublicId,
                publicId,
                StringComparison.Ordinal) ||
            order.Order.CommissionPublication?.Ownership != ownership)
        {
            return Conflict("The commission brief is not bound to the current Trade order revision.");
        }

        if (!options.CanPublishDirectly)
        {
            return Conflict("Discord direct publishing is not configured.");
        }

        var now = timeProvider.GetUtcNow();
        var state = ResolvePublicationState(order.Order);
        var actionToken = SqliteDiscordCollaborationStore.CreateActionToken();
        var initialPayload = DiscordCommissionMessage.Create(
            published,
            options.CommissionBaseUrl,
            state,
            state == DiscordPublicationState.Open ? actionToken : null);
        var created = await collaboration.CreatePublicationAsync(
            ownership,
            publicId,
            published.Version,
            idempotencyKey,
            actionToken,
            state,
            JsonSerializer.Serialize(initialPayload, JsonOptions),
            now,
            cancellationToken);
        return new DiscordDirectPublicationResult(
            created.Status,
            created.Publication,
            created.Error);
    }

    public async Task<DiscordNewPublicationResult> PublishNewBriefAsync(
        TradeCompanyAccessContext access,
        Guid orderId,
        CompanyRecordRevision orderRevision,
        CommissionBriefDocument brief,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        RequireOperator(access);
        if (orderId == Guid.Empty || orderRevision.Value <= 0)
        {
            return NewPublicationConflict(
                "Discord publication requires a revisioned canonical Trade order.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey.Length > 120)
        {
            return NewPublicationConflict("The publication idempotency key is too long.");
        }

        var validationError = CommissionBriefValidator.Validate(brief);
        if (validationError != null)
        {
            return NewPublicationConflict(validationError);
        }

        var ownership = new TradeCompanyPublicationOwnership(
            access.CompanyId,
            orderId,
            orderRevision);
        var expectedPublicId = SqliteCommissionBriefStore.CreateCompanyPublicId(
            ownership,
            idempotencyKey);
        var order = await orders.LoadOrderAsync(access, orderId, cancellationToken);
        if (order == null)
        {
            return NewPublicationConflict(
                "The canonical Trade order is unavailable.");
        }
        if (order.Envelope.RecordRevision != orderRevision &&
            !string.Equals(
                order.Order.CommissionPublication?.PublicId,
                expectedPublicId,
                StringComparison.Ordinal))
        {
            return NewPublicationConflict(
                "The Trade order changed before publication began.");
        }

        if (!options.CanPublishDirectly)
        {
            return NewPublicationConflict(
                "Discord direct publishing is not configured.");
        }

        PublishedCommissionBrief published;
        try
        {
            published = await briefs.CreateCompanyOwnedAsync(
                brief,
                ownership,
                idempotencyKey,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            return NewPublicationConflict(exception.Message);
        }

        TradeCompanyMutationResult orderMutation;
        if (order.Envelope.RecordRevision == orderRevision)
        {
            var publishedOrder = TradeOrderWorkflow.CopyOrder(order.Order);
            publishedOrder.CommissionPublication = new TradeCommissionPublication
            {
                PublicId = published.PublicId,
                Version = published.Version,
                PublishedAtUtc = published.PublishedAtUtc,
                Ownership = ownership
            };
            publishedOrder.UpdatedAtUtc = published.PublishedAtUtc;
            publishedOrder.History = publishedOrder.History
                .Append(new TradeOrderHistoryEvent
                {
                    Id = Guid.NewGuid(),
                    CompanyProfileId = publishedOrder.CompanyProfileId,
                    OrderId = publishedOrder.Id,
                    Kind = TradeOrderHistoryEventKind.CommissionPublished,
                    Note = $"Published Discord commission brief v{published.Version}.",
                    CreatedAtUtc = published.PublishedAtUtc
                })
                .ToArray();
            orderMutation = await companies.PutRecordAsync(
                access,
                TradeCompanyRecordKinds.Order,
                orderId.ToString("D"),
                JsonSerializer.Serialize(publishedOrder, JsonOptions),
                orderRevision,
                $"publication-order:{idempotencyKey}",
                cancellationToken);
            if (!orderMutation.Success)
            {
                var current = await orders.LoadOrderAsync(
                    access,
                    orderId,
                    cancellationToken);
                if (string.Equals(
                        current?.Order.CommissionPublication?.PublicId,
                        published.PublicId,
                        StringComparison.Ordinal))
                {
                    orderMutation = new TradeCompanyMutationResult(
                        TradeCompanyMutationStatus.Replayed,
                        current!.Envelope);
                }
                else
                {
                    await DiscardUncommittedBriefAsync(
                        published,
                        ownership,
                        cancellationToken);
                    return new DiscordNewPublicationResult(
                        Conflict(
                            orderMutation.ErrorMessage ??
                            "The Trade order changed before publication completed."),
                        published,
                        OrderCommitted: false);
                }
            }
        }
        else if (string.Equals(
                     order.Order.CommissionPublication?.PublicId,
                     published.PublicId,
                     StringComparison.Ordinal))
        {
            orderMutation = new TradeCompanyMutationResult(
                TradeCompanyMutationStatus.Replayed,
                order.Envelope);
        }
        else
        {
            await DiscardUncommittedBriefAsync(
                published,
                ownership,
                cancellationToken);
            return new DiscordNewPublicationResult(
                Conflict("The Trade order changed before publication completed."),
                published,
                OrderCommitted: false);
        }

        var ownershipMutation = await companies.PutRecordAsync(
            access,
            TradeCompanyRecordKinds.Publication,
            published.PublicId,
            JsonSerializer.Serialize(ownership, JsonOptions),
            CompanyRecordRevision.None,
            $"publication:{idempotencyKey}",
            cancellationToken);
        if (!ownershipMutation.Success)
        {
            var existingOwnership = await companies.ResolvePublicationOwnershipAsync(
                published.PublicId,
                cancellationToken);
            if (existingOwnership != ownership)
            {
                var current = await orders.LoadOrderAsync(
                    access,
                    orderId,
                    cancellationToken);
                if (string.Equals(
                        current?.Order.CommissionPublication?.PublicId,
                        published.PublicId,
                        StringComparison.Ordinal))
                {
                    ownershipMutation = await companies.PutRecordAsync(
                        access,
                        TradeCompanyRecordKinds.Publication,
                        published.PublicId,
                        JsonSerializer.Serialize(ownership, JsonOptions),
                        CompanyRecordRevision.None,
                        $"publication-reconcile:{idempotencyKey}",
                        cancellationToken);
                    existingOwnership = ownershipMutation.Success
                        ? ownership
                        : await companies.ResolvePublicationOwnershipAsync(
                            published.PublicId,
                            cancellationToken);
                }

                if (existingOwnership != ownership)
                {
                    return new DiscordNewPublicationResult(
                        Conflict(
                            ownershipMutation.ErrorMessage ??
                            "Canonical publication ownership could not be recorded."),
                        published,
                        OrderCommitted: true);
                }
            }
        }

        var delivery = await PublishExistingBriefAsync(
            access,
            published.PublicId,
            idempotencyKey,
            cancellationToken);
        return new DiscordNewPublicationResult(
            delivery,
            published,
            OrderCommitted: true);
    }

    public async Task RevokeAsync(
        string publicId,
        CancellationToken cancellationToken = default)
    {
        var publication = await collaboration.LoadPublicationByPublicIdAsync(
            publicId,
            cancellationToken);
        if (publication == null ||
            publication.State == DiscordPublicationState.Revoked)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                content = "This commission publication was revoked.",
                embeds = Array.Empty<object>(),
                components = Array.Empty<object>(),
                allowed_mentions = new { parse = Array.Empty<string>() }
            },
            JsonOptions);
        await collaboration.EnqueueProjectionAsync(
            publication.PublicationId,
            DiscordPublicationState.Revoked,
            checked(publication.DesiredProjectionRevision + 1),
            payload,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public async Task RefreshOrderAsync(
        TradeCompanyAccessContext access,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var publication = await collaboration.LoadPublicationByOrderAsync(
            access.CompanyId,
            orderId,
            cancellationToken);
        var order = await orders.LoadOrderAsync(access, orderId, cancellationToken);
        if (publication == null || order == null)
        {
            return;
        }

        var published = await briefs.LoadAsync(publication.PublicId, cancellationToken)
            ?? throw new InvalidOperationException(
                "An active Discord publication no longer has immutable commission terms.");
        string? assignmentLabel = null;
        if (order.Order.AssignedCrafterId is { } crafterId)
        {
            var crafter = await orders.LoadCrafterAsync(access, crafterId, cancellationToken);
            assignmentLabel = crafter == null
                ? "Assigned by the operator"
                : $"Assigned to {crafter.Crafter.DisplayName}";
        }

        var state = ResolvePublicationState(order.Order);
        var payload = DiscordCommissionMessage.Create(
            published,
            options.CommissionBaseUrl,
            state,
            state == DiscordPublicationState.Open ? publication.ActionToken : null,
            assignmentLabel);
        await collaboration.EnqueueProjectionAsync(
            publication.PublicationId,
            state,
            checked(publication.DesiredProjectionRevision + 1),
            JsonSerializer.Serialize(payload, JsonOptions),
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task DiscardUncommittedBriefAsync(
        PublishedCommissionBrief published,
        TradeCompanyPublicationOwnership ownership,
        CancellationToken cancellationToken)
    {
        if (!await briefs.DiscardCompanyOwnedAsync(
                published.PublicId,
                ownership,
                cancellationToken))
        {
            await briefs.RevokeCompanyOwnedAsync(
                published.PublicId,
                ownership.CompanyId,
                cancellationToken);
        }
    }

    internal static DiscordPublicationState ResolvePublicationState(TradeOrder order)
    {
        if (order.CommissionPublication?.RevokedAtUtc != null)
        {
            return DiscordPublicationState.Revoked;
        }

        if (TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            return DiscordPublicationState.Closed;
        }

        return order.AssignedCrafterId.HasValue
            ? DiscordPublicationState.Assigned
            : DiscordPublicationState.Open;
    }

    private static void RequireOperator(TradeCompanyAccessContext access)
    {
        if (access.GrantId == Guid.Empty ||
            access.Role is not (TradeCompanyRole.Operator or TradeCompanyRole.Owner))
        {
            throw new UnauthorizedAccessException(
                "Direct Discord publication requires a company operator.");
        }
    }

    private static DiscordDirectPublicationResult Conflict(string error) =>
        new(DiscordPublicationCreateStatus.Conflict, null, error);

    private static DiscordNewPublicationResult NewPublicationConflict(string error) =>
        new(Conflict(error), null);
}
