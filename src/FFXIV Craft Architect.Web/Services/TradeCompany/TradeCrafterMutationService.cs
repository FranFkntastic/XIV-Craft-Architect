using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCrafterMutationService(
    TradeOperationsPersistenceService local,
    TradeCompanyClientOrchestrator company)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task SynchronizeAsync(
        TradeCompanyProfile profile,
        CancellationToken cancellationToken = default)
    {
        await company.RefreshAsync(profile, cancellationToken);
        var localCrafters = (await local.LoadCraftersAsync(profile.Id))
            .ToDictionary(crafter => crafter.Id);
        var remoteIds = new HashSet<Guid>();

        foreach (var record in company.GetRecords(TradeCompanyRecordKinds.Crafter))
        {
            if (!Guid.TryParse(record.RecordId, out var crafterId))
            {
                continue;
            }

            var remote = Deserialize(record);
            if (remote == null || remote.Id != crafterId)
            {
                continue;
            }

            remoteIds.Add(crafterId);
            if (localCrafters.TryGetValue(crafterId, out var current) &&
                current.SyncState is TradeSyncState.PendingSync or TradeSyncState.Conflict)
            {
                continue;
            }

            NormalizeForLocalProfile(remote, profile.Id);
            await local.SaveCrafterAsync(remote);
        }

        if (!CompanyId.TryParse(profile.RemoteId, out _))
        {
            return;
        }

        foreach (var localOnly in localCrafters.Values.Where(crafter =>
                     !remoteIds.Contains(crafter.Id) &&
                     crafter.SyncState == TradeSyncState.LocalOnly))
        {
            await SaveAsync(profile, localOnly, cancellationToken);
        }
    }

    public async Task<bool> SaveAsync(
        TradeCompanyProfile profile,
        TradeCrafterProfile crafter,
        CancellationToken cancellationToken = default)
    {
        var connected = CompanyId.TryParse(profile.RemoteId, out var companyId);
        crafter.SyncState = connected
            ? TradeSyncState.PendingSync
            : TradeSyncState.LocalOnly;
        if (!await local.SaveCrafterAsync(crafter))
        {
            return false;
        }

        var canonical = Copy(crafter);
        if (connected)
        {
            canonical.CompanyProfileId = companyId.Value;
        }
        canonical.RemoteId = crafter.Id.ToString("D");
        canonical.SyncState = TradeSyncState.Synced;

        var result = await company.MutateAsync(
            TradeCompanyRecordKinds.Crafter,
            crafter.Id.ToString("D"),
            canonical,
            requiresCurrentCompany: false,
            cancellationToken: cancellationToken);
        if (result.Disposition == TradeCompanyMutationDisposition.Synced)
        {
            var authoritative = Deserialize(result.Record) ?? canonical;
            NormalizeForLocalProfile(authoritative, profile.Id);
            return await local.SaveCrafterAsync(authoritative);
        }

        crafter.SyncState = result.Disposition == TradeCompanyMutationDisposition.Conflict
            ? TradeSyncState.Conflict
            : connected
                ? TradeSyncState.PendingSync
                : TradeSyncState.LocalOnly;
        await local.SaveCrafterAsync(crafter);
        return true;
    }

    private static TradeCrafterProfile? Deserialize(
        TradeCompanyRecordEnvelope? record)
    {
        if (record == null || record.Deleted)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TradeCrafterProfile>(
                record.PayloadJson,
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TradeCrafterProfile Copy(TradeCrafterProfile crafter) =>
        JsonSerializer.Deserialize<TradeCrafterProfile>(
            JsonSerializer.Serialize(crafter, JsonOptions),
            JsonOptions)
        ?? throw new InvalidOperationException("The Trade crafter could not be copied.");

    private static void NormalizeForLocalProfile(
        TradeCrafterProfile crafter,
        Guid localCompanyProfileId)
    {
        crafter.CompanyProfileId = localCompanyProfileId;
        crafter.RemoteId = crafter.Id.ToString("D");
        crafter.SyncState = TradeSyncState.Synced;
    }
}
