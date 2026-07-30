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
        var plans = await _indexedDb.LoadAllPlansRequiredAsync();
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

        if (!await _indexedDb.SavePlansBatchAsync([plan]))
        {
            throw new InvalidOperationException(
                $"Browser storage could not apply hosted plan '{envelope.ObjectId}'.");
        }
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        if (!await _planPersistence.DeletePlanAsync(objectId))
        {
            throw new InvalidOperationException(
                $"Browser storage could not delete hosted plan '{objectId}'.");
        }
    }
}
