using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IRecipeOperationSnapshotLifecycleService
{
    Task<RecipeOperationSnapshot> GetOrBuildAsync(
        CraftingPlan? plan,
        RecipeOperationSnapshotIdentity identity,
        RecipeOperationSnapshotBuildOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<RecipeOperationSnapshot?> GetCurrentOrNullAsync(
        CraftingPlan? plan,
        RecipeOperationSnapshotIdentity identity,
        Func<RecipeOperationSnapshotIdentity, bool> isCurrent,
        RecipeOperationSnapshotBuildOptions? options = null,
        CancellationToken cancellationToken = default);
}
