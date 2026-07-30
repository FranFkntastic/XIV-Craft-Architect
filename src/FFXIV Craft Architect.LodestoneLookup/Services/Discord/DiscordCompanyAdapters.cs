using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed record DiscordCanonicalOrderProjection(
    TradeOrder Order,
    TradeCompanyRecordEnvelope Envelope);

public sealed record DiscordCanonicalCrafterProjection(
    TradeCrafterProfile Crafter,
    TradeCompanyRecordEnvelope Envelope);

public sealed record DiscordOrderAssignmentMutation(
    TradeOrder Order,
    TradeCompanyMutationResult Mutation)
{
    public bool Success => Mutation.Success;

    public bool Conflict => Mutation.Status == TradeCompanyMutationStatus.Conflict;
}

public sealed class DiscordCompanyOrderAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ProfileHostedTradeCompanyService _companies;

    public DiscordCompanyOrderAdapter(ProfileHostedTradeCompanyService companies)
    {
        _companies = companies;
    }

    public async Task<DiscordCanonicalOrderProjection?> LoadOrderAsync(
        TradeCompanyAccessContext access,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order IDs cannot be empty.", nameof(orderId));
        }

        var envelope = await _companies.LoadRecordAsync(
            access,
            TradeCompanyRecordKinds.Order,
            orderId.ToString("D"),
            cancellationToken);
        if (envelope == null)
        {
            return null;
        }

        var order = Deserialize<TradeOrder>(envelope, "Trade order");
        if (order.Id != orderId)
        {
            throw new InvalidOperationException(
                "The canonical Trade order payload does not match its record identity.");
        }
        return new DiscordCanonicalOrderProjection(order, envelope);
    }

    public async Task<DiscordCanonicalCrafterProjection?> LoadCrafterAsync(
        TradeCompanyAccessContext access,
        Guid crafterId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        if (crafterId == Guid.Empty)
        {
            throw new ArgumentException("Crafter IDs cannot be empty.", nameof(crafterId));
        }

        var envelope = await _companies.LoadRecordAsync(
            access,
            TradeCompanyRecordKinds.Crafter,
            crafterId.ToString("D"),
            cancellationToken);
        if (envelope == null)
        {
            return null;
        }

        var crafter = Deserialize<TradeCrafterProfile>(envelope, "Trade crafter");
        if (crafter.Id != crafterId)
        {
            throw new InvalidOperationException(
                "The canonical Trade crafter payload does not match its record identity.");
        }
        return new DiscordCanonicalCrafterProjection(crafter, envelope);
    }

    public async Task<DiscordOrderAssignmentMutation> AssignAsync(
        TradeCompanyAccessContext access,
        DiscordCanonicalOrderProjection current,
        DiscordCanonicalCrafterProjection selectedCrafter,
        string idempotencyKey,
        DateTime confirmedAtUtc,
        CancellationToken cancellationToken = default)
    {
        RequireOperator(access);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(selectedCrafter);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (current.Envelope.CompanyId != access.CompanyId ||
            selectedCrafter.Envelope.CompanyId != access.CompanyId)
        {
            throw new InvalidOperationException(
                "The selected order and crafter must belong to the authenticated company.");
        }

        if (current.Order.CompanyProfileId != selectedCrafter.Crafter.CompanyProfileId)
        {
            throw new InvalidOperationException(
                "The selected crafter does not belong to the Trade order's company profile.");
        }

        if (current.Order.AssignedCrafterId.HasValue ||
            current.Order.Status != TradeOrderStatus.ReadyToAssign ||
            TradeOrderStatusWorkflow.IsArchived(current.Order.Status))
        {
            throw new InvalidOperationException(
                "Only an unassigned Trade order that is ready to assign can accept Discord interest.");
        }

        var updated = TradeOrderWorkflow.CopyOrder(current.Order);
        var previousStatus = updated.Status;
        var previousCrafterId = updated.AssignedCrafterId;
        updated.AssignedCrafterId = selectedCrafter.Crafter.Id;
        updated.Status = TradeOrderWorkflow.ResolveStatusForAssignment(
            updated.Status,
            updated.AssignedCrafterId);
        updated.UpdatedAtUtc = confirmedAtUtc;

        TradeOrderWorkflow.AppendAssignmentHistory(
            updated,
            previousCrafterId,
            updated.AssignedCrafterId,
            selectedCrafter.Crafter.DisplayName,
            confirmedAtUtc);
        TradeOrderWorkflow.AppendStatusHistory(
            updated,
            previousStatus,
            updated.Status,
            "Assigned from operator-confirmed Discord interest.",
            confirmedAtUtc);

        var mutation = await _companies.PutRecordAsync(
            access,
            TradeCompanyRecordKinds.Order,
            current.Envelope.RecordId,
            JsonSerializer.Serialize(updated, JsonOptions),
            current.Envelope.RecordRevision,
            idempotencyKey,
            cancellationToken);

        return new DiscordOrderAssignmentMutation(updated, mutation);
    }

    private static T Deserialize<T>(
        TradeCompanyRecordEnvelope envelope,
        string description)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(envelope.PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException($"{description} payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"{description} payload is not valid canonical JSON.",
                exception);
        }
    }

    private static void ValidateAccess(TradeCompanyAccessContext access)
    {
        if (access.CompanyId == default ||
            access.GrantId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Canonical company access is incomplete.");
        }
    }

    private static void RequireOperator(TradeCompanyAccessContext access)
    {
        ValidateAccess(access);
        if (access.Role is not (TradeCompanyRole.Operator or TradeCompanyRole.Owner))
        {
            throw new UnauthorizedAccessException(
                "Discord collaboration mutations require a company operator.");
        }
    }

}
