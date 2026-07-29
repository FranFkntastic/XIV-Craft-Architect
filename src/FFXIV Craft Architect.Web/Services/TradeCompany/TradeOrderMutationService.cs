using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public interface ITradeOrderLocalStore
{
    Task<IReadOnlyList<TradeOrder>> LoadAsync(Guid companyProfileId);

    Task<bool> SaveAsync(TradeOrder order);

    Task<bool> DeleteAsync(Guid orderId);
}

public sealed class TradeOperationsOrderLocalStore : ITradeOrderLocalStore
{
    private readonly TradeOperationsPersistenceService _persistence;

    public TradeOperationsOrderLocalStore(TradeOperationsPersistenceService persistence)
    {
        _persistence = persistence;
    }

    public Task<IReadOnlyList<TradeOrder>> LoadAsync(Guid companyProfileId) =>
        _persistence.LoadOrdersAsync(companyProfileId);

    public Task<bool> SaveAsync(TradeOrder order) =>
        _persistence.SaveOrderAsync(order);

    public Task<bool> DeleteAsync(Guid orderId) =>
        _persistence.DeleteOrderAsync(orderId);
}

public sealed class TradeOrderMutationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ITradeOrderLocalStore _localStore;
    private readonly TradeCompanyClientOrchestrator _company;

    public TradeOrderMutationService(
        ITradeOrderLocalStore localStore,
        TradeCompanyClientOrchestrator company)
    {
        _localStore = localStore;
        _company = company;
    }

    public async Task<TradeCompanyRefreshResult> SynchronizeAsync(
        TradeCompanyProfile profile,
        CancellationToken cancellationToken = default)
    {
        var refresh = await _company.RefreshAsync(profile, cancellationToken);
        var orderRecords = _company.GetRecords(TradeCompanyRecordKinds.Order)
            .Concat(refresh.ChangedRecords.Where(record =>
                record.Deleted &&
                string.Equals(
                    record.RecordKind,
                    TradeCompanyRecordKinds.Order,
                    StringComparison.Ordinal)))
            .GroupBy(record => record.RecordId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(record => record.CompanyRevision.Value)
                .First())
            .ToArray();

        var localOrders = (await _localStore.LoadAsync(profile.Id))
            .ToDictionary(order => order.Id);
        var remoteIds = new HashSet<Guid>();
        foreach (var record in orderRecords)
        {
            if (!Guid.TryParse(record.RecordId, out var orderId))
            {
                continue;
            }
            remoteIds.Add(orderId);

            if (record.Deleted)
            {
                if (!localOrders.TryGetValue(orderId, out var localDeleted) ||
                    localDeleted.SyncState is not (TradeSyncState.PendingSync or TradeSyncState.Conflict))
                {
                    await _localStore.DeleteAsync(orderId);
                }
                else
                {
                    _company.RegisterIncomingConflict(
                        record,
                        "This order was deleted in another client while local edits were pending.");
                }

                continue;
            }

            var remoteOrder = DeserializeOrder(record);
            if (remoteOrder == null)
            {
                continue;
            }

            NormalizeForLocalProfile(remoteOrder, profile.Id);
            if (localOrders.TryGetValue(orderId, out var localOrder) &&
                localOrder.SyncState is TradeSyncState.PendingSync or TradeSyncState.Conflict)
            {
                _company.RegisterIncomingConflict(
                    record,
                    "This order changed in another client while local edits were pending.");
                continue;
            }

            MarkSynced(remoteOrder, record.RecordId);
            await _localStore.SaveAsync(remoteOrder);
        }

        if (CompanyId.TryParse(profile.RemoteId, out _))
        {
            foreach (var localOnly in localOrders.Values.Where(order =>
                         !remoteIds.Contains(order.Id) &&
                         order.SyncState == TradeSyncState.LocalOnly))
            {
                await SaveAsync(profile, localOnly, cancellationToken);
            }
        }

        return new TradeCompanyRefreshResult(_company.Connection, orderRecords);
    }

    public async Task<TradeOrderMutationOutcome> SaveAsync(
        TradeCompanyProfile profile,
        TradeOrder order,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(order);

        var connected = CompanyId.TryParse(profile.RemoteId, out _);
        order.SyncState = connected ? TradeSyncState.PendingSync : TradeSyncState.LocalOnly;
        var localSaved = await _localStore.SaveAsync(order);
        if (!localSaved)
        {
            return new TradeOrderMutationOutcome(
                false,
                connected
                    ? TradeCompanyMutationDisposition.Pending
                    : TradeCompanyMutationDisposition.LocalOnly,
                order,
                Message: "The order could not be saved locally.");
        }

        var companyOrder = TradeOrderWorkflow.CopyOrder(order);
        companyOrder.RemoteId = order.Id.ToString("D");
        companyOrder.SyncState = TradeSyncState.Synced;
        if (CompanyId.TryParse(profile.RemoteId, out var companyId))
        {
            NormalizeForCanonicalCompany(companyOrder, companyId);
        }
        var companyResult = await _company.MutateAsync(
            TradeCompanyRecordKinds.Order,
            order.Id.ToString("D"),
            companyOrder,
            requiresCurrentCompany: false,
            cancellationToken: cancellationToken);
        switch (companyResult.Disposition)
        {
            case TradeCompanyMutationDisposition.Synced:
                {
                    var authoritative = DeserializeOrder(companyResult.Record) ?? order;
                    NormalizeForLocalProfile(authoritative, profile.Id);
                    MarkSynced(authoritative, companyResult.Record?.RecordId ?? order.Id.ToString("D"));
                    await _localStore.SaveAsync(authoritative);
                    return new TradeOrderMutationOutcome(
                        true,
                        TradeCompanyMutationDisposition.Synced,
                        authoritative,
                        Message: companyResult.Message);
                }
            case TradeCompanyMutationDisposition.LocalOnly:
                order.SyncState = TradeSyncState.LocalOnly;
                await _localStore.SaveAsync(order);
                return new TradeOrderMutationOutcome(
                    true,
                    TradeCompanyMutationDisposition.LocalOnly,
                    order,
                    Message: companyResult.Message);
            case TradeCompanyMutationDisposition.Conflict:
                {
                    order.SyncState = TradeSyncState.Conflict;
                    await _localStore.SaveAsync(order);
                    var currentRemote = DeserializeOrder(companyResult.CurrentRecord);
                    if (currentRemote != null)
                    {
                        NormalizeForLocalProfile(currentRemote, profile.Id);
                        MarkSynced(currentRemote, companyResult.CurrentRecord!.RecordId);
                    }

                    return new TradeOrderMutationOutcome(
                        true,
                        TradeCompanyMutationDisposition.Conflict,
                        order,
                        currentRemote,
                        companyResult.Message);
                }
            case TradeCompanyMutationDisposition.Pending:
                order.SyncState = TradeSyncState.PendingSync;
                await _localStore.SaveAsync(order);
                return new TradeOrderMutationOutcome(
                    true,
                    companyResult.Disposition,
                    order,
                    Message: companyResult.Message);
            case TradeCompanyMutationDisposition.Rejected:
            default:
                order.SyncState = TradeSyncState.Conflict;
                await _localStore.SaveAsync(order);
                _company.RegisterRejectedMutation(
                    TradeCompanyRecordKinds.Order,
                    order.Id.ToString("D"),
                    companyResult.Message ?? "The company rejected this order update.");
                return new TradeOrderMutationOutcome(
                    true,
                    TradeCompanyMutationDisposition.Rejected,
                    order,
                    Message: companyResult.Message);
        }
    }

    public async Task<TradeOrderMutationOutcome> ApplyCanonicalOrderAsync(
        TradeOrder order,
        Guid localCompanyProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        NormalizeForLocalProfile(order, localCompanyProfileId);
        MarkSynced(order, order.Id.ToString("D"));
        var saved = await _localStore.SaveAsync(order);
        return new TradeOrderMutationOutcome(
            saved,
            saved
                ? TradeCompanyMutationDisposition.Synced
                : TradeCompanyMutationDisposition.Rejected,
            order,
            Message: saved
                ? "The company order was applied locally."
                : "The company accepted the action, but the order could not be saved locally.");
    }

    public async Task<IReadOnlyList<TradeOrderMutationOutcome>> RetryPendingAsync(
        TradeCompanyProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        var results = await _company.RetryPendingAsync(cancellationToken);
        var outcomes = new List<TradeOrderMutationOutcome>();
        foreach (var result in results.Where(result =>
                     result.Record != null &&
                     string.Equals(
                         result.Record.RecordKind,
                         TradeCompanyRecordKinds.Order,
                         StringComparison.Ordinal)))
        {
            var order = DeserializeOrder(result.Record);
            if (order == null)
            {
                continue;
            }

            if (profile != null)
            {
                NormalizeForLocalProfile(order, profile.Id);
            }
            MarkSynced(order, result.Record!.RecordId);
            var saved = await _localStore.SaveAsync(order);
            outcomes.Add(new TradeOrderMutationOutcome(
                saved,
                result.Disposition,
                order,
                Message: result.Message));
        }

        return outcomes;
    }

    public async Task<bool> AcceptRemoteConflictAsync(
        TradeCompanyRecordEnvelope currentRecord,
        Guid localCompanyProfileId,
        CancellationToken cancellationToken = default)
    {
        var order = DeserializeOrder(currentRecord);
        if (order == null)
        {
            return false;
        }

        NormalizeForLocalProfile(order, localCompanyProfileId);
        MarkSynced(order, currentRecord.RecordId);
        if (!await _localStore.SaveAsync(order))
        {
            return false;
        }

        _company.ResolveConflictWithRemote(currentRecord);
        return true;
    }

    private static TradeOrder? DeserializeOrder(TradeCompanyRecordEnvelope? record)
    {
        if (record == null || record.Deleted)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TradeOrder>(record.PayloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void MarkSynced(TradeOrder order, string remoteId)
    {
        order.RemoteId = remoteId;
        order.SyncState = TradeSyncState.Synced;
    }

    private static void NormalizeForCanonicalCompany(
        TradeOrder order,
        CompanyId companyId)
    {
        order.CompanyProfileId = companyId.Value;
        foreach (var history in order.History)
        {
            history.CompanyProfileId = companyId.Value;
        }
    }

    private static void NormalizeForLocalProfile(
        TradeOrder order,
        Guid localCompanyProfileId)
    {
        order.CompanyProfileId = localCompanyProfileId;
        foreach (var history in order.History)
        {
            history.CompanyProfileId = localCompanyProfileId;
        }
    }
}
