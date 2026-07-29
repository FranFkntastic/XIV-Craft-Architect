using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public interface IDiscordCompanyAccessResolver
{
    Task<TradeCompanyAccessContext?> ResolveAsync(
        HttpRequest request,
        CompanyId companyId,
        CancellationToken cancellationToken = default);
}

public sealed class DenyDiscordCompanyAccessResolver : IDiscordCompanyAccessResolver
{
    public Task<TradeCompanyAccessContext?> ResolveAsync(
        HttpRequest request,
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult<TradeCompanyAccessContext?>(null);
    }
}

public sealed class UnavailableTradeCompanyService : ITradeCompanyService
{
    public Task<TradeCompanyIdentity?> GetCompanyAsync(
        TradeCompanyAccessContext access,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<TradeCompanyIdentity?>(null);

    public Task<TradeCompanyChangeSet> GetChangesAsync(
        TradeCompanyAccessContext access,
        CompanyRevision afterRevision,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TradeCompanyChangeSet(
            access.CompanyId,
            afterRevision,
            Array.Empty<TradeCompanyRecordEnvelope>()));

    public Task<TradeCompanyMutationResult> MutateAsync(
        TradeCompanyAccessContext access,
        TradeCompanyMutationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TradeCompanyMutationResult(
            TradeCompanyMutationStatus.Rejected,
            null,
            ErrorCode: "company_service_unavailable",
            ErrorMessage: "The canonical Trade company service is not configured."));

    public Task<TradeCompanyPublicationOwnership?> ResolvePublicationOwnershipAsync(
        string publicId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<TradeCompanyPublicationOwnership?>(null);
}

public sealed record DiscordInstallationDestination(
    string InstallationId,
    CompanyId CompanyId,
    string ApplicationId,
    string GuildId,
    string ChannelId,
    bool CanViewChannel,
    bool CanSendMessages,
    bool CanEmbedLinks,
    bool Enabled)
{
    public bool CanPublish =>
        Enabled &&
        !string.IsNullOrWhiteSpace(InstallationId) &&
        !string.IsNullOrWhiteSpace(ApplicationId) &&
        !string.IsNullOrWhiteSpace(GuildId) &&
        !string.IsNullOrWhiteSpace(ChannelId) &&
        CanViewChannel &&
        CanSendMessages &&
        CanEmbedLinks;
}

public interface IDiscordInstallationRegistry
{
    Task<DiscordInstallationDestination?> ResolveAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default);
}

public interface IDiscordInstallationBindingWriter
{
    Task UpsertInstallationAsync(
        DiscordCompanyInstallationBinding binding,
        CancellationToken cancellationToken = default);
}

public sealed class DenyDiscordInstallationRegistry : IDiscordInstallationRegistry
{
    public Task<DiscordInstallationDestination?> ResolveAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<DiscordInstallationDestination?>(null);
}

public static class DiscordCompanyAdapterRegistrations
{
    public static IServiceCollection AddDiscordCompanyAdapters(this IServiceCollection services)
    {
        services.TryAddSingleton<ITradeCompanyService, UnavailableTradeCompanyService>();
        services.TryAddSingleton<IDiscordCompanyAccessResolver, DenyDiscordCompanyAccessResolver>();
        services.TryAddSingleton<SqliteDiscordCollaborationStore>();
        services.Replace(ServiceDescriptor.Singleton<IDiscordInstallationRegistry>(
            provider => provider.GetRequiredService<SqliteDiscordCollaborationStore>()));
        services.Replace(ServiceDescriptor.Singleton<IDiscordInstallationBindingWriter>(
            provider => provider.GetRequiredService<SqliteDiscordCollaborationStore>()));
        services.Replace(ServiceDescriptor.Singleton<IDiscordVolunteerInteractionService>(
            provider => provider.GetRequiredService<SqliteDiscordCollaborationStore>()));
        services.Replace(ServiceDescriptor.Singleton<IDiscordOutboxLeaseStore>(
            provider => provider.GetRequiredService<SqliteDiscordCollaborationStore>()));
        services.TryAddScoped<DiscordCompanyOrderAdapter>();
        services.TryAddScoped<DiscordPublicationService>();
        services.TryAddScoped<DiscordClaimService>();
        services.TryAddScoped<DiscordProjectionService>();
        return services;
    }
}

public sealed record DiscordCanonicalOrderProjection(
    TradeOrder Order,
    TradeCompanyRecordEnvelope Envelope,
    CompanyRevision CompanyRevision);

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
    private readonly ITradeCompanyService _companies;

    public DiscordCompanyOrderAdapter(ITradeCompanyService companies)
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

        var changes = await _companies.GetChangesAsync(
            access,
            CompanyRevision.None,
            cancellationToken);
        ValidateChangeSet(access, changes);

        var envelope = FindRecord(changes.Records, TradeCompanyRecordKinds.Order, orderId);
        if (envelope == null || envelope.Deleted)
        {
            return null;
        }

        var order = Deserialize<TradeOrder>(envelope, "Trade order");
        if (order.Id != orderId)
        {
            throw new InvalidOperationException(
                "The canonical Trade order payload does not match its record identity.");
        }

        return new DiscordCanonicalOrderProjection(order, envelope, changes.CompanyRevision);
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

        var changes = await _companies.GetChangesAsync(
            access,
            CompanyRevision.None,
            cancellationToken);
        ValidateChangeSet(access, changes);

        var envelope = FindRecord(changes.Records, TradeCompanyRecordKinds.Crafter, crafterId);
        if (envelope == null || envelope.Deleted)
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

        var mutation = await _companies.MutateAsync(
            access,
            new TradeCompanyMutationRequest(
                access.CompanyId,
                TradeCompanyRecordKinds.Order,
                current.Envelope.RecordId,
                JsonSerializer.Serialize(updated, JsonOptions),
                current.Envelope.RecordRevision,
                current.CompanyRevision,
                idempotencyKey),
            cancellationToken);

        return new DiscordOrderAssignmentMutation(updated, mutation);
    }

    private static TradeCompanyRecordEnvelope? FindRecord(
        IEnumerable<TradeCompanyRecordEnvelope> records,
        string recordKind,
        Guid recordId)
    {
        return records
            .Where(record =>
                string.Equals(record.RecordKind, recordKind, StringComparison.Ordinal) &&
                Guid.TryParse(record.RecordId, out var parsedId) &&
                parsedId == recordId)
            .OrderByDescending(record => record.RecordRevision.Value)
            .ThenByDescending(record => record.CompanyRevision.Value)
            .FirstOrDefault();
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

    private static void ValidateChangeSet(
        TradeCompanyAccessContext access,
        TradeCompanyChangeSet changes)
    {
        if (changes.CompanyId != access.CompanyId ||
            changes.Records.Any(record => record.CompanyId != access.CompanyId))
        {
            throw new InvalidOperationException(
                "The company service returned a cross-company change set.");
        }
    }
}
