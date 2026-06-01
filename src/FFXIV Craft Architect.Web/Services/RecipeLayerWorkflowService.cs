using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public interface IRecipeLayerWorkflowService
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

    Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default);
}

public sealed class RecipeLayerWorkflowService : IRecipeLayerWorkflowService
{
    private const string RecipeDataIdentity = "garland-current";

    private readonly AppState _appState;
    private readonly IRecipeOperationSnapshotLifecycleService _snapshotLifecycleService;
    private readonly IRecipeDemandProjectionService _demandProjectionService;

    public RecipeLayerWorkflowService(
        AppState appState,
        IRecipeOperationSnapshotLifecycleService snapshotLifecycleService,
        IRecipeDemandProjectionService demandProjectionService)
    {
        _appState = appState;
        _snapshotLifecycleService = snapshotLifecycleService;
        _demandProjectionService = demandProjectionService;
    }

    public RecipeOperationSnapshotIdentity CreateSnapshotIdentity()
    {
        var versions = _appState.CurrentVersions;
        return new RecipeOperationSnapshotIdentity(
            _appState.PlanSessionVersion,
            versions.PlanStructureVersion,
            versions.PlanDecisionVersion,
            versions.PlanPriceVersion,
            versions.SettingsVersion,
            RecipeDataIdentity);
    }

    public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan)
    {
        return _demandProjectionService.Build(plan, snapshot: null);
    }

    public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan)
    {
        return BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();
    }

    public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan)
    {
        return BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates();
    }

    public async Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default)
    {
        if (plan == null)
        {
            return BuildDemandProjection(plan);
        }

        var identity = CreateSnapshotIdentity();
        if (!_appState.IsCurrentPlanSession(plan, identity.PlanSessionVersion))
        {
            return null;
        }

        var snapshot = await _snapshotLifecycleService.GetCurrentOrNullAsync(
            plan,
            identity,
            IsCurrentSnapshotIdentity,
            cancellationToken: cancellationToken);
        if (snapshot == null)
        {
            return null;
        }

        var projection = _demandProjectionService.Build(plan, snapshot);
        return IsCurrentSnapshotIdentity(identity) && _appState.IsCurrentPlanSession(plan, identity.PlanSessionVersion)
            ? projection
            : null;
    }

    public async Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default)
    {
        var projection = await BuildCurrentDemandProjectionAsync(plan, cancellationToken);
        return projection?.ToMarketAnalysisMaterialAggregates();
    }

    public async Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default)
    {
        var projection = await BuildCurrentDemandProjectionAsync(plan, cancellationToken);
        return projection?.ToActiveProcurementMaterialAggregates();
    }

    private bool IsCurrentSnapshotIdentity(RecipeOperationSnapshotIdentity identity)
    {
        var versions = _appState.CurrentVersions;
        return _appState.PlanSessionVersion == identity.PlanSessionVersion &&
               versions.PlanStructureVersion == identity.PlanStructureVersion &&
               versions.PlanDecisionVersion == identity.PlanDecisionVersion &&
               versions.PlanPriceVersion == identity.PlanPriceVersion &&
               versions.SettingsVersion == identity.SettingsVersion &&
               string.Equals(identity.RecipeDataIdentity, RecipeDataIdentity, StringComparison.Ordinal);
    }
}

internal sealed class LightweightRecipeLayerWorkflowService : IRecipeLayerWorkflowService
{
    private readonly RecipeDemandProjectionService _demandProjectionService = new();

    public RecipeOperationSnapshotIdentity CreateSnapshotIdentity()
    {
        return RecipeOperationSnapshotIdentity.Unspecified;
    }

    public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan)
    {
        return _demandProjectionService.Build(plan, snapshot: null);
    }

    public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan)
    {
        return BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();
    }

    public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan)
    {
        return BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates();
    }

    public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<RecipeDemandProjection?>(BuildDemandProjection(plan));
    }

    public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildMarketAnalysisCandidates(plan));
    }

    public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildActiveProcurementItems(plan));
    }
}
