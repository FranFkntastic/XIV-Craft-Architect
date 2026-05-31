using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface IRecipeOperationSnapshotService
{
    Task<RecipeOperationSnapshot> BuildAsync(CraftingPlan? plan, CancellationToken ct = default);

    Task<RecipeOperationSnapshot> BuildAsync(
        CraftingPlan? plan,
        RecipeOperationSnapshotIdentity identity,
        RecipeOperationSnapshotBuildOptions? options = null,
        CancellationToken ct = default);
}
