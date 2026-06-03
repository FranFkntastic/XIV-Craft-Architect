using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Core.Services;

public sealed class CoreRecipeLayerWorkflowService : ICoreRecipeLayerWorkflowService
{
    private const string RecipeDataIdentity = "garland-current";

    private readonly CraftSessionState _session;
    private readonly IRecipeOperationSnapshotLifecycleService _snapshotLifecycleService;
    private readonly IRecipeDemandProjectionService _demandProjectionService;

    public CoreRecipeLayerWorkflowService(
        CraftSessionState session,
        IRecipeOperationSnapshotLifecycleService snapshotLifecycleService,
        IRecipeDemandProjectionService demandProjectionService)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _snapshotLifecycleService = snapshotLifecycleService ?? throw new ArgumentNullException(nameof(snapshotLifecycleService));
        _demandProjectionService = demandProjectionService ?? throw new ArgumentNullException(nameof(demandProjectionService));
    }

    public RecipeOperationSnapshotIdentity CreateSnapshotIdentity()
    {
        var versions = _session.CaptureVersionStamp();
        return new RecipeOperationSnapshotIdentity(
            PlanSessionVersion: _session.PlanSessionVersion,
            PlanStructureVersion: versions.PlanCore,
            PlanDecisionVersion: versions.PlanDecision,
            PlanPriceVersion: versions.PlanPrice,
            SettingsVersion: versions.SettingsContext,
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
        if (!_session.IsCurrentPlanSession(plan, identity.PlanSessionVersion))
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
        return IsCurrentSnapshotIdentity(identity) && _session.IsCurrentPlanSession(plan, identity.PlanSessionVersion)
            ? projection
            : null;
    }

    public async Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default)
    {
        var result = await BuildCurrentMarketAnalysisCandidateResultAsync(plan, cancellationToken);
        return result?.Candidates;
    }

    public async Task<CoreMarketAnalysisCandidateBuildResult?> BuildCurrentMarketAnalysisCandidateResultAsync(
        CraftingPlan? plan,
        CancellationToken cancellationToken = default)
    {
        if (plan == null)
        {
            var fallbackProjection = BuildDemandProjection(plan);
            return new CoreMarketAnalysisCandidateBuildResult(
                fallbackProjection.ToMarketAnalysisMaterialAggregates(),
                RecipeBasis: null);
        }

        var identity = CreateSnapshotIdentity();
        if (!_session.IsCurrentPlanSession(plan, identity.PlanSessionVersion))
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
        var candidates = projection.ToMarketAnalysisMaterialAggregates();
        if (!IsCurrentSnapshotIdentity(identity) ||
            !_session.IsCurrentPlanSession(plan, identity.PlanSessionVersion))
        {
            return null;
        }

        return new CoreMarketAnalysisCandidateBuildResult(
            candidates,
            StoredRecipeBasisMapper.ToStored(snapshot, candidates));
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
        var versions = _session.CaptureVersionStamp();
        return identity.PlanSessionVersion == _session.PlanSessionVersion &&
               identity.PlanStructureVersion == versions.PlanCore &&
               identity.PlanDecisionVersion == versions.PlanDecision &&
               identity.PlanPriceVersion == versions.PlanPrice &&
               identity.SettingsVersion == versions.SettingsContext &&
               string.Equals(identity.RecipeDataIdentity, RecipeDataIdentity, StringComparison.Ordinal);
    }
}
