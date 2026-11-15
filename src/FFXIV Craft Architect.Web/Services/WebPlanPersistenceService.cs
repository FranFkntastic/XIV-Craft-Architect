using System.Text.Json;
using System.Text.Json.Nodes;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class WebPlanPersistenceService
{
    /// <summary>Name suffix given to copies preserved from order-linked plans.</summary>
    public const string LocalChangesSuffix = " (local changes)";

    private static readonly JsonSerializerOptions ComparisonJson = new(JsonSerializerDefaults.Web);
    private readonly IndexedDbService _indexedDb;

    public WebPlanPersistenceService(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
    }

    /// <summary>
    /// Every stored plan summary, including order-linked plans. Summaries carry
    /// <see cref="StoredPlanSummary.LinkedOrderId"/> and
    /// <see cref="StoredPlanSummary.SourceOrderId"/> so callers classify without
    /// loading full plans.
    /// </summary>
    public async Task<IReadOnlyList<StoredPlanSummary>> LoadPlanSummariesAsync() =>
        await _indexedDb.LoadPlanSummariesAsync();

    /// <summary>
    /// Plan summaries that are not sealed to a Trade order (the historical
    /// saved-plan list; "local changes" forks are included).
    /// </summary>
    public async Task<IReadOnlyList<StoredPlanSummary>> LoadUnlinkedPlanSummariesAsync() =>
        (await _indexedDb.LoadPlanSummariesAsync())
            .Where(summary => !summary.LinkedOrderId.HasValue)
            .ToArray();

    public Task<StoredPlan?> LoadPlanPayloadAsync(string planId) =>
        _indexedDb.LoadPlanAsync(planId);

    public async Task<bool> SaveSnapshotAsync(StoredPlan snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var existing = await _indexedDb.LoadPlanAsync(snapshot.Id);
        if (existing?.LinkedOrderId.HasValue == true &&
            !HasSameStoredSnapshot(existing, snapshot))
        {
            return false;
        }

        // Worker exports do not know the fork's source order; keep it across saves.
        if (existing is { LinkedOrderId: null, SourceOrderId: { } sourceOrderId } &&
            !snapshot.LinkedOrderId.HasValue &&
            !snapshot.SourceOrderId.HasValue)
        {
            snapshot.SourceOrderId = sourceOrderId;
        }

        return await _indexedDb.SavePlanAsync(snapshot);
    }

    public async Task<PlanReplacementPreservationResult> PreserveBeforeReplacementAsync(
        StoredPlan snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var existing = await _indexedDb.LoadPlanAsync(snapshot.Id);
        if (existing?.LinkedOrderId.HasValue == true)
        {
            if (HasSameRevisionContent(existing, snapshot))
            {
                return new PlanReplacementPreservationResult(
                    Success: true,
                    AlreadyDurable: true,
                    Forked: false,
                    PlanId: existing.Id,
                    PlanName: existing.Name);
            }

            var now = DateTime.UtcNow;
            snapshot.Id = Guid.NewGuid().ToString("D");
            snapshot.Name = $"{snapshot.Name}{LocalChangesSuffix}";
            snapshot.CreatedAt = now;
            snapshot.ModifiedAt = now;
            snapshot.SavedAt = now;
            snapshot.LinkedOrderId = null;
            snapshot.SourceOrderId = existing.LinkedOrderId;
        }

        var saved = await SaveSnapshotAsync(snapshot);
        return new PlanReplacementPreservationResult(
            saved,
            AlreadyDurable: false,
            Forked: existing?.LinkedOrderId.HasValue == true,
            PlanId: snapshot.Id,
            PlanName: snapshot.Name);
    }

    public static bool HasSameRevisionContent(StoredPlan left, StoredPlan right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(
            NormalizeRevisionContent(left),
            NormalizeRevisionContent(right),
            StringComparison.Ordinal);
    }

    public static bool HasSameStoredSnapshot(StoredPlan left, StoredPlan right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(
            JsonSerializer.Serialize(left, ComparisonJson),
            JsonSerializer.Serialize(right, ComparisonJson),
            StringComparison.Ordinal);
    }

    public async Task<bool> PreserveLocalAndAdoptLinkedSnapshotAsync(StoredPlan replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var existing = await _indexedDb.LoadPlanAsync(replacement.Id);
        if (existing?.LinkedOrderId is not { } linkedOrderId ||
            replacement.LinkedOrderId != linkedOrderId)
        {
            return false;
        }

        var preserved = JsonSerializer.Deserialize<StoredPlan>(
            JsonSerializer.Serialize(existing, ComparisonJson),
            ComparisonJson)
            ?? throw new InvalidOperationException("Linked plan preservation failed.");
        var now = DateTime.UtcNow;
        preserved.Id = Guid.NewGuid().ToString("D");
        preserved.Name = $"{existing.Name}{LocalChangesSuffix}";
        preserved.CreatedAt = now;
        preserved.ModifiedAt = now;
        preserved.SavedAt = now;
        preserved.LinkedOrderId = null;
        preserved.SourceOrderId = linkedOrderId;

        return await _indexedDb.SavePlansBatchAsync([preserved, replacement]);
    }

    private static string NormalizeRevisionContent(StoredPlan snapshot)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, ComparisonJson)?.AsObject()
            ?? throw new InvalidOperationException("Stored plan normalization failed.");
        node.Remove("createdAt");
        node.Remove("modifiedAt");
        node.Remove("savedAt");
        node.Remove("linkedOrderId");
        return node.ToJsonString(ComparisonJson);
    }

    public async Task<bool> DeletePlanAsync(string planId)
    {
        if (await IsDeleteProtectedAsync(planId))
        {
            return false;
        }

        return await _indexedDb.DeletePlanAsync(planId);
    }

    /// <summary>
    /// Deletes several plans in one storage transaction. Plans linked to any of
    /// <paramref name="protectedLinkedOrderIds"/> are skipped. This is the plan
    /// browser's user-initiated delete; sync tombstones keep using
    /// <see cref="IsDeleteProtectedAsync"/>.
    /// </summary>
    public Task<StoredPlanBatchDeleteResult> DeletePlansAsync(
        IReadOnlyCollection<string> planIds,
        IReadOnlyCollection<Guid> protectedLinkedOrderIds) =>
        _indexedDb.DeletePlansBatchAsync(planIds, protectedLinkedOrderIds);

    /// <summary>
    /// Detaches a "local changes" fork from its source order so it lists as an
    /// ordinary user plan: clears <see cref="StoredPlan.SourceOrderId"/> and, when
    /// present, the generated fork-name suffix. Linked plans are never touched.
    /// </summary>
    public Task<bool> DetachFromSourceOrderAsync(string planId, string? renamedTo) =>
        _indexedDb.PatchUnlinkedPlanMetadataAsync(
            planId,
            renamedTo == null
                ? new Dictionary<string, object?> { ["sourceOrderId"] = null }
                : new Dictionary<string, object?>
                {
                    ["sourceOrderId"] = null,
                    ["name"] = renamedTo
                });

    public async Task<bool> DeletePlanForLinkedOrderAsync(
        string planId,
        Guid deletingOrderId)
    {
        var existing = await _indexedDb.LoadPlanAsync(planId);
        if (existing?.LinkedOrderId != deletingOrderId)
        {
            return false;
        }

        return await _indexedDb.DeletePlanAsync(planId);
    }

    public async Task<bool> IsDeleteProtectedAsync(string planId)
    {
        var existing = await _indexedDb.LoadPlanAsync(planId);
        if (existing?.LinkedOrderId is not { } linkedOrderId)
        {
            return false;
        }

        var profiles = await _indexedDb.LoadTradeCompanyProfilesAsync();
        foreach (var profile in profiles)
        {
            var referenced = (await _indexedDb.LoadTradeOrdersAsync(profile.Id)).Any(order =>
                order.Id == linkedOrderId);
            if (referenced)
            {
                return true;
            }
        }
        return false;
    }

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

public sealed class StoredPlanBatchDeleteResult
{
    public List<string> DeletedIds { get; set; } = [];
    public List<string> SkippedIds { get; set; } = [];
    public List<string> MissingIds { get; set; } = [];
}

public sealed record RenameStoredPlanResult(
    bool Success,
    string? OldName,
    string? NewName);

public sealed record PlanReplacementPreservationResult(
    bool Success,
    bool AlreadyDurable,
    bool Forked,
    string PlanId,
    string PlanName);
