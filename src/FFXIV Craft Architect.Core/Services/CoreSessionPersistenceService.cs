using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CoreSessionPersistenceService
{
    private const string AutoSaveId = "autosave";
    private const string AutoSaveName = "Autosave";

    private readonly CraftSessionState _session;
    private readonly ICoreStoredPlanStore _store;
    private readonly CoreStoredPlanSnapshotBuilder _snapshotBuilder;
    private readonly CorePlanSessionLoadService _sessionLoadService;

    public CoreSessionPersistenceService(
        CraftSessionState session,
        ICoreStoredPlanStore store,
        CoreStoredPlanSnapshotBuilder snapshotBuilder,
        CorePlanSessionLoadService sessionLoadService)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshotBuilder = snapshotBuilder ?? throw new ArgumentNullException(nameof(snapshotBuilder));
        _sessionLoadService = sessionLoadService ?? throw new ArgumentNullException(nameof(sessionLoadService));
    }

    public Task<IReadOnlyList<CoreStoredPlanSummary>> LoadPlanSummariesAsync(CancellationToken ct = default) =>
        _store.LoadPlanSummariesAsync(ct);

    public Task<CoreStoredPlanSnapshot?> LoadPlanPayloadAsync(string planId, CancellationToken ct = default) =>
        _store.LoadPlanSnapshotAsync(planId, ct);

    public async Task<CorePlanSessionLoadResult?> LoadPlanIntoSessionAsync(
        string planId,
        bool trackStoredPlanIdentity = true,
        CancellationToken ct = default)
    {
        var storedPlan = await LoadPlanPayloadAsync(planId, ct);
        if (storedPlan == null)
        {
            return null;
        }

        var result = _sessionLoadService.Load(storedPlan, trackStoredPlanIdentity);
        if (result.CanLoad)
        {
            MarkLoadedSessionPersisted();
        }

        return result;
    }

    public async Task<CorePlanSessionLoadResult?> LoadAutoSaveIntoSessionAsync(CancellationToken ct = default)
    {
        var storedPlan = await _store.LoadAutoSaveAsync(ct);
        if (storedPlan == null)
        {
            return null;
        }

        var result = _sessionLoadService.Load(storedPlan, trackStoredPlanIdentity: false);
        if (result.CanLoad)
        {
            MarkLoadedSessionPersisted();
        }

        return result;
    }

    public CoreStoredPlanSnapshot BuildSnapshot(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false) =>
        _snapshotBuilder.Build(planId, planName, savedAt, includeSourcePlanIdentity);

    public Task<bool> SaveSnapshotAsync(CoreStoredPlanSnapshot snapshot, CancellationToken ct = default) =>
        _store.SavePlanSnapshotAsync(snapshot, ct);

    public async Task<bool> SaveCurrentPlanAsync(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false,
        CancellationToken ct = default)
    {
        var snapshot = BuildSnapshot(planId, planName, savedAt, includeSourcePlanIdentity);
        return await SaveSnapshotAsync(snapshot, ct);
    }

    public async Task<bool> SaveCurrentAutoSaveAsync(
        DateTime? savedAt = null,
        CancellationToken ct = default)
    {
        if (_session.ActivePlan == null && _session.ProjectItems.Count == 0)
        {
            return false;
        }

        if (!_session.TryBeginAutoSave(out var capturedVersions, out var dirtyBuckets))
        {
            return false;
        }

        var snapshot = BuildSnapshot(
            AutoSaveId,
            AutoSaveName,
            savedAt,
            includeSourcePlanIdentity: true);
        var saved = await _store.SaveAutoSaveAsync(snapshot, ct);
        _session.CompleteAutoSave(saved, capturedVersions, dirtyBuckets);
        return saved;
    }

    public async Task<bool> DeletePlanAsync(string planId, CancellationToken ct = default)
    {
        var deleted = await _store.DeletePlanSnapshotAsync(planId, ct);
        if (deleted)
        {
            _session.ClearSourceIdentity(planId);
        }

        return deleted;
    }

    public Task<bool> DeleteAutoSaveAsync(CancellationToken ct = default) =>
        _store.DeleteAutoSaveAsync(ct);

    public async Task<CoreRenameStoredPlanResult> RenamePlanAsync(
        string planId,
        string newName,
        CancellationToken ct = default)
    {
        var plan = await LoadPlanPayloadAsync(planId, ct);
        if (plan == null)
        {
            return new CoreRenameStoredPlanResult(false, null, null);
        }

        var oldName = plan.Name;
        plan.Name = newName;
        plan.ModifiedAt = DateTime.UtcNow;
        var saved = await SaveSnapshotAsync(plan, ct);
        if (saved)
        {
            _session.RenameSourceIdentity(planId, newName);
        }

        return new CoreRenameStoredPlanResult(saved, oldName, newName);
    }

    private void MarkLoadedSessionPersisted()
    {
        _session.MarkCurrentPersisted(
            CraftSessionDirtyBucket.PlanCore,
            CraftSessionDirtyBucket.MarketAnalysis,
            CraftSessionDirtyBucket.SettingsContext);
    }
}

public sealed record CoreRenameStoredPlanResult(bool Success, string? OldName, string? NewName);
