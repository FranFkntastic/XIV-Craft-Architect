using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class TradePayrollDraftProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private readonly TradeOperationsPersistenceService _tradeOperations;
    private readonly TradePayrollPersistenceService _tradePayrollPersistence;

    public TradePayrollDraftProfileSyncAdapter(
        TradeOperationsPersistenceService tradeOperations,
        TradePayrollPersistenceService tradePayrollPersistence)
    {
        _tradeOperations = tradeOperations;
        _tradePayrollPersistence = tradePayrollPersistence;
    }

    public string Collection => ProfileSyncCollections.TradePayrollDrafts;

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct)
    {
        var profiles = await _tradeOperations.LoadCompanyProfilesAsync();
        var drafts = new List<TradePayrollWorkflowDraft>();
        foreach (var profile in profiles.OrderBy(profile => profile.Id))
        {
            ct.ThrowIfCancellationRequested();
            drafts.AddRange(await _tradePayrollPersistence.LoadDraftsAsync(profile.Id));
        }

        var now = DateTime.UtcNow;
        return drafts.Select(draft => ToEnvelope(draft, now)).ToArray();
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        var draft = JsonSerializer.Deserialize<TradePayrollWorkflowDraft>(envelope.PayloadJson);
        if (draft == null)
        {
            throw new InvalidOperationException($"Hosted Trade payroll draft payload '{envelope.ObjectId}' could not be deserialized.");
        }

        await _tradeOperations.RequireCompanyProfileAsync(
            draft.CompanyProfileId,
            "payroll draft",
            envelope.ObjectId);
        if (!await _tradePayrollPersistence.SaveDraftAsync(draft))
        {
            throw new InvalidOperationException(
                $"Browser storage could not apply hosted Trade payroll draft '{envelope.ObjectId}'.");
        }
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        if (!await _tradePayrollPersistence.DeleteDraftAsync(objectId))
        {
            throw new InvalidOperationException(
                $"Browser storage could not delete hosted Trade payroll draft '{objectId}'.");
        }
    }

    private static ProfileSyncObjectEnvelope ToEnvelope(TradePayrollWorkflowDraft draft, DateTime updatedAtUtc)
    {
        return new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.TradePayrollDrafts,
            ObjectId = draft.Id,
            PayloadJson = JsonSerializer.Serialize(draft),
            UpdatedAtUtc = updatedAtUtc
        };
    }
}
