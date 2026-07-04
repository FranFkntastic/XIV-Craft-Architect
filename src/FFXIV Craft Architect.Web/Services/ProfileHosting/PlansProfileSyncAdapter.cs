using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class PlansProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private readonly IndexedDbService _indexedDb;
    private readonly WebPlanPersistenceService _planPersistence;

    public PlansProfileSyncAdapter(IndexedDbService indexedDb, WebPlanPersistenceService planPersistence)
    {
        _indexedDb = indexedDb;
        _planPersistence = planPersistence;
    }

    public string Collection => ProfileSyncCollections.Plans;

    public static ProfileSyncObjectEnvelope ToSyncObject(StoredPlan plan, DateTime updatedAtUtc)
    {
        return new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.Plans,
            ObjectId = plan.Id,
            PayloadJson = JsonSerializer.Serialize(plan),
            UpdatedAtUtc = updatedAtUtc
        };
    }

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct)
    {
        var plans = await _indexedDb.LoadAllPlansAsync();
        var now = DateTime.UtcNow;
        return plans.Select(plan => ToSyncObject(plan, now)).ToArray();
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        var plan = JsonSerializer.Deserialize<StoredPlan>(envelope.PayloadJson);
        if (plan == null)
        {
            throw new InvalidOperationException($"Hosted profile plan payload '{envelope.ObjectId}' could not be deserialized.");
        }

        await _indexedDb.SavePlansBatchAsync([plan]);
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        await _planPersistence.DeletePlanAsync(objectId);
    }
}
