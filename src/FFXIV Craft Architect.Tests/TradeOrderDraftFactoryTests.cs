using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class TradeOrderDraftFactoryTests
{
    [Fact]
    public void CreateFromCurrentPlan_UsesHighestEstimatedRootSaleValueForDefaultTitle()
    {
        var appState = CreateAppStateWithPlan();
        var companyProfileId = Guid.NewGuid();
        var now = new DateTime(2026, 6, 17, 14, 0, 0, DateTimeKind.Utc);
        var factory = CreateFactory();

        var result = factory.CreateFromCurrentPlan(new TradeOrderCreateRequest(
            appState,
            companyProfileId,
            AssignedCrafterId: null,
            Title: null,
            now));

        Assert.True(result.CanCreate);
        Assert.Equal("Fancy Robe Commission", result.Order!.Title);
        Assert.Equal(TradeOrderSourceKind.ActiveCraftPlan, result.Order.SourceSnapshot.SourceKind);
        Assert.Equal("Aether", result.Order.SourceSnapshot.DataCenter);
        Assert.Equal(12_000m, result.Order.SourceSnapshot.RootItems.Single(item => item.Name == "Fancy Robe").EstimatedSaleValue);
        Assert.Null(result.Order.CraftPlanId);
        Assert.Null(result.Order.CraftPlanName);
        Assert.Null(result.Order.CraftPlanSavedAtUtc);
    }

    [Fact]
    public void CreateFromCurrentPlan_AssignsWholeOrderWhenCrafterProvided()
    {
        var appState = CreateAppStateWithPlan();
        var companyProfileId = Guid.NewGuid();
        var crafterId = Guid.NewGuid();
        var now = new DateTime(2026, 6, 17, 14, 0, 0, DateTimeKind.Utc);
        var factory = CreateFactory();

        var result = factory.CreateFromCurrentPlan(new TradeOrderCreateRequest(
            appState,
            companyProfileId,
            crafterId,
            "Raid Gear Batch",
            now));

        Assert.True(result.CanCreate);
        Assert.Equal("Raid Gear Batch", result.Order!.Title);
        Assert.Equal(crafterId, result.Order.AssignedCrafterId);
        Assert.Equal(TradeOrderStatus.Assigned, result.Order.Status);
        Assert.Contains(result.Order.History, history => history.Kind == TradeOrderHistoryEventKind.Assigned);
    }

    [Fact]
    public void CreateFromCurrentPlan_WithoutCrafterStartsReadyToAssignAndCapturesMaterials()
    {
        var appState = CreateAppStateWithPlan();
        var companyProfileId = Guid.NewGuid();
        var now = new DateTime(2026, 6, 17, 14, 0, 0, DateTimeKind.Utc);
        var factory = CreateFactory();

        var result = factory.CreateFromCurrentPlan(new TradeOrderCreateRequest(
            appState,
            companyProfileId,
            AssignedCrafterId: null,
            Title: "Open Commission",
            now));

        Assert.True(result.CanCreate);
        Assert.Equal(TradeOrderStatus.ReadyToAssign, result.Order!.Status);
        Assert.Null(result.Order.AssignedCrafterId);
        Assert.Equal(now, result.Order.CommissionedAtUtc);
        Assert.Contains(result.Order.SourceSnapshot.RootItems, item => item.Name == "Fancy Robe");
        var material = Assert.Single(result.Order.SourceSnapshot.Materials, item => item.ItemId == 300);
        Assert.Equal("Silk Thread", material.Name);
        Assert.Equal(6, material.Quantity);
        Assert.Equal(100m, material.UnitCost);
        Assert.Equal(600m, material.TotalCost);
    }

    [Fact]
    public void CreateFromRequestedOutputs_CreatesTradeNativeOrderWithoutActivePlan()
    {
        var companyProfileId = Guid.NewGuid();
        var crafterId = Guid.NewGuid();
        var now = new DateTime(2026, 6, 18, 18, 0, 0, DateTimeKind.Utc);
        var factory = CreateFactory();

        var result = factory.CreateFromRequestedOutputs(new TradeRequestedOrderCreateRequest(
            companyProfileId,
            crafterId,
            Title: null,
            Outputs:
            [
                new TradeRequestedOrderOutput(
                    700,
                    "Cobalt Ingot",
                    999,
                    MustBeHq: false,
                    EstimatedSaleValue: 1_500_000m),
                new TradeRequestedOrderOutput(
                    800,
                    "Rose Gold Nugget",
                    999,
                    MustBeHq: true,
                    EstimatedSaleValue: 2_000_000m)
            ],
            DataCenter: "Aether",
            World: "Siren",
            Notes: "Mail materials after payment.",
            now));

        Assert.True(result.CanCreate);
        Assert.Equal("Rose Gold Nugget Commission", result.Order!.Title);
        Assert.Equal(TradeOrderSourceKind.TradeRequestedOutputs, result.Order.SourceSnapshot.SourceKind);
        Assert.Equal("Trade requested outputs", result.Order.SourceSnapshot.SourcePlanName);
        Assert.Equal("Aether", result.Order.SourceSnapshot.DataCenter);
        Assert.Equal("Siren", result.Order.SourceSnapshot.World);
        Assert.Equal(crafterId, result.Order.AssignedCrafterId);
        Assert.Equal(TradeOrderStatus.Assigned, result.Order.Status);
        Assert.Equal("Mail materials after payment.", result.Order.Notes);
        Assert.Empty(result.Order.SourceSnapshot.Materials);
        Assert.Contains(result.Order.SourceSnapshot.RootItems, item => item.Name == "Rose Gold Nugget" && item.MustBeHq);
        Assert.Contains(result.Order.History, history => history.Kind == TradeOrderHistoryEventKind.Created && history.Note == "Created from requested outputs.");
        Assert.Contains(result.Order.History, history => history.Kind == TradeOrderHistoryEventKind.Assigned && history.CrafterId == crafterId);
    }

    [Fact]
    public void CreateFromRequestedOutputs_WithoutOutputsReturnsUnavailable()
    {
        var factory = CreateFactory();

        var result = factory.CreateFromRequestedOutputs(new TradeRequestedOrderCreateRequest(
            Guid.NewGuid(),
            AssignedCrafterId: null,
            Title: "Empty Order",
            Outputs: [],
            DataCenter: "Aether",
            World: null,
            Notes: null,
            DateTime.UtcNow));

        Assert.False(result.CanCreate);
        Assert.Null(result.Order);
    }

    private static AppState CreateAppStateWithPlan()
    {
        var appState = new AppState();
        appState.ApplyBuiltRecipePlanWithActiveItems(
            new CraftingPlan
            {
                Name = "Imported Gear",
                RootItems =
                [
                    new PlanNode
                    {
                        ItemId = 100,
                        Name = "Simple Ring",
                        Quantity = 1,
                        MarketPrice = 500m,
                        Source = AcquisitionSource.Craft,
                        CanCraft = true
                    },
                    new PlanNode
                    {
                        ItemId = 200,
                        Name = "Fancy Robe",
                        Quantity = 2,
                        MarketPrice = 6_000m,
                        Source = AcquisitionSource.Craft,
                        CanCraft = true,
                        Children =
                        [
                            new PlanNode
                            {
                                ItemId = 300,
                                Name = "Silk Thread",
                                Quantity = 6,
                                Source = AcquisitionSource.MarketBuyNq,
                                CanBuyFromMarket = true,
                                MarketPrice = 100m
                            }
                        ]
                    }
                ]
            });

        return appState;
    }

    private static TradeOrderDraftFactory CreateFactory()
    {
        return new TradeOrderDraftFactory(
            new LightweightRecipeLayerWorkflowForTradeOrderTests(),
            new CommissionCostBasisResolver());
    }

    private sealed class LightweightRecipeLayerWorkflowForTradeOrderTests : IRecipeLayerWorkflowService
    {
        private readonly RecipeDemandProjectionService _projectionService = new();

        public RecipeOperationSnapshotIdentity CreateSnapshotIdentity() => RecipeOperationSnapshotIdentity.Unspecified;

        public RecipeDemandProjection BuildDemandProjection(CraftingPlan? plan) =>
            _projectionService.Build(plan, snapshot: null);

        public IReadOnlyList<MaterialAggregate> BuildMarketAnalysisCandidates(CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToMarketAnalysisMaterialAggregates();

        public IReadOnlyList<MaterialAggregate> BuildActiveProcurementItems(CraftingPlan? plan) =>
            BuildDemandProjection(plan).ToActiveProcurementMaterialAggregates();

        public Task<RecipeDemandProjection?> BuildCurrentDemandProjectionAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<RecipeDemandProjection?>(BuildDemandProjection(plan));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentMarketAnalysisCandidatesAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildMarketAnalysisCandidates(plan));

        public Task<IReadOnlyList<MaterialAggregate>?> BuildCurrentActiveProcurementItemsAsync(
            CraftingPlan? plan,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MaterialAggregate>?>(BuildActiveProcurementItems(plan));
    }
}
