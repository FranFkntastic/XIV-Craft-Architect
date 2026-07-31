using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.ProfileHosting;

public sealed class PlansProfileSyncAdapter : IProfileSyncCollectionAdapter
{
    private readonly IndexedDbService _indexedDb;
    private readonly WebPlanPersistenceService _planPersistence;
    private readonly TradeOperationsPersistenceService _tradeOperations;

    public PlansProfileSyncAdapter(
        IndexedDbService indexedDb,
        WebPlanPersistenceService planPersistence,
        TradeOperationsPersistenceService tradeOperations)
    {
        _indexedDb = indexedDb;
        _planPersistence = planPersistence;
        _tradeOperations = tradeOperations;
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
            SourcePlanName = plan.SourcePlanName
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
        var generatedPlanIds = await LoadOrderGeneratedPlanIdsAsync(ct);
        var summaries = await _indexedDb.LoadPlanSummariesAsync();
        var now = DateTime.UtcNow;
        var objects = new List<ProfileSyncObjectEnvelope>();
        foreach (var summary in summaries.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (generatedPlanIds.Contains(summary.Id))
            {
                continue;
            }

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
            SourcePlanName = snapshot.SourcePlanName
        };

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

    private async Task<HashSet<string>> LoadOrderGeneratedPlanIdsAsync(CancellationToken ct)
    {
        var planIds = new HashSet<string>(StringComparer.Ordinal);
        var profiles = await _tradeOperations.LoadCompanyProfilesAsync();
        foreach (var profile in profiles.OrderBy(item => item.Id))
        {
            ct.ThrowIfCancellationRequested();
            var orders = await _tradeOperations.LoadOrdersAsync(profile.Id);
            foreach (var order in orders)
            {
                if (order.CraftPlanLinkKind == TradeOrderCraftPlanLinkKind.OrderGenerated &&
                    !string.IsNullOrWhiteSpace(order.CraftPlanId))
                {
                    planIds.Add(order.CraftPlanId);
                }
            }
        }

        return planIds;
    }
}
