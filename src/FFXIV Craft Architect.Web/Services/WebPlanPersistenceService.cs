using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class WebPlanPersistenceService
{
    private readonly IndexedDbService _indexedDb;

    public WebPlanPersistenceService(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
    }

    public async Task<IReadOnlyList<StoredPlanSummary>> LoadPlanSummariesAsync() =>
        await _indexedDb.LoadPlanSummariesAsync();

    public Task<StoredPlan?> LoadPlanPayloadAsync(string planId) =>
        _indexedDb.LoadPlanAsync(planId);

    public Task<bool> SaveSnapshotAsync(StoredPlan snapshot) =>
        _indexedDb.SavePlanAsync(snapshot);

    public Task<bool> DeletePlanAsync(string planId) =>
        _indexedDb.DeletePlanAsync(planId);

    public async Task<RenameStoredPlanResult> RenamePlanAsync(
        string planId,
        string newName)
    {
        var plan = await LoadPlanPayloadAsync(planId);
        if (plan == null)
        {
            return new RenameStoredPlanResult(false, null, null);
        }

        var oldName = plan.Name;
        plan.Name = newName;
        plan.ModifiedAt = DateTime.UtcNow;
        var saved = await SaveSnapshotAsync(plan);
        return new RenameStoredPlanResult(saved, oldName, newName);
    }
}

public sealed record RenameStoredPlanResult(
    bool Success,
    string? OldName,
    string? NewName);
