using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed record TradeCommissionResetBackup(
    int SchemaVersion,
    DateTime ExportedAtUtc,
    IReadOnlyList<TradeCompanyProfile> Companies,
    IReadOnlyList<TradeOrder> Orders,
    IReadOnlyList<TradePayrollWorkflowDraft> PayrollDrafts,
    IReadOnlyList<string> GeneratedPlanIds,
    IReadOnlyList<JsonElement> LegacyCraftSnapshots,
    ProfileHostBootstrapPayload? HostedProfile);

public sealed record TradeCommissionPurgeResult(
    int OrdersDeleted,
    int PayrollDraftsDeleted,
    int GeneratedPlansDeleted,
    int DiscordPublicationsRetracted,
    int LegacyCraftSnapshotsDeleted);

public sealed class TradeOrderLifecycleService(
    TradeOperationsPersistenceService tradeOperations,
    TradePayrollPersistenceService payrollPersistence,
    IndexedDbService indexedDb,
    ProfileSyncService profileSync,
    ProfileSyncLocalStateService localState,
    ProfileHostClient profileHostClient,
    TradeCompanyCollaborationService collaboration,
    TradeCommissionOperationsService commissions,
    AppState appState)
{
    private static readonly JsonSerializerOptions SyncJsonOptions =
        ProfileSyncJson.CreateOptions();

    public async Task<TradeOrder> CancelAndRetractAsync(
        TradeOrder order,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var cancellationReason = string.IsNullOrWhiteSpace(reason)
            ? "Canceled by the commissioner."
            : reason.Trim();
        if (order.Status == TradeOrderStatus.Completed)
        {
            throw new InvalidOperationException("A completed order cannot be canceled.");
        }

        if (order.CompanyCommission != null)
        {
            await commissions.RefreshAsync(order, cancellationToken);
            var owner = commissions.GetForOrder(order.Id) ??
                throw new InvalidOperationException(
                    commissions.GetErrorForOrder(order.Id) ??
                    "The hosted commission could not be loaded for cancellation.");
            if (owner.Order.Status != TradeOrderStatus.Canceled)
            {
                var canceled = await commissions.CancelAsync(
                    owner,
                    cancellationReason,
                    cancellationToken);
                owner = RequireProjection(canceled, "cancel the commission");
            }

            if (owner.Order.CompanyCommission?.PublicMetadata.ViewState ==
                CompanyCommissionPublicViewState.Published)
            {
                var revoked = await commissions.RevokePublicationAsync(
                    owner,
                    cancellationToken);
                owner = RequireProjection(revoked, "retract the commission publication");
            }

            await RetractDiscordPublicationAsync(owner.Order, cancellationToken);
            appState.NotifyTradeOperationsDataChanged();
            return owner.Order;
        }

        var updated = TradeOrderWorkflow.CopyOrder(order);
        var previousStatus = updated.Status;
        updated.Status = TradeOrderStatus.Canceled;
        updated.UpdatedAtUtc = DateTime.UtcNow;
        TradeOrderWorkflow.AppendStatusHistory(
            updated,
            previousStatus,
            TradeOrderStatus.Canceled,
            cancellationReason,
            updated.UpdatedAtUtc);
        if (!await tradeOperations.SaveOrderAsync(updated))
        {
            throw new InvalidOperationException("Browser storage could not save the canceled order.");
        }
        await profileSync.QueueLocalSaveAsync(
            ProfileSyncCollections.TradeOrders,
            updated.Id.ToString("D"),
            cancellationToken);
        await RetractDiscordPublicationAsync(updated, cancellationToken);
        appState.NotifyTradeOperationsDataChanged();
        return updated;
    }

    public async Task DeleteOrderAsync(
        TradeOrder order,
        CancellationToken cancellationToken = default)
    {
        if (!TradeOrderStatusWorkflow.IsArchived(order.Status))
        {
            throw new InvalidOperationException("Cancel or complete the order before deleting it.");
        }

        await RetractDiscordPublicationAsync(order, cancellationToken);
        var identities = new List<(string Collection, string ObjectId)>
        {
            (ProfileSyncCollections.TradeOrders, order.Id.ToString("D"))
        };
        var drafts = await payrollPersistence.LoadDraftsAsync(order.CompanyProfileId);
        identities.AddRange(drafts
            .Where(draft => draft.OrderId == order.Id)
            .Select(draft => (ProfileSyncCollections.TradePayrollDrafts, draft.Id)));

        var allOrders = await LoadAllOrdersAsync(cancellationToken);
        if (order.CraftPlanLinkKind == TradeOrderCraftPlanLinkKind.OrderGenerated &&
            !string.IsNullOrWhiteSpace(order.CraftPlanId) &&
            !allOrders.Any(other =>
                other.Id != order.Id &&
                string.Equals(other.CraftPlanId, order.CraftPlanId, StringComparison.Ordinal)))
        {
            identities.Add((ProfileSyncCollections.Plans, order.CraftPlanId));
        }

        await profileSync.DeleteObjectsAsync(identities, cancellationToken);
        await indexedDb.DeleteTradeOrderCraftSnapshotsForOrderAsync(order.Id);
        appState.NotifyTradeOperationsDataChanged();
    }

    public async Task<TradeCommissionResetBackup> CreateResetBackupAsync(
        CancellationToken cancellationToken = default)
    {
        var companies = await tradeOperations.LoadCompanyProfilesAsync();
        var orders = await LoadAllOrdersAsync(cancellationToken, companies);
        var drafts = await LoadAllPayrollDraftsAsync(cancellationToken, companies);
        var generatedPlanIds = orders
            .Where(order => order.CraftPlanLinkKind == TradeOrderCraftPlanLinkKind.OrderGenerated)
            .Select(order => order.CraftPlanId)
            .Where(planId => !string.IsNullOrWhiteSpace(planId))
            .Select(planId => planId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var snapshots = await indexedDb.LoadAllTradeOrderCraftSnapshotsAsync();
        var connection = await localState.LoadConnectionSettingsAsync();
        ProfileHostBootstrapPayload? hosted = null;
        if (connection.IsConfigured)
        {
            var exported = await profileHostClient.ExportBootstrapAsync(
                connection.HostUrl!,
                connection.AccessKey!,
                cancellationToken);
            hosted = new ProfileHostBootstrapPayload
            {
                Objects = exported.Objects
                    .Where(item => item.Collection is
                        ProfileSyncCollections.TradeOrders or
                        ProfileSyncCollections.TradePayrollDrafts)
                    .ToArray()
            };
        }
        return new TradeCommissionResetBackup(
            1,
            DateTime.UtcNow,
            companies,
            orders,
            drafts,
            generatedPlanIds,
            snapshots,
            hosted);
    }

    public async Task<TradeCommissionPurgeResult> PurgeAllAsync(
        TradeCommissionResetBackup backup,
        CancellationToken cancellationToken = default)
    {
        var companies = await tradeOperations.LoadCompanyProfilesAsync();
        var localOrders = await LoadAllOrdersAsync(cancellationToken, companies);
        var orders = localOrders
            .Concat((backup.HostedProfile?.Objects ?? [])
                .Where(item => item.Collection == ProfileSyncCollections.TradeOrders)
                .Select(item => TryDeserializeHostedOrder(item.PayloadJson))
                .Where(order => order != null)
                .Select(order => order!))
            .DistinctBy(order => order.Id)
            .ToArray();
        var orderIds = orders.Select(order => order.Id.ToString("D"))
            .Concat((backup.HostedProfile?.Objects ?? [])
                .Where(item => item.Collection == ProfileSyncCollections.TradeOrders)
                .Select(item => item.ObjectId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var localDrafts = await LoadAllPayrollDraftsAsync(cancellationToken, companies);
        var draftIds = localDrafts.Select(draft => draft.Id)
            .Concat((backup.HostedProfile?.Objects ?? [])
                .Where(item => item.Collection == ProfileSyncCollections.TradePayrollDrafts)
                .Select(item => item.ObjectId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var snapshots = await indexedDb.LoadAllTradeOrderCraftSnapshotsAsync();
        var retracted = 0;
        foreach (var order in orders)
        {
            if (await RetractDiscordPublicationAsync(order, cancellationToken))
            {
                retracted++;
            }
        }

        var generatedPlanIds = orders
            .Where(order => order.CraftPlanLinkKind == TradeOrderCraftPlanLinkKind.OrderGenerated)
            .Select(order => order.CraftPlanId)
            .Where(planId => !string.IsNullOrWhiteSpace(planId))
            .Select(planId => planId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var identities = orderIds
            .Select(orderId => (ProfileSyncCollections.TradeOrders, orderId))
            .Concat(draftIds.Select(draftId => (ProfileSyncCollections.TradePayrollDrafts, draftId)))
            .Concat(generatedPlanIds.Select(planId => (ProfileSyncCollections.Plans, planId)))
            .ToArray();
        await profileSync.DeleteObjectsAsync(identities, cancellationToken);
        await indexedDb.ClearTradeOrderCraftSnapshotsAsync();
        appState.NotifyTradeOperationsDataChanged();
        return new TradeCommissionPurgeResult(
            orderIds.Length,
            draftIds.Length,
            generatedPlanIds.Length,
            retracted,
            snapshots.Count);
    }

    private async Task<bool> RetractDiscordPublicationAsync(
        TradeOrder order,
        CancellationToken cancellationToken)
    {
        var knownPublicId = order.CommissionPublication?.PublicId ??
            order.CompanyCommission?.PublicMetadata.PublicBriefId;
        if (!string.IsNullOrWhiteSpace(knownPublicId))
        {
            await collaboration.RevokePublicationAsync(
                new TradeCompanyPublicationOwnership(
                    new CompanyId(order.CompanyProfileId),
                    order.Id,
                    new CompanyRecordRevision(1)),
                knownPublicId,
                cancellationToken);
            return true;
        }

        if (order.CompanyCommission == null)
        {
            return false;
        }

        await collaboration.RefreshAsync(
            order.CompanyProfileId,
            order.Id,
            cancellationToken);
        var publication = collaboration.GetPublication(order.Id);
        if (publication == null ||
            publication.State == TradeCommissionDeliveryState.Revoked ||
            string.IsNullOrWhiteSpace(publication.PublicId))
        {
            return false;
        }

        await collaboration.RevokePublicationAsync(
            new TradeCompanyPublicationOwnership(
                new CompanyId(order.CompanyProfileId),
                order.Id,
                new CompanyRecordRevision(1)),
            publication.PublicId,
            cancellationToken);
        return true;
    }

    private async Task<IReadOnlyList<TradeOrder>> LoadAllOrdersAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<TradeCompanyProfile>? companies = null)
    {
        companies ??= await tradeOperations.LoadCompanyProfilesAsync();
        var orders = new List<TradeOrder>();
        foreach (var company in companies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            orders.AddRange(await tradeOperations.LoadOrdersAsync(company.Id));
        }
        return orders;
    }

    private async Task<IReadOnlyList<TradePayrollWorkflowDraft>> LoadAllPayrollDraftsAsync(
        CancellationToken cancellationToken,
        IReadOnlyList<TradeCompanyProfile> companies)
    {
        var drafts = new List<TradePayrollWorkflowDraft>();
        foreach (var company in companies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            drafts.AddRange(await payrollPersistence.LoadDraftsAsync(company.Id));
        }
        return drafts;
    }

    private static CompanyCommissionOwnerProjection RequireProjection(
        TradeCommissionOperatorResult result,
        string operation) =>
        result.Success && result.Projection != null
            ? result.Projection
            : throw new InvalidOperationException(
                result.Message ?? $"Could not {operation}.");

    private static TradeOrder? TryDeserializeHostedOrder(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<TradeOrder>(payloadJson, SyncJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
