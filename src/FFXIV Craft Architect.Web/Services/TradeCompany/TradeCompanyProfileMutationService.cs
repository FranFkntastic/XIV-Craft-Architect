using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class TradeCompanyProfileMutationService(
    TradeOperationsPersistenceService local,
    TradeCompanyClientOrchestrator company)
{
    private const string CanonicalProfileRecordId = "company";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task SynchronizeAsync(
        TradeCompanyProfile profile,
        CancellationToken cancellationToken = default)
    {
        await company.RefreshAsync(profile, cancellationToken);
        var record = company.GetRecords(TradeCompanyRecordKinds.Profile)
            .FirstOrDefault(candidate =>
                string.Equals(
                    candidate.RecordId,
                    CanonicalProfileRecordId,
                    StringComparison.Ordinal));
        if (record == null)
        {
            if (CompanyId.TryParse(profile.RemoteId, out _))
            {
                await SaveAsync(profile, cancellationToken);
            }
            return;
        }

        if (profile.SyncState is TradeSyncState.PendingSync or TradeSyncState.Conflict)
        {
            company.RegisterIncomingConflict(
                record,
                "The company profile changed while local edits were pending.");
            profile.SyncState = TradeSyncState.Conflict;
            await local.SaveCompanyProfileAsync(profile);
            return;
        }

        var canonical = Deserialize(record);
        if (canonical == null)
        {
            return;
        }

        ApplyCanonicalToLocal(canonical, profile);
        await local.SaveCompanyProfileAsync(profile);
    }

    public async Task<bool> SaveAsync(
        TradeCompanyProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (!await local.SaveCompanyProfileAsync(profile))
        {
            return false;
        }

        if (!CompanyId.TryParse(profile.RemoteId, out var companyId))
        {
            profile.SyncState = TradeSyncState.LocalOnly;
            return true;
        }

        profile.SyncState = TradeSyncState.PendingSync;
        await local.SaveCompanyProfileAsync(profile);
        var canonical = Copy(profile);
        canonical.Id = companyId.Value;
        canonical.RemoteId = companyId.ToString();
        canonical.SyncState = TradeSyncState.Synced;
        var result = await company.MutateAsync(
            TradeCompanyRecordKinds.Profile,
            CanonicalProfileRecordId,
            canonical,
            requiresCurrentCompany: false,
            cancellationToken: cancellationToken);
        if (result.Disposition == TradeCompanyMutationDisposition.Synced)
        {
            var authoritative = Deserialize(result.Record);
            if (authoritative != null)
            {
                ApplyCanonicalToLocal(authoritative, profile);
            }
            profile.SyncState = TradeSyncState.Synced;
        }
        else
        {
            profile.SyncState = result.Disposition == TradeCompanyMutationDisposition.Conflict
                ? TradeSyncState.Conflict
                : TradeSyncState.PendingSync;
        }

        return await local.SaveCompanyProfileAsync(profile);
    }

    private static TradeCompanyProfile? Deserialize(
        TradeCompanyRecordEnvelope? record)
    {
        if (record == null || record.Deleted)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TradeCompanyProfile>(
                record.PayloadJson,
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TradeCompanyProfile Copy(TradeCompanyProfile profile) =>
        JsonSerializer.Deserialize<TradeCompanyProfile>(
            JsonSerializer.Serialize(profile, JsonOptions),
            JsonOptions)
        ?? throw new InvalidOperationException(
            "The Trade company profile could not be copied.");

    private static void ApplyCanonicalToLocal(
        TradeCompanyProfile canonical,
        TradeCompanyProfile local)
    {
        local.SchemaVersion = canonical.SchemaVersion;
        local.Name = canonical.Name;
        local.Description = canonical.Description;
        local.CommissionContact = canonical.CommissionContact;
        local.PaymentPolicy = canonical.PaymentPolicy;
        local.CreatedAtUtc = canonical.CreatedAtUtc;
        local.UpdatedAtUtc = canonical.UpdatedAtUtc;
        local.SyncState = TradeSyncState.Synced;
    }
}
