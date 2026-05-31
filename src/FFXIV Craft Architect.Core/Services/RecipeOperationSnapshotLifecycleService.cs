using System.Collections.Concurrent;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class RecipeOperationSnapshotLifecycleService : IRecipeOperationSnapshotLifecycleService
{
    private readonly IRecipeOperationSnapshotService _snapshotService;
    private readonly ConcurrentDictionary<RecipeOperationSnapshotCacheKey, Lazy<Task<RecipeOperationSnapshot>>> _inFlightBuilds = new();

    public RecipeOperationSnapshotLifecycleService(IRecipeOperationSnapshotService snapshotService)
    {
        _snapshotService = snapshotService;
    }

    public async Task<RecipeOperationSnapshot> GetOrBuildAsync(
        CraftingPlan? plan,
        RecipeOperationSnapshotIdentity identity,
        RecipeOperationSnapshotBuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var buildOptions = options ?? RecipeOperationSnapshotBuildOptions.Default;
        var key = new RecipeOperationSnapshotCacheKey(identity, buildOptions);
        var lazy = _inFlightBuilds.GetOrAdd(
            key,
            _ => new Lazy<Task<RecipeOperationSnapshot>>(
                () => BuildAndEvictAsync(plan, identity, buildOptions, key),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return await lazy.Value.WaitAsync(cancellationToken);
    }

    public async Task<RecipeOperationSnapshot?> GetCurrentOrNullAsync(
        CraftingPlan? plan,
        RecipeOperationSnapshotIdentity identity,
        Func<RecipeOperationSnapshotIdentity, bool> isCurrent,
        RecipeOperationSnapshotBuildOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!isCurrent(identity))
        {
            return null;
        }

        var snapshot = await GetOrBuildAsync(plan, identity, options, cancellationToken);
        return isCurrent(identity) ? snapshot : null;
    }

    private async Task<RecipeOperationSnapshot> BuildAndEvictAsync(
        CraftingPlan? plan,
        RecipeOperationSnapshotIdentity identity,
        RecipeOperationSnapshotBuildOptions options,
        RecipeOperationSnapshotCacheKey key)
    {
        try
        {
            return await _snapshotService.BuildAsync(plan, identity, options, CancellationToken.None);
        }
        finally
        {
            _inFlightBuilds.TryRemove(key, out _);
        }
    }

    private sealed record RecipeOperationSnapshotCacheKey(
        RecipeOperationSnapshotIdentity Identity,
        RecipeOperationSnapshotBuildOptions Options);
}
