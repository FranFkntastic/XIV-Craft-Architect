using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class ProcurementSafetyContractTests
{
    [Fact]
    public async Task MissingConfigurationDefault_DeniesGenerationWithoutMutation()
    {
        var state = CreateState(100);
        state.ReplaceProcurementOverlay([RoutePlan(100, "Siren")]);
        var execution = new FakeRouteExecution();
        var service = CreateService(state, execution, new ProcurementRouteAvailability(default));
        var versions = state.CurrentVersions;

        var result = await service.RunAnalysisAsync(new ProcurementWorkflowRequest(() => true));

        Assert.Equal(ProcurementWorkflowStatus.Disabled, result.Status);
        Assert.Equal(ProcurementRouteAvailability.DisabledMessage, result.Message);
        Assert.Equal(versions, state.CurrentVersions);
        Assert.Equal("Siren", Assert.Single(state.ProcurementShoppingPlans).RecommendedWorld?.WorldName);
        Assert.Equal(0, execution.Calls);
    }

    [Fact]
    public void ExplicitFalseAvailability_DoesNotScheduleHiddenReconciliation()
    {
        var state = CreateState(100);
        state.ReplaceMarketAnalysis([], [RoutePlan(100, "Siren")]);
        var workflow = new FakeWorkflow();
        using var service = new ProcurementRouteReconciliationService(
            state,
            workflow,
            new CancellableOperationService(state),
            NullLogger<ProcurementRouteReconciliationService>.Instance,
            new ProcurementRouteAvailability(false),
            TimeSpan.Zero);

        service.Start();

        Assert.False(service.IsScheduled);
        Assert.False(state.IsProcurementRouteReconciling);
        Assert.Equal(0, workflow.Calls);
    }

    [Fact]
    public async Task DecisionRevisionChange_FencesLateRouteOutput()
    {
        var state = CreateState(100);
        var execution = new FakeRouteExecution(() => state.NotifyPlanDecisionChanged());
        var service = CreateService(state, execution, new ProcurementRouteAvailability(true));

        var result = await service.RunAnalysisAsync(new ProcurementWorkflowRequest(() => true));

        Assert.Equal(ProcurementWorkflowStatus.StaleDecision, result.Status);
        Assert.Empty(state.ProcurementShoppingPlans);
    }

    [Fact]
    public async Task PlanSessionChange_FencesLateRouteOutput()
    {
        var state = CreateState(100);
        var execution = new FakeRouteExecution(() =>
            state.ApplyBuiltRecipePlan(CreatePlan(200), []));
        var service = CreateService(state, execution, new ProcurementRouteAvailability(true));

        var result = await service.RunAnalysisAsync(new ProcurementWorkflowRequest(() => true));

        Assert.Equal(ProcurementWorkflowStatus.StalePlan, result.Status);
        Assert.Equal(200, Assert.Single(state.CurrentPlan!.RootItems).ItemId);
        Assert.Empty(state.ProcurementShoppingPlans);
    }

    [Fact]
    public async Task SupersededOperation_FencesLateRouteOutput()
    {
        var state = CreateState(100);
        var current = true;
        var execution = new FakeRouteExecution(() => current = false);
        var service = CreateService(state, execution, new ProcurementRouteAvailability(true));

        var result = await service.RunAnalysisAsync(new ProcurementWorkflowRequest(() => current));

        Assert.Equal(ProcurementWorkflowStatus.Superseded, result.Status);
        Assert.Empty(state.ProcurementShoppingPlans);
    }

    [Fact]
    public void TravelToleranceSelection_AppliesPublishedRouteWithoutSchedulingExecution()
    {
        var state = CreateState(100);
        var toleranceSelections = new[]
        {
            new MarketRouteToleranceSelection(
                0, 5, "fewest", 500, 0, 1, 0, 0,
                [RoutePlan(100, "Siren")], []),
            new MarketRouteToleranceSelection(
                6, 11, "cheapest", 400, 0, 2, 0, 0,
                [RoutePlan(100, "Faerie")], [])
        };
        var decision = new MarketRouteDecision(
            0, null, 400, 500, 0, 2, 1, 0, 0, false, null,
            FrontierOptions:
            [
                new MarketRouteFrontierOption(0, 5, 500, 1, 0),
                new MarketRouteFrontierOption(6, 11, 400, 2, 0)
            ])
        {
            ToleranceSelections = toleranceSelections
        };
        state.ReplaceProcurementOverlay([RoutePlan(100, "Siren")], decision);
        var workflow = new FakeWorkflow();
        using var reconciliation = new ProcurementRouteReconciliationService(
            state,
            workflow,
            new CancellableOperationService(state),
            NullLogger<ProcurementRouteReconciliationService>.Instance,
            new ProcurementRouteAvailability(true),
            TimeSpan.Zero);
        reconciliation.Start();

        var selected = state.TrySelectProcurementTravelTolerance(11);

        Assert.True(selected);
        Assert.Equal(11, state.ProcurementTravelTolerance);
        Assert.Equal(ProcurementRoutePublicationValidity.Current, state.ProcurementRouteValidity);
        Assert.Equal("Faerie", Assert.Single(state.ProcurementShoppingPlans).RecommendedWorld?.WorldName);
        Assert.Equal(400, state.ProcurementRouteDecision?.SelectedGilCost);
        Assert.False(reconciliation.IsScheduled);
        Assert.Equal(0, workflow.Calls);
    }

    [Fact]
    public void ProcurementScope_DefaultsToEntireRegion()
    {
        Assert.True(new AppState().ProcurementSearchEntireRegion);
    }

    private static ProcurementWorkflowService CreateService(
        AppState state,
        IProcurementRouteExecutionService execution,
        ProcurementRouteAvailability availability) =>
        new(
            state,
            execution,
            itemRefreshService: null!,
            new FixedRecipeLayer(),
            availability);

    private static AppState CreateState(int itemId)
    {
        var state = new AppState();
        state.ApplyBuiltRecipePlan(CreatePlan(itemId), []);
        return state;
    }

    private static CraftingPlan CreatePlan(int itemId) => new()
    {
        DataCenter = "Aether",
        World = "Siren",
        RootItems =
        [
            new PlanNode
            {
                ItemId = itemId,
                Name = $"Item {itemId}",
                Quantity = 5,
                Source = AcquisitionSource.MarketBuyNq,
                CanBuyFromMarket = true,
            },
        ],
    };

    private static DetailedShoppingPlan RoutePlan(int itemId, string world) => new()
    {
        ItemId = itemId,
        Name = $"Item {itemId}",
        QuantityNeeded = 5,
        RecommendedWorld = new WorldShoppingSummary
        {
            DataCenter = "Aether",
            WorldName = world,
            TotalQuantityPurchased = 5,
            TotalCost = 500,
        },
    };

    private sealed class FakeRouteExecution(Action? duringExecution = null) : IProcurementRouteExecutionService
    {
        public int Calls { get; private set; }

        public Task<ProcurementRouteExecutionResult> AnalyzeAsync(
            ProcurementRouteExecutionRequest request,
            IProgress<string>? progress = null,
            CancellationToken ct = default,
            MarketAnalysisExecutionOptions? executionOptions = null)
        {
            Calls++;
            duringExecution?.Invoke();
            return Task.FromResult(new ProcurementRouteExecutionResult(
                [RoutePlan(100, "Faerie")],
                [],
                [],
                [],
                []));
        }
    }

    private sealed class FixedRecipeLayer : IRecipeLayerWorkflowService
    {
        private static readonly IReadOnlyList<MaterialAggregate> Candidates =
        [
            new MaterialAggregate { ItemId = 100, Name = "Item 100", TotalQuantity = 5 },
        ];

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity() => RecipeOperationSnapshotIdentity.Unspecified;

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan) =>
            new RecipeDemandProjectionService().Build(plan, snapshot: null);

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan) => Candidates;

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan) => Candidates;

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RecipeDemandProjection?>(BuildDemandProjection(plan));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(Candidates);

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(Candidates);
    }

    private sealed class FakeWorkflow : IProcurementWorkflowService
    {
        public int Calls { get; private set; }

        public Task<ProcurementWorkflowResult> RunAnalysisAsync(
            ProcurementWorkflowRequest request,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new ProcurementWorkflowResult(ProcurementWorkflowStatus.Published, 1));
        }
    }
}
