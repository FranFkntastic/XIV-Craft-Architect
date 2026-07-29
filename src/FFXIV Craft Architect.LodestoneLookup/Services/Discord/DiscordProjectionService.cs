using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed record DiscordReconciliationResult(
    CompanyRevision PreviousRevision,
    CompanyRevision CurrentRevision,
    int ProjectionCount);

public interface IDiscordPublicationRevocationSink
{
    Task RevokeAsync(
        string publicId,
        CancellationToken cancellationToken = default);
}

public sealed class DiscordPublicationRevocationSink(
    SqliteDiscordCollaborationStore collaboration,
    TimeProvider timeProvider) : IDiscordPublicationRevocationSink
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
}

public sealed class DiscordProjectionService(
    ITradeCompanyService companies,
    DiscordCompanyOrderAdapter orders,
    SqliteCommissionBriefStore briefs,
    SqliteDiscordCollaborationStore collaboration,
    DiscordCommissionOptions options,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DiscordReconciliationResult> ReconcileAsync(
        TradeCompanyAccessContext access,
        CancellationToken cancellationToken = default)
    {
        if (access.GrantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Canonical company access is required.");
        }

        var cursor = new CompanyRevision(
            await collaboration.LoadReconciliationCursorAsync(
                access.CompanyId,
                cancellationToken));
        var changes = await companies.GetChangesAsync(access, cursor, cancellationToken);
        if (changes.CompanyId != access.CompanyId ||
            changes.CompanyRevision.Value < cursor.Value ||
            changes.Records.Any(record => record.CompanyId != access.CompanyId))
        {
            throw new InvalidOperationException(
                "The canonical company returned an invalid reconciliation change set.");
        }

        var projections = new List<(DiscordPublicationRecord Publication, DiscordPublicationState State, string Payload)>();
        foreach (var envelope in changes.Records
                     .Where(record => record.RecordKind == TradeCompanyRecordKinds.Order)
                     .OrderBy(record => record.CompanyRevision.Value))
        {
            if (envelope.Deleted)
            {
                if (!Guid.TryParse(envelope.RecordId, out var deletedOrderId))
                {
                    throw new InvalidOperationException(
                        "A deleted canonical order has an invalid record identity.");
                }

                var deletedPublication = await collaboration.LoadPublicationByOrderAsync(
                    access.CompanyId,
                    deletedOrderId,
                    cancellationToken);
                if (deletedPublication == null)
                {
                    continue;
                }

                var closedOrder = new TradeOrder
                {
                    Id = deletedOrderId,
                    Status = TradeOrderStatus.Canceled,
                    CommissionPublication = new TradeCommissionPublication
                    {
                        PublicId = deletedPublication.PublicId,
                        Version = deletedPublication.BriefVersion
                    }
                };
                var closedPayload = await BuildPayloadAsync(
                    access,
                    closedOrder,
                    deletedPublication,
                    DiscordPublicationState.Closed,
                    cancellationToken);
                projections.Add((
                    deletedPublication,
                    DiscordPublicationState.Closed,
                    closedPayload));
                continue;
            }

            var order = DeserializeOrder(envelope);
            if (order.Id == Guid.Empty ||
                !Guid.TryParse(envelope.RecordId, out var recordId) ||
                order.Id != recordId)
            {
                throw new InvalidOperationException(
                    "The canonical order projection does not match its record identity.");
            }

            var publicId = order.CommissionPublication?.PublicId;
            if (string.IsNullOrWhiteSpace(publicId))
            {
                continue;
            }

            var publication = await collaboration.LoadPublicationByPublicIdAsync(
                publicId,
                cancellationToken);
            if (publication == null)
            {
                continue;
            }

            if (publication.CompanyId != access.CompanyId ||
                publication.OrderId != order.Id)
            {
                throw new InvalidOperationException(
                    "A Discord publication resolved across canonical company or order ownership.");
            }

            var state = DiscordPublicationService.ResolvePublicationState(order);
            var payload = await BuildPayloadAsync(
                access,
                order,
                publication,
                state,
                cancellationToken);
            projections.Add((publication, state, payload));
        }

        var now = timeProvider.GetUtcNow();
        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            var desiredRevision = checked(Math.Max(
                projection.Publication.DesiredProjectionRevision + 1,
                changes.CompanyRevision.Value + 1));
            await collaboration.EnqueueProjectionAsync(
                projection.Publication.PublicationId,
                projection.State,
                desiredRevision,
                projection.Payload,
                now,
                cancellationToken,
                index == projections.Count - 1 ? changes.CompanyRevision : null);
        }

        await collaboration.AdvanceReconciliationCursorAsync(
            access.CompanyId,
            changes.CompanyRevision,
            now,
            cancellationToken);

        return new DiscordReconciliationResult(
            cursor,
            changes.CompanyRevision,
            projections.Count);
    }

    private async Task<string> BuildPayloadAsync(
        TradeCompanyAccessContext access,
        TradeOrder order,
        DiscordPublicationRecord publication,
        DiscordPublicationState state,
        CancellationToken cancellationToken)
    {
        var published = await briefs.LoadAsync(publication.PublicId, cancellationToken);
        if (published == null)
        {
            if (state != DiscordPublicationState.Revoked)
            {
                throw new InvalidOperationException(
                    "An active Discord publication no longer has immutable commission terms.");
            }

            return JsonSerializer.Serialize(
                new
                {
                    content = "This commission publication was revoked.",
                    embeds = Array.Empty<object>(),
                    components = Array.Empty<object>(),
                    allowed_mentions = new { parse = Array.Empty<string>() }
                },
                JsonOptions);
        }

        string? assignmentLabel = null;
        if (order.AssignedCrafterId is { } crafterId)
        {
            var crafter = await orders.LoadCrafterAsync(
                access,
                crafterId,
                cancellationToken);
            assignmentLabel = crafter == null
                ? "Assigned by the operator"
                : $"Assigned to {crafter.Crafter.DisplayName}";
        }

        var payload = DiscordCommissionMessage.Create(
            published,
            options.CommissionBaseUrl,
            state,
            state == DiscordPublicationState.Open ? publication.ActionToken : null,
            assignmentLabel);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static TradeOrder DeserializeOrder(TradeCompanyRecordEnvelope envelope)
    {
        try
        {
            return JsonSerializer.Deserialize<TradeOrder>(envelope.PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException("Canonical Trade order payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "Canonical Trade order payload is invalid.",
                exception);
        }
    }
}
