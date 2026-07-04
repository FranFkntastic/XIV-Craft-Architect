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
        var profile = await _tradeOperations.GetOrCreateActiveCompanyProfileAsync();
        var drafts = await _tradePayrollPersistence.LoadDraftsAsync(profile.Id);
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

        await _tradePayrollPersistence.SaveDraftAsync(draft);
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        await _tradePayrollPersistence.DeleteDraftAsync(objectId);
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
