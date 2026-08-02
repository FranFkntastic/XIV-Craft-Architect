using System.Collections.Concurrent;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record CompanyCommissionMigrationDiagnostic(
    CompanyId CompanyId,
    Guid OrderId,
    string ErrorCode,
    string Message,
    DateTime RecordedAtUtc);

public sealed class CompanyCommissionMigrationDiagnostics
{
    private readonly ConcurrentDictionary<(CompanyId CompanyId, Guid OrderId), CompanyCommissionMigrationDiagnostic>
        _failures = new();

    public IReadOnlyList<CompanyCommissionMigrationDiagnostic> Failures =>
        _failures.Values
            .OrderBy(item => item.CompanyId.Value)
            .ThenBy(item => item.OrderId)
            .ToArray();

    public void Record(CompanyCommissionMigrationDiagnostic diagnostic) =>
        _failures[(diagnostic.CompanyId, diagnostic.OrderId)] = diagnostic;

    public void Clear(CompanyId companyId, Guid orderId) =>
        _failures.TryRemove((companyId, orderId), out _);
}

public sealed class CompanyCommissionSchemaMigrationHostedService(
    ProfileHostOptions options,
    SqliteProfileHostStore profiles,
    ProfileHostedTradeCompanyService companies,
    SqliteCommissionBriefStore briefs,
    DiscordPublicationReconciliationService discordReconciliation,
    CompanyCommissionMigrationDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger<CompanyCommissionSchemaMigrationHostedService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        var hostedOrders = await profiles.LoadObjectsAsync(
            ProfileSyncCollections.TradeOrders,
            stoppingToken);
        foreach (var hosted in hostedOrders)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await MigrateAsync(hosted, stoppingToken);
        }

        var canonicalOrders = await profiles.LoadObjectsAsync(
            ProfileSyncCollections.TradeOrders,
            includeDeleted: true,
            stoppingToken);
        await discordReconciliation.ReconcileStaleAsync(
            canonicalOrders,
            stoppingToken);
    }

    private async Task MigrateAsync(
        HostedProfileObject hosted,
        CancellationToken cancellationToken)
    {
        TradeOrder? order;
        try
        {
            order = JsonSerializer.Deserialize<TradeOrder>(
                hosted.Object.PayloadJson,
                JsonOptions);
        }
        catch (JsonException)
        {
            logger.LogError(
                "Company commission migration skipped unreadable hosted order {OrderId}.",
                hosted.Object.ObjectId);
            return;
        }
        if (order == null ||
            order.Id == Guid.Empty ||
            order.CompanyProfileId == Guid.Empty)
        {
            logger.LogError(
                "Company commission migration skipped invalid hosted order {OrderId}.",
                hosted.Object.ObjectId);
            return;
        }
        if (order.CompanyCommission != null &&
            !TradeCompanyCommissionMigrationService.RequiresAssignedClaimRepair(order))
        {
            return;
        }

        var companyId = new CompanyId(order.CompanyProfileId);
        try
        {
            var published = order.CommissionPublication == null
                ? null
                : await briefs.LoadIncludingRevokedAsync(
                    order.CommissionPublication.PublicId,
                    cancellationToken);
            companyId = published?.Ownership?.CompanyId ?? companyId;
            var migrated = TradeCompanyCommissionMigrationService.ConvertLegacyOrder(
                order,
                published,
                companyId,
                initialCommissionRevision: 0,
                timeProvider.GetUtcNow().UtcDateTime);
            if (!Guid.TryParse(hosted.ProfileId, out var hostProfileId) ||
                hostProfileId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "The hosted profile identity is invalid.");
            }

            var access = new TradeCompanyAccessContext(
                companyId,
                hostProfileId,
                TradeCompanyRole.Owner,
                hostProfileId);
            var companyRevision = await companies.LoadCompanyRevisionAsync(
                access,
                cancellationToken);
            var mutation = await companies.PutRecordAsync(
                access,
                TradeCompanyRecordKinds.Order,
                order.Id.ToString("D"),
                JsonSerializer.Serialize(migrated, JsonOptions),
                new CompanyRecordRevision(hosted.Object.Revision),
                $"company-commission-schema-v{TradeCompanyCommission.CurrentSchemaVersion}:{order.Id:D}",
                cancellationToken,
                companyRevision);
            if (!mutation.Success)
            {
                throw new InvalidOperationException(
                    mutation.ErrorMessage ??
                    "The hosted Trade order changed during commission migration.");
            }

            diagnostics.Clear(companyId, order.Id);
            logger.LogInformation(
                "Migrated or repaired hosted Trade order {OrderId} at company commission schema {SchemaVersion}.",
                order.Id,
                TradeCompanyCommission.CurrentSchemaVersion);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var diagnostic = new CompanyCommissionMigrationDiagnostic(
                companyId,
                order.Id,
                "company_commission_migration_failed",
                exception.Message,
                timeProvider.GetUtcNow().UtcDateTime);
            diagnostics.Record(diagnostic);
            logger.LogError(
                exception,
                "Company commission migration failed for company {CompanyId}, order {OrderId}.",
                companyId,
                order.Id);
        }
    }
}
