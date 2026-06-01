using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CoreAcquisitionEvaluationWorkflowService
{
    private readonly ICoreRecipeLayerWorkflowService _recipeLayerWorkflow;

    public CoreAcquisitionEvaluationWorkflowService(ICoreRecipeLayerWorkflowService recipeLayerWorkflow)
    {
        _recipeLayerWorkflow = recipeLayerWorkflow ?? throw new ArgumentNullException(nameof(recipeLayerWorkflow));
    }

    public async Task<CoreAcquisitionEvaluationSnapshot?> BuildCurrentSnapshotAsync(
        CraftingPlan? plan,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlySet<int> unavailableMarketItemIds,
        CoreAcquisitionFilter filter,
        CancellationToken cancellationToken = default)
    {
        var projection = await _recipeLayerWorkflow.BuildCurrentDemandProjectionAsync(
            plan,
            cancellationToken);
        if (projection == null)
        {
            return null;
        }

        return CoreAcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans,
            unavailableMarketItemIds,
            filter,
            projection);
    }
}
