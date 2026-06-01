using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class AcquisitionEvaluationWorkflowService
{
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;

    public AcquisitionEvaluationWorkflowService(IRecipeLayerWorkflowService recipeLayerWorkflow)
    {
        _recipeLayerWorkflow = recipeLayerWorkflow;
    }

    public async Task<AcquisitionEvaluationSnapshot?> BuildCurrentSnapshotAsync(
        CraftingPlan? plan,
        IReadOnlyList<DetailedShoppingPlan> shoppingPlans,
        IReadOnlyList<CoreMarketDataUnavailableItem> unavailableMarketItems,
        AcquisitionFilter filter,
        CancellationToken cancellationToken = default)
    {
        var projection = await _recipeLayerWorkflow.BuildCurrentDemandProjectionAsync(
            plan,
            cancellationToken);
        if (projection == null)
        {
            return null;
        }

        return AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            shoppingPlans,
            unavailableMarketItems,
            filter,
            projection);
    }
}
