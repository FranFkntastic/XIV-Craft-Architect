using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public interface ICoreStoredPlanStore
{
    Task<IReadOnlyList<CoreStoredPlanSummary>> LoadPlanSummariesAsync(CancellationToken ct = default);

    Task<CoreStoredPlanSnapshot?> LoadPlanSnapshotAsync(string planId, CancellationToken ct = default);

    Task<bool> SavePlanSnapshotAsync(CoreStoredPlanSnapshot snapshot, CancellationToken ct = default);

    Task<bool> DeletePlanSnapshotAsync(string planId, CancellationToken ct = default);

    Task<CoreStoredPlanSnapshot?> LoadAutoSaveAsync(CancellationToken ct = default);

    Task<bool> SaveAutoSaveAsync(CoreStoredPlanSnapshot snapshot, CancellationToken ct = default);

    Task<bool> DeleteAutoSaveAsync(CancellationToken ct = default);
}
