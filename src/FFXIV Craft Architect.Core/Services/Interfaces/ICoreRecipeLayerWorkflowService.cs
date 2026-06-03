using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services.Interfaces;

public interface ICoreRecipeLayerWorkflowService
{
    RecipeOperationSnapshotIdentity CreateSnapshotIdentity();

    RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan);

    IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan);

    IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan);

    Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default);

    async Task<CoreMarketAnalysisCandidateBuildResult?> BuildCurrentMarketAnalysisCandidateResultAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default)
    {
        var candidates = await BuildCurrentMarketAnalysisCandidatesAsync(plan, cancellationToken);
        return candidates == null
            ? null
            : new CoreMarketAnalysisCandidateBuildResult(candidates, RecipeBasis: null);
    }

    Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default);
}

public sealed record CoreMarketAnalysisCandidateBuildResult(
    IReadOnlyList<MaterialAggregate> Candidates,
    StoredRecipeOperationSnapshot? RecipeBasis);
