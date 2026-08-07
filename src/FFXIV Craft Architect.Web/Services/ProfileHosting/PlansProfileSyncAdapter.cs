using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class PlansProfileSyncAdapter :
    IProfileSyncCollectionAdapter,
    IProfileSyncSingleObjectAdapter
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

    public async Task<ProfileSyncObjectEnvelope?> LoadLocalObjectAsync(
        string objectId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var plan = await _indexedDb.LoadPlanAsync(objectId);
        return plan == null ? null : ToSyncObject(plan, DateTime.UtcNow);
    }

    public async Task ApplyRemoteObjectAsync(ProfileSyncObjectEnvelope envelope, CancellationToken ct)
    {
        var plan = DeserializePlan(envelope);

        var existing = await _planPersistence.LoadPlanPayloadAsync(plan.Id);
        if (existing?.LinkedOrderId.HasValue == true)
        {
            if (WebPlanPersistenceService.HasSameStoredSnapshot(existing, plan))
            {
                return;
            }

            if (!plan.LinkedOrderId.HasValue &&
                WebPlanPersistenceService.HasSameRevisionContent(existing, plan))
            {
                throw new ProfileSyncObjectReconciliationException(
                    ProfileSyncCollections.Plans,
                    envelope.ObjectId,
                    ProfileSyncObjectReconciliation.PromoteLocalAuthority,
                    $"Hosted plan '{envelope.ObjectId}' predates its linked-order seal.");
            }

            throw new ProfileSyncObjectReconciliationException(
                ProfileSyncCollections.Plans,
                envelope.ObjectId,
                ProfileSyncObjectReconciliation.ProtectedConflict,
                $"Hosted plan '{envelope.ObjectId}' conflicts with its linked-order plan.");
        }

        if (!await _planPersistence.SaveSnapshotAsync(plan))
        {
            throw new InvalidOperationException(
                $"Browser storage could not apply hosted plan '{envelope.ObjectId}'.");
        }
    }

    public async Task AdoptProtectedRemoteObjectAsync(
        ProfileSyncObjectEnvelope envelope,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var plan = DeserializePlan(envelope);
        if (!await _planPersistence.PreserveLocalAndAdoptLinkedSnapshotAsync(plan))
        {
            throw new InvalidOperationException(
                $"Browser storage could not preserve the local plan before applying hosted plan '{envelope.ObjectId}'.");
        }
    }

    private static StoredPlan DeserializePlan(ProfileSyncObjectEnvelope envelope)
    {
        var snapshot = ProfileSyncPlanPayloadCodec.Deserialize(
            envelope.PayloadJson,
            envelope.ObjectId);
        return new StoredPlan
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
    }

    public async Task DeleteLocalObjectAsync(string objectId, CancellationToken ct)
    {
        if (!await _planPersistence.DeletePlanAsync(objectId))
        {
            throw new InvalidOperationException(
                $"Browser storage could not delete hosted plan '{objectId}'.");
        }
    }

    public async Task DeleteLocalObjectForOrderDeletionAsync(
        string objectId,
        Guid deletingOrderId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!await _planPersistence.DeletePlanForLinkedOrderAsync(
                objectId,
                deletingOrderId))
        {
            throw new InvalidOperationException(
                $"Browser storage could not delete plan '{objectId}' with order '{deletingOrderId:D}'.");
        }
    }

    public Task<bool> IsDeleteProtectedAsync(string objectId) =>
        _planPersistence.IsDeleteProtectedAsync(objectId);

    public async Task<bool> IsLinkedOrderPlanAsync(string objectId) =>
        (await _planPersistence.LoadPlanPayloadAsync(objectId))?.LinkedOrderId.HasValue == true;

    public async Task<Guid?> LoadLinkedOrderIdAsync(string objectId) =>
        (await _planPersistence.LoadPlanPayloadAsync(objectId))?.LinkedOrderId;

}
