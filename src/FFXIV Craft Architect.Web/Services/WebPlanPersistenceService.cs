using System.Text.Json;
using System.Text.Json.Nodes;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class WebPlanPersistenceService
{
    private static readonly JsonSerializerOptions ComparisonJson = new(JsonSerializerDefaults.Web);
    private readonly IndexedDbService _indexedDb;

    public WebPlanPersistenceService(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
    }

    public async Task<IReadOnlyList<StoredPlanSummary>> LoadPlanSummariesAsync()
    {
        var summaries = await _indexedDb.LoadPlanSummariesAsync();
        var visible = new List<StoredPlanSummary>();
        foreach (var summary in summaries)
        {
            var plan = await _indexedDb.LoadPlanAsync(summary.Id);
            if (plan?.LinkedOrderId.HasValue != true)
            {
                visible.Add(summary);
            }
        }
        return visible;
    }

    public Task<StoredPlan?> LoadPlanPayloadAsync(string planId) =>
        _indexedDb.LoadPlanAsync(planId);

    public async Task<bool> SaveSnapshotAsync(StoredPlan snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var existing = await _indexedDb.LoadPlanAsync(snapshot.Id);
        if (existing?.LinkedOrderId.HasValue == true &&
            !string.Equals(
                JsonSerializer.Serialize(existing, ComparisonJson),
                JsonSerializer.Serialize(snapshot, ComparisonJson),
                StringComparison.Ordinal))
        {
            return false;
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
            snapshot.Name = $"{snapshot.Name} (local changes)";
            snapshot.CreatedAt = now;
            snapshot.ModifiedAt = now;
            snapshot.SavedAt = now;
            snapshot.LinkedOrderId = null;
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
