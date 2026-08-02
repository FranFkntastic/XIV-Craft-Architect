using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class PlansProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private readonly IndexedDbService _indexedDb;
    private readonly WebPlanPersistenceService _planPersistence;

    public PlansProfileSyncAdapter(
        IndexedDbService indexedDb,
        WebPlanPersistenceService planPersistence)
    {
        _indexedDb = indexedDb;
        _planPersistence = planPersistence;
    }

    public string Collection => ProfileSyncCollections.Plans;

    public static ProfileSyncObjectEnvelope ToSyncObject(StoredPlan plan, DateTime updatedAtUtc)
    {
        var snapshot = new ProfileSyncPlanSnapshot
        {
            Id = plan.Id,
            Name = plan.Name,
            CreatedAt = plan.CreatedAt,
            ModifiedAt = plan.ModifiedAt,
            SavedAt = plan.SavedAt,
            DataCenter = plan.DataCenter,
            ProjectItems = plan.ProjectItems
                .Select(item => new ProfileSyncPlanProjectItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    IconId = item.IconId,
                    Quantity = item.Quantity,
                    MustBeHq = item.MustBeHq
                })
                .ToList(),
            PlanJson = plan.PlanJson,
            PlanStateJson = plan.PlanStateJson,
            ProcurementTravelTolerance = plan.ProcurementTravelTolerance,
            MarketAnalysisScopeSnapshotJson = plan.MarketAnalysisScopeSnapshotJson,
            SavedRecommendationMode = plan.SavedRecommendationMode,
            SavedMarketAnalysisLens = plan.SavedMarketAnalysisLens,
            SourcePlanId = plan.SourcePlanId,
            SourcePlanName = plan.SourcePlanName,
            LinkedOrderId = plan.LinkedOrderId
        };
        return new ProfileSyncObjectEnvelope
        {
            Collection = ProfileSyncCollections.Plans,
            ObjectId = plan.Id,
            PayloadJson = ProfileSyncPlanPayloadCodec.Serialize(snapshot),
            UpdatedAtUtc = updatedAtUtc
        };
    }

    public async Task<IReadOnlyList<ProfileSyncObjectEnvelope>> LoadLocalObjectsAsync(CancellationToken ct)
    {
        var summaries = await _indexedDb.LoadPlanSummariesAsync();
        var now = DateTime.UtcNow;
        var objects = new List<ProfileSyncObjectEnvelope>();
        foreach (var summary in summaries.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var plan = await _indexedDb.LoadPlanAsync(summary.Id)
                ?? throw new InvalidOperationException(
                    $"Saved plan summary '{summary.Id}' has no browser payload.");
            objects.Add(ToSyncObject(plan, now));
        }

        return objects;
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        var snapshot = ProfileSyncPlanPayloadCodec.Deserialize(
            envelope.PayloadJson,
            envelope.ObjectId);
        var plan = new StoredPlan
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            CreatedAt = snapshot.CreatedAt,
            ModifiedAt = snapshot.ModifiedAt,
            SavedAt = snapshot.SavedAt,
            DataCenter = snapshot.DataCenter,
            ProjectItems = snapshot.ProjectItems
                .Select(item => new StoredProjectItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    IconId = item.IconId,
                    Quantity = item.Quantity,
                    MustBeHq = item.MustBeHq
                })
                .ToList(),
            PlanJson = snapshot.PlanJson,
            PlanStateJson = snapshot.PlanStateJson,
            ProcurementTravelTolerance = snapshot.ProcurementTravelTolerance,
            MarketAnalysisScopeSnapshotJson = snapshot.MarketAnalysisScopeSnapshotJson,
            SavedRecommendationMode = snapshot.SavedRecommendationMode,
            SavedMarketAnalysisLens = snapshot.SavedMarketAnalysisLens,
            SourcePlanId = snapshot.SourcePlanId,
            SourcePlanName = snapshot.SourcePlanName,
            LinkedOrderId = snapshot.LinkedOrderId
        };

        if (!await _planPersistence.SaveSnapshotAsync(plan))
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

    public Task<bool> IsDeleteProtectedAsync(string objectId) =>
        _planPersistence.IsDeleteProtectedAsync(objectId);

}
