using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed record DiscordPublicationReconciliationSummary(
    int Examined,
    int Reconciled,
    int Skipped,
    int Failed);

public sealed class DiscordPublicationReconciliationService(
    IServiceScopeFactory scopeFactory,
    SqliteDiscordCollaborationStore collaboration,
    DiscordCommissionOptions options,
    TimeProvider timeProvider,
    ILogger<DiscordPublicationReconciliationService> logger)
{
    private const int MaximumStartupBatchSize = 500;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<DiscordPublicationReconciliationSummary> ReconcileStaleAsync(
        IReadOnlyList<HostedProfileObject> hostedOrders,
        CancellationToken cancellationToken = default)
    {
        if (!options.CanPublishDirectly)
        {
            return new DiscordPublicationReconciliationSummary(0, 0, 0, 0);
        }

        var stale = await collaboration.LoadPublicationsRequiringProjectionAsync(
            DiscordPublicationProjectionFormat.CurrentVersion,
            MaximumStartupBatchSize,
            cancellationToken);
        var reconciled = 0;
        var skipped = 0;
        var failed = 0;
        await using var scope = scopeFactory.CreateAsyncScope();
        var publications = scope.ServiceProvider
            .GetRequiredService<IDiscordPublicationRefresher>();
        foreach (var publication in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var hosted = ResolveHostedOrder(publication, hostedOrders);
                if (hosted is { Object.Deleted: true })
                {
                    await collaboration.EnqueueProjectionAsync(
                        publication.PublicationId,
                        DiscordPublicationState.Suppressed,
                        checked(publication.DesiredProjectionRevision + 1),
                        "{}",
                        timeProvider.GetUtcNow(),
                        cancellationToken);
                    reconciled++;
                    continue;
                }

                if (hosted == null ||
                    !Guid.TryParse(hosted.ProfileId, out var profileId) ||
                    profileId == Guid.Empty)
                {
                    skipped++;
                    logger.LogError(
                        "Discord publication {PublicationId} could not resolve its canonical hosted order {OrderId}; projection migration remains pending.",
                        publication.PublicationId,
                        publication.OrderId);
                    continue;
                }

                var access = new TradeCompanyAccessContext(
                    publication.CompanyId,
                    profileId,
                    TradeCompanyRole.Owner,
                    profileId);
                await publications.RefreshOrderAsync(
                    access,
                    publication.OrderId,
                    cancellationToken);
                var refreshed = await collaboration.LoadPublicationAsync(
                    publication.PublicationId,
                    cancellationToken);
                if (refreshed?.ProjectionFormatVersion !=
                    DiscordPublicationProjectionFormat.CurrentVersion)
                {
                    skipped++;
                    logger.LogError(
                        "Discord publication {PublicationId} did not accept projection format {ProjectionFormatVersion}; migration remains pending.",
                        publication.PublicationId,
                        DiscordPublicationProjectionFormat.CurrentVersion);
                    continue;
                }

                reconciled++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                logger.LogError(
                    exception,
                    "Discord publication {PublicationId} failed projection migration for order {OrderId}.",
                    publication.PublicationId,
                    publication.OrderId);
            }
        }

        if (stale.Count == MaximumStartupBatchSize)
        {
            logger.LogWarning(
                "Discord projection migration reached the bounded startup limit of {MaximumCount}; remaining publications will be reconciled on the next restart.",
                MaximumStartupBatchSize);
        }

        logger.LogInformation(
            "Discord projection migration examined {Examined} publications: {Reconciled} reconciled, {Skipped} skipped, {Failed} failed.",
            stale.Count,
            reconciled,
            skipped,
            failed);
        return new DiscordPublicationReconciliationSummary(
            stale.Count,
            reconciled,
            skipped,
            failed);
    }

    private static HostedProfileObject? ResolveHostedOrder(
        DiscordPublicationRecord publication,
        IReadOnlyList<HostedProfileObject> hostedOrders)
    {
        HostedProfileObject? resolved = null;
        var candidates = hostedOrders.Where(item =>
                     string.Equals(
                         item.Object.ObjectId,
                         publication.OrderId.ToString("D"),
                         StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length != 1)
        {
            return null;
        }

        foreach (var hosted in candidates)
        {
            if (hosted.Object.Deleted)
            {
                return hosted;
            }

            TradeOrder? order;
            try
            {
                order = JsonSerializer.Deserialize<TradeOrder>(
                    hosted.Object.PayloadJson,
                    JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (order?.Id != publication.OrderId ||
                order.CompanyProfileId != publication.CompanyId.Value ||
                !string.Equals(
                    order.CommissionPublication?.PublicId,
                    publication.PublicId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (resolved != null)
            {
                return null;
            }

            resolved = hosted;
        }

        return resolved;
    }
}
