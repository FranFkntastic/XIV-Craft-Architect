using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed record DiscordDirectPublicationResult(
    DiscordPublicationCreateStatus Status,
    DiscordPublicationRecord? Publication,
    string? Error = null)
{
    public bool Success =>
        Status is DiscordPublicationCreateStatus.Created or DiscordPublicationCreateStatus.Replayed;
}

public sealed class DiscordPublicationService(
    ITradeCompanyService companies,
    DiscordCompanyOrderAdapter orders,
    SqliteCommissionBriefStore briefs,
    SqliteDiscordCollaborationStore collaboration,
    DiscordCommissionOptions options,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DiscordDirectPublicationResult> PublishExistingBriefAsync(
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
            order.Envelope.RecordRevision != ownership.OrderRevision)
        {
            return Conflict("The commission brief is not bound to the current Trade order revision.");
        }

        var installation = await collaboration.LoadInstallationAsync(
            access.CompanyId,
            cancellationToken);
        if (!IsUsableInstallation(installation))
        {
            return Conflict("The company does not have a healthy least-privilege Discord installation.");
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
            installation!,
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

    private bool IsUsableInstallation(DiscordCompanyInstallationBinding? installation)
    {
        return installation is
        {
            Active: true
        } &&
            DiscordRuntimePermission.CanPublish(installation.GrantedPermissions) &&
            string.Equals(installation.ApplicationId, options.ApplicationId, StringComparison.Ordinal) &&
            string.Equals(installation.GuildId, options.AllowedGuildId, StringComparison.Ordinal) &&
            string.Equals(installation.ChannelId, options.AllowedChannelId, StringComparison.Ordinal) &&
            options.CanPublishDirectly;
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
}
